Imports System.Threading
Imports System.Threading.Tasks

Namespace AI

    ''' 一段对话: 界面状态 (消息、工具进度、确认卡片、待发送的附件) 和给模型的历史都放在这里, 网页只负责显示。
    ''' 网页刷新或换窗口后要一份快照 (Snapshot) 即可还原。
    '''
    ''' 线程: 每轮放进 Task.Run 并设 RunOnServer, 流式片段和工具都在后台线程到达;
    ''' 工具里读写数据编组回界面线程, 等用户确认时在后台线程阻塞。状态的每次改动和对应的消息在同一把锁里完成, 快照与增量不会错位。
    Friend NotInheritable Class ChatSession

        Private Const MAX_RETRIES As Integer = 2
        Private Const MAX_DRAFT As Integer = 6
        Private Shared ReadOnly ConfirmTimeout As TimeSpan = TimeSpan.FromMinutes(30)

        Private ReadOnly _ai As ProAI
        Private ReadOnly _outbox As Outbox
        Private ReadOnly _lock As New Object
        Private ReadOnly _messages As New List(Of JObject)
        Private _history As New JArray()
        Private _turn As Turn
        Private _seq As Integer
        ' 这段对话是谁的 (Who.Key)。换人时清掉, 上一个人查到的数据不留在历史里
        Private _owner As String
        ' 历史被裁剪过: 提示词里告诉模型更早的内容已省略
        Private _trimmed As Boolean
        ' 上传过的文件 (整段对话可用) 和还没发出去的附件
        Private ReadOnly _files As New AttachmentStore()
        Private ReadOnly _draft As New List(Of Attachment)
        ' 等用户点确认的工具调用、可以在狐表中打开的工具结果, 按工具 id
        Private ReadOnly _pending As New Dictionary(Of String, PendingConfirm)
        Private ReadOnly _opens As New Dictionary(Of String, OpenTarget)

        Public Sub New(ai As ProAI, outbox As Outbox)
            _ai = ai
            _outbox = outbox
        End Sub

        ''' 一轮问答。被取消后作废, 晚到的片段和结果一律丢掉。
        Private NotInheritable Class Turn
            Public Message As JObject
            Public Question As String
            ''' 交给模型的用户消息: 文本, 或带图片的内容数组
            Public UserContent As Object
            Public Caller As Object
            Public Tools As Toolbox
            Public Prompt As String
            Public History As JArray
            Public Log As JObject
            ' 下面几个跨线程读写
            Public Cancelled As Boolean
            Public InTool As Boolean
            Public ToolSeq As Integer
            ''' 本轮已经写过数据: 失败了不自动重试, 免得写两遍
            Public Wrote As Boolean

            Public ReadOnly Property Id As String
                Get
                    Return CStr(Message("id"))
                End Get
            End Property
        End Class

        Private NotInheritable Class PendingConfirm
            Public ReadOnly Done As New ManualResetEventSlim(False)
            Public Approved As Boolean
            Public Turn As Turn
        End Class

        Public ReadOnly Property Busy As Boolean
            Get
                SyncLock _lock
                    Return _turn IsNot Nothing
                End SyncLock
            End Get
        End Property

        Private Function NextId(prefix As String) As String
            Return prefix & Interlocked.Increment(_seq).ToString()
        End Function

        '==============================================================
        ' 快照与配置
        '==============================================================

        ''' 当前完整状态。同时清掉还没推出去的增量: 快照之后只推快照之后的变化。
        Public Function Snapshot() As JObject
            SyncLock _lock
                _outbox.Clear()
                Return BuildSnapshot()
            End SyncLock
        End Function

        ''' 当前完整状态, 不动发件箱 (自测用)。
        Public Function PeekSnapshot() As JObject
            SyncLock _lock
                Return BuildSnapshot()
            End SyncLock
        End Function

        Private Function BuildSnapshot() As JObject
            Return New JObject From {
                {"type", "snapshot"},
                {"config", _ai.ConfigJson()},
                {"busy", _turn IsNot Nothing},
                {"messages", New JArray(_messages.Select(Function(m) m.DeepClone()).Cast(Of Object)().ToArray())},
                {"draft", DraftJson()}}
        End Function

        Private Function DraftJson() As JArray
            Return New JArray(_draft.Select(Function(a) a.ToJson()).Cast(Of Object)().ToArray())
        End Function

        Public Sub PushConfig()
            _outbox.Enqueue(New JObject From {{"type", "config"}, {"config", _ai.ConfigJson()}})
        End Sub

        Private Sub Notice(text As String, Optional level As String = "error")
            _outbox.Enqueue(New JObject From {{"type", "notice"}, {"text", text}, {"level", level}})
        End Sub

        '==============================================================
        ' 网页发来的操作
        '==============================================================

        ''' 处理网页消息。在界面线程上调用。
        Public Sub HandleWebMessage(msg As JObject)
            Select Case CStr(msg("type"))
                Case "send" : Send(CStr(msg("text")))
                Case "cancel" : Cancel()
                Case "newChat" : NewChat()
                Case "confirm" : Decide(CStr(msg("id")), msg("ok") IsNot Nothing AndAlso CBool(msg("ok")))
                Case "open" : OpenInFoxtable(CStr(msg("id")))
                Case "attach" : Attach(CStr(msg("name")), CStr(msg("kind")), CStr(msg("data")))
                Case "detach" : Detach(CStr(msg("id")))
                Case "voice" : Transcribe(CStr(msg("id")), CStr(msg("data")))
            End Select
        End Sub

        ''' 识别一段录音, 结果回 transcript 消息。
        Private Sub Transcribe(id As String, data As String)
            Dim speech = _ai.Speech
            If speech Is Nothing Then
                _outbox.Enqueue(New JObject From {{"type", "transcript"}, {"id", id}, {"error", "这个助手没有配置语音识别。"}})
                Return
            End If
            speech.Transcribe(id, data, _outbox)
        End Sub

        ''' 开始新对话: 清掉消息、模型历史和上传的文件。正在回答的先取消。
        Public Sub NewChat()
            Cancel()
            SyncLock _lock
                ClearLocked()
            End SyncLock
        End Sub

        Private Sub ClearLocked()
            _messages.Clear()
            _history = New JArray()
            _trimmed = False
            _owner = Nothing
            _files.Clear()
            _draft.Clear()
            _opens.Clear()
            _outbox.Clear()
            _outbox.Enqueue(BuildSnapshot())
        End Sub

        ''' 给模型的历史 (自测用)。
        Public Function HistoryCopy() As JArray
            SyncLock _lock
                Return DirectCast(_history.DeepClone(), JArray)
            End SyncLock
        End Function

        ''' 上传过的文件 (提示词预览用)。
        Public ReadOnly Property Files As AttachmentStore
            Get
                Return _files
            End Get
        End Property

        '==============================================================
        ' 附件
        '==============================================================

        ''' 加一个待发送的附件。kind: image (data 为 data URL, 网页已缩小) 或 file (data 为 base64)。在界面线程上调用。
        Public Function Attach(name As String, kind As String, data As String) As Boolean
            Return AddDraft(name,
                Function()
                    If kind = "image" Then
                        CheckAllowed(True)
                        Return AttachmentReader.Image(name, data)
                    End If
                    CheckAllowed(False)
                    Dim bytes As Byte()
                    Try
                        bytes = Convert.FromBase64String(If(data, ""))
                    Catch ex As FormatException
                        Throw New IO.InvalidDataException("文件内容有误。")
                    End Try
                    Return AttachmentReader.Sheet(name, bytes)
                End Function)
        End Function

        ''' 按文件内容附上: 图片 (按扩展名认) 缩小后附上, 其余当表格读。在界面线程上调用。
        Public Function AttachBytes(name As String, bytes As Byte()) As Boolean
            Return AddDraft(name,
                Function()
                    Select Case IO.Path.GetExtension(If(name, "")).ToLowerInvariant()
                        Case ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
                            CheckAllowed(True)
                            Return AttachmentReader.ImageFile(name, bytes)
                    End Select
                    CheckAllowed(False)
                    Return AttachmentReader.Sheet(name, bytes)
                End Function)
        End Function

        Private Sub CheckAllowed(image As Boolean)
            If image AndAlso Not _ai.AllowImages Then Throw New IO.InvalidDataException("这个助手不支持发图片。")
            If Not image AndAlso Not _ai.AllowFiles Then Throw New IO.InvalidDataException("这个助手不支持发文件。")
        End Sub

        Private Function AddDraft(name As String, make As Func(Of Attachment)) As Boolean
            Dim a As Attachment
            Try
                SyncLock _lock
                    If _draft.Count >= MAX_DRAFT Then Throw New IO.InvalidDataException("一次最多附 " & MAX_DRAFT & " 个文件。")
                End SyncLock
                a = make()
            Catch ex As Exception
                SyncLock _lock
                    Notice("「" & name & "」没有附上: " & ex.GetBaseException().Message)
                End SyncLock
                Return False
            End Try
            SyncLock _lock
                _files.Add(a)
                _draft.Add(a)
                _outbox.Enqueue(New JObject From {{"type", "draft"}, {"draft", DraftJson()}})
            End SyncLock
            Return True
        End Function

        Public Sub Detach(id As String)
            SyncLock _lock
                _draft.RemoveAll(Function(a) a.Id = id)
                _outbox.Enqueue(New JObject From {{"type", "draft"}, {"draft", DraftJson()}})
            End SyncLock
        End Sub

        '==============================================================
        ' 发问题
        '==============================================================

        ''' 发一个问题 (连同待发送的附件)。正在回答时忽略, 返回 False。在界面线程上调用。
        Public Function Send(text As String) As Boolean
            text = If(text, "").Trim()
            Dim attachments As List(Of Attachment)
            SyncLock _lock
                attachments = _draft.ToList()
            End SyncLock
            If (text = "" AndAlso attachments.Count = 0) OrElse Busy Then Return False
            If text = "" Then text = If(attachments.Any(Function(a) a.Kind = "image"), "请看图片。", "请看文件。")

            Dim user As New JObject From {{"id", NextId("u")}, {"role", "user"}, {"text", text}}
            If attachments.Count > 0 Then user("attachments") = New JArray(attachments.Select(Function(a) a.ToJson()).Cast(Of Object)().ToArray())
            Dim answer As New JObject From {
                {"id", NextId("a")}, {"role", "assistant"}, {"text", ""}, {"think", ""},
                {"tools", New JArray()}, {"status", "streaming"}}

            Dim problem As String = Nothing
            Dim turn As New Turn With {.Message = answer, .Question = text, .UserContent = UserContent(text, attachments)}
            Dim setup As TurnSetup = Nothing
            Try
                If Not Llm.Available Then Throw New NotSupportedException("AI 面板需要狐表 2026-11-16 或更新的版本, 请先升级狐表。")
                ' 谁在问、表结构、技能库、现场每轮重读: 读狐表对象要在界面线程, 这里正是
                setup = _ai.PrepareTurn(_files)
                turn.Tools = setup.Tools
                turn.Prompt = setup.Prompt
            Catch ex As Exception
                problem = ex.Message
            End Try
            turn.Log = New JObject From {
                {"time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")},
                {"user", If(setup Is Nothing, "", setup.Who.Name)},
                {"roles", If(setup Is Nothing, New JArray(), New JArray(setup.Who.Roles.Cast(Of Object)().ToArray()))},
                {"window", If(_ai.HostWindowName(), "")},
                {"question", text},
                {"tools", New JArray()},
                {"writes", New JArray()}}
            If attachments.Count > 0 Then turn.Log("files") = New JArray(attachments.Select(Function(a) a.Name).Cast(Of Object)().ToArray())

            SyncLock _lock
                ' 换了人: 清掉上一个人的对话再开始
                If setup IsNot Nothing AndAlso _owner IsNot Nothing AndAlso _owner <> setup.Who.Key Then ClearLocked()
                If setup IsNot Nothing Then _owner = setup.Who.Key
                If problem Is Nothing Then
                    Dim trimmed As Boolean
                    Dim budget = Math.Max(2000, _ai.MaxContextChars - turn.Prompt.Length - text.Length)
                    turn.History = HistoryTrimmer.Trim(_history, budget, trimmed)
                    If trimmed Then _trimmed = True
                    If _trimmed Then turn.Prompt &= vbLf & "注意: 更早的对话内容已省略。用户提到你看不到的前文时, 请他重新说明。" & vbLf
                End If
                _draft.Clear()
                _messages.Add(user)
                _messages.Add(answer)
                _outbox.Enqueue(New JObject From {{"type", "draft"}, {"draft", DraftJson()}})
                _outbox.Enqueue(New JObject From {{"type", "message"}, {"message", user.DeepClone()}})
                _outbox.Enqueue(New JObject From {{"type", "message"}, {"message", answer.DeepClone()}})
                If problem IsNot Nothing Then
                    Finish(answer, "error", problem)
                    WriteLog(turn, "error", problem)
                    Return True
                End If
                _turn = turn
                _outbox.Enqueue(New JObject From {{"type", "busy"}, {"busy", True}})
            End SyncLock
            Task.Run(Function() RunTurnAsync(turn))
            Return True
        End Function

        ''' 用户消息: 表格附上前几行; 有图片时用 OpenAI 的内容数组。
        Private Shared Function UserContent(text As String, attachments As List(Of Attachment)) As Object
            If attachments.Count = 0 Then Return text
            Dim sb As New Text.StringBuilder(text)
            For Each a In attachments
                sb.AppendLine().AppendLine().Append(a.Describe())
            Next
            Dim images = attachments.Where(Function(a) a.Kind = "image").ToList()
            If images.Count = 0 Then Return sb.ToString()
            Dim parts As New JArray(New JObject From {{"type", "text"}, {"text", sb.ToString()}})
            For Each img In images
                parts.Add(New JObject From {{"type", "image_url"}, {"image_url", New JObject From {{"url", img.DataUrl}}}})
            Next
            Return parts
        End Function

        ''' 停止当前回答。界面上立即显示已取消; 模型那边能停就停, 停不了的晚到结果丢掉。等确认的一律当作没确认。
        Public Sub Cancel()
            Dim t As Turn
            SyncLock _lock
                t = _turn
                If t Is Nothing Then Return
                t.Cancelled = True
                _turn = Nothing
                For Each tool As JObject In DirectCast(t.Message("tools"), JArray)
                    Dim st = CStr(tool("status"))
                    If st = "running" OrElse st = "confirm" Then tool("status") = "cancelled"
                Next
                For Each p In _pending.Values.Where(Function(x) x.Turn Is t).ToList()
                    p.Approved = False
                    p.Done.Set()
                Next
                Finish(t.Message, "cancelled", Nothing, includeTools:=True)
                _outbox.Enqueue(New JObject From {{"type", "busy"}, {"busy", False}})
                WriteLog(t, "cancelled", Nothing)
            End SyncLock
            ' 工具执行中调 Cancel 会让之后的 Cancel 失效 (stage0): 只记标记, 下一个片段到达时再调
            If Not t.InTool AndAlso t.Caller IsNot Nothing Then Llm.Cancel(t.Caller)
        End Sub

        '==============================================================
        ' 确认卡片、在狐表中打开
        '==============================================================

        ''' 用户在确认卡片上点了确认或取消。
        Public Function Decide(toolId As String, approved As Boolean) As Boolean
            SyncLock _lock
                Dim p As PendingConfirm = Nothing
                If toolId Is Nothing OrElse Not _pending.TryGetValue(toolId, p) Then Return False
                p.Approved = approved
                p.Done.Set()
                Return True
            End SyncLock
        End Function

        ''' 等用户确认的工具 id (自测用)。
        Public Function PendingIds() As String()
            SyncLock _lock
                Return _pending.Keys.ToArray()
            End SyncLock
        End Function

        ' 工具线程上: 弹确认卡片, 阻塞到用户点击、取消或超时
        Private Function WaitConfirm(turn As Turn, tool As JObject, preview As JObject) As Boolean
            Dim id = CStr(tool("id"))
            Dim p As New PendingConfirm With {.Turn = turn}
            SyncLock _lock
                If turn.Cancelled Then Return False
                tool("status") = "confirm"
                tool("confirm") = preview
                _pending(id) = p
                _outbox.Enqueue(New JObject From {{"type", "tool"}, {"id", turn.Id}, {"tool", tool.DeepClone()}})
            End SyncLock
            Dim answered = p.Done.Wait(ConfirmTimeout)
            SyncLock _lock
                _pending.Remove(id)
                If turn.Cancelled Then Return False
                tool("status") = "running"
                tool("decision") = If(Not answered, "timeout", If(p.Approved, "approved", "rejected"))
                _outbox.Enqueue(New JObject From {{"type", "tool"}, {"id", turn.Id}, {"tool", tool.DeepClone()}})
            End SyncLock
            Return answered AndAlso p.Approved
        End Function

        ''' 把工具结果对应的行在狐表主界面里筛出来。在界面线程上调用。
        Public Function OpenInFoxtable(toolId As String) As String
            Dim target As OpenTarget = Nothing
            SyncLock _lock
                If toolId Is Nothing OrElse Not _opens.TryGetValue(toolId, target) Then Return "这个结果不能打开。"
            End SyncLock
            Dim problem = TableOpener.Open(target)
            If problem IsNot Nothing Then
                SyncLock _lock
                    Notice(problem)
                End SyncLock
            End If
            Return problem
        End Function

        '==============================================================
        ' 一轮问答 (后台线程)
        '==============================================================

        Private Async Function RunTurnAsync(turn As Turn) As Task
            For attempt = 0 To MAX_RETRIES
                Dim caller As Object
                Dim ok As Boolean
                Dim err As String = ""
                Try
                    caller = NewCaller(turn)
                    turn.Caller = caller
                    If turn.Cancelled Then Return
                    ok = Await Llm.GetResultAsync(caller).ConfigureAwait(False)
                    If Not ok Then err = Fx.GetStr(caller, "Error")
                Catch ex As Exception
                    ok = False
                    caller = turn.Caller
                    err = ex.GetBaseException().Message
                End Try
                If turn.Cancelled Then Return

                If ok Then
                    Complete(turn, caller)
                    Return
                End If
                ' 已经写过数据的轮次不自动重试 (stage0), 免得重复写入
                If attempt < MAX_RETRIES AndAlso Retryable(err) AndAlso Not turn.Wrote Then
                    SyncLock _lock
                        If turn.Cancelled Then Return
                        ' 重试前清掉这次已经显示的内容, 免得重复
                        turn.Message("text") = ""
                        turn.Message("think") = ""
                        turn.Message("tools") = New JArray()
                        Patch(turn.Message, New JObject From {{"text", ""}, {"think", ""}, {"tools", New JArray()}, {"retry", attempt + 1}})
                    End SyncLock
                    Await Task.Delay(If(attempt = 0, 1500, 4000)).ConfigureAwait(False)
                    If turn.Cancelled Then Return
                    Continue For
                End If
                SyncLock _lock
                    If turn.Cancelled Then Return
                    _turn = Nothing
                    Dim friendly = FriendlyError(err)
                    If turn.Wrote Then friendly &= vbLf & "本轮已经写入过数据, 没有自动重试; 重试前请先确认数据是否已经改好。"
                    Finish(turn.Message, "error", friendly, includeTools:=True)
                    _outbox.Enqueue(New JObject From {{"type", "busy"}, {"busy", False}})
                    WriteLog(turn, "error", err)
                End SyncLock
                Return
            Next
        End Function

        ''' 按模板新建这一轮的 caller: 历史用副本, 失败或取消时原历史不动, 相当于把这轮截掉。
        Private Function NewCaller(turn As Turn) As Object
            Dim c = Llm.CloneConfig(_ai.Caller)
            ' LLMCaller 只在 Messages 为空时才用 SystemPrompt, 之后各轮沿用历史里第一轮的。
            ' 提示词每轮都变 (现场、权限、技能库), 所以自己放在开头; 历史里不留 system 消息 (见 HistoryTrimmer)
            Dim msgs As New JArray(New JObject From {{"role", "system"}, {"content", turn.Prompt}})
            For Each m In turn.History
                msgs.Add(m.DeepClone())
            Next
            Fx.Set(c, "Messages", msgs)
            Dim content = TryCast(turn.UserContent, JToken)
            Fx.Set(c, "UserMessage", If(content Is Nothing, turn.UserContent, content.DeepClone()))
            Fx.Set(c, "RunOnServer", True)
            ' 一律开流式: 非流式和等首个响应期间都取消不了 (stage0)
            Fx.Set(c, "stream", True)
            Fx.Set(c, "SSEStreamHandler", New Action(Of String, String)(Sub(k, s) OnChunk(turn, c, k, s)))
            Fx.Set(c, "ToolExecutor", New Func(Of String, JObject, String)(Function(n, a) RunTool(turn, n, a)))
            For Each def In turn.Tools.Definitions()
                Llm.AddJsonTool(c, def.ToString(Newtonsoft.Json.Formatting.None))
            Next
            Return c
        End Function

        Private Sub OnChunk(turn As Turn, caller As Object, kind As String, text As String)
            If turn.Cancelled Then
                If Not turn.InTool Then Llm.Cancel(caller)
                Return
            End If
            If String.IsNullOrEmpty(text) OrElse (kind <> "answer" AndAlso kind <> "think") Then Return
            Dim field = If(kind = "answer", "text", "think")
            SyncLock _lock
                If turn.Cancelled Then Return
                turn.Message(field) = CStr(turn.Message(field)) & text
                _outbox.Delta(turn.Id, kind, text)
            End SyncLock
        End Sub

        Private Function RunTool(turn As Turn, name As String, args As JObject) As String
            If turn.Cancelled Then Return "用户已取消本次提问, 不要再调用工具。"
            turn.InTool = True
            Dim tool As New JObject From {
                {"id", turn.Id & "-t" & Interlocked.Increment(turn.ToolSeq)},
                {"name", name},
                {"label", turn.Tools.Label(name, args)},
                {"status", "running"}}
            Dim logItem As New JObject From {{"name", name}, {"args", Shorten(If(args Is Nothing, "", args.ToString(Newtonsoft.Json.Formatting.None)), 2000)}}
            Try
                SyncLock _lock
                    If turn.Cancelled Then Return "用户已取消本次提问。"
                    DirectCast(turn.Message("tools"), JArray).Add(tool)
                    _outbox.Enqueue(New JObject From {{"type", "tool"}, {"id", turn.Id}, {"tool", tool.DeepClone()}})
                    DirectCast(turn.Log("tools"), JArray).Add(logItem)
                End SyncLock

                Dim ctx As New ToolContext(
                    Function(preview) WaitConfirm(turn, tool, preview),
                    Function() turn.Cancelled,
                    Sub(entry)
                        turn.Wrote = True
                        SyncLock _lock
                            ' 本轮已经结束 (被取消) 时日志已写出, 补记一条
                            If turn.Log IsNot Nothing Then
                                DirectCast(turn.Log("writes"), JArray).Add(entry)
                            Else
                                Dim late As New JObject From {{"time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}, {"status", "late-write"},
                                                              {"question", turn.Question}, {"tools", New JArray()}, {"writes", New JArray(entry)}}
                                UiThread.Post(Sub() _ai.OnTurnLogged(late))
                            End If
                        End SyncLock
                    End Sub,
                    _files)
                Dim outcome As ToolOutcome = Nothing
                Dim failure As String = Nothing
                Try
                    outcome = turn.Tools.Execute(name, args, ctx)
                Catch ex As ToolArgumentException
                    failure = ex.Message
                Catch ex As Exception
                    failure = ex.GetBaseException().Message
                End Try

                SyncLock _lock
                    logItem("status") = If(failure Is Nothing, "done", "error")
                    logItem("summary") = If(failure, outcome.Summary)
                    If Not turn.Cancelled Then
                        If failure Is Nothing Then
                            tool("status") = "done"
                            tool("summary") = outcome.Summary
                            If outcome.Table IsNot Nothing Then tool("table") = outcome.Table
                            ' 图表数据只给界面 (可多达 500 组), 给模型的是 outcome.Result 里截断后的结果
                            If outcome.Chart IsNot Nothing Then tool("chart") = outcome.Chart
                            If outcome.Open IsNot Nothing AndAlso outcome.Open.Usable Then
                                _opens(CStr(tool("id"))) = outcome.Open
                                tool("open") = True
                            End If
                        Else
                            tool("status") = "error"
                            tool("summary") = failure
                        End If
                        _outbox.Enqueue(New JObject From {{"type", "tool"}, {"id", turn.Id}, {"tool", tool.DeepClone()}})
                    End If
                End SyncLock
                ' 出错时把原因交给模型, 让它改参数再试
                If failure IsNot Nothing Then Return "调用失败: " & failure
                Return outcome.Result.ToString(Newtonsoft.Json.Formatting.None)
            Finally
                turn.InTool = False
            End Try
        End Function

        Private Sub Complete(turn As Turn, caller As Object)
            Dim result = Fx.GetStr(caller, "Result")
            Dim think = Fx.GetStr(caller, "Think")
            Dim history = TryCast(Fx.TryGet(caller, "Messages"), JArray)
            Dim usage As JToken = Nothing
            Dim rjo = TryCast(Fx.TryGet(caller, "ResultJObject"), JObject)
            If rjo IsNot Nothing Then usage = rjo("usage")
            SyncLock _lock
                If turn.Cancelled Then Return
                _turn = Nothing
                If history IsNot Nothing Then _history = history
                ' 以最终结果为准, 流式拼出来的只是过程
                If result <> "" Then turn.Message("text") = result
                If think <> "" Then turn.Message("think") = think
                Dim patchObj As New JObject From {{"text", turn.Message("text")}, {"think", turn.Message("think")}}
                If usage IsNot Nothing AndAlso usage.Type = JTokenType.Object Then
                    turn.Message("usage") = usage.DeepClone()
                    patchObj("usage") = usage.DeepClone()
                    turn.Log("usage") = usage.DeepClone()
                End If
                turn.Message("status") = "done"
                patchObj("status") = "done"
                Patch(turn.Message, patchObj)
                _outbox.Enqueue(New JObject From {{"type", "busy"}, {"busy", False}})
                WriteLog(turn, "done", Nothing)
            End SyncLock
        End Sub

        ' 以下在锁内调用
        Private Sub Finish(message As JObject, status As String, errorText As String, Optional includeTools As Boolean = False)
            message("status") = status
            Dim p As New JObject From {{"status", status}}
            If errorText IsNot Nothing Then
                message("error") = errorText
                p("error") = errorText
            End If
            If includeTools Then p("tools") = message("tools").DeepClone()
            Patch(message, p)
        End Sub

        Private Sub Patch(message As JObject, p As JObject)
            _outbox.Enqueue(New JObject From {{"type", "update"}, {"id", message("id")}, {"patch", p}})
        End Sub

        Private Sub WriteLog(turn As Turn, status As String, errorText As String)
            If turn.Log Is Nothing Then Return
            Dim entry = DirectCast(turn.Log.DeepClone(), JObject)
            entry("status") = status
            entry("answer") = Shorten(CStr(turn.Message("text")), 4000)
            If errorText IsNot Nothing Then entry("error") = errorText
            turn.Log = Nothing
            UiThread.Post(Sub() _ai.OnTurnLogged(entry))
        End Sub

        Private Shared Function Shorten(s As String, max As Integer) As String
            If s Is Nothing Then Return ""
            Return If(s.Length > max, s.Substring(0, max) & "…", s)
        End Function

        '==============================================================
        ' 错误
        '==============================================================

        ' LLMCaller 的 Error 是文本, 如 "状态码: 429"; 超时是 "状态码: 0" (stage0)
        Private Shared Function StatusCode(err As String) As Integer
            Dim m = System.Text.RegularExpressions.Regex.Match(If(err, ""), "状态码\s*[:：]\s*(\d+)")
            If m.Success Then Return CInt(m.Groups(1).Value)
            Return -1
        End Function

        Private Shared Function Retryable(err As String) As Boolean
            Dim code = StatusCode(err)
            Return code = 0 OrElse code = 429 OrElse (code >= 500 AndAlso code < 600)
        End Function

        Private Shared Function FriendlyError(err As String) As String
            Dim code = StatusCode(err)
            Dim head As String
            Select Case code
                Case 0 : head = "连不上模型服务或等待超时, 请检查网络后重试。"
                Case 400 : head = "模型服务拒绝了这次请求 (参数有误; 如果发了图片, 可能是模型不支持看图)。"
                Case 401, 403 : head = "模型服务拒绝访问, 请检查 API Key 是否正确、是否有权限。"
                Case 402 : head = "模型服务账户余额不足。"
                Case 404 : head = "模型服务地址或模型名有误。"
                Case 429 : head = "模型服务繁忙 (请求太频繁或额度用完), 请稍后再试。"
                Case 500 To 599 : head = "模型服务出错, 请稍后再试。"
                Case Else : head = "回答失败。"
            End Select
            If String.IsNullOrWhiteSpace(err) Then Return head
            Return head & vbLf & "(" & err.Trim() & ")"
        End Function
    End Class

    ''' 一轮开始时在界面线程上准备好的东西。
    Friend NotInheritable Class TurnSetup
        Public Who As Who
        Public Tools As Toolbox
        Public Prompt As String
    End Class

    ''' 在狐表主界面里筛出某张表的若干行。只在界面线程上调用。
    Friend Module TableOpener

        Private Const MAX_IDS As Integer = 5000

        ''' 成功返回 Nothing, 否则返回原因。
        Public Function Open(target As OpenTarget) As String
            Dim tbl As Object = Nothing
            Try
                For Each t As Object In DirectCast(Fx.Global("Tables"), IEnumerable)
                    If String.Equals(Fx.GetStr(t, "Name"), target.Table, StringComparison.OrdinalIgnoreCase) Then tbl = t : Exit For
                Next
            Catch
            End Try
            If tbl Is Nothing Then Return "表「" & target.Table & "」没有在主界面显示, 不能在狐表中打开。"
            Dim filter = target.Filter
            If filter Is Nothing Then
                filter = "[_Identify] In (" & String.Join(",", target.Ids.Take(MAX_IDS).Select(Function(x) x.ToString(Globalization.CultureInfo.InvariantCulture))) & ")"
            End If
            Try
                Fx.Set(tbl, "Filter", filter)
            Catch ex As Exception
                Return "没能筛出这些行: " & ex.GetBaseException().Message
            End Try
            Try
                Fx.SetGlobal("MainTable", tbl)
            Catch
            End Try
            Dim main = Fx.MainForm()
            If main IsNot Nothing Then
                If main.WindowState = Windows.Forms.FormWindowState.Minimized Then main.WindowState = Windows.Forms.FormWindowState.Normal
                main.Activate()
            End If
            If target.Filter Is Nothing AndAlso target.Ids.Count > MAX_IDS Then Return "行太多, 只筛出了前 " & MAX_IDS & " 行。"
            Return Nothing
        End Function
    End Module
End Namespace
