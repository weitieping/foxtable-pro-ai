Imports System.Globalization

Namespace AI

    ''' 统计结果里的一列, 出图时按名字引用。
    Friend NotInheritable Class ChartColumn
        ''' 给模型的名字, 如 日期(月)、sum(金额)。
        Public Label As String
        ''' 界面上的名字, 如 日期(月)、金额 合计。
        Public Display As String
        ''' 其他可以接受的写法 (列名、列标题、统计函数名等)。
        Public ReadOnly Aliases As New List(Of String)
        ''' 分组列 (否则是统计项)。
        Public IsKey As Boolean
        ''' 分组列是日期 (按年月日、星期分组的也算): 横轴按时间先后排。
        Public IsTime As Boolean
    End Class

    ''' 出图的结果: 给界面的图表数据, 给模型的一句话。
    Friend NotInheritable Class ChartOutcome
        ''' 给界面的图表; 没出图时为 Nothing。
        Public Chart As JObject
        ''' 给模型: 出了什么图, 或者为什么没出。
        Public Message As String

        Public ReadOnly Property Failed As Boolean
            Get
                Return Chart Is Nothing
            End Get
        End Property
    End Class

    ''' 按模型的出图要求, 从统计结果里取数画图。模型只决定画法 (类型、横轴、数值、系列),
    ''' 数字一律由这里从结果里取, 图和表格里的数字一致。不碰狐表, 只处理已经算好的结果。
    Friend NotInheritable Class ChartBuilder

        ''' 图表最多显示多少组 (横轴上的点); 给模型的仍按 limit 截断。
        Public Const MAX_CATEGORIES As Integer = 500
        ''' 拆成多条系列时最多几条。
        Public Const MAX_SERIES As Integer = 20

        Public Shared ReadOnly Types As String() = {"bar", "bar_horizontal", "bar_stacked", "line", "area", "pie", "donut", "combo"}

        Public Shared Function TypeName(type As String) As String
            Select Case type
                Case "bar" : Return "柱状图"
                Case "bar_horizontal" : Return "横向柱状图"
                Case "bar_stacked" : Return "堆叠柱状图"
                Case "line" : Return "折线图"
                Case "area" : Return "面积图"
                Case "pie" : Return "饼图"
                Case "donut" : Return "环形图"
                Case "combo" : Return "组合图"
                Case Else : Return type
            End Select
        End Function

        ''' 给模型的出图参数定义。
        Public Shared Function Schema() As JObject
            Return New JObject From {
                {"type", "object"},
                {"description", "出图要求。结果适合看图时附上, 统计完直接显示图表, 图上的数字由系统从统计结果里取。" &
                                "选图: 随时间的变化趋势用 line 或 area; 各类别比大小、排名用 bar, 类别超过 8 个或名称较长用 bar_horizontal; " &
                                "各部分占总体的比例用 pie 或 donut (超过 8 块系统自动把其余合并为""其他"", 有负数时不能用); " &
                                "两个数量一起看 (如金额和单数) 用 combo, metrics 给两项; 按两列分组 (如月份×类别) 用 line 或 bar_stacked。" &
                                "不出图: 结果只有一个数字、只有一两组, 或用户只要数字。一次回答最多出一张图。"},
                {"properties", New JObject From {
                    {"type", New JObject From {{"type", "string"}, {"enum", New JArray(Types.Cast(Of Object)().ToArray())},
                                               {"description", "bar 柱状、bar_horizontal 横向柱状、bar_stacked 堆叠柱状、line 折线、area 面积、pie 饼图、donut 环形、combo 组合 (第一项柱状用左轴, 第二项折线用右轴)"}}},
                    {"x", New JObject From {{"type", "string"}, {"description", "横轴 (饼图为分块): 一个分组列, 如 日期(月)。不填为第一个分组列"}}},
                    {"y", New JObject From {{"type", "array"}, {"items", New JObject From {{"type", "string"}}},
                                            {"description", "数值: 统计项, 如 sum(金额)。不填为全部统计项"}}},
                    {"series", New JObject From {{"type", "string"}, {"description", "按两列分组时, 拆成多条系列的那一列 (如 产品类别)。不填为另一个分组列"}}},
                    {"title", New JObject From {{"type", "string"}, {"description", "图表标题, 简短; 不填自动生成"}}}}},
                {"required", New JArray("type")}}
        End Function

        ''' rows: 给图表用的结果行 (每行先分组列、后统计项, 与 cols 对应), 已按统计时的顺序排好。
        ''' modelRows: 给模型的行数; explicitOrder: 模型指定了排序 (就不再按数值重排); partialRows: 只统计了已加载的行。
        Public Shared Function Build(spec As JToken, cols As List(Of ChartColumn), rows As List(Of Object()),
                                     modelRows As Integer, explicitOrder As Boolean, partialRows As Boolean) As ChartOutcome
            Try
                Return BuildCore(spec, cols, rows, modelRows, explicitOrder, partialRows)
            Catch ex As ToolArgumentException
                Return New ChartOutcome With {.Message = "没有出图: " & ex.Message & " 统计结果照常给出; 要出图就改正 chart 后再调用一次。"}
            End Try
        End Function

        Private Shared Function BuildCore(spec As JToken, cols As List(Of ChartColumn), rows As List(Of Object()),
                                          modelRows As Integer, explicitOrder As Boolean, partialRows As Boolean) As ChartOutcome
            Dim o = TryCast(spec, JObject)
            If o Is Nothing Then Throw New ToolArgumentException("chart 应是对象, 如 {""type"":""bar""}。")
            Dim type = NormalizeType(CStr(o("type")))

            Dim keys = cols.Where(Function(c) c.IsKey).ToList()
            Dim metrics = cols.Where(Function(c) Not c.IsKey).ToList()
            If keys.Count = 0 Then Throw New ToolArgumentException("没有分组, 结果只有一个数字, 不用出图。")
            If keys.Count > 2 Then Throw New ToolArgumentException("按 3 列分组的结果画不清, 出图时最多按两列分组 (横轴 + 系列)。")

            ' 横轴、系列、数值
            Dim x = If(Resolve(o("x"), keys, "x"), keys(0))
            Dim ser = Resolve(o("series"), keys, "series")
            If ser Is x Then Throw New ToolArgumentException("series 和 x 不能是同一列。")
            If ser Is Nothing AndAlso keys.Count = 2 Then ser = keys.First(Function(k) k IsNot x)
            Dim ys As New List(Of ChartColumn)
            Dim yArg = o("y")
            If yArg IsNot Nothing AndAlso yArg.Type = JTokenType.String Then yArg = New JArray(yArg)
            If TypeOf yArg Is JArray AndAlso DirectCast(yArg, JArray).Count > 0 Then
                For Each t In DirectCast(yArg, JArray)
                    Dim c = Resolve(t, metrics, "y")
                    If c IsNot Nothing AndAlso Not ys.Contains(c) Then ys.Add(c)
                Next
            Else
                ys.AddRange(metrics)
            End If
            If ys.Count = 0 Then Throw New ToolArgumentException("没有可以画的统计项。")
            If ser IsNot Nothing AndAlso ys.Count > 1 Then Throw New ToolArgumentException("拆成多条系列时只能画一个统计项, 用 y 指定一项。")
            Dim xi = cols.IndexOf(x)
            Dim si = If(ser Is Nothing, -1, cols.IndexOf(ser))
            For Each y In ys
                Dim yi = cols.IndexOf(y)
                Dim bad = rows.Select(Function(r) r(yi)).FirstOrDefault(Function(v) v IsNot Nothing AndAlso Not IsNumber(v))
                If bad IsNot Nothing Then Throw New ToolArgumentException(y.Label & " 不是数字 (如 """ & Label(bad) & """), 不能当数值画图。")
            Next

            ' 横轴顺序: 时间按先后; 比大小、占比没指定排序时按数值从大到小
            Dim ordered As IEnumerable(Of Object()) = rows
            If x.IsTime AndAlso (Not explicitOrder OrElse type = "line" OrElse type = "area" OrElse type = "combo" OrElse ser IsNot Nothing) Then
                ordered = rows.OrderBy(Function(r) r(xi), ValueComparer.Instance)
            ElseIf Not x.IsTime AndAlso Not explicitOrder AndAlso ser Is Nothing AndAlso ys.Count = 1 AndAlso
                   (type = "bar" OrElse type = "bar_horizontal" OrElse type = "pie" OrElse type = "donut") Then
                Dim yi = cols.IndexOf(ys(0))
                ordered = rows.OrderByDescending(Function(r) r(yi), ValueComparer.Instance)
            End If

            ' 取数
            Dim cats As New List(Of Object)
            Dim catIndex As New Dictionary(Of String, Integer)
            Dim seriesKeys As New List(Of Object)
            Dim cells As New Dictionary(Of String, Object)
            For Each r In ordered
                Dim ck = Label(r(xi))
                If Not catIndex.ContainsKey(ck) Then
                    catIndex(ck) = cats.Count
                    cats.Add(r(xi))
                End If
                If ser IsNot Nothing Then
                    Dim sk = Label(r(si))
                    If Not seriesKeys.Any(Function(s) Label(s) = sk) Then seriesKeys.Add(r(si))
                    cells(ck & vbNullChar & sk) = r(cols.IndexOf(ys(0)))
                Else
                    For Each y In ys
                        cells(ck & vbNullChar & y.Label) = r(cols.IndexOf(y))
                    Next
                End If
            Next
            Dim totalCats = cats.Count
            If cats.Count > MAX_CATEGORIES Then cats = cats.Take(MAX_CATEGORIES).ToList()
            If ser IsNot Nothing Then
                seriesKeys = seriesKeys.OrderBy(Function(s) s, ValueComparer.Instance).ToList()
                If seriesKeys.Count > MAX_SERIES Then Throw New ToolArgumentException("按 " & ser.Label & " 拆出 " & seriesKeys.Count & " 条系列, 太多画不清; 换个分组或加条件缩小范围。")
            End If

            Dim series As New JArray()
            Dim allValues As New List(Of Decimal)
            Dim addSeries = Sub(name As String, key As String, axis As Integer)
                                Dim data As New JArray()
                                For Each c In cats
                                    Dim v As Object = Nothing
                                    cells.TryGetValue(Label(c) & vbNullChar & key, v)
                                    data.Add(QueryEngine.ToJValue(v))
                                    If v IsNot Nothing Then allValues.Add(Convert.ToDecimal(v, CultureInfo.InvariantCulture))
                                Next
                                Dim s As New JObject From {{"name", name}, {"data", data}}
                                If axis > 0 Then s("axis") = axis
                                series.Add(s)
                            End Sub
            If ser IsNot Nothing Then
                For Each s In seriesKeys
                    addSeries(Label(s), Label(s), 0)
                Next
            Else
                For i = 0 To ys.Count - 1
                    addSeries(ys(i).Display, ys(i).Label, If(i = 1 AndAlso ys.Count = 2, 1, 0))
                Next
            End If

            If cats.Count < 2 OrElse cats.Count * series.Count < 3 Then
                Throw New ToolArgumentException("只有 " & cats.Count & " 组数据, 直接用文字回答即可, 不用出图。")
            End If

            ' 数据形状决定能切换到哪些图
            Dim pivot = ser IsNot Nothing
            Dim multi = series.Count > 1
            Dim types As New List(Of String)
            If pivot Then
                types.AddRange({"line", "bar", "bar_stacked", "bar_horizontal", "area"})
            ElseIf multi Then
                If series.Count = 2 Then types.Add("combo")
                types.AddRange({"bar", "bar_horizontal", "line"})
            Else
                types.AddRange({"bar", "bar_horizontal", "line", "area"})
                If allValues.All(Function(v) v >= 0) AndAlso allValues.Any(Function(v) v > 0) Then types.AddRange({"pie", "donut"})
            End If

            Dim fallback As String = Nothing
            If Not types.Contains(type) Then
                fallback = TypeName(type) & WhyNot(type, pivot, multi, series.Count)
                type = If(x.IsTime AndAlso types.Contains("line"), "line", types.First(Function(t) t <> "combo"))
                fallback &= ", 已改为" & TypeName(type)
            End If

            Dim title = If(CStr(o("title")), "").Trim()
            If title.Length > 40 Then title = title.Substring(0, 40)
            If title = "" Then
                title = If(pivot, ys(0).Display & " · 按 " & x.Display & "、" & ser.Display,
                                  String.Join("、", ys.Select(Function(y) y.Display)) & " · 按 " & x.Display)
            End If

            Dim chart As New JObject From {
                {"type", type},
                {"types", New JArray(types.Cast(Of Object)().ToArray())},
                {"title", title},
                {"x", x.Display},
                {"categories", New JArray(cats.Select(Function(c) Label(c)).Cast(Of Object)().ToArray())},
                {"series", series}}
            If pivot Then chart("y") = ys(0).Display
            Dim notes As New List(Of String)
            If totalCats > cats.Count Then notes.Add("共 " & totalCats & " 组, 图上只显示前 " & cats.Count & " 组")
            If partialRows Then notes.Add("只统计了已加载的行")
            If notes.Count > 0 Then chart("note") = String.Join("; ", notes)

            Dim msg As New Text.StringBuilder()
            msg.Append("已在界面上显示").Append(TypeName(type)).Append(", ").Append(cats.Count).Append(" 组")
            If pivot Then msg.Append(" × ").Append(series.Count).Append(" 条系列")
            msg.Append("。")
            If fallback IsNot Nothing Then msg.Append(fallback).Append("。")
            If cats.Count > modelRows Then msg.Append("图表显示了全部 ").Append(cats.Count).Append(" 组, 这里的 rows 只是其中一部分。")
            If totalCats > cats.Count Then msg.Append("组数太多, 图上只显示了前 ").Append(cats.Count).Append(" 组。")
            msg.Append("回答里不用再列完整表格, 说结论和要点即可。")
            Return New ChartOutcome With {.Chart = chart, .Message = msg.ToString()}
        End Function

        Private Shared Function WhyNot(type As String, pivot As Boolean, multi As Boolean, count As Integer) As String
            Select Case type
                Case "pie", "donut" : Return If(multi, "只能画一个系列", "要求数值都不小于 0 且不全为 0")
                Case "combo" : Return If(pivot, "要两个不同的统计项, 不能用拆系列的结果", "要正好两个统计项, 现在是 " & count & " 个")
                Case "bar_stacked" : Return "要按两列分组 (横轴 + 系列)"
                Case "area" : Return "不适合多个不同的统计项"
                Case Else : Return "不适合这个结果"
            End Select
        End Function

        Private Shared Function NormalizeType(t As String) As String
            Dim s = If(t, "").Trim().ToLowerInvariant().Replace("-", "_")
            Select Case s
                Case "hbar", "horizontal_bar", "bar_h", "barh" : s = "bar_horizontal"
                Case "stacked_bar", "stack", "stacked" : s = "bar_stacked"
                Case "column" : s = "bar"
                Case "doughnut", "ring" : s = "donut"
                Case "mixed", "bar_line", "dual" : s = "combo"
            End Select
            If Not Types.Contains(s) Then Throw New ToolArgumentException("chart.type 只能是 " & String.Join("、", Types) & "。")
            Return s
        End Function

        ''' 按名字找结果里的列: 先认给模型的名字, 再认显示名和别名。没给返回 Nothing。
        Private Shared Function Resolve(t As JToken, candidates As List(Of ChartColumn), what As String) As ChartColumn
            If t Is Nothing OrElse t.Type = JTokenType.Null Then Return Nothing
            Dim name = CStr(t).Trim()
            If name = "" Then Return Nothing
            Dim eq As Func(Of String, Boolean) = Function(a As String) String.Equals(a, name, StringComparison.OrdinalIgnoreCase)
            Dim hit = candidates.Where(Function(c) eq(c.Label)).ToList()
            If hit.Count = 0 Then hit = candidates.Where(Function(c) eq(c.Display)).ToList()
            If hit.Count = 0 Then hit = candidates.Where(Function(c) c.Aliases.Any(eq)).ToList()
            If hit.Count = 1 Then Return hit(0)
            Dim avail = String.Join("、", candidates.Select(Function(c) c.Label))
            If hit.Count > 1 Then Throw New ToolArgumentException("chart." & what & " 写的 """ & name & """ 对应不止一列, 请写全: " & avail & "。")
            Throw New ToolArgumentException("统计结果里没有 """ & name & """ 这一列, chart." & what & " 可用: " & avail & "。")
        End Function

        ''' 分组值显示成文字: 日期按 yyyy-MM-dd, 空值为 (空)。
        Private Shared Function Label(v As Object) As String
            If v Is Nothing Then Return "(空)"
            Dim j = QueryEngine.ToJValue(v)
            If j.Type = JTokenType.Null Then Return "(空)"
            Return Convert.ToString(DirectCast(j, JValue).Value, CultureInfo.InvariantCulture)
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
End Namespace
