Namespace AI

    ''' 上下文过长时裁剪给模型的历史 (OpenAI 消息格式, 含工具调用和结果)。
    ''' 先把较早几轮里的长工具结果换成一句占位, 还超就从最早的整轮丢起; 最近一轮总是保留。
    ''' 按字符数估算, 不精确计 token。
    Friend Module HistoryTrimmer

        ''' 最近几轮的工具结果原样保留, 追问 "刚才那几张单" 时还用得上。
        Private Const KEEP_FULL_TURNS As Integer = 2
        Private Const LONG_TOOL_RESULT As Integer = 300
        Public Const ELIDED As String = "(较早的工具结果已省略, 需要时重新查询)"
        Public Const IMAGE_ELIDED As String = "(较早的图片已省略)"
        ''' 一张图片按多少字估算 (base64 本身不算)。
        Private Const IMAGE_CHARS As Integer = 1500

        ''' 返回裁剪后的副本; 原历史不动。trimmed: 有没有省略或丢掉内容。
        Public Function Trim(history As JArray, budget As Integer, ByRef trimmed As Boolean) As JArray
            trimmed = False
            Dim turns = SplitTurns(history)
            ' 0. 较早几轮的图片一律换成占位: 每轮都重发很费, 认出的内容已经在后面的回答和工具调用里
            For i = 0 To turns.Count - 1 - KEEP_FULL_TURNS
                For Each m In turns(i)
                    ElideImages(m)
                Next
            Next
            If Size(turns) <= budget Then Return Join(turns)

            ' 1. 较早几轮的长工具结果换成占位, 思考过程去掉
            For i = 0 To turns.Count - 1 - KEEP_FULL_TURNS
                For Each m In turns(i)
                    If CStr(m("role")) = "tool" Then
                        Dim content = TryCast(m("content"), JValue)
                        If content IsNot Nothing AndAlso CStr(content).Length > LONG_TOOL_RESULT Then
                            m("content") = ELIDED
                            trimmed = True
                        End If
                    End If
                    If m.Remove("reasoning_content") Then trimmed = True
                Next
            Next
            ' 2. 还超就丢最早的整轮
            While turns.Count > 1 AndAlso Size(turns) > budget
                turns.RemoveAt(0)
                trimmed = True
            End While
            Return Join(turns)
        End Function

        ' 每轮从一条用户消息开始, 到下一条用户消息之前 (中间是助手的工具调用、工具结果和回答)。
        ' 系统提示词每轮单独给, 不留在历史里
        Private Function SplitTurns(history As JArray) As List(Of List(Of JObject))
            Dim turns As New List(Of List(Of JObject))
            If history Is Nothing Then Return turns
            For Each tok In history
                Dim m = TryCast(tok, JObject)
                If m Is Nothing Then Continue For
                Dim role = CStr(m("role"))
                If role = "system" Then Continue For
                If role = "user" OrElse turns.Count = 0 Then turns.Add(New List(Of JObject))
                turns(turns.Count - 1).Add(DirectCast(m.DeepClone(), JObject))
            Next
            ' 开头不是用户消息的残段 (不该出现) 丢掉, 免得工具结果前面没有对应的调用
            If turns.Count > 0 AndAlso CStr(turns(0)(0)("role")) <> "user" Then turns.RemoveAt(0)
            Return turns
        End Function

        Private Sub ElideImages(m As JObject)
            Dim parts = TryCast(m("content"), JArray)
            If parts Is Nothing Then Return
            For i = 0 To parts.Count - 1
                Dim p = TryCast(parts(i), JObject)
                If p IsNot Nothing AndAlso CStr(p("type")) = "image_url" Then
                    parts(i) = New JObject From {{"type", "text"}, {"text", IMAGE_ELIDED}}
                End If
            Next
        End Sub

        Private Function Size(turns As List(Of List(Of JObject))) As Integer
            Return turns.Sum(Function(t) t.Sum(Function(m) MessageSize(m)))
        End Function

        Private Function MessageSize(m As JObject) As Integer
            Dim parts = TryCast(m("content"), JArray)
            If parts Is Nothing OrElse Not parts.Any(Function(p) CStr(p("type")) = "image_url") Then Return m.ToString(Newtonsoft.Json.Formatting.None).Length
            Dim n = 0
            For Each p In parts
                If CStr(p("type")) = "image_url" Then n += IMAGE_CHARS Else n += p.ToString(Newtonsoft.Json.Formatting.None).Length
            Next
            Return n + 40
        End Function

        Private Function Join(turns As List(Of List(Of JObject))) As JArray
            Dim res As New JArray()
            For Each t In turns
                For Each m In t
                    res.Add(m)
                Next
            Next
            Return res
        End Function
    End Module
End Namespace
