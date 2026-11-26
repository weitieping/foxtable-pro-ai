Imports System.Data
Imports System.Globalization

Namespace AI

    ''' 工具参数有误, 原因会原样交给模型, 让它改了再调。
    Friend NotInheritable Class ToolArgumentException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ''' 在放行的表上做只读查询和统计。筛选条件是结构化的 (列、比较符、值), 在内存里逐行判断,
    ''' 不拼表达式字符串, 模型给的值不会被当成代码执行。只在界面线程上调用。
    Friend NotInheritable Class QueryEngine

        Private ReadOnly _info As TableInfo
        Private ReadOnly _table As System.Data.DataTable

        Public Sub New(info As TableInfo)
            _info = info
            Dim dt = Fx.DataTable(info.Name)
            If dt Is Nothing Then Throw New ToolArgumentException("表 " & info.Name & " 已不存在。")
            _table = DirectCast(Fx.Get(dt, "BaseTable"), System.Data.DataTable)
        End Sub

        Public ReadOnly Property Info As TableInfo
            Get
                Return _info
            End Get
        End Property

        '==============================================================
        ' 列
        '==============================================================

        Public Function Col(name As String) As ColumnInfo
            Dim c = _info.Column(name)
            If c Is Nothing Then
                Throw New ToolArgumentException("表 " & _info.Name & " 没有可用的列 """ & name & """。可用的列: " &
                                                String.Join("、", _info.Columns.Select(Function(x) x.Name)))
            End If
            If Not _table.Columns.Contains(c.Name) Then Throw New ToolArgumentException("列 " & c.Name & " 不能查询。")
            Return c
        End Function

        Private Shared Function Cell(row As DataRow, c As ColumnInfo) As Object
            Dim v = row(c.Name)
            Return If(v Is DBNull.Value, Nothing, v)
        End Function

        '==============================================================
        ' 筛选
        '==============================================================

        Private MustInherit Class Cond
            Public MustOverride Function Match(row As DataRow) As Boolean
        End Class

        Private NotInheritable Class AnyCond
            Inherits Cond
            Public Items As New List(Of Cond)
            Public Overrides Function Match(row As DataRow) As Boolean
                Return Items.Any(Function(c) c.Match(row))
            End Function
        End Class

        Private NotInheritable Class AllCond
            Inherits Cond
            Public Items As New List(Of Cond)
            Public Overrides Function Match(row As DataRow) As Boolean
                Return Items.All(Function(c) c.Match(row))
            End Function
        End Class

        Private NotInheritable Class FuncCond
            Inherits Cond
            Public Test As Func(Of DataRow, Boolean)
            Public Overrides Function Match(row As DataRow) As Boolean
                Return Test(row)
            End Function
        End Class

        Public Const OPS As String = "= != > >= < <= contains not_contains starts_with ends_with in not_in between is_empty not_empty"

        Private Function ParseWhere(where As JToken) As Cond
            Dim all As New AllCond()
            If where Is Nothing OrElse where.Type = JTokenType.Null Then Return all
            If where.Type = JTokenType.Object Then where = New JArray(where)
            If where.Type <> JTokenType.Array Then Throw New ToolArgumentException("where 应是条件数组。")
            For Each cond In DirectCast(where, JArray)
                all.Items.Add(ParseCond(cond))
            Next
            Return all
        End Function

        Private Function ParseCond(item As JToken) As Cond
            Dim o = TryCast(item, JObject)
            If o Is Nothing Then Throw New ToolArgumentException("每个条件应是对象: {""column"":列名, ""op"":比较符, ""value"":值}。")
            Dim orList = TryCast(o("or"), JArray)
            If orList IsNot Nothing Then
                Dim any As New AnyCond()
                For Each x In orList
                    any.Items.Add(ParseCond(x))
                Next
                Return any
            End If
            Dim c = Col(CStr(o("column")))
            Dim op = If(CStr(o("op")), "=").Trim().ToLowerInvariant()
            Select Case op
                Case "==", "eq" : op = "="
                Case "<>", "ne" : op = "!="
                Case "like" : op = "contains"
            End Select
            Dim value = o("value")
            Dim values = TryCast(o("values"), JArray)
            If values Is Nothing AndAlso TypeOf value Is JArray Then values = DirectCast(value, JArray)

            Select Case op
                Case "is_empty"
                    Return New FuncCond With {.Test = Function(r) IsEmpty(Cell(r, c))}
                Case "not_empty"
                    Return New FuncCond With {.Test = Function(r) Not IsEmpty(Cell(r, c))}
                Case "in", "not_in"
                    If values Is Nothing Then values = New JArray(value)
                    Dim tests = values.Select(Function(v) Matcher(c, "=", v)).ToList()
                    If op = "in" Then Return New FuncCond With {.Test = Function(r) tests.Any(Function(t) t(Cell(r, c)))}
                    Return New FuncCond With {.Test = Function(r) Not tests.Any(Function(t) t(Cell(r, c)))}
                Case "between"
                    If values Is Nothing OrElse values.Count <> 2 Then Throw New ToolArgumentException("between 要给 values: [下限, 上限]。")
                    Dim lo = Matcher(c, ">=", values(0))
                    Dim hi = Matcher(c, "<=", values(1))
                    Return New FuncCond With {.Test = Function(r)
                                                          Dim v = Cell(r, c)
                                                          Return lo(v) AndAlso hi(v)
                                                      End Function}
                Case "=", "!=", ">", ">=", "<", "<=", "contains", "not_contains", "starts_with", "ends_with"
                    If value Is Nothing Then Throw New ToolArgumentException("条件 " & c.Name & " " & op & " 缺少 value。")
                    Dim t = Matcher(c, op, value)
                    Return New FuncCond With {.Test = Function(r) t(Cell(r, c))}
                Case Else
                    Throw New ToolArgumentException("不支持的比较符 """ & op & """。可用: " & OPS)
            End Select
        End Function

        Private Shared Function IsEmpty(v As Object) As Boolean
            Return v Is Nothing OrElse (TypeOf v Is String AndAlso DirectCast(v, String).Trim() = "")
        End Function

        ''' 按列类型把条件值换成对单元格的判断。
        Private Function Matcher(c As ColumnInfo, op As String, value As JToken) As Func(Of Object, Boolean)
            Dim text = If(value Is Nothing OrElse value.Type = JTokenType.Null, "", value.ToString())
            Select Case op
                Case "contains", "not_contains", "starts_with", "ends_with"
                    Dim cmp = StringComparison.OrdinalIgnoreCase
                    Select Case op
                        Case "contains" : Return Function(v) v IsNot Nothing AndAlso Str(v).IndexOf(text, cmp) >= 0
                        Case "not_contains" : Return Function(v) v Is Nothing OrElse Str(v).IndexOf(text, cmp) < 0
                        Case "starts_with" : Return Function(v) v IsNot Nothing AndAlso Str(v).StartsWith(text, cmp)
                        Case Else : Return Function(v) v IsNot Nothing AndAlso Str(v).EndsWith(text, cmp)
                    End Select
            End Select

            Select Case c.Kind
                Case ColumnKind.Integer, ColumnKind.Number
                    Dim d As Decimal
                    If Not Decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, d) Then
                        Throw New ToolArgumentException("列 " & c.Name & " 是数值, """ & text & """ 不是数字。")
                    End If
                    Return Compare(op, Function(v) If(v Is Nothing, CType(Nothing, Decimal?), ToDecimal(v)), d)
                Case ColumnKind.Date
                    Dim range = ParseDateRange(text)
                    If range Is Nothing Then Throw New ToolArgumentException("列 " & c.Name & " 是日期, """ & text & """ 不是日期。日期写 2026-03-05, 整月写 2026-03, 整年写 2026。")
                    Dim start = range.Item1, [end] = range.Item2
                    Dim getD As Func(Of Object, Date?) = Function(v) If(TypeOf v Is Date, DirectCast(v, Date), CType(Nothing, Date?))
                    ' 2026-03 这类粗粒度的值按区间比: "= 2026-03" 是整个三月, "> 2026-03" 是四月起
                    Select Case op
                        Case "=" : Return Function(v) InRange(getD(v), start, [end])
                        Case "!=" : Return Function(v) Not InRange(getD(v), start, [end])
                        Case ">" : Return Function(v) getD(v).HasValue AndAlso getD(v).Value >= [end]
                        Case ">=" : Return Function(v) getD(v).HasValue AndAlso getD(v).Value >= start
                        Case "<" : Return Function(v) getD(v).HasValue AndAlso getD(v).Value < start
                        Case Else : Return Function(v) getD(v).HasValue AndAlso getD(v).Value < [end]
                    End Select
                Case ColumnKind.Boolean
                    Dim b = ParseBool(text)
                    If Not b.HasValue Then Throw New ToolArgumentException("列 " & c.Name & " 是逻辑列, 值写 true 或 false。")
                    Dim want = b.Value
                    Select Case op
                        Case "=" : Return Function(v) (TypeOf v Is Boolean AndAlso DirectCast(v, Boolean) = want) OrElse (v Is Nothing AndAlso Not want)
                        Case "!=" : Return Function(v) Not ((TypeOf v Is Boolean AndAlso DirectCast(v, Boolean) = want) OrElse (v Is Nothing AndAlso Not want))
                        Case Else : Throw New ToolArgumentException("逻辑列只能用 = 或 !=。")
                    End Select
                Case Else
                    Dim cmp = StringComparer.OrdinalIgnoreCase
                    Select Case op
                        Case "=" : Return Function(v) v IsNot Nothing AndAlso cmp.Equals(Str(v).Trim(), text.Trim())
                        Case "!=" : Return Function(v) v Is Nothing OrElse Not cmp.Equals(Str(v).Trim(), text.Trim())
                        Case ">" : Return Function(v) v IsNot Nothing AndAlso cmp.Compare(Str(v), text) > 0
                        Case ">=" : Return Function(v) v IsNot Nothing AndAlso cmp.Compare(Str(v), text) >= 0
                        Case "<" : Return Function(v) v IsNot Nothing AndAlso cmp.Compare(Str(v), text) < 0
                        Case Else : Return Function(v) v IsNot Nothing AndAlso cmp.Compare(Str(v), text) <= 0
                    End Select
            End Select
        End Function

        Private Shared Function Compare(op As String, getV As Func(Of Object, Decimal?), d As Decimal) As Func(Of Object, Boolean)
            Dim test As Func(Of Decimal, Boolean)
            Select Case op
                Case "=" : test = Function(x) x = d
                Case "!=" : Return Function(v)
                                       Dim x = getV(v)
                                       Return Not x.HasValue OrElse x.Value <> d
                                   End Function
                Case ">" : test = Function(x) x > d
                Case ">=" : test = Function(x) x >= d
                Case "<" : test = Function(x) x < d
                Case Else : test = Function(x) x <= d
            End Select
            Return Function(v)
                       Dim x = getV(v)
                       Return x.HasValue AndAlso test(x.Value)
                   End Function
        End Function

        Private Shared Function InRange(v As Date?, start As Date, [end] As Date) As Boolean
            Return v.HasValue AndAlso v.Value >= start AndAlso v.Value < [end]
        End Function

        Private Shared Function Str(v As Object) As String
            If v Is Nothing Then Return ""
            If TypeOf v Is Date Then Return FormatDate(DirectCast(v, Date))
            Return Convert.ToString(v, CultureInfo.InvariantCulture)
        End Function

        Private Shared Function ToDecimal(v As Object) As Decimal?
            Try
                Return Convert.ToDecimal(v, CultureInfo.InvariantCulture)
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function ParseBool(s As String) As Boolean?
            Select Case s.Trim().ToLowerInvariant()
                Case "true", "1", "是", "yes", "y", "真" : Return True
                Case "false", "0", "否", "no", "n", "假" : Return False
                Case Else : Return Nothing
            End Select
        End Function

        ''' "2026" → 整年, "2026-03" → 整月, "2026-03-05" → 整天, 带时间的 → 那一秒。返回 [起, 止)。
        Friend Shared Function ParseDateRange(s As String) As Tuple(Of Date, Date)
            s = s.Trim().Replace("/"c, "-"c).Replace("."c, "-"c)
            Dim inv = CultureInfo.InvariantCulture
            Dim d As Date
            If Date.TryParseExact(s, "yyyy", inv, DateTimeStyles.None, d) Then Return Tuple.Create(d, d.AddYears(1))
            If Date.TryParseExact(s, {"yyyy-M", "yyyy-MM"}, inv, DateTimeStyles.None, d) Then Return Tuple.Create(d, d.AddMonths(1))
            If Date.TryParseExact(s, {"yyyy-M-d", "yyyy-MM-dd"}, inv, DateTimeStyles.None, d) Then Return Tuple.Create(d, d.AddDays(1))
            If Date.TryParse(s, inv, DateTimeStyles.None, d) OrElse Date.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, d) Then
                If d.TimeOfDay = TimeSpan.Zero Then Return Tuple.Create(d, d.AddDays(1))
                Return Tuple.Create(d, d.AddSeconds(1))
            End If
            Return Nothing
        End Function

        ''' 符合条件的行 (底层 DataRow, 不含已删除的)。
        Public Function FilterRows(where As JToken) As List(Of DataRow)
            Dim cond = ParseWhere(where)
            Dim res As New List(Of DataRow)
            For Each r As DataRow In _table.Rows
                If r.RowState = DataRowState.Deleted OrElse r.RowState = DataRowState.Detached Then Continue For
                If cond.Match(r) Then res.Add(r)
            Next
            Return res
        End Function

        '==============================================================
        ' 查询明细
        '==============================================================

        ''' 查明细行。返回给模型的 JSON 与给界面的表格。
        Public Function Query(args As JObject, maxRows As Integer) As ToolOutcome
            Dim cols As List(Of ColumnInfo)
            Dim colArg = TryCast(args("columns"), JArray)
            If colArg Is Nothing OrElse colArg.Count = 0 Then
                cols = _info.Columns.ToList()
            Else
                cols = colArg.Select(Function(x) Col(CStr(x))).ToList()
            End If
            Dim limit = LimitOf(args("limit"), 20, maxRows)
            Dim rows = FilterRows(args("where"))
            Dim sorted As IEnumerable(Of DataRow) = rows
            Dim orderArg = args("order_by")
            If orderArg IsNot Nothing AndAlso orderArg.Type <> JTokenType.Null Then
                If orderArg.Type = JTokenType.Object Then orderArg = New JArray(orderArg)
                Dim ordered As IOrderedEnumerable(Of DataRow) = Nothing
                For Each o In DirectCast(orderArg, JArray)
                    Dim c = Col(If(o.Type = JTokenType.Object, CStr(o("column")), CStr(o)))
                    Dim desc = o.Type = JTokenType.Object AndAlso o("desc") IsNot Nothing AndAlso CBool(o("desc"))
                    Dim key As Func(Of DataRow, Object) = Function(r) Cell(r, c)
                    If ordered Is Nothing Then
                        ordered = If(desc, sorted.OrderByDescending(key, ValueComparer.Instance), sorted.OrderBy(key, ValueComparer.Instance))
                    Else
                        ordered = If(desc, ordered.ThenByDescending(key, ValueComparer.Instance), ordered.ThenBy(key, ValueComparer.Instance))
                    End If
                Next
                If ordered IsNot Nothing Then sorted = ordered
            End If
            Dim picked = sorted.Take(limit).ToList()

            Dim data As New JArray()
            For Each r In picked
                data.Add(New JArray(cols.Select(Function(c) ToJValue(Cell(r, c))).Cast(Of Object)().ToArray()))
            Next
            Dim res As New JObject From {
                {"table", _info.Name},
                {"columns", New JArray(cols.Select(Function(c) c.Name).Cast(Of Object)().ToArray())},
                {"rows", data},
                {"matched", rows.Count},
                {"returned", picked.Count}}
            If rows.Count > picked.Count Then res("note") = "共 " & rows.Count & " 行符合条件, 只返回了前 " & picked.Count & " 行。要总数或汇总请用 aggregate。"
            If _info.PartiallyLoaded Then res("partial") = "只针对已加载的行。"

            Dim ui = UiTable(_info.Display, cols.Select(Function(c) c.Display), data, rows.Count)
            Return New ToolOutcome(res, If(rows.Count = picked.Count, rows.Count & " 行", "共 " & rows.Count & " 行, 显示前 " & picked.Count & " 行"), ui) With {.Open = OpenOf(rows, args("where"))}
        End Function

        '==============================================================
        ' 统计
        '==============================================================

        Private NotInheritable Class GroupKey
            Public Column As ColumnInfo
            Public Part As String
            Public Label As String
            Public Function KeyOf(r As DataRow) As Object
                Dim v = Cell(r, Column)
                If v Is Nothing OrElse String.IsNullOrEmpty(Part) Then Return v
                If Not TypeOf v Is Date Then Return v
                Dim d = DirectCast(v, Date)
                Select Case Part
                    Case "year" : Return d.Year.ToString("0000")
                    Case "quarter" : Return d.Year.ToString("0000") & "-Q" & ((d.Month - 1) \ 3 + 1)
                    Case "month" : Return d.ToString("yyyy-MM", CultureInfo.InvariantCulture)
                    Case "weekday" : Return New WeekdayKey(d.DayOfWeek)
                    Case Else : Return d.Date
                End Select
            End Function
        End Class

        ''' 星期分组的键: 显示为 星期一, 按星期一到星期日排序 (按文字排是乱的)。
        Private NotInheritable Class WeekdayKey
            Implements IComparable
            ''' 0 = 星期一 … 6 = 星期日
            Private ReadOnly _index As Integer

            Public Sub New(d As DayOfWeek)
                _index = (CInt(d) + 6) Mod 7
            End Sub

            Public Overrides Function ToString() As String
                Return "星期" & "一二三四五六日".Substring(_index, 1)
            End Function

            Public Function CompareTo(obj As Object) As Integer Implements IComparable.CompareTo
                Dim o = TryCast(obj, WeekdayKey)
                If o Is Nothing Then Return -1
                Return _index.CompareTo(o._index)
            End Function

            Public Overrides Function Equals(obj As Object) As Boolean
                Dim o = TryCast(obj, WeekdayKey)
                Return o IsNot Nothing AndAlso o._index = _index
            End Function

            Public Overrides Function GetHashCode() As Integer
                Return _index
            End Function
        End Class

        Private NotInheritable Class Metric
            Public Func As String
            Public Column As ColumnInfo
            Public Label As String
        End Class

        Private Shared ReadOnly PartNames As New Dictionary(Of String, String) From {
            {"year", "年"}, {"quarter", "季度"}, {"month", "月"}, {"day", "日"}, {"weekday", "星期"}}

        ''' 分组统计。charts: 能不能出图 (开发者没关掉图表); 能出图时按 args("chart") 画图, 图表数据与给模型的分开。
        Public Function Aggregate(args As JObject, maxGroups As Integer, Optional charts As Boolean = False) As ToolOutcome
            ' 分组
            Dim keys As New List(Of GroupKey)
            Dim gb = args("group_by")
            If gb IsNot Nothing AndAlso gb.Type <> JTokenType.Null Then
                If gb.Type <> JTokenType.Array Then gb = New JArray(gb)
                For Each g In DirectCast(gb, JArray)
                    Dim k As New GroupKey()
                    If g.Type = JTokenType.Object Then
                        k.Column = Col(CStr(g("column")))
                        k.Part = If(CStr(g("date_part")), "").Trim().ToLowerInvariant()
                    Else
                        k.Column = Col(CStr(g))
                        k.Part = ""
                    End If
                    If k.Part <> "" Then
                        If Not PartNames.ContainsKey(k.Part) Then Throw New ToolArgumentException("date_part 只能是 year、quarter、month、day、weekday。")
                        If k.Column.Kind <> ColumnKind.Date Then Throw New ToolArgumentException("列 " & k.Column.Name & " 不是日期列, 不能按 " & k.Part & " 分组。")
                    End If
                    k.Label = k.Column.Name & If(k.Part = "", "", "(" & PartNames(k.Part) & ")")
                    keys.Add(k)
                Next
            End If
            If keys.Count > 3 Then Throw New ToolArgumentException("最多按 3 列分组。")

            ' 统计项
            Dim metrics As New List(Of Metric)
            Dim ms = TryCast(args("metrics"), JArray)
            If ms Is Nothing OrElse ms.Count = 0 Then ms = New JArray(New JObject From {{"func", "count"}})
            For Each m In ms
                Dim mo = TryCast(m, JObject)
                If mo Is Nothing Then Throw New ToolArgumentException("metrics 的每一项应是 {""func"":..., ""column"":...}。")
                Dim f = If(CStr(mo("func")), "count").Trim().ToLowerInvariant()
                Dim colName = CStr(mo("column"))
                Dim c = If(String.IsNullOrWhiteSpace(colName), Nothing, Col(colName))
                Select Case f
                    Case "count"
                    Case "count_distinct", "min", "max"
                        If c Is Nothing Then Throw New ToolArgumentException(f & " 要给 column。")
                    Case "sum", "avg"
                        If c Is Nothing Then Throw New ToolArgumentException(f & " 要给 column。")
                        If c.Kind <> ColumnKind.Integer AndAlso c.Kind <> ColumnKind.Number Then Throw New ToolArgumentException("列 " & c.Name & " 不是数值列, 不能 " & f & "。")
                    Case Else
                        Throw New ToolArgumentException("不支持的统计 """ & f & """。可用: count、count_distinct、sum、avg、min、max。")
                End Select
                metrics.Add(New Metric With {.Func = f, .Column = c, .Label = f & If(c Is Nothing, "", "(" & c.Name & ")")})
            Next

            Dim rows = FilterRows(args("where"))
            Dim groups = rows.GroupBy(Function(r) New CompositeKey(keys.Select(Function(k) k.KeyOf(r)).ToArray()))
            Dim result As New List(Of Object())
            For Each g In groups
                Dim line As New List(Of Object)(g.Key.Values)
                For Each m In metrics
                    line.Add(Compute(m, g))
                Next
                result.Add(line.ToArray())
            Next

            ' 排序: order_by.by 写分组列或统计项 (如 "sum(金额)"); 默认按分组列升序
            Dim labels = keys.Select(Function(k) k.Label).Concat(metrics.Select(Function(m) m.Label)).ToList()
            Dim ob = TryCast(args("order_by"), JObject)
            Dim sortIdx = -1, desc = False
            If ob IsNot Nothing Then
                Dim by = If(CStr(ob("by")), "").Trim()
                sortIdx = labels.FindIndex(Function(l) String.Equals(l, by, StringComparison.OrdinalIgnoreCase))
                If sortIdx < 0 Then
                    Dim byFunc = metrics.Where(Function(m) String.Equals(m.Func, by, StringComparison.OrdinalIgnoreCase)).ToList()
                    If byFunc.Count = 1 Then sortIdx = keys.Count + metrics.IndexOf(byFunc(0))
                End If
                If sortIdx < 0 AndAlso by <> "" Then Throw New ToolArgumentException("order_by.by 应是这些之一: " & String.Join("、", labels))
                desc = ob("desc") IsNot Nothing AndAlso CBool(ob("desc"))
            End If
            If sortIdx < 0 AndAlso keys.Count > 0 Then sortIdx = 0
            If sortIdx >= 0 Then
                Dim i = sortIdx
                result = If(desc, result.OrderByDescending(Function(r) r(i), ValueComparer.Instance),
                                  result.OrderBy(Function(r) r(i), ValueComparer.Instance)).ToList()
            End If
            Dim limit = LimitOf(args("limit"), 50, maxGroups)
            Dim picked = result.Take(limit).ToList()

            Dim data As New JArray()
            For Each r In picked
                data.Add(New JArray(r.Select(Function(v) ToJValue(v)).Cast(Of Object)().ToArray()))
            Next
            Dim res As New JObject From {
                {"table", _info.Name},
                {"columns", New JArray(labels.Cast(Of Object)().ToArray())},
                {"rows", data},
                {"matched_rows", rows.Count},
                {"groups", result.Count}}
            ' 分组时附上全部符合行的总计: 模型自己加总常出错
            If keys.Count > 0 Then
                Dim totals As New JObject()
                For Each m In metrics
                    totals(m.Label) = ToJValue(Compute(m, rows))
                Next
                res("totals") = totals
            End If
            If result.Count > picked.Count Then res("note") = "共 " & result.Count & " 组, 只返回了前 " & picked.Count & " 组。"
            If _info.PartiallyLoaded Then res("partial") = "只针对已加载的行。"

            Dim display = keys.Select(Function(k) k.Column.Display & If(k.Part = "", "", "(" & PartNames(k.Part) & ")")).
                          Concat(metrics.Select(Function(m) MetricDisplay(m)))
            Dim summary = If(keys.Count = 0, "统计了 " & rows.Count & " 行", rows.Count & " 行, " & result.Count & " 组")
            Dim outcome = New ToolOutcome(res, summary, UiTable(_info.Display, display, data, result.Count)) With {.Open = OpenOf(rows, args("where"))}

            ' 出图: 只用这次统计的结果。模型指定了 limit (如前 10 名) 就只画这几组, 否则画全部 (最多 500 组)
            Dim chartArg = args("chart")
            If chartArg IsNot Nothing AndAlso chartArg.Type <> JTokenType.Null Then
                If Not charts Then
                    res("chart_error") = "当前不支持出图, 用表格和文字回答。"
                Else
                    Dim cols = keys.Select(Function(k) ChartColumnOf(k)).Concat(metrics.Select(Function(m) ChartColumnOf(m, metrics))).ToList()
                    Dim explicitLimit = args("limit") IsNot Nothing AndAlso args("limit").Type <> JTokenType.Null
                    Dim explicitOrder = ob IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(CStr(ob("by")))
                    Dim c = ChartBuilder.Build(chartArg, cols, If(explicitLimit, picked, result), picked.Count, explicitOrder, _info.PartiallyLoaded)
                    If c.Failed Then
                        res("chart_error") = c.Message
                    Else
                        res("chart") = c.Message
                        outcome.Chart = c.Chart
                    End If
                End If
            End If
            Return outcome
        End Function

        Private Shared Function ChartColumnOf(k As GroupKey) As ChartColumn
            Dim c As New ChartColumn With {.Label = k.Label, .Display = k.Column.Display & If(k.Part = "", "", "(" & PartNames(k.Part) & ")"),
                                           .IsKey = True, .IsTime = k.Column.Kind = ColumnKind.Date}
            c.Aliases.Add(k.Column.Name)
            If Not String.IsNullOrEmpty(k.Column.Caption) Then c.Aliases.Add(k.Column.Caption)
            Return c
        End Function

        Private Shared Function ChartColumnOf(m As Metric, all As List(Of Metric)) As ChartColumn
            Dim c As New ChartColumn With {.Label = m.Label, .Display = MetricDisplay(m)}
            ' 只有一项是这个函数、或只有一项用这一列时, 也认 "sum"、"金额" 这类简写
            If all.Where(Function(x) x.Func = m.Func).Count() = 1 Then c.Aliases.Add(m.Func)
            If m.Column IsNot Nothing Then
                c.Aliases.Add(m.Func & "(" & m.Column.Display & ")")
                If all.Where(Function(x) x.Column Is m.Column).Count() = 1 Then
                    c.Aliases.Add(m.Column.Name)
                    If Not String.IsNullOrEmpty(m.Column.Caption) Then c.Aliases.Add(m.Column.Caption)
                End If
            End If
            Return c
        End Function

        Private Shared Function MetricDisplay(m As Metric) As String
            Dim name = If(m.Column Is Nothing, "", m.Column.Display)
            Select Case m.Func
                Case "count" : Return If(name = "", "行数", name & " 个数")
                Case "count_distinct" : Return name & " 不同值个数"
                Case "sum" : Return name & " 合计"
                Case "avg" : Return name & " 平均"
                Case "min" : Return name & " 最小"
                Case Else : Return name & " 最大"
            End Select
        End Function

        Private Shared Function Compute(m As Metric, rows As IEnumerable(Of DataRow)) As Object
            Select Case m.Func
                Case "count"
                    If m.Column Is Nothing Then Return rows.Count()
                    Return rows.Count(Function(r) Cell(r, m.Column) IsNot Nothing)
                Case "count_distinct"
                    Return rows.Select(Function(r) Cell(r, m.Column)).Where(Function(v) v IsNot Nothing).Distinct().Count()
                Case "sum", "avg"
                    Dim nums = rows.Select(Function(r) ToDecimal(Cell(r, m.Column))).Where(Function(v) v.HasValue).Select(Function(v) v.Value).ToList()
                    If m.Func = "sum" Then Return nums.Sum()
                    If nums.Count = 0 Then Return Nothing
                    Return Math.Round(nums.Average(), 4)
                Case "min"
                    Return rows.Select(Function(r) Cell(r, m.Column)).Where(Function(v) v IsNot Nothing).OrderBy(Function(v) v, ValueComparer.Instance).FirstOrDefault()
                Case Else
                    Return rows.Select(Function(r) Cell(r, m.Column)).Where(Function(v) v IsNot Nothing).OrderByDescending(Function(v) v, ValueComparer.Instance).FirstOrDefault()
            End Select
        End Function

        '==============================================================
        ' 公用
        '==============================================================

        Private Shared Function LimitOf(t As JToken, def As Integer, max As Integer) As Integer
            Dim n = def
            If t IsNot Nothing AndAlso t.Type <> JTokenType.Null Then
                Try
                    n = CInt(t)
                Catch
                End Try
            End If
            Return Math.Max(1, Math.Min(n, max))
        End Function

        Friend Shared Function FormatDate(d As Date) As String
            Return If(d.TimeOfDay = TimeSpan.Zero, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                                   d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
        End Function

        Friend Shared Function ToJValue(v As Object) As JToken
            If v Is Nothing OrElse v Is DBNull.Value Then Return JValue.CreateNull()
            If TypeOf v Is Date Then Return New JValue(FormatDate(DirectCast(v, Date)))
            If TypeOf v Is Boolean Then Return New JValue(DirectCast(v, Boolean))
            Select Case Type.GetTypeCode(v.GetType())
                Case TypeCode.Byte, TypeCode.SByte, TypeCode.Int16, TypeCode.Int32, TypeCode.Int64, TypeCode.UInt16, TypeCode.UInt32
                    Return New JValue(Convert.ToInt64(v))
                Case TypeCode.Decimal
                    Return New JValue(DirectCast(v, Decimal))
                Case TypeCode.Double, TypeCode.Single
                    Return New JValue(Math.Round(Convert.ToDouble(v), 6))
            End Select
            Dim s = Convert.ToString(v, CultureInfo.InvariantCulture)
            If s.Length > 500 Then s = s.Substring(0, 500) & "…"
            Return New JValue(s)
        End Function

        ''' 在狐表中打开这些行: 条件翻成筛选表达式; 翻不了时按 _Identify 筛。
        Private Function OpenOf(rows As List(Of DataRow), where As JToken) As OpenTarget
            If rows.Count = 0 Then Return Nothing
            Dim filter = FilterBuilder.Build(_info, where)
            If filter IsNot Nothing Then Return New OpenTarget(_info.Name, filter)
            If Not _table.Columns.Contains("_Identify") Then Return Nothing
            Return New OpenTarget(_info.Name, Nothing, rows.Select(Function(r) IdOf(r)).Where(Function(x) x.HasValue).Select(Function(x) x.Value))
        End Function

        Public Shared Function IdOf(r As DataRow) As Long?
            Try
                Dim v = r("_Identify")
                If v Is Nothing OrElse TypeOf v Is DBNull Then Return Nothing
                Return Convert.ToInt64(v)
            Catch
                Return Nothing
            End Try
        End Function

        Friend Shared Function UiTable(title As String, columns As IEnumerable(Of String), rows As JArray, total As Integer) As JObject
            Return New JObject From {
                {"title", title},
                {"columns", New JArray(columns.Cast(Of Object)().ToArray())},
                {"rows", rows},
                {"total", total}}
        End Function

        ''' 分组键: 按值比较的数组。
        Private NotInheritable Class CompositeKey
            Public ReadOnly Values() As Object
            Public Sub New(values() As Object)
                Me.Values = values
            End Sub
            Public Overrides Function Equals(obj As Object) As Boolean
                Dim o = TryCast(obj, CompositeKey)
                If o Is Nothing OrElse o.Values.Length <> Values.Length Then Return False
                For i = 0 To Values.Length - 1
                    If Not Object.Equals(Values(i), o.Values(i)) Then Return False
                Next
                Return True
            End Function
            Public Overrides Function GetHashCode() As Integer
                Dim h = 17
                For Each v In Values
                    h = h * 31 + If(v Is Nothing, 0, v.GetHashCode())
                Next
                Return h
            End Function
        End Class
    End Class

    ''' 排序用: 空值排最后, 数字按数值比, 其余按文本比。
    Friend NotInheritable Class ValueComparer
        Implements IComparer(Of Object)

        Public Shared ReadOnly Instance As New ValueComparer()

        Public Function Compare(x As Object, y As Object) As Integer Implements IComparer(Of Object).Compare
            If x Is Nothing AndAlso y Is Nothing Then Return 0
            If x Is Nothing Then Return 1
            If y Is Nothing Then Return -1
            If IsNumber(x) AndAlso IsNumber(y) Then Return Convert.ToDecimal(x).CompareTo(Convert.ToDecimal(y))
            If TypeOf x Is Date AndAlso TypeOf y Is Date Then Return DirectCast(x, Date).CompareTo(DirectCast(y, Date))
            If TypeOf x Is IComparable AndAlso x.GetType() Is y.GetType() Then Return DirectCast(x, IComparable).CompareTo(y)
            Return String.Compare(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture), StringComparison.CurrentCulture)
        End Function

        Private Shared Function IsNumber(v As Object) As Boolean
            Select Case Type.GetTypeCode(v.GetType())
                Case TypeCode.Byte, TypeCode.SByte, TypeCode.Int16, TypeCode.Int32, TypeCode.Int64,
                     TypeCode.UInt16, TypeCode.UInt32, TypeCode.UInt64, TypeCode.Decimal, TypeCode.Double, TypeCode.Single
                    Return True
            End Select
            Return False
        End Function
    End Class

    ''' 一次工具调用的结果: 给模型的 JSON、给界面的一句话摘要和表格。
    Friend NotInheritable Class ToolOutcome
        Public ReadOnly Result As JObject
        Public ReadOnly Summary As String
        Public ReadOnly Table As JObject
        ''' 可以"在狐表中打开"的行。
        Public Property Open As OpenTarget
        ''' 给界面的图表 (只给界面, 不进模型的历史)。
        Public Property Chart As JObject

        Public Sub New(result As JObject, summary As String, Optional table As JObject = Nothing)
            Me.Result = result
            Me.Summary = summary
            Me.Table = table
        End Sub
    End Class
End Namespace
