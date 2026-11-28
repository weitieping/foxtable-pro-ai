Imports System.Collections
Imports System.Globalization
Imports System.Reflection

Namespace AI

    ''' 一个第 4 档工具: 内部函数 (Handler 为 Nothing) 或代码里加的只读工具。
    Friend NotInheritable Class CustomTool
        Public Spec As ToolSpec
        Public ToolName As String
        Public Handler As Func(Of ToolArgs, Object)
        ''' 开发者在 Allow(...).Describe 里补的说明。
        Public Note As String = ""
    End Class

    ''' 第 4 档: 内部函数和代码里加的工具。调用前按参数说明统一检查; 只读的直接执行;
    ''' 写入的先做预览 (标了预览的调一次函数, 否则列出参数), 用户确认后再执行。
    Friend NotInheritable Class CustomTools
        Implements IToolSet

        Private ReadOnly _tools As List(Of CustomTool)
        Private ReadOnly _who As Who
        Private ReadOnly _maxRows As Integer

        Public Sub New(tools As List(Of CustomTool), who As Who, maxRows As Integer)
            _tools = tools
            _who = who
            _maxRows = Math.Max(1, maxRows)
        End Sub

        Public ReadOnly Property HasAny As Boolean
            Get
                Return _tools.Count > 0
            End Get
        End Property

        Public ReadOnly Property HasWrite As Boolean
            Get
                Return _tools.Any(Function(t) t.Spec.Writes)
            End Get
        End Property

        Private Function Find(name As String) As CustomTool
            Return _tools.FirstOrDefault(Function(t) t.ToolName = name)
        End Function

        Public Function Owns(name As String) As Boolean Implements IToolSet.Owns
            Return Find(name) IsNot Nothing
        End Function

        Public Function Definitions() As List(Of JObject) Implements IToolSet.Definitions
            Dim list As New List(Of JObject)
            For Each t In _tools
                Dim d = t.Spec.Title & ": " & t.Spec.Description
                If t.Note <> "" Then d &= " " & t.Note
                ' 会改数据的工具怎么用 (不必先口头征求同意等) 写在系统提示词里
                If t.Spec.Writes Then d &= " (会改数据: 执行前系统把" & If(t.Spec.Preview, "预览", "参数") & "给用户看并等确认。)"
                list.Add(New JObject From {
                    {"type", "function"},
                    {"function", New JObject From {{"name", t.ToolName}, {"description", d}, {"parameters", t.Spec.ParametersSchema()}}}})
            Next
            Return list
        End Function

        ''' 进度: 显示名加第一个文本参数, 如 "出库 笔记本"。
        Public Function Label(name As String, args As JObject) As String Implements IToolSet.Label
            Dim t = Find(name)
            If t Is Nothing Then Return name
            Dim first = t.Spec.Params.FirstOrDefault(Function(p) p.Kind = ParamKind.Text)
            Dim v = If(first Is Nothing OrElse args Is Nothing, Nothing, TryCast(GetArg(args, first.Name), JValue))
            Dim s = If(v Is Nothing OrElse v.Value Is Nothing, "", v.ToString().Trim())
            Return t.Spec.Title & If(s <> "" AndAlso s.Length <= 20, " " & s, "")
        End Function

        '==============================================================
        ' 执行
        '==============================================================

        Public Function Execute(name As String, args As JObject, ctx As ToolContext) As ToolOutcome Implements IToolSet.Execute
            Dim t = Find(name)
            If t Is Nothing Then Throw New ToolArgumentException("没有名为 " & name & " 的工具。")
            Dim given As List(Of String) = Nothing
            Dim vals = ConvertArgs(t.Spec, args, given)

            If Not t.Spec.Writes Then
                Dim r = UiThread.Run(Function() Shape(Invoke(t, vals, given, False), t.Spec.Title))
                Dim res As New JObject From {{"result", r.Data}}
                If r.Note IsNot Nothing Then res("note") = r.Note
                Return New ToolOutcome(res, r.Summary, r.Table) With {.Source = SourceOf(r, t)}
            End If

            Dim preview As JObject
            If t.Spec.Preview Then
                Dim pv As Shaped
                Try
                    pv = UiThread.Run(Function() Shape(Invoke(t, vals, given, True), "预览"))
                Catch ex As Exception
                    Throw New ToolArgumentException("预览时出错, 没有执行: " & ex.GetBaseException().Message)
                End Try
                preview = Card(t, vals, pv)
            Else
                preview = Card(t, vals, Nothing)
            End If
            If ctx.Cancelled Then Return Cancelled()
            If Not ctx.Confirm(preview) Then
                If ctx.Cancelled Then Return Cancelled()
                Return New ToolOutcome(New JObject From {
                    {"confirmed", False},
                    {"message", "用户没有确认, 没有执行。不要重复提交同样的内容; 问用户哪里需要改。"}}, "用户未确认")
            End If

            Dim log As New JObject From {{"action", "function"}, {"tool", t.Spec.Title}, {"function", t.Spec.FunctionName}, {"args", ArgsJson(vals)}}
            Dim done As Shaped
            Try
                done = UiThread.Run(Function() Shape(Invoke(t, vals, given, False), t.Spec.Title))
            Catch ex As Exception
                Dim why = ex.GetBaseException().Message
                log("error") = why
                ctx.Wrote(log)
                Throw New ToolArgumentException("用户已确认, 但执行出错: " & why & "。把原因告诉用户, 不要换个办法绕过。")
            End Try
            log("result") = done.Summary
            ctx.Wrote(log)
            Dim ok As New JObject From {{"confirmed", True}, {"result", done.Data}, {"message", "用户已确认并执行了 " & t.Spec.Title & "。按 result 如实转述结果。"}}
            If done.Note IsNot Nothing Then ok("note") = done.Note
            Return New ToolOutcome(ok, done.Summary, done.Table) With {.Source = SourceOf(done, t)}
        End Function

        ''' 函数返回的表格留一份, 之后可以画图; 记下来自哪个工具, 画图前复核这个用户还能不能用它。
        Private Shared Function SourceOf(s As Shaped, t As CustomTool) As ChartSource
            If s.Source Is Nothing Then Return Nothing
            s.Source.Tool = t.ToolName
            s.Source.Title = t.Spec.Title
            Return s.Source
        End Function

        Private Shared Function Cancelled() As ToolOutcome
            Return New ToolOutcome(New JObject From {{"cancelled", True}, {"message", "用户已取消本次提问, 没有执行。"}}, "已取消")
        End Function

        Private Function Invoke(t As CustomTool, vals As Dictionary(Of String, Object), given As List(Of String), preview As Boolean) As Object
            Dim a As New ToolArgs(t.Spec.Title, vals, given, preview, _who.Name, _who.Roles)
            If t.Handler IsNot Nothing Then Return t.Handler(a)
            Return FunctionRunner.Execute(t.Spec.FunctionName, a)
        End Function

        ''' 确认卡片: 函数给的预览 (文字、表格), 没有预览时列出参数。
        Private Function Card(t As CustomTool, vals As Dictionary(Of String, Object), pv As Shaped) As JObject
            Dim o As New JObject From {{"kind", "action"}, {"title", t.Spec.Title}, {"confirmText", "确认" & t.Spec.Title}}
            Dim tables As New JArray()
            If pv IsNot Nothing AndAlso pv.Text IsNot Nothing Then o("text") = pv.Text
            If pv IsNot Nothing AndAlso pv.Table IsNot Nothing Then tables.Add(pv.Table)
            If pv Is Nothing OrElse (pv.Text Is Nothing AndAlso pv.Table Is Nothing) Then
                Dim rows As New JArray()
                For Each p In t.Spec.Params
                    Dim v = vals(p.Name)
                    If v Is Nothing Then Continue For
                    rows.Add(New JArray(p.Name, DisplayValue(v)))
                Next
                If rows.Count > 0 Then tables.Add(QueryEngine.UiTable("参数", {"参数", "值"}, rows, rows.Count))
            End If
            o("tables") = tables
            Return o
        End Function

        '==============================================================
        ' 参数检查与转换 (按 @参数 说明)
        '==============================================================

        Private Shared Function GetArg(args As JObject, name As String) As JToken
            Dim p = args.Properties().FirstOrDefault(Function(x) String.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            Return If(p Is Nothing, Nothing, p.Value)
        End Function

        ''' 检查并转换; 有问题时一次列出全部原因。given: 模型实际给了的参数。
        Friend Shared Function ConvertArgs(spec As ToolSpec, args As JObject, ByRef given As List(Of String)) As Dictionary(Of String, Object)
            args = If(args, New JObject())
            given = New List(Of String)
            Dim vals As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
            Dim problems As New List(Of String)
            Dim unknown = args.Properties().Where(Function(x) Not spec.Params.Any(Function(p) String.Equals(p.Name, x.Name, StringComparison.OrdinalIgnoreCase))).Select(Function(x) x.Name).ToList()
            If unknown.Count > 0 Then
                problems.Add("没有参数 " & String.Join("、", unknown) & If(spec.Params.Count = 0, " (这个工具不要参数)", " (参数: " & String.Join("、", spec.Params.Select(Function(p) p.Name)) & ")"))
            End If
            For Each p In spec.Params
                Dim tok = GetArg(args, p.Name)
                Dim empty = tok Is Nothing OrElse tok.Type = JTokenType.Null OrElse (tok.Type = JTokenType.String AndAlso CStr(tok).Trim() = "")
                Try
                    If empty Then
                        If p.Required Then Throw New ToolArgumentException("缺少必填参数 " & p.Name)
                        vals(p.Name) = If(p.DefaultText Is Nothing, Nothing, ConvertOne(p, New JValue(p.DefaultText)))
                    Else
                        vals(p.Name) = ConvertOne(p, tok)
                        given.Add(p.Name)
                    End If
                Catch ex As ToolArgumentException
                    problems.Add(ex.Message)
                End Try
            Next
            If problems.Count > 0 Then Throw New ToolArgumentException("参数有误: " & String.Join("; ", problems) & "。")
            Return vals
        End Function

        Private Shared Function ConvertOne(p As ParamSpec, tok As JToken) As Object
            Dim text = If(TypeOf tok Is JValue, Convert.ToString(DirectCast(tok, JValue).Value, CultureInfo.InvariantCulture), tok.ToString(Newtonsoft.Json.Formatting.None)).Trim()
            Select Case p.Kind
                Case ParamKind.Json
                    If tok.Type = JTokenType.String Then
                        Try
                            tok = JToken.Parse(text)
                        Catch
                        End Try
                    End If
                    Return Plain(tok)
                Case ParamKind.Bool
                    Dim b = If(tok.Type = JTokenType.Boolean, CBool(tok), Values.ParseBool(text))
                    If Not b.HasValue Then Throw New ToolArgumentException(p.Name & " 要 true 或 false, 给的是 """ & text & """")
                    Return b.Value
                Case ParamKind.Date
                    Dim d As Date
                    If Not Date.TryParse(text, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.AllowWhiteSpaces, d) Then
                        Throw New ToolArgumentException(p.Name & " 要日期 (如 2026-03-05), 给的是 """ & text & """")
                    End If
                    Return d
                Case ParamKind.Integer, ParamKind.Number
                    Dim n As Double
                    If Not Double.TryParse(text.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, n) Then
                        Throw New ToolArgumentException(p.Name & " 要" & If(p.Kind = ParamKind.Integer, "整数", "数字") & ", 给的是 """ & text & """")
                    End If
                    If p.Kind = ParamKind.Integer AndAlso n <> Math.Floor(n) Then Throw New ToolArgumentException(p.Name & " 要整数, 给的是 " & text)
                    If p.Min.HasValue AndAlso n < p.Min.Value Then Throw New ToolArgumentException(p.Name & " 不能小于 " & Num(p.Min.Value) & ", 给的是 " & text)
                    If p.Max.HasValue AndAlso n > p.Max.Value Then Throw New ToolArgumentException(p.Name & " 不能大于 " & Num(p.Max.Value) & ", 给的是 " & text)
                    If p.Options.Count > 0 AndAlso Not p.Options.Any(Function(o) OptionNumber(o).GetValueOrDefault(Double.NaN) = n) Then
                        Throw New ToolArgumentException(p.Name & " 只能是 " & String.Join("、", p.Options) & ", 给的是 " & text)
                    End If
                    If p.Kind = ParamKind.Integer Then
                        If n >= Integer.MinValue AndAlso n <= Integer.MaxValue Then Return CInt(n)
                        Return CLng(n)
                    End If
                    Return n
                Case Else
                    If p.Options.Count > 0 Then
                        Dim hit = p.Options.FirstOrDefault(Function(o) String.Equals(o, text, StringComparison.OrdinalIgnoreCase))
                        If hit Is Nothing Then Throw New ToolArgumentException(p.Name & " 只能是 " & String.Join("、", p.Options) & ", 给的是 """ & text & """")
                        text = hit
                    End If
                    If p.MaxLength.HasValue AndAlso text.Length > p.MaxLength.Value Then
                        Throw New ToolArgumentException(p.Name & " 最长 " & p.MaxLength.Value & " 个字, 给了 " & text.Length & " 个")
                    End If
                    Return text
            End Select
        End Function

        Private Shared Function OptionNumber(o As String) As Double?
            Dim d As Double
            If Double.TryParse(o, NumberStyles.Float, CultureInfo.InvariantCulture, d) Then Return d
            Return Nothing
        End Function

        Private Shared Function Num(d As Double) As String
            Return d.ToString(CultureInfo.InvariantCulture)
        End Function

        ''' JSON → Dictionary / List / 值, 狐表代码里用得惯。
        Private Shared Function Plain(tok As JToken) As Object
            Select Case tok.Type
                Case JTokenType.Null, JTokenType.Undefined : Return Nothing
                Case JTokenType.Object
                    Dim d As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
                    For Each p In DirectCast(tok, JObject).Properties()
                        d(p.Name) = Plain(p.Value)
                    Next
                    Return d
                Case JTokenType.Array
                    Return DirectCast(tok, JArray).Select(Function(x) Plain(x)).ToList()
                Case Else
                    Return DirectCast(tok, JValue).Value
            End Select
        End Function

        Private Shared Function ArgsJson(vals As Dictionary(Of String, Object)) As JObject
            Dim o As New JObject()
            For Each kv In vals
                If kv.Value IsNot Nothing Then o(kv.Key) = DisplayValue(kv.Value)
            Next
            Return o
        End Function

        Private Shared Function DisplayValue(v As Object) As JToken
            If TypeOf v Is IDictionary OrElse (TypeOf v Is IEnumerable AndAlso Not TypeOf v Is String) Then
                Try
                    Return JToken.FromObject(v)
                Catch
                    Return v.ToString()
                End Try
            End If
            Return Values.Display(v)
        End Function

        '==============================================================
        ' 返回值 → 给模型的 JSON、面板上的表格 (界面线程)
        '==============================================================

        Private NotInheritable Class Shaped
            Public Data As JToken = JValue.CreateNull()
            Public Table As JObject
            Public Text As String
            Public Summary As String = "完成"
            Public Note As String
            ''' 表格的原始值 (画图用); 不是表格时为 Nothing。
            Public Source As ChartSource
        End Class

        Private Function Shape(value As Object, title As String) As Shaped
            Dim s As New Shaped()
            If value Is Nothing OrElse TypeOf value Is DBNull Then Return s
            If TypeOf value Is String Then
                Dim text = DirectCast(value, String)
                s.Data = text
                s.Text = text
                Dim first = text.Trim().Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                s.Summary = If(first Is Nothing, "完成", If(first.Length > 40, first.Substring(0, 40) & "…", first))
                Return s
            End If
            If TypeOf value Is Foxtable.DataTable Then
                Return Rows(DirectCast(Fx.Get(value, "DataRows"), IEnumerable).Cast(Of Object)().ToList(), title)
            End If
            If TypeOf value Is System.Data.DataTable Then
                Return Rows(DirectCast(value, System.Data.DataTable).Rows.Cast(Of Object)().ToList(), title)
            End If
            If TypeOf value Is System.Data.DataView Then
                Return Rows(DirectCast(value, System.Data.DataView).Cast(Of Object)().Select(Function(v) CObj(DirectCast(v, System.Data.DataRowView).Row)).ToList(), title)
            End If
            If TypeOf value Is Foxtable.DataRow OrElse TypeOf value Is System.Data.DataRow Then
                Dim r = Rows(New List(Of Object) From {value}, title)
                r.Data = DirectCast(r.Data, JArray)(0)
                r.Summary = "1 行"
                Return r
            End If
            If TypeOf value Is IDictionary Then
                Dim o As New JObject()
                Dim lines As New JArray()
                For Each k In DirectCast(value, IDictionary).Keys
                    Dim v = DirectCast(value, IDictionary)(k)
                    o(Convert.ToString(k, CultureInfo.InvariantCulture)) = DisplayValue(v)
                    lines.Add(New JArray(Convert.ToString(k, CultureInfo.InvariantCulture), Values.Text(If(TypeOf v Is IEnumerable AndAlso Not TypeOf v Is String, DisplayValue(v).ToString(Newtonsoft.Json.Formatting.None), v))))
                Next
                s.Data = o
                s.Table = QueryEngine.UiTable(title, {"项目", "值"}, lines, lines.Count)
                s.Summary = lines.Count & " 项"
                ' 画图: 项目/值 两列 (指标卡按项目名取数)
                Dim src As New ChartSource With {.ModelRows = lines.Count, .Total = lines.Count}
                src.Columns.Add(New ChartColumn With {.Label = "项目", .Display = "项目"})
                src.Columns.Add(New ChartColumn With {.Label = "值", .Display = "值"})
                For Each k In DirectCast(value, IDictionary).Keys
                    src.Rows.Add({Convert.ToString(k, CultureInfo.InvariantCulture), RawValue(DirectCast(value, IDictionary)(k))})
                Next
                s.Source = src
                Return s
            End If
            If TypeOf value Is IEnumerable Then
                Dim items = DirectCast(value, IEnumerable).Cast(Of Object)().ToList()
                If items.Count > 0 AndAlso items.All(Function(x) TypeOf x Is Foxtable.DataRow OrElse TypeOf x Is System.Data.DataRow OrElse TypeOf x Is IDictionary) Then Return Rows(items, title)
                s.Data = New JArray(items.Take(_maxRows).Select(Function(x) DisplayValue(x)).ToArray())
                s.Summary = items.Count & " 项"
                If items.Count > _maxRows Then s.Note = "共 " & items.Count & " 项, 只给了前 " & _maxRows & " 项。"
                Return s
            End If
            Select Case Type.GetTypeCode(value.GetType())
                Case TypeCode.Object
                    Try
                        s.Data = JToken.FromObject(value)
                    Catch
                        s.Data = value.ToString()
                    End Try
                Case Else
                    s.Data = Values.Display(value)
                    s.Summary = Values.Text(value)
            End Select
            Return s
        End Function

        ''' 一组行 (狐表行、DataRow、Dictionary) → 对象数组和表格。列名以 _ 开头的系统列不给。
        Private Function Rows(items As List(Of Object), title As String) As Shaped
            Dim cols As New List(Of String)
            Dim recs As New List(Of List(Of KeyValuePair(Of String, Object)))
            ' 给模型的按 _maxRows 截断; 画图的最多留 500 行 (与图表的组数上限一致)
            Dim keep = Math.Max(_maxRows, ChartBuilder.MAX_CATEGORIES)
            For Each it In items.Take(keep)
                Dim rec = RowValues(it)
                For Each kv In rec
                    If Not cols.Contains(kv.Key) Then cols.Add(kv.Key)
                Next
                recs.Add(rec)
            Next
            Dim src As New ChartSource With {.ModelRows = Math.Min(items.Count, _maxRows), .Total = items.Count}
            For Each c In cols
                src.Columns.Add(New ChartColumn With {.Label = c, .Display = c})
            Next
            For Each rec In recs
                src.Rows.Add(cols.Select(Function(c) RawValue(rec.FirstOrDefault(Function(x) x.Key = c).Value)).ToArray())
            Next
            For Each c In src.Columns
                Dim i = src.Columns.IndexOf(c)
                Dim vals = src.Rows.Select(Function(r) r(i)).Where(Function(v) v IsNot Nothing).ToList()
                c.IsTime = vals.Count > 0 AndAlso vals.All(Function(v) TypeOf v Is Date)
            Next
            Dim data As New JArray()
            Dim lines As New JArray()
            For Each rec In recs.Take(_maxRows)
                Dim o As New JObject()
                Dim line As New JArray()
                For Each c In cols
                    Dim kv = rec.FirstOrDefault(Function(x) x.Key = c)
                    Dim v = If(kv.Key Is Nothing, Nothing, kv.Value)
                    o(c) = DisplayValue(If(v, DBNull.Value))
                    line.Add(DisplayValue(If(v, DBNull.Value)))
                Next
                data.Add(o)
                lines.Add(line)
            Next
            Dim s As New Shaped With {.Data = data, .Summary = items.Count & " 行", .Source = src}
            s.Table = QueryEngine.UiTable(title, cols, lines, items.Count)
            If items.Count > _maxRows Then s.Note = "共 " & items.Count & " 行, 只给了前 " & _maxRows & " 行。"
            Return s
        End Function

        ''' 画图用的原始值: 空值为 Nothing, 日期、数字原样, 其余 (含嵌套的列表) 为文字。
        Private Shared Function RawValue(v As Object) As Object
            If v Is Nothing OrElse TypeOf v Is DBNull Then Return Nothing
            If TypeOf v Is Date OrElse TypeOf v Is Boolean OrElse ChartBuilder.IsNumber(v) Then Return v
            If TypeOf v Is String Then Return v
            Return DisplayValue(v).ToString(Newtonsoft.Json.Formatting.None)
        End Function

        Private Shared Function RowValues(it As Object) As List(Of KeyValuePair(Of String, Object))
            Dim res As New List(Of KeyValuePair(Of String, Object))
            If TypeOf it Is IDictionary Then
                For Each k In DirectCast(it, IDictionary).Keys
                    res.Add(New KeyValuePair(Of String, Object)(Convert.ToString(k, CultureInfo.InvariantCulture), DirectCast(it, IDictionary)(k)))
                Next
                Return res
            End If
            Dim dr = TryCast(it, System.Data.DataRow)
            If dr Is Nothing AndAlso TypeOf it Is Foxtable.DataRow Then dr = DirectCast(Fx.Get(it, "BaseRow"), System.Data.DataRow)
            If dr Is Nothing Then Return res
            For Each c As System.Data.DataColumn In dr.Table.Columns
                If c.ColumnName.StartsWith("_", StringComparison.Ordinal) Then Continue For
                Dim v = If(dr.RowState = System.Data.DataRowState.Deleted, Nothing, dr(c))
                res.Add(New KeyValuePair(Of String, Object)(c.ColumnName, v))
            Next
            Return res
        End Function
    End Class

    ''' ai\tools.json: 设计环境保存项目时由函数注释生成。按修改时间缓存。
    Friend NotInheritable Class FunctionCatalog
        Private Shared ReadOnly _cache As New Dictionary(Of String, FunctionCatalog)(StringComparer.OrdinalIgnoreCase)

        Public ReadOnly Path As String
        Public ReadOnly Exists As Boolean
        Private ReadOnly _stamp As Date
        Private ReadOnly _tools As New Dictionary(Of String, ToolSpec)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _errors As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        Private Sub New(path As String)
            Me.Path = path
            Exists = IO.File.Exists(path)
            If Not Exists Then Return
            _stamp = IO.File.GetLastWriteTimeUtc(path)
            Dim root = JObject.Parse(IO.File.ReadAllText(path, System.Text.Encoding.UTF8))
            For Each o In If(TryCast(root("tools"), JArray), New JArray()).OfType(Of JObject)()
                Dim spec = ToolSpec.FromJson(o)
                _tools(spec.FunctionName) = spec
            Next
            For Each o In If(TryCast(root("errors"), JArray), New JArray()).OfType(Of JObject)()
                _errors(CStr(o("function"))) = CStr(o("error"))
            Next
        End Sub

        Public Shared Function Load(path As String) As FunctionCatalog
            SyncLock _cache
                Dim cur As FunctionCatalog = Nothing
                If _cache.TryGetValue(path, cur) Then
                    Dim exists = IO.File.Exists(path)
                    If exists = cur.Exists AndAlso (Not exists OrElse IO.File.GetLastWriteTimeUtc(path) = cur._stamp) Then Return cur
                End If
                cur = New FunctionCatalog(path)
                _cache(path) = cur
                Return cur
            End SyncLock
        End Function

        Public Function Find(name As String) As ToolSpec
            Dim spec As ToolSpec = Nothing
            If name IsNot Nothing AndAlso _tools.TryGetValue(name.Trim(), spec) Then Return spec
            Return Nothing
        End Function

        ''' 放行的函数没有定义时给开发者的原因。
        Public Function WhyMissing(name As String) As String
            If Not Exists Then
                Return "没找到工具定义文件 " & Path & "。在函数 " & name & " 开头写好 ' @AI工具 注释, 在设计环境里保存一次项目就会生成。"
            End If
            Dim err As String = Nothing
            If _errors.TryGetValue(If(name, "").Trim(), err) Then Return "函数 " & name & " 的 @AI工具 注释有误: " & err
            Return "函数 " & name & " 没有工具定义: 开头要写 ' @AI工具 注释, 写好后在设计环境里保存一次项目 (" & Path & " 会更新)。"
        End Function
    End Class

    ''' 调内部函数: Functions.Execute(名, ToolArgs)。只在界面线程上调用。
    Friend Module FunctionRunner

        ''' 函数在不在运行时里。查不了 (各版没有 Contains) 时不报错。
        Public Sub CheckExists(name As String)
            Dim funcs = Fx.Global("Functions")
            Dim contains = funcs.GetType().GetMethod("Contains", {GetType(String)})
            If contains Is Nothing Then Return
            Dim ok As Boolean
            Try
                ok = CBool(contains.Invoke(funcs, New Object() {name}))
            Catch
                Return
            End Try
            If Not ok Then Throw New ArgumentException("没有名为 " & name & " 的内部函数 (ai\tools.json 里有它, 可能函数已改名或删除, 在设计环境里保存一次项目更新定义)。")
        End Sub

        Public Function Execute(name As String, args As ToolArgs) As Object
            Dim funcs = Fx.Global("Functions")
            If funcs Is Nothing Then Throw New InvalidOperationException("狐表的内部函数还没就绪。")
            Dim exec = funcs.GetType().GetMethod("Execute", {GetType(String), GetType(Object())})
            If exec Is Nothing Then Throw New NotSupportedException("当前狐表的 Functions 上没有 Execute(String, Object()), 请反馈狐表版本号。")
            ' ThrowException = False 时狐表自己弹错误框并吞掉异常; 调用期间改成 True, 错误以异常回来交给 AI
            Dim throwProp = funcs.GetType().GetProperty("ThrowException")
            Dim old As Object = Nothing
            If throwProp IsNot Nothing AndAlso throwProp.CanRead AndAlso throwProp.CanWrite Then
                old = throwProp.GetValue(funcs, Nothing)
                throwProp.SetValue(funcs, True, Nothing)
            Else
                throwProp = Nothing
            End If
            Try
                Return exec.Invoke(funcs, New Object() {name, New Object() {args}})
            Catch ex As TargetInvocationException When ex.InnerException IsNot Nothing
                Throw New InvalidOperationException(Unwrap(ex.InnerException).Message, ex.InnerException)
            Finally
                If throwProp IsNot Nothing Then
                    Try
                        throwProp.SetValue(funcs, old, Nothing)
                    Catch
                    End Try
                End If
            End Try
        End Function

        ''' 狐表可能把函数里 Throw 的异常再包一层, 取最里面那条原因。
        Private Function Unwrap(ex As Exception) As Exception
            Dim cur = ex
            While cur.InnerException IsNot Nothing AndAlso (TypeOf cur Is TargetInvocationException OrElse String.IsNullOrEmpty(cur.Message))
                cur = cur.InnerException
            End While
            Return cur
        End Function
    End Module
End Namespace
