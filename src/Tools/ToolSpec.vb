Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

Namespace AI

    ''' 第 4 档的工具定义: 来自函数开头的注释, 或代码里 ai.Tools.Add 加的工具。
    ''' 只做文本解析和 JSON 进出, 不碰狐表。设计期插件 (FoxTablePro) 链接同一个文件, 保存项目时生成 ai\tools.json。
    '''
    '''     ' @AI工具 出库 写入 预览
    '''     ' 按产品出库, 扣减库存并生成出库单。
    '''     ' @参数 产品 文本 必填 产品名称
    '''     ' @参数 数量 整数 必填 最小1
    '''     ' @参数 仓库 文本 默认总仓 入库|出库 这类写法是可选值
    Friend NotInheritable Class ToolSpec

        Public Const MARK As String = "@AI工具"
        Public Const FILE_VERSION As Integer = 1

        ''' 内部函数名 (Functions.Execute 用); 代码里加的工具为工具名。
        Public FunctionName As String = ""
        ''' 显示名, 如 "出库"; 没写就是函数名。
        Public Title As String = ""
        Public Description As String = ""
        ''' 会改数据: 执行前要用户确认。
        Public Writes As Boolean
        ''' 先以 Preview = True 调一次, 把返回的内容放在确认卡片上。
        Public Preview As Boolean
        Public ReadOnly Params As New List(Of ParamSpec)

        '==============================================================
        ' 解析
        '==============================================================

        ''' 解析函数开头的注释。没有 @AI工具 返回 Nothing 且 errorText 为 Nothing; 写错了返回 Nothing 并给出原因。
        Public Shared Function Parse(functionName As String, code As String, ByRef errorText As String) As ToolSpec
            errorText = Nothing
            Dim header = HeaderLines(code)
            If Not header.Any(Function(l) IsMark(l)) Then Return Nothing
            Dim spec As New ToolSpec With {.FunctionName = If(functionName, "").Trim(), .Title = If(functionName, "").Trim()}
            Dim desc As New List(Of String)
            Try
                For Each ln In header
                    If IsMark(ln) Then
                        spec.ParseFlags(ln.Substring(MARK.Length))
                    ElseIf StartsWithWord(ln, "@参数") OrElse StartsWithWord(ln, "@param") Then
                        Dim p = ParamSpec.Parse(AfterWord(ln))
                        If spec.Params.Any(Function(x) String.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase)) Then
                            Throw New FormatException("参数 " & p.Name & " 写了两次。")
                        End If
                        spec.Params.Add(p)
                    ElseIf StartsWithWord(ln, "@返回") OrElse StartsWithWord(ln, "@return") Then
                        Dim rest = AfterWord(ln)
                        If rest <> "" Then desc.Add("返回: " & rest)
                    ElseIf ln.StartsWith("@", StringComparison.Ordinal) Then
                        Throw New FormatException("看不懂 " & ln.Split(" "c)(0) & ", 只认 @AI工具、@参数、@返回。")
                    Else
                        desc.Add(ln)
                    End If
                Next
            Catch ex As FormatException
                errorText = ex.Message
                Return Nothing
            End Try
            spec.Description = String.Join(" ", desc).Trim()
            If spec.Description = "" Then
                errorText = "没写说明: 在 @AI工具 下面写一两句做什么、什么时候用。"
                Return Nothing
            End If
            Return spec
        End Function

        ''' 函数开头连续的注释行 (去掉 ' 和首尾空白)。空行不算结束, 碰到第一行代码结束。
        Private Shared Function HeaderLines(code As String) As List(Of String)
            Dim res As New List(Of String)
            For Each raw In If(code, "").Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(ChrW(10))
                Dim line = raw.Trim()
                If line = "" Then Continue For
                If Not line.StartsWith("'", StringComparison.Ordinal) Then Exit For
                Dim text = line.TrimStart("'"c).Trim()
                If text <> "" Then res.Add(text)
            Next
            Return res
        End Function

        Private Shared Function IsMark(text As String) As Boolean
            Return text.StartsWith(MARK, StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function StartsWithWord(text As String, word As String) As Boolean
            Return text.StartsWith(word, StringComparison.OrdinalIgnoreCase) AndAlso
                   (text.Length = word.Length OrElse Char.IsWhiteSpace(text(word.Length)))
        End Function

        Private Shared Function AfterWord(text As String) As String
            Return Regex.Replace(text, "^\S+\s*", "").Trim()
        End Function

        ''' @AI工具 后面: [显示名] [只读|写入] [预览]
        Private Sub ParseFlags(rest As String)
            For Each w In Words(rest)
                Select Case w
                    Case "写入", "写", "改数据" : Writes = True
                    Case "只读", "读" : Writes = False
                    Case "预览" : Preview = True
                    Case Else
                        If Title <> FunctionName AndAlso Title <> "" Then Throw New FormatException("@AI工具 后面只能写一个显示名和 只读/写入/预览, 多了 """ & w & """。")
                        Title = w
                End Select
            Next
            If Preview Then Writes = True
        End Sub

        Friend Shared Function Words(text As String) As String()
            Return If(text, "").Split(New Char() {" "c, ControlChars.Tab, "　"c}, StringSplitOptions.RemoveEmptyEntries)
        End Function

        '==============================================================
        ' 给模型的定义
        '==============================================================

        ''' 工具名: 模型只认字母、数字、下划线。函数名本身合规就用它, 否则用名字的哈希。
        Public Shared Function ToolNameOf(prefix As String, name As String) As String
            If Regex.IsMatch(If(name, ""), "^[A-Za-z0-9_]{1,50}$") Then Return prefix & name
            Dim h As UInteger = 2166136261UI
            For Each b In Encoding.UTF8.GetBytes(If(name, ""))
                h = CUInt((CULng(h Xor b) * 16777619UL) And &HFFFFFFFFUL)
            Next
            Return prefix & h.ToString("x8", CultureInfo.InvariantCulture)
        End Function

        ''' 参数的 JSON Schema。
        Public Function ParametersSchema() As JObject
            Dim props As New JObject()
            For Each p In Params
                props(p.Name) = p.Schema()
            Next
            Dim o As New JObject From {{"type", "object"}, {"properties", props}}
            Dim req = Params.Where(Function(p) p.Required).Select(Function(p) p.Name).ToArray()
            If req.Length > 0 Then o("required") = New JArray(req.Cast(Of Object)().ToArray())
            Return o
        End Function

        '==============================================================
        ' ai\tools.json
        '==============================================================

        Public Function ToJson() As JObject
            Dim o As New JObject From {{"function", FunctionName}}
            If Title <> FunctionName Then o("title") = Title
            o("description") = Description
            If Writes Then o("writes") = True
            If Preview Then o("preview") = True
            If Params.Count > 0 Then o("params") = New JArray(Params.Select(Function(p) p.ToJson()).Cast(Of Object)().ToArray())
            Return o
        End Function

        Public Shared Function FromJson(o As JObject) As ToolSpec
            Dim fn = CStr(o("function"))
            If String.IsNullOrWhiteSpace(fn) Then Throw New FormatException("缺少 function。")
            Dim spec As New ToolSpec With {
                .FunctionName = fn.Trim(),
                .Title = If(CStr(o("title")), fn).Trim(),
                .Description = If(CStr(o("description")), ""),
                .Writes = o.Value(Of Boolean?)("writes").GetValueOrDefault() OrElse o.Value(Of Boolean?)("preview").GetValueOrDefault(),
                .Preview = o.Value(Of Boolean?)("preview").GetValueOrDefault()}
            Dim ps = TryCast(o("params"), JArray)
            If ps IsNot Nothing Then
                For Each p In ps.OfType(Of JObject)()
                    spec.Params.Add(ParamSpec.FromJson(p))
                Next
            End If
            Return spec
        End Function
    End Class

    Friend Enum ParamKind
        Text
        [Integer]
        Number
        [Date]
        Bool
        Json
    End Enum

    ''' 一个参数: @参数 名 类型 [必填] [最小N] [最大N] [最长N] [默认X] [a|b|c] [说明]。
    ''' 类型可省 (文本); 修饰词写在说明前面, 第一个不是修饰词的词起都算说明。
    Friend NotInheritable Class ParamSpec
        Public Name As String = ""
        Public Kind As ParamKind = ParamKind.Text
        Public Required As Boolean
        Public Min As Double?
        Public Max As Double?
        Public MaxLength As Integer?
        Public DefaultText As String
        Public ReadOnly Options As New List(Of String)
        Public Description As String = ""
        ''' 解析时写了类型词 / 必填、选填 (不进 JSON; 给代码里补充参数说明时合并用)。
        Public KindGiven As Boolean
        Public RequiredGiven As Boolean

        Private Shared ReadOnly ModifierRe As New Regex("^(最小|最大|最长|默认)[:=：]?(.*)$", RegexOptions.CultureInvariant)

        ''' 类型词 → 类型; 不是类型词返回 Nothing。
        Public Shared Function KindOf(word As String) As ParamKind?
            Select Case If(word, "").ToLowerInvariant()
                Case "文本", "字符", "字符串", "string", "text", "str" : Return ParamKind.Text
                Case "整数", "int", "integer", "long" : Return ParamKind.Integer
                Case "数字", "数值", "小数", "金额", "number", "double", "decimal" : Return ParamKind.Number
                Case "日期", "日期时间", "date", "datetime" : Return ParamKind.Date
                Case "是否", "逻辑", "布尔", "bool", "boolean" : Return ParamKind.Bool
                Case "json", "对象", "列表", "明细" : Return ParamKind.Json
            End Select
            Return Nothing
        End Function

        Public Shared Function KindName(k As ParamKind) As String
            Select Case k
                Case ParamKind.Integer : Return "整数"
                Case ParamKind.Number : Return "数字"
                Case ParamKind.Date : Return "日期"
                Case ParamKind.Bool : Return "是否"
                Case ParamKind.Json : Return "JSON"
                Case Else : Return "文本"
            End Select
        End Function

        Public Shared Function Parse(text As String) As ParamSpec
            Dim w = ToolSpec.Words(text)
            If w.Length = 0 Then Throw New FormatException("@参数 后面要写参数名, 如 @参数 数量 整数 必填。")
            Dim p As New ParamSpec With {.Name = w(0)}
            If p.Name.StartsWith("@", StringComparison.Ordinal) OrElse p.Name.Contains("|") Then Throw New FormatException("参数名 """ & p.Name & """ 不对。")
            Dim i = 1
            If i < w.Length Then
                Dim k = KindOf(w(i))
                If k.HasValue Then
                    p.Kind = k.Value
                    p.KindGiven = True
                    i += 1
                End If
            End If
            While i < w.Length
                Dim word = w(i)
                If word = "必填" Then
                    p.Required = True
                    p.RequiredGiven = True
                ElseIf word = "选填" OrElse word = "可选" Then
                    p.Required = False
                    p.RequiredGiven = True
                ElseIf word.Contains("|") Then
                    p.Options.AddRange(word.Split("|"c).Select(Function(x) x.Trim()).Where(Function(x) x <> ""))
                Else
                    Dim m = ModifierRe.Match(word)
                    If Not m.Success Then Exit While
                    Dim v = m.Groups(2).Value
                    If v = "" Then
                        If i + 1 >= w.Length Then Throw New FormatException("参数 " & p.Name & " 的 " & m.Groups(1).Value & " 后面没有值。")
                        i += 1
                        v = w(i)
                    End If
                    Select Case m.Groups(1).Value
                        Case "最小" : p.Min = Num(p, "最小", v)
                        Case "最大" : p.Max = Num(p, "最大", v)
                        Case "最长" : p.MaxLength = CInt(Num(p, "最长", v))
                        Case "默认" : p.DefaultText = v
                    End Select
                End If
                i += 1
            End While
            p.Description = String.Join(" ", w.Skip(i)).Trim()
            If p.Required AndAlso p.DefaultText IsNot Nothing Then p.Required = False
            Return p
        End Function

        Private Shared Function Num(p As ParamSpec, what As String, v As String) As Double
            Dim d As Double
            If Not Double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, d) Then
                Throw New FormatException("参数 " & p.Name & " 的 " & what & " 要写数字, 写的是 """ & v & """。")
            End If
            Return d
        End Function

        Public Function Schema() As JObject
            Dim o As New JObject()
            Select Case Kind
                Case ParamKind.Integer : o("type") = "integer"
                Case ParamKind.Number : o("type") = "number"
                Case ParamKind.Bool : o("type") = "boolean"
                Case ParamKind.Json
                Case Else : o("type") = "string"
            End Select
            If Options.Count > 0 AndAlso (Kind = ParamKind.Text OrElse Kind = ParamKind.Integer OrElse Kind = ParamKind.Number) Then
                o("enum") = New JArray(Options.Select(Function(x) EnumValue(x)).ToArray())
            End If
            If Min.HasValue AndAlso (Kind = ParamKind.Integer OrElse Kind = ParamKind.Number) Then o("minimum") = NumToken(Min.Value)
            If Max.HasValue AndAlso (Kind = ParamKind.Integer OrElse Kind = ParamKind.Number) Then o("maximum") = NumToken(Max.Value)
            If MaxLength.HasValue AndAlso Kind = ParamKind.Text Then o("maxLength") = MaxLength.Value
            Dim d As New StringBuilder(Description)
            If Kind = ParamKind.Date Then Hint(d, "日期, 如 2026-03-05")
            If Kind = ParamKind.Json Then Hint(d, "JSON 对象或数组")
            If DefaultText IsNot Nothing Then Hint(d, "不填为 " & DefaultText)
            If d.Length > 0 Then o("description") = d.ToString()
            Return o
        End Function

        Private Function EnumValue(x As String) As Object
            Dim d As Double
            If Kind <> ParamKind.Text AndAlso Double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, d) Then
                If Kind = ParamKind.Integer Then Return CLng(d)
                Return d
            End If
            Return x
        End Function

        ''' 整数写成 1 而不是 1.0。
        Private Shared Function NumToken(d As Double) As JToken
            If d = Math.Floor(d) AndAlso Math.Abs(d) < 1.0E+15 Then Return New JValue(CLng(d))
            Return New JValue(d)
        End Function

        Private Shared Sub Hint(sb As StringBuilder, text As String)
            If sb.Length > 0 Then sb.Append(" ")
            sb.Append("(").Append(text).Append(")")
        End Sub

        Public Function ToJson() As JObject
            Dim o As New JObject From {{"name", Name}, {"type", KindName(Kind)}}
            If Required Then o("required") = True
            If Min.HasValue Then o("min") = NumToken(Min.Value)
            If Max.HasValue Then o("max") = NumToken(Max.Value)
            If MaxLength.HasValue Then o("maxLength") = MaxLength.Value
            If DefaultText IsNot Nothing Then o("default") = DefaultText
            If Options.Count > 0 Then o("options") = New JArray(Options.Cast(Of Object)().ToArray())
            If Description <> "" Then o("description") = Description
            Return o
        End Function

        Public Shared Function FromJson(o As JObject) As ParamSpec
            Dim name = CStr(o("name"))
            If String.IsNullOrWhiteSpace(name) Then Throw New FormatException("参数缺少 name。")
            Dim k = KindOf(If(CStr(o("type")), "文本"))
            Dim p As New ParamSpec With {
                .Name = name.Trim(),
                .Kind = If(k, ParamKind.Text),
                .Required = o.Value(Of Boolean?)("required").GetValueOrDefault(),
                .Min = o.Value(Of Double?)("min"),
                .Max = o.Value(Of Double?)("max"),
                .MaxLength = o.Value(Of Integer?)("maxLength"),
                .DefaultText = If(o("default") Is Nothing OrElse o("default").Type = JTokenType.Null, Nothing, CStr(o("default"))),
                .Description = If(CStr(o("description")), "")}
            Dim opts = TryCast(o("options"), JArray)
            If opts IsNot Nothing Then p.Options.AddRange(opts.Select(Function(x) CStr(x)))
            Return p
        End Function
    End Class
End Namespace
