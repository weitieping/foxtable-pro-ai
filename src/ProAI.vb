Imports System.Globalization
Imports System.Text
Imports FoxtablePro.AI

''' 运行期 AI 助手: 嵌在系统里, 让使用系统的人用自然语言查数、录单、导入。
''' <example>
''' <code>
''' ' 全局代码里: Public ai As FoxtablePro.ProAI
''' ai = New FoxtablePro.ProAI("https://api.deepseek.com/chat/completions", "deepseek-flash", key)
''' ai.Tables.Allow("订单", "客户", "产品")
''' ai.AttachTo(e.Form.Controls("Panel1"))   ' 或 ai.ShowPopup()、ai.ShowSidebar()
''' </code>
''' </example>
''' 一个 ProAI 就是一段常驻的对话: 在不同窗口间挪动、窗口关闭再打开, 对话都还在。
''' 通常建一个放在全局变量里, 在各窗口的 AfterLoad 里 AttachTo。
Public NotInheritable Class ProAI
    Implements IDisposable

    Private ReadOnly _template As Object
    Private ReadOnly _tables As New AITables()
    Private ReadOnly _forms As New AIForms()
    Private ReadOnly _functions As New AIFunctions()
    Private ReadOnly _tools As New AITools()
    Private ReadOnly _fillTracker As New FormFillTracker()
    Private ReadOnly _outbox As New Outbox()
    Private ReadOnly _session As ChatSession
    Private _host As PanelHost
    Private _title As String = "AI 助手"
    Private _welcome As String = "你好，我可以帮你查询和统计系统里的数据。"
    Private _theme As String = "#2563eb"
    Private _placeholder As String = "输入问题，Enter 发送，Shift+Enter 换行"
    Private _disposed As Boolean

    ''' <param name="url">OpenAI 兼容的对话接口, 如 https://api.deepseek.com/chat/completions</param>
    ''' <param name="model">模型名</param>
    ''' <param name="apiKey">API Key</param>
    Public Sub New(url As String, model As String, apiKey As String)
        UiThread.Init()
        If Llm.Available Then _template = Llm.Create(url, model, apiKey)
        _session = New ChatSession(Me, _outbox)
        QuickQuestions = New QuickQuestionList(Me)
    End Sub

    '==============================================================
    ' 配置
    '==============================================================

    ''' 放行哪些表和列。没放行的表 AI 看不到。
    Public ReadOnly Property Tables As AITables
        Get
            Return _tables
        End Get
    End Property

    ''' 放行哪些录入窗口: AI 打开窗口填好内容, 用户核对后自己保存。如 ai.Forms.Allow("订单录入")。
    Public ReadOnly Property Forms As AIForms
        Get
            Return _forms
        End Get
    End Property

    ''' 放行哪些内部函数 (第 4 档): 出库、审核这类业务动作。函数开头写 @AI工具 注释, 如 ai.Functions.Allow("出库")。
    Public ReadOnly Property Functions As AIFunctions
        Get
            Return _functions
        End Get
    End Property

    ''' 代码里直接写的只读工具, 如 ai.Tools.Add("查库存", "查产品的当前库存。", Function(产品 As String) ...)。
    Public ReadOnly Property Tools As AITools
        Get
            Return _tools
        End Get
    End Property

    ''' 用户能不能发图片 (拍照入表), 默认不能。要模型支持看图, 如 deepseek-flash。
    Public Property AllowImages As Boolean
        Get
            Return _allowImages
        End Get
        Set(value As Boolean)
            _allowImages = value
            _session.PushConfig()
        End Set
    End Property

    ''' 用户能不能发 Excel / CSV 文件 (智能导入、按文件问答), 默认不能。
    Public Property AllowFiles As Boolean
        Get
            Return _allowFiles
        End Get
        Set(value As Boolean)
            _allowFiles = value
            _session.PushConfig()
        End Set
    End Property

    ''' AI 统计完能不能直接出图 (柱状、折线、饼图等), 默认能。设为 False 则不出任何图表, 只用表格和文字回答。
    Public Property AllowCharts As Boolean
        Get
            Return _allowCharts
        End Get
        Set(value As Boolean)
            _allowCharts = value
            _session.PushConfig()
        End Set
    End Property

    Private _allowImages As Boolean
    Private _allowFiles As Boolean
    Private _allowCharts As Boolean = True

    ''' 开启语音输入: 输入框旁出现麦克风, 说完转成文字填进输入框。url 为空则关闭。
    ''' DeepSeek: SetSpeech("https://api.deepseek.com/v1/tools/asr", "", key), 不用填模型, key 与对话的相同。
    ''' 也可以用 OpenAI 兼容的 /audio/transcriptions 接口, 如硅基流动 https://api.siliconflow.cn/v1/audio/transcriptions + FunAudioLLM/SenseVoiceSmall。
    Public Sub SetSpeech(url As String, model As String, apiKey As String)
        _speech = If(String.IsNullOrWhiteSpace(url), Nothing, New SpeechClient(url.Trim(), model, apiKey))
        _session.PushConfig()
    End Sub

    Private _speech As SpeechClient

    Friend ReadOnly Property Speech As SpeechClient
        Get
            Return _speech
        End Get
    End Property

    ''' 问答日志目录, 默认是工程目录下的 ailogs, 每天一个 .jsonl 文件。相对路径按工程目录算; 设为空字符串则不写文件。
    ''' 要写进数据库等, 用 Logged 事件。
    Public Property LogFolder As String = "ailogs"

    ''' 每轮问答结束 (完成、出错或取消) 时触发, 在界面线程上。e 里有谁、问了什么、查了或改了哪些数据。
    Public Event Logged As EventHandler(Of AILogEventArgs)

    ''' 底层的 LLMCaller (狐表官方), 可以设模型参数, 如 ai.Caller.temperature = 0.3。
    ''' 狐表低于 2026-11-16 时为 Nothing。
    Public ReadOnly Property Caller As Object
        Get
            Return _template
        End Get
    End Property

    ''' 面板标题。
    Public Property Title As String
        Get
            Return _title
        End Get
        Set(value As String)
            _title = If(value, "")
            _host?.UpdateTitle()
            _session.PushConfig()
        End Set
    End Property

    ''' 没有对话时显示的欢迎语。
    Public Property Welcome As String
        Get
            Return _welcome
        End Get
        Set(value As String)
            _welcome = If(value, "")
            _session.PushConfig()
        End Set
    End Property

    ''' 主题色, 如 "#2563eb"。
    Public Property ThemeColor As String
        Get
            Return _theme
        End Get
        Set(value As String)
            _theme = If(value, "")
            _session.PushConfig()
        End Set
    End Property

    ''' 输入框里的提示文字。
    Public Property Placeholder As String
        Get
            Return _placeholder
        End Get
        Set(value As String)
            _placeholder = If(value, "")
            _session.PushConfig()
        End Set
    End Property

    ''' 快捷问题, 显示在欢迎语下面, 点一下就发送。如 ai.QuickQuestions.Add("本月销售额是多少？")
    Public ReadOnly Property QuickQuestions As IList(Of String)

    ''' 追加给 AI 的说明 (口径、术语等), 放进系统提示词。
    Public Property Instructions As String = ""

    ''' 查询明细时最多返回多少行, 默认 100。
    Public Property MaxRows As Integer = 100

    ''' 项目技能库目录, 默认是工程目录下的 ai。相对路径按工程目录算; 设为空字符串则不用技能库。
    ''' ai/SKILL.md 写系统概况、术语、红线, 每次都给 AI; 其余带 description 的 .md 由 AI 按需读取。
    Public Property SkillsFolder As String = "ai"

    ''' 每次提问是否带上当前现场 (谁在问、面板所在窗口、当前表的当前行或选中行), 默认带。
    Public Property IncludeScene As Boolean = True

    ''' 给 AI 的上下文 (提示词 + 历史) 大约最多多少字, 默认 60000。超了先省略较早的查询结果, 再丢掉最早的对话。
    Public Property MaxContextChars As Integer = 60000

    ''' 指定谁在问 (用户名和角色), 不再取狐表登录用户。如 ai.SetUser("张三", "销售", "华东区")。
    ''' 用户名给 Nothing 则恢复为狐表登录用户。换了人会自动开始新对话。
    Public Sub SetUser(name As String, ParamArray roles() As String)
        _user = If(name Is Nothing, Nothing, New Who(name, roles))
    End Sub

    Private _user As Who

    '==============================================================
    ' 界面
    '==============================================================

    Private Function Host() As PanelHost
        If _disposed Then Throw New ObjectDisposedException("ProAI")
        If _host Is Nothing Then _host = New PanelHost(Me, _session, _outbox)
        Return _host
    End Function

    ''' 放进窗口里的容器控件, 如 ai.AttachTo(e.Form.Controls("Panel1"))。也可以给 WinForms 控件。
    ''' 原来在别的窗口或弹窗里的会挪过来, 对话不丢。
    Public Sub AttachTo(container As Object)
        Host().AttachTo(container)
    End Sub

    ''' 在独立的小窗口里显示; 已经开着就激活。
    Public Sub ShowPopup()
        Host().ShowPopup()
    End Sub

    ''' 停靠在狐表主窗口右侧, 宽度可拖, 用户点顶栏的 × 关闭 (对话保留)。已经开着时 width 大于 0 就调宽度。
    ''' 如在菜单按钮里: ai.ShowSidebar()
    Public Sub ShowSidebar(Optional width As Integer = 0)
        Host().ShowSidebar(width)
    End Sub

    ''' 是否正停靠在主窗口右侧。
    Public ReadOnly Property SidebarVisible As Boolean
        Get
            Return _host IsNot Nothing AndAlso _host.InSidebar
        End Get
    End Property

    ''' 从窗口里收起 (对话保留, 下次 AttachTo、ShowPopup 或 ShowSidebar 接着用)。
    Public Sub Detach()
        If _host Is Nothing Then Return
        _host.ClosePopup()
        _host.Park()
    End Sub

    '==============================================================
    ' 对话
    '==============================================================

    ''' 代用户提一个问题, 和用户在面板里发送一样。正在回答时返回 False。
    Public Function Ask(question As String) As Boolean
        Return _session.Send(question)
    End Function

    ''' 停止当前回答。
    Public Sub Cancel()
        _session.Cancel()
    End Sub

    ''' 清空对话, 重新开始。
    Public Sub NewChat()
        _session.NewChat()
    End Sub

    ''' 是否正在回答。
    Public ReadOnly Property Busy As Boolean
        Get
            Return _session.Busy
        End Get
    End Property

    '==============================================================
    ' 内部
    '==============================================================

    Friend Function ConfigJson() As JObject
        Return New JObject From {
            {"title", _title},
            {"welcome", _welcome},
            {"theme", _theme},
            {"placeholder", _placeholder},
            {"quick", New JArray(QuickQuestions.Cast(Of Object)().ToArray())},
            {"images", _allowImages},
            {"files", _allowFiles},
            {"charts", _allowCharts},
            {"voice", _speech IsNot Nothing},
            {"closable", _host IsNot Nothing AndAlso _host.InSidebar}}
    End Function

    Friend Sub OnQuickQuestionsChanged()
        _session.PushConfig()
    End Sub

    ''' 谁在问。只在界面线程上调用。
    Friend Function CurrentWho() As Who
        Return If(_user, Who.FromFoxtable())
    End Function

    ''' 技能库目录的完整路径; 不用技能库时为 Nothing。
    Friend Function SkillsPath() As String
        If String.IsNullOrWhiteSpace(SkillsFolder) Then Return Nothing
        If IO.Path.IsPathRooted(SkillsFolder) Then Return SkillsFolder
        Dim root As String
        Try
            root = CStr(Fx.Global("ProjectPath"))
        Catch
            Return Nothing
        End Try
        Return IO.Path.Combine(root, SkillsFolder)
    End Function

    ''' 面板所在窗口的名字; 在弹窗里或收起时为空。
    Friend Function HostWindowName() As String
        Return If(_host Is Nothing, Nothing, _host.HostWindowName)
    End Function

    ''' 准备一轮: 谁在问、这个人能用的表、技能、窗口、工具和提示词。只在界面线程上调用。
    Friend Function PrepareTurn(files As AttachmentStore) As TurnSetup
        Dim w = CurrentWho()
        Dim tables = SchemaReader.Read(_tables, w)
        Dim skills = SkillLibrary.Scan(SkillsPath(), w)
        Dim sceneText = If(IncludeScene, Scene.Describe(w, HostWindowName(), tables), Nothing)
        Dim formTools As New FormTools(_forms.VisibleTo(w), _fillTracker)
        Dim custom As New CustomTools(_functions.VisibleTo(w).Concat(_tools.VisibleTo(w)).ToList(), w, MaxRows)
        Dim box As New Toolbox()
        box.Add(New ReadTools(tables, MaxRows, skills, _allowCharts))
        box.Add(New WriteTools(tables, files))
        box.Add(formTools)
        box.Add(New FileTools(files))
        box.Add(custom)
        Return New TurnSetup With {
            .Who = w, .Tools = box,
            .Prompt = BuildPrompt(tables, skills, sceneText, box.HasWrite, formTools.PromptSection(), files.All().Count > 0, custom)}
    End Function

    ''' 系统提示词。tables、skills: 本轮这个用户能看的表和技能; scene: 当前现场, 不带时为 Nothing;
    ''' canWrite: 有可写的表; forms: 可填写的窗口一节; hasFiles: 用户上传过文件; custom: 第 4 档的业务工具。
    Friend Function BuildPrompt(tables As List(Of TableInfo), skills As SkillCatalog, scene As String,
                                Optional canWrite As Boolean = False, Optional forms As String = "", Optional hasFiles As Boolean = False,
                                Optional custom As CustomTools = Nothing) As String
        Dim sb As New StringBuilder()
        Dim today = Date.Today
        Dim canFill = Not String.IsNullOrEmpty(forms)
        Dim hasCustom = custom IsNot Nothing AndAlso custom.HasAny
        Dim customWrite = hasCustom AndAlso custom.HasWrite
        Dim anyWrite = canWrite OrElse customWrite
        sb.Append("你是「").Append(If(_title = "", "AI 助手", _title)).Append("」, 帮使用这个业务系统的人查询和统计数据")
        sb.AppendLine(If(anyWrite OrElse canFill, "、录入和修改数据" & If(customWrite, "、办理业务", "") & "。", "。"))
        sb.AppendLine()
        sb.AppendLine("## 规则")
        If anyWrite OrElse canFill Then
            sb.AppendLine("- 数据通过工具读取。不要编造数据; 没查到就如实说。")
            If anyWrite Then
                If canWrite Then
                    sb.AppendLine("- 改数据只能用 add_rows、update_rows" & If(hasFiles, "、import_rows", "") & If(customWrite, " 和标了会改数据的业务工具", "") &
                                  ", 只能改标了可写的表。不能删除数据, 用户要删除时请他在系统里自己操作。")
                Else
                    sb.AppendLine("- 改数据只能用标了会改数据的业务工具, 不能直接改表。用户要删除数据时请他在系统里自己操作。")
                End If
                sb.AppendLine("- 会改数据的工具执行前, 系统会把要做的事给用户看并等用户确认, 你不用先口头征求同意, 内容清楚就直接调用。用户没确认就不要重复提交, 问清哪里要改。")
                sb.AppendLine("- 用户给出的值按用户说的写, 编号格式和已有数据不同、和别处对不上这类小事不要先反问, 有必要就在回答里提一句; 只有缺必填项或要写哪张表、哪些行说不清时才先问。")
                sb.AppendLine("- 写入后如实转述结果; 有行被系统的校验拦下或工具报错时, 把原因告诉用户, 不要换个办法绕过。")
            Else
                sb.AppendLine("- 你不能直接修改数据。")
            End If
            If canFill Then
                sb.AppendLine("- fill_form 只把内容填进录入窗口, 不保存。填完告诉用户填了什么、哪些没填, 请用户核对后自己点保存。")
                If canWrite Then sb.AppendLine("- 用户要把图片、文件里的单据录进系统时, 有对应的录入窗口就用 fill_form, 让用户核对后保存; 批量导入才用写入工具。")
            End If
        Else
            sb.AppendLine("- 数据只能通过工具读取, 你不能修改数据。不要编造数据; 没查到就如实说。")
        End If
        If hasCustom Then
            sb.AppendLine("- 名字以 fn_、my_ 开头的是系统的业务工具, 说明开头是工具名。业务动作 (如出库、审核、结算) 有对应的业务工具时只用它" &
                          If(canWrite, ", 不要用 add_rows、update_rows 改表代替", "") & "; 参数按工具说明给, 拿不准的先问用户。")
        End If
        If AllowImages Then sb.AppendLine("- 用户发来图片 (单据、名片、截图) 时, 先认出内容。看不清、拿不准的字段不要猜, 留空并在回答里请用户补上。")
        sb.AppendLine("- 业务口径 (如销售额怎么算、哪些单不计入) 以下面的系统概况、系统说明和技能库为准, 与常识不同时照它们的来, 并在回答里点明口径。")
        sb.AppendLine("- 下面没列出的表和列, 当前用户无权查看。被问到时直接说明没有权限, 不要猜测或用别的数据推算。")
        sb.AppendLine("- 求总数、合计、平均、排名、占比时用 aggregate, 不要把明细查出来自己算。看具体记录才用 query_rows。")
        sb.AppendLine("- 数字以工具结果为准。分组结果的合计直接用 totals, 不要自己把各组相加; 需要别的口径就再调一次工具。")
        sb.AppendLine("- 表名、列名以下面列出的为准; 拿不准时先调用 describe_table。用户说的名称可能是列标题或口语, 自己对应到列名。")
        sb.AppendLine("- 用户的问题有歧义 (如没说时间范围、有多个同名客户) 时, 按最常见的理解查询并在回答里说明口径; 实在无法判断再反问。")
        sb.AppendLine("- 只回答这些表能回答的问题; 超出范围时说明你能查哪些数据。")
        sb.AppendLine("- 用中文回答, 先给结论再给明细。多行结果用 Markdown 表格, 行多时只列前若干行并说明总数。金额保留两位小数。")
        sb.AppendLine("- 工具结果里有 note 或 partial 时, 在回答中如实提示 (如只显示了部分、只统计了已加载的数据)。")
        If tables.Count > 0 Then
            If _allowCharts Then
                sb.AppendLine("- 统计结果适合看图 (趋势、比较、占比) 时, 在 aggregate 里附上 chart, 系统统计完直接出图; 不要自己画字符图或给图表代码。" &
                              "出了图 (结果里有 chart) 就不必再列完整表格, 说结论和要点; 结果里有 chart_error 时按原因改正, 或者不出图直接回答。" &
                              "用户要换图表类型时, 可以提示他在图表上点切换, 也可以用新的 chart 再统计一次。")
            Else
                sb.AppendLine("- 当前不支持画图表。用户要图表时, 说明目前不支持, 用表格和文字回答。")
            End If
        End If
        sb.Append("- 今天是 ").Append(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(", ").
           Append(CultureInfo.GetCultureInfo("zh-CN").DateTimeFormat.GetDayName(today.DayOfWeek)).AppendLine("。")
        If skills.Overview <> "" Then
            sb.AppendLine()
            sb.AppendLine("## 系统概况")
            If skills.Overview.Length > MAX_OVERVIEW_CHARS Then
                sb.AppendLine(skills.Overview.Substring(0, MAX_OVERVIEW_CHARS))
                sb.Append("(以下省略, 需要时用 read_skill 读 ").Append(SkillLibrary.OVERVIEW_FILE).AppendLine(")")
            Else
                sb.AppendLine(skills.Overview)
            End If
        End If
        If Not String.IsNullOrWhiteSpace(Instructions) Then
            sb.AppendLine()
            sb.AppendLine("## 系统说明")
            sb.AppendLine(Instructions.Trim())
        End If
        If skills.Entries.Count > 0 Then
            sb.AppendLine()
            sb.AppendLine("## 技能库")
            sb.AppendLine("各业务模块的口径和流程。问题涉及下列主题时, 先用 read_skill 读对应文件, 再查数回答。")
            sb.Append(SkillLibrary.PromptSection(skills))
        End If
        sb.AppendLine()
        sb.AppendLine("## 可查询的表")
        If tables.Count = 0 Then
            sb.AppendLine("(当前用户没有可查询的表。告诉用户目前没有可查询的数据。)")
        Else
            sb.Append(SchemaReader.PromptSection(tables))
        End If
        If canFill Then
            sb.AppendLine("## 可填写的窗口")
            sb.Append(forms)
            sb.AppendLine()
        End If
        If Not String.IsNullOrEmpty(scene) Then
            sb.AppendLine("## 当前现场")
            sb.AppendLine("用户说 ""这张单""、""选中的这些"" 时指下面的行; 和问题无关时忽略。")
            sb.Append(scene)
        End If
        Return sb.ToString()
    End Function

    Private Const MAX_OVERVIEW_CHARS As Integer = 8000

    ''' 自测用: 面板状态。
    <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
    Public Function PanelState() As String
        Return If(_host Is Nothing, New JObject From {{"web", False}}, _host.State()).ToString(Newtonsoft.Json.Formatting.None)
    End Function

    ''' 自测用: 在面板网页里执行脚本。
    <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
    Public Function PanelScriptAsync(js As String) As Threading.Tasks.Task(Of String)
        Return Host().ExecuteScriptAsync(js)
    End Function

    ''' 自测用: 当前对话快照 (不影响面板)。
    <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
    Public Function StateJson() As String
        Return _session.PeekSnapshot().ToString(Newtonsoft.Json.Formatting.None)
    End Function

    ''' 自测用: 给模型的历史。
    <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
    Public Function HistoryJson() As String
        Return _session.HistoryCopy().ToString(Newtonsoft.Json.Formatting.None)
    End Function

    ''' 自测用: 下一次提问时的系统提示词。
    <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
    Public Function PromptPreview() As String
        Return PrepareTurn(_session.Files).Prompt
    End Function

    ''' 自测用: 下一次提问时的工具定义。
    <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
    Public Function ToolsPreview() As String
        Return New JArray(PrepareTurn(_session.Files).Tools.Definitions().Cast(Of Object)().ToArray()).ToString(Newtonsoft.Json.Formatting.None)
    End Function

    ''' 自测用: 不经模型直接调一个工具 (工具名, 或第 4 档工具的显示名), 确认卡片按 approve 自动回答。
    ''' 返回 {"result","summary"} 或 {"error"}, 弹过确认卡片时带 "preview", 出了图时带 "chart"。
    <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
    Public Function ToolTest(name As String, argsJson As String, approve As Boolean) As String
        Dim setup = PrepareTurn(_session.Files)
        Dim preview As JObject = Nothing
        Dim ctx As New ToolContext(Function(p)
                                       preview = p
                                       Return approve
                                   End Function, Function() False, Sub(e)
                                                                   End Sub, _session.Files)
        Dim res As New JObject()
        Try
            Dim actual = {name, ToolSpec.ToolNameOf("fn_", name), ToolSpec.ToolNameOf("my_", name)}.FirstOrDefault(Function(n) setup.Tools.Owns(n))
            If actual Is Nothing Then Throw New ToolArgumentException("本轮没有工具 " & name)
            Dim o = setup.Tools.Execute(actual, If(String.IsNullOrWhiteSpace(argsJson), New JObject(), JObject.Parse(argsJson)), ctx)
            res("result") = o.Result
            res("summary") = o.Summary
            If o.Chart IsNot Nothing Then res("chart") = o.Chart
        Catch ex As Exception
            res("error") = ex.GetBaseException().Message
        End Try
        If preview IsNot Nothing Then res("preview") = preview
        Return res.ToString(Newtonsoft.Json.Formatting.None)
    End Function

    ''' 自测用: 等用户确认的工具 id。
    <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
    Public Function PendingConfirms() As String()
        Return _session.PendingIds()
    End Function

    ''' 自测用: 代用户在确认卡片上点确认或取消。
    <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
    Public Function Confirm(toolId As String, approved As Boolean) As Boolean
        Return _session.Decide(toolId, approved)
    End Function

    ''' 自测用: 代用户点 "在狐表中打开"。成功返回空串, 否则返回原因。
    <ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)>
    Public Function OpenResult(toolId As String) As String
        Return If(_session.OpenInFoxtable(toolId), "")
    End Function

    ''' 代用户附上一个文件 (路径), 下次提问时一起发出。图片要 AllowImages, 表格要 AllowFiles。失败返回 False 并在面板上提示。
    Public Function AttachFile(path As String) As Boolean
        Return _session.AttachBytes(IO.Path.GetFileName(path), IO.File.ReadAllBytes(path))
    End Function

    ''' 一轮问答结束: 写日志文件并触发 Logged。在界面线程上调用。
    Friend Sub OnTurnLogged(entry As JObject)
        Dim folder = LogPath()
        If folder IsNot Nothing Then
            Try
                IO.Directory.CreateDirectory(folder)
                IO.File.AppendAllText(IO.Path.Combine(folder, Date.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) & ".jsonl"),
                                      entry.ToString(Newtonsoft.Json.Formatting.None) & vbLf, New UTF8Encoding(False))
            Catch
                ' 日志写不了 (没权限、磁盘满) 不影响对话
            End Try
        End If
        Try
            RaiseEvent Logged(Me, New AILogEventArgs(entry))
        Catch
        End Try
    End Sub

    Private Function LogPath() As String
        If String.IsNullOrWhiteSpace(LogFolder) Then Return Nothing
        If IO.Path.IsPathRooted(LogFolder) Then Return LogFolder
        Try
            Return IO.Path.Combine(CStr(Fx.Global("ProjectPath")), LogFolder)
        Catch
            Return Nothing
        End Try
    End Function

    ''' 释放面板。对话随之结束。
    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        _session.Cancel()
        _fillTracker.Dispose()
        _host?.Dispose()
        _host = Nothing
    End Sub

    ''' 改动时通知网页的快捷问题列表。
    Private NotInheritable Class QuickQuestionList
        Inherits ObjectModel.Collection(Of String)

        Private ReadOnly _owner As ProAI

        Public Sub New(owner As ProAI)
            _owner = owner
        End Sub

        Protected Overrides Sub InsertItem(index As Integer, item As String)
            MyBase.InsertItem(index, item)
            _owner.OnQuickQuestionsChanged()
        End Sub

        Protected Overrides Sub SetItem(index As Integer, item As String)
            MyBase.SetItem(index, item)
            _owner.OnQuickQuestionsChanged()
        End Sub

        Protected Overrides Sub RemoveItem(index As Integer)
            MyBase.RemoveItem(index)
            _owner.OnQuickQuestionsChanged()
        End Sub

        Protected Overrides Sub ClearItems()
            MyBase.ClearItems()
            _owner.OnQuickQuestionsChanged()
        End Sub
    End Class
End Class

''' 一轮问答的日志。
Public NotInheritable Class AILogEventArgs
    Inherits EventArgs

    Private ReadOnly _entry As JObject

    Friend Sub New(entry As JObject)
        _entry = entry
    End Sub

    ''' 用户名。
    Public ReadOnly Property User As String
        Get
            Return CStr(_entry("user"))
        End Get
    End Property

    ''' 问题。
    Public ReadOnly Property Question As String
        Get
            Return CStr(_entry("question"))
        End Get
    End Property

    ''' 回答 (长的截断)。
    Public ReadOnly Property Answer As String
        Get
            Return CStr(_entry("answer"))
        End Get
    End Property

    ''' done、error 或 cancelled。
    Public ReadOnly Property Status As String
        Get
            Return CStr(_entry("status"))
        End Get
    End Property

    ''' 调用了哪些工具, 如 "query_rows, add_rows"。
    Public ReadOnly Property Tools As String
        Get
            Return String.Join(", ", DirectCast(_entry("tools"), JArray).Select(Function(t) CStr(t("name"))))
        End Get
    End Property

    ''' 有没有改数据 (含填窗口)。
    Public ReadOnly Property Wrote As Boolean
        Get
            Return DirectCast(_entry("writes"), JArray).Count > 0
        End Get
    End Property

    ''' 完整日志 (JSON): 时间、用户、角色、窗口、问题、附件、每次工具调用的参数和结果、写入了哪些行、回答、用量。
    Public ReadOnly Property Json As String
        Get
            Return _entry.ToString(Newtonsoft.Json.Formatting.None)
        End Get
    End Property
End Class
