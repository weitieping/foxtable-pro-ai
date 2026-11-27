Imports System.Globalization

Namespace AI

    ''' 结果里的一列, 出图时按名字引用。
    Friend NotInheritable Class ChartColumn
        ''' 给模型的名字, 如 日期(月)、sum(金额)、金额。
        Public Label As String
        ''' 界面上的名字, 如 日期(月)、金额 合计。
        Public Display As String
        ''' 其他可以接受的写法 (列名、列标题、统计函数名等)。
        Public ReadOnly Aliases As New List(Of String)
        ''' 分组列 (统计结果里的; 明细、业务工具的表格没有分组列)。
        Public IsKey As Boolean
        ''' 值是日期 (按年月日、星期分组的也算): 横轴按时间先后排。
        Public IsTime As Boolean
        ''' 可选值 (列的列表项目): 漏斗图等按这个顺序排, 如订单状态的流程。
        Public Options As String()
        ''' 取自表里的哪一列: 画图前复核当前用户还能不能看。计数这类不对应某一列的为 Nothing。
        Public Source As String
    End Class

    ''' 一份可以画图的结果: 统计、明细查询或业务工具查出的表格。数字原样保存, 画图时只从这里取。
    Friend NotInheritable Class ChartSource
        ''' 给模型引用的编号, 如 r3 (登记进 ResultStore 时分配)。
        Public Id As String
        ''' 界面上的名字, 如 统计 订单。
        Public Title As String
        ''' 来自哪张表; 业务工具的结果为 Nothing。
        Public Table As String
        ''' 来自哪个业务工具 (工具名); 表的结果为 Nothing。
        Public Tool As String
        Public ReadOnly Columns As New List(Of ChartColumn)
        ''' 每行与 Columns 一一对应, 已按查询时的顺序排好。
        Public Rows As New List(Of Object())
        ''' 给了模型几行。
        Public ModelRows As Integer
        ''' 结果一共几行 (Rows 可能只是其中一部分, 如明细只查了前 20 行)。
        Public Total As Integer
        ''' 查询时指定了排序: 不再按数值重排。
        Public ExplicitOrder As Boolean
        ''' 只统计了已加载的行。
        Public PartialRows As Boolean
    End Class

    ''' 出图的结果: 给界面的图表数据, 给模型的一句话。
    Friend NotInheritable Class ChartOutcome
        ''' 给界面的图表; 没出图时为 Nothing。
        Public Chart As JObject
        ''' 给模型: 出了什么图, 或者为什么没出。
        Public Message As String
        ''' 没出图的原因 (给用户看, 不带给模型的指示)。
        Public Reason As String

        Public ReadOnly Property Failed As Boolean
            Get
                Return Chart Is Nothing
            End Get
        End Property
    End Class

    ''' 按模型的出图要求, 从结果里取数画图。模型只决定画法 (类型、横轴、数值、系列),
    ''' 数字一律由这里从结果里取, 图和表格里的数字一致。不碰狐表, 只处理已经查好的结果。
    Friend NotInheritable Class ChartBuilder

        ''' 图表最多显示多少组 (横轴上的点); 给模型的仍按 limit 截断。
        Public Const MAX_CATEGORIES As Integer = 500
        ''' 拆成多条系列时最多几条。
        Public Const MAX_SERIES As Integer = 20
        ''' 热力图纵轴最多几行。
        Private Const MAX_HEAT_ROWS As Integer = 60
        ''' 一份结果最多存多少行 (按两列分组时是 组数 × 系列数)。
        Public Const MAX_SOURCE_ROWS As Integer = 10000
        ''' 指标卡最多几项。
        Private Const MAX_KPIS As Integer = 4

        Public Shared ReadOnly Types As String() = {
            "bar", "bar_horizontal", "bar_stacked", "line", "area", "pie", "donut", "combo",
            "heatmap", "scatter", "funnel", "treemap", "radar", "waterfall", "pareto", "kpi"}

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
                Case "heatmap" : Return "热力图"
                Case "scatter" : Return "散点图"
                Case "funnel" : Return "漏斗图"
                Case "treemap" : Return "矩形树图"
                Case "radar" : Return "雷达图"
                Case "waterfall" : Return "瀑布图"
                Case "pareto" : Return "帕累托图"
                Case "kpi" : Return "指标卡"
                Case Else : Return type
            End Select
        End Function

        ''' 选图规则 (写进工具说明)。
        Public Const RULES As String =
            "选图: 随时间的变化趋势用 line 或 area; 各类别比大小、排名用 bar, 类别超过 8 个或名称较长用 bar_horizontal; " &
            "各部分占总体的比例用 pie 或 donut (超过 8 块系统自动把其余合并为""其他"", 有负数时不能用); " &
            "两个数量一起看 (如金额和单数) 用 combo, y 给两项; 按两列分组 (如月份×类别) 用 line 或 bar_stacked; " &
            "两个维度交叉看分布 (如月份×星期) 用 heatmap; 两个数量之间的关系 (如各客户的单数和金额) 用 scatter, y 给两项; " &
            "按流程各阶段的数量 (如各状态的订单数) 用 funnel, order 给流程顺序; 大小构成用 treemap; 几项指标的综合对比用 radar; " &
            "逐项增减累计到合计用 waterfall; 哪些少数项贡献了八成 (二八分析) 用 pareto; " &
            "一两个关键数字 (如实际与目标、完成率) 用 kpi, target 给目标值所在的列。" &
            "不出图: 结果只有一个数字 (指标卡除外)、只有一两组, 或用户只要数字。一次回答最多出一张图。"

        ''' 出图参数 (统计时附带的 chart 和 make_chart 共用)。
        Public Shared Function SpecProperties() As JObject
            Return New JObject From {
                {"type", New JObject From {{"type", "string"}, {"enum", New JArray(Types.Cast(Of Object)().ToArray())},
                                           {"description", "bar 柱状、bar_horizontal 横向柱状、bar_stacked 堆叠柱状、line 折线、area 面积、pie 饼图、donut 环形、" &
                                                           "combo 组合 (第一项柱状用左轴, 第二项折线用右轴)、heatmap 热力图 (x 为横轴, series 为纵轴)、" &
                                                           "scatter 散点 (y 的两项分别是横轴、纵轴的数值, x 为每个点的名字)、funnel 漏斗、treemap 矩形树图、radar 雷达、" &
                                                           "waterfall 瀑布 (逐项增减, 最后是合计)、pareto 帕累托 (柱状从大到小 + 累计占比折线)、kpi 指标卡"}}},
                {"x", New JObject From {{"type", "string"}, {"description", "横轴 (饼图为分块): 结果里的一列, 如 日期(月)、客户。不填为第一个分组列"}}},
                {"y", New JObject From {{"type", "array"}, {"items", New JObject From {{"type", "string"}}},
                                        {"description", "数值: 结果里的数字列, 如 sum(金额)。不填为全部统计项"}}},
                {"series", New JObject From {{"type", "string"}, {"description", "按两列分组时, 拆成多条系列 (热力图为纵轴) 的那一列, 如 产品类别。不填为另一个分组列"}}},
                {"order", New JObject From {{"type", "array"}, {"items", New JObject From {{"type", "string"}}},
                                            {"description", "横轴按这个顺序排, 如漏斗图的流程 [""下单"",""付款"",""发货"",""完成""]; 没列出的排在后面"}}},
                {"target", New JObject From {{"type", "string"}, {"description", "kpi 的目标值: 结果里的列名 (结果是 项目/值 两列时写项目名)"}}},
                {"target_result", New JObject From {{"type", "string"}, {"description", "kpi 的目标值在另一份结果里时, 那份结果的 result_id (如开发者工具返回的目标)"}}},
                {"title", New JObject From {{"type", "string"}, {"description", "图表标题, 简短; 不填自动生成"}}}}
        End Function

        ''' 统计工具上的 chart 参数。
        Public Shared Function Schema() As JObject
            Return New JObject From {
                {"type", "object"},
                {"description", "出图要求。结果适合看图时附上, 统计完直接显示图表, 图上的数字由系统从统计结果里取。" & RULES},
                {"properties", SpecProperties()},
                {"required", New JArray("type")}}
        End Function

        ''' 按出图要求画图。画不了时返回原因 (不抛异常), 统计结果照常给出。
        ''' lookup: 按 result_id 找本次对话里的另一份结果 (kpi 的 target_result 用), 没有时为 Nothing。
        Public Shared Function Build(spec As JToken, src As ChartSource, Optional lookup As Func(Of String, ChartSource) = Nothing) As ChartOutcome
            Try
                Return Make(spec, src, lookup)
            Catch ex As ToolArgumentException
                Return New ChartOutcome With {.Reason = ex.Message, .Message = "没有出图: " & ex.Message & " 结果照常给出; 要出图就改正 chart 后再调用一次。"}
            End Try
        End Function

        ''' 按出图要求画图。画不了时抛 ToolArgumentException。
        Public Shared Function Make(spec As JToken, src As ChartSource, Optional lookup As Func(Of String, ChartSource) = Nothing) As ChartOutcome
            Dim o = TryCast(spec, JObject)
            If o Is Nothing Then Throw New ToolArgumentException("chart 应是对象, 如 {""type"":""bar""}。")
            Dim type = NormalizeType(CStr(o("type")))
            If type = "kpi" Then Return BuildKpi(o, src, lookup)
            Return BuildSeries(o, type, src)
        End Function

        ''' 用户在表格卡片上点 "画图": 按数据形状自己选图。
        Public Shared Function Auto(src As ChartSource) As ChartOutcome
            Return Build(New JObject From {{"type", "auto"}}, src)
        End Function

        ''' 这份结果能不能画成图 (决定表格卡片上有没有 "画图")。
        Public Shared Function Chartable(src As ChartSource) As Boolean
            If src Is Nothing OrElse src.Rows.Count < 2 OrElse src.Columns.Count < 2 Then Return False
            If Not src.Columns.Any(Function(c) IsNumericColumn(src, c)) Then Return False
            Return Not Auto(src).Failed
        End Function

        '==============================================================
        ' 横轴 + 系列的图
        '==============================================================

        Private Shared Function BuildSeries(o As JObject, type As String, src As ChartSource) As ChartOutcome
            Dim cols = src.Columns
            Dim rows = src.Rows
            If rows.Count = 0 Then Throw New ToolArgumentException("结果是空的, 没有可以画的数据。")
            If rows.Count = 1 Then Throw New ToolArgumentException("结果只有一行, 直接用文字回答即可; 要突出这几个数字可以用 kpi 指标卡。")
            Dim numeric = cols.Where(Function(c) IsNumericColumn(src, c)).ToList()
            Dim keys = cols.Where(Function(c) c.IsKey).ToList()
            Dim avail = String.Join("、", cols.Select(Function(c) c.Label))

            ' 横轴: 给了就用; 否则第一个分组列, 没有分组列 (明细、业务工具的表格) 时第一个不是数字的列
            Dim x = Resolve(o("x"), cols, "x")
            If x Is Nothing Then x = If(keys.FirstOrDefault(), If(cols.FirstOrDefault(Function(c) Not numeric.Contains(c)), cols(0)))
            Dim ser = Resolve(o("series"), cols, "series")
            If ser Is x Then Throw New ToolArgumentException("series 和 x 不能是同一列。")
            If ser IsNot Nothing AndAlso numeric.Contains(ser) AndAlso Not ser.IsKey Then
                Throw New ToolArgumentException("series 应是分组列 (按它拆成多条系列), " & ser.Label & " 是数值。")
            End If
            If ser Is Nothing AndAlso keys.Count = 2 AndAlso keys.Contains(x) Then ser = keys.First(Function(k) k IsNot x)

            ' 数值
            Dim ys As New List(Of ChartColumn)
            Dim yArg = o("y")
            If yArg IsNot Nothing AndAlso yArg.Type = JTokenType.String Then yArg = New JArray(yArg)
            Dim yGiven = TypeOf yArg Is JArray AndAlso DirectCast(yArg, JArray).Count > 0
            If yGiven Then
                For Each t In DirectCast(yArg, JArray)
                    Dim c = Resolve(t, cols, "y")
                    If c Is Nothing OrElse ys.Contains(c) Then Continue For
                    If Not numeric.Contains(c) Then
                        Dim bad = rows.Select(Function(r) r(cols.IndexOf(c))).FirstOrDefault(Function(v) v IsNot Nothing)
                        Throw New ToolArgumentException(c.Label & " 不是数字" & If(bad Is Nothing, "", " (如 """ & Label(bad) & """)") & ", 不能当数值画图。")
                    End If
                    ys.Add(c)
                Next
            End If
            ' 散点图: 模型把横轴数值写在 x 上时, 换成 y 的第一项, 点的名字另找一列
            If type = "scatter" AndAlso ys.Count = 1 AndAlso numeric.Contains(x) AndAlso Not ys.Contains(x) Then
                ys.Insert(0, x)
                x = If(keys.FirstOrDefault(Function(k) Not ys.Contains(k)), cols.FirstOrDefault(Function(c) Not numeric.Contains(c)))
                If x Is Nothing Then Throw New ToolArgumentException("散点图要有一列做点的名字 (如 客户), 这份结果里没有。")
            End If
            ys.Remove(x)
            If ser IsNot Nothing Then ys.Remove(ser)
            If Not yGiven Then
                Dim cands = numeric.Where(Function(c) c IsNot x AndAlso c IsNot ser AndAlso Not c.IsKey).ToList()
                ' 统计结果默认画全部统计项; 明细和业务工具的表格默认只画第一个数字列 (散点、组合图要两列)
                If keys.Count > 0 Then
                    ys.AddRange(cands)
                Else
                    ys.AddRange(cands.Take(If(type = "scatter" OrElse type = "combo", 2, 1)))
                End If
            End If
            If ys.Count = 0 Then Throw New ToolArgumentException("没有可以画的数值列 (要数字)。结果里的列: " & avail & "。")
            If ser IsNot Nothing AndAlso ys.Count > 1 Then Throw New ToolArgumentException("拆成多条系列时只能画一个数值, 用 y 指定一项。")
            Dim xi = cols.IndexOf(x)
            Dim si = If(ser Is Nothing, -1, cols.IndexOf(ser))
            Dim y0 = cols.IndexOf(ys(0))

            ' 没指定类型 (用户点 "画图"): 按数据形状选
            If type = "auto" Then
                Dim labels = rows.Select(Function(r) Label(r(xi))).Distinct().ToList()
                If ser IsNot Nothing Then
                    type = If(x.IsTime, "line", "bar_stacked")
                ElseIf ys.Count = 2 Then
                    type = "combo"
                ElseIf ys.Count > 2 Then
                    type = If(x.IsTime, "line", "bar")
                ElseIf x.IsTime Then
                    type = "line"
                ElseIf labels.Count > 8 OrElse labels.Any(Function(l) l.Length > 6) Then
                    type = "bar_horizontal"
                Else
                    type = "bar"
                End If
            End If

            ' 横轴顺序: 给了 order 按它; 时间按先后; 比大小、占比没指定排序时按数值从大到小; 有可选值 (如状态) 按可选值的顺序
            Dim ordered As IEnumerable(Of Object()) = rows
            Dim custom = TextList(o("order"))
            Dim seqTypes = {"line", "area", "combo", "heatmap", "waterfall"}
            Dim rankTypes = {"bar", "bar_horizontal", "pie", "donut", "pareto", "treemap"}
            If custom.Count > 0 Then
                ordered = rows.OrderBy(Function(r) RankOf(custom, Label(r(xi))))
            ElseIf x.IsTime AndAlso (Not src.ExplicitOrder OrElse seqTypes.Contains(type) OrElse ser IsNot Nothing) Then
                ordered = rows.OrderBy(Function(r) r(xi), ValueComparer.Instance)
            ElseIf Not x.IsTime AndAlso Not src.ExplicitOrder Then
                Dim oneSeries = ser Is Nothing AndAlso ys.Count = 1
                If oneSeries AndAlso (rankTypes.Contains(type) OrElse (type = "funnel" AndAlso x.Options Is Nothing)) Then
                    ordered = rows.OrderByDescending(Function(r) r(y0), ValueComparer.Instance)
                ElseIf x.Options IsNot Nothing Then
                    Dim opts = x.Options.ToList()
                    ordered = rows.OrderBy(Function(r) RankOf(opts, Label(r(xi))))
                End If
            End If

            ' 取数: 每组 (横轴 × 系列) 只能有一行, 否则说不清画哪个数
            Dim cats As New List(Of Object)
            Dim catIndex As New Dictionary(Of String, Integer)
            Dim seriesKeys As New List(Of Object)
            Dim seriesSeen As New HashSet(Of String)
            Dim cells As New Dictionary(Of String, Object)
            For Each r In ordered
                Dim ck = Label(r(xi))
                Dim isNew = Not catIndex.ContainsKey(ck)
                If isNew Then
                    catIndex(ck) = cats.Count
                    cats.Add(r(xi))
                End If
                If ser IsNot Nothing Then
                    Dim sk = Label(r(si))
                    If seriesSeen.Add(sk) Then seriesKeys.Add(r(si))
                    Dim cell = ck & vbNullChar & sk
                    If cells.ContainsKey(cell) Then Throw Duplicate(x, ser, ck & " / " & sk, keys.Count)
                    cells(cell) = r(y0)
                Else
                    If Not isNew Then Throw Duplicate(x, Nothing, ck, keys.Count)
                    For Each y In ys
                        cells(ck & vbNullChar & y.Label) = r(cols.IndexOf(y))
                    Next
                End If
            Next
            Dim totalCats = cats.Count
            If cats.Count > MAX_CATEGORIES Then cats = cats.Take(MAX_CATEGORIES).ToList()
            If ser IsNot Nothing Then
                If ser.Options IsNot Nothing Then
                    Dim opts = ser.Options.ToList()
                    seriesKeys = seriesKeys.OrderBy(Function(s) RankOf(opts, Label(s))).ToList()
                Else
                    seriesKeys = seriesKeys.OrderBy(Function(s) s, ValueComparer.Instance).ToList()
                End If
                Dim maxSeries = If(type = "heatmap", MAX_HEAT_ROWS, MAX_SERIES)
                If seriesKeys.Count > maxSeries Then Throw New ToolArgumentException("按 " & ser.Label & " 拆出 " & seriesKeys.Count & " 条系列, 太多画不清; 换个分组或加条件缩小范围。")
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
            Dim nonNeg = allValues.All(Function(v) v >= 0) AndAlso allValues.Any(Function(v) v > 0)
            Dim n = cats.Count
            Dim types As New List(Of String)
            If pivot Then
                If series.Count <= MAX_SERIES Then
                    types.AddRange({"line", "bar", "bar_stacked", "bar_horizontal", "area"})
                    If n >= 3 AndAlso n <= 12 AndAlso series.Count <= 8 Then types.Add("radar")
                    If nonNeg Then types.Add("treemap")
                End If
                types.Add("heatmap")
            ElseIf multi Then
                If series.Count = 2 Then types.AddRange({"combo", "scatter"})
                types.AddRange({"bar", "bar_horizontal", "line"})
            Else
                types.AddRange({"bar", "bar_horizontal", "line", "area"})
                If nonNeg Then
                    types.AddRange({"pie", "donut"})
                    If n <= 30 Then types.Add("funnel")
                    types.AddRange({"treemap", "pareto"})
                End If
                If n <= 60 Then types.Add("waterfall")
                If n >= 3 AndAlso n <= 12 Then types.Add("radar")
            End If

            Dim fallback As String = Nothing
            If Not types.Contains(type) Then
                fallback = TypeName(type) & WhyNot(type, pivot, multi, series.Count, n, nonNeg)
                type = If(x.IsTime AndAlso types.Contains("line"), "line", types.First(Function(t) t <> "combo"))
                fallback &= ", 已改为" & TypeName(type)
            End If

            Dim title = TitleOf(o)
            If title = "" Then
                If pivot Then
                    title = ys(0).Display & " · 按 " & x.Display & "、" & ser.Display
                ElseIf type = "scatter" Then
                    title = ys(1).Display & " 与 " & ys(0).Display
                Else
                    title = String.Join("、", ys.Select(Function(y) y.Display)) & " · 按 " & x.Display
                End If
            End If

            Dim chart As New JObject From {
                {"type", type},
                {"types", New JArray(types.Cast(Of Object)().ToArray())},
                {"title", title},
                {"x", x.Display},
                {"categories", New JArray(cats.Select(Function(c) Label(c)).Cast(Of Object)().ToArray())},
                {"series", series}}
            If pivot Then
                chart("y") = ys(0).Display
                chart("group") = ser.Display
            End If
            Dim notes As New List(Of String)
            If totalCats > cats.Count Then notes.Add("共 " & totalCats & " 组, 图上只显示前 " & cats.Count & " 组")
            If src.Total > rows.Count Then notes.Add("共 " & src.Total & " 行, 图上只画了查出的 " & rows.Count & " 行")
            If src.PartialRows Then notes.Add("只统计了已加载的行")
            If notes.Count > 0 Then chart("note") = String.Join("; ", notes)

            Dim msg As New Text.StringBuilder()
            msg.Append("已在界面上显示").Append(TypeName(type)).Append(", ").Append(cats.Count).Append(If(type = "scatter", " 个点", " 组"))
            If pivot Then msg.Append(" × ").Append(series.Count).Append(" 条系列")
            msg.Append("。")
            If fallback IsNot Nothing Then msg.Append(fallback).Append("。")
            If cats.Count > src.ModelRows Then msg.Append("图表显示了全部 ").Append(cats.Count).Append(" 组, 工具结果里的 rows 只是其中一部分。")
            If totalCats > cats.Count Then msg.Append("组数太多, 图上只显示了前 ").Append(cats.Count).Append(" 组。")
            If src.Total > rows.Count Then msg.Append("结果共 ").Append(src.Total).Append(" 行, 图上只画了查出的 ").Append(rows.Count).Append(" 行。")
            msg.Append("回答里不用再列完整表格, 说结论和要点即可。")
            Return New ChartOutcome With {.Chart = chart, .Message = msg.ToString()}
        End Function

        Private Shared Function Duplicate(x As ChartColumn, ser As ChartColumn, sample As String, keyCount As Integer) As ToolArgumentException
            Dim what = If(ser Is Nothing, "横轴 " & x.Label, "横轴 " & x.Label & " 和系列 " & ser.Label)
            Dim fix As String
            If keyCount > 2 Then
                fix = "结果按 " & keyCount & " 列分组, 画图时最多两列 (横轴 + 系列); 重新统计时只按要画的列分组。"
            ElseIf keyCount > 0 Then
                fix = "用 x、series 指定要画的分组列。"
            Else
                fix = "先用 aggregate 按要画的列分组统计, 再画图。"
            End If
            Return New ToolArgumentException(what & " 有重复的组 (如 """ & sample & """), 每组应只有一行。" & fix)
        End Function

        Private Shared Function WhyNot(type As String, pivot As Boolean, multi As Boolean, count As Integer, cats As Integer, nonNeg As Boolean) As String
            Select Case type
                Case "pie", "donut", "treemap", "pareto"
                    If type = "treemap" AndAlso pivot Then Return "要求数值都不小于 0 且不全为 0"
                    Return If(multi, "只能画一个系列", "要求数值都不小于 0 且不全为 0")
                Case "funnel" : Return If(multi, "只能画一个系列", If(nonNeg, "最多 30 组", "要求数值都不小于 0 且不全为 0"))
                Case "combo" : Return If(pivot, "要两个不同的统计项, 不能用拆系列的结果", "要正好两个统计项, 现在是 " & count & " 个")
                Case "scatter" : Return If(pivot, "要两个数值列, 不能用拆系列的结果", "要正好两个数值列 (y 给两项), 现在是 " & count & " 个")
                Case "bar_stacked" : Return "要按两列分组 (横轴 + 系列)"
                Case "heatmap" : Return "要按两列分组 (横轴 × 纵轴), 用 series 指定纵轴那一列"
                Case "radar" : Return If(cats < 3 OrElse cats > 12, "要 3～12 组, 现在是 " & cats & " 组", "不适合多个不同的统计项")
                Case "waterfall" : Return If(multi, "只能画一个系列", "最多 60 组")
                Case "area" : Return "不适合多个不同的统计项"
                Case Else : Return If(pivot AndAlso count > MAX_SERIES, "系列太多 (" & count & " 条)", "不适合这个结果")
            End Select
        End Function

        '==============================================================
        ' 指标卡
        '==============================================================

        Private NotInheritable Class KpiItem
            Public Name As String
            Public Value As Object
        End Class

        ''' 指标卡: 一行结果里的几个数, 或 项目/值 两列的结果里的几项。有目标值时显示完成率。
        Private Shared Function BuildKpi(o As JObject, src As ChartSource, lookup As Func(Of String, ChartSource)) As ChartOutcome
            Dim yArg = o("y")
            If yArg IsNot Nothing AndAlso yArg.Type = JTokenType.String Then yArg = New JArray(yArg)
            Dim names = TextList(yArg)
            Dim targetName = If(CStr(o("target")), "").Trim()
            Dim targetResult = If(CStr(o("target_result")), "").Trim()
            Dim tsrc = src
            If targetResult <> "" Then
                If targetName = "" Then Throw New ToolArgumentException("给了 target_result, 还要用 target 写目标值是那份结果里的哪一列。")
                If lookup Is Nothing Then Throw New ToolArgumentException("统计时附带的 chart 不能引用别的结果; 用 make_chart 画指标卡, target_result 写目标所在的 result_id。")
                tsrc = lookup(targetResult)
            End If

            Dim target As KpiItem = Nothing
            If targetName <> "" Then target = FindKpi(KpiItems(tsrc), targetName, tsrc)
            Dim items = KpiItems(src)
            Dim picked As New List(Of KpiItem)
            If names.Count > 0 Then
                For Each nm In names
                    Dim it = FindKpi(items, nm, src)
                    If Not picked.Contains(it) Then picked.Add(it)
                Next
                If picked.Count > MAX_KPIS Then Throw New ToolArgumentException("指标卡最多 " & MAX_KPIS & " 项。")
                If target IsNot Nothing AndAlso picked.Count > 1 Then Throw New ToolArgumentException("有目标值时指标卡只显示一项, y 只给一项。")
            Else
                picked.AddRange(items.Where(Function(it) target Is Nothing OrElse Not (tsrc Is src AndAlso it.Name = target.Name)).Take(If(target Is Nothing, MAX_KPIS, 1)))
            End If
            If picked.Count = 0 Then Throw New ToolArgumentException("结果里没有可以显示的数字。")

            Dim kpis As New JArray()
            Dim msg As New Text.StringBuilder("已在界面上显示指标卡: ")
            For Each it In picked
                Dim k As New JObject From {{"name", it.Name}, {"value", QueryEngine.ToJValue(it.Value)}}
                msg.Append(it.Name).Append(" ").Append(Num(it.Value))
                If target IsNot Nothing Then
                    k("target") = QueryEngine.ToJValue(target.Value)
                    k("targetName") = target.Name
                    msg.Append(", 目标 (").Append(target.Name).Append(") ").Append(Num(target.Value))
                    If it.Value IsNot Nothing AndAlso target.Value IsNot Nothing Then
                        Dim t = Convert.ToDecimal(target.Value, CultureInfo.InvariantCulture)
                        If t <> 0 Then
                            Dim rate = Math.Round(Convert.ToDecimal(it.Value, CultureInfo.InvariantCulture) / t, 4)
                            k("rate") = rate
                            msg.Append(", 完成率 ").Append((rate * 100D).ToString("0.0", CultureInfo.InvariantCulture)).Append("%")
                        End If
                    End If
                End If
                msg.Append("; ")
                kpis.Add(k)
            Next
            msg.Length -= 2
            msg.Append("。回答里说明结论即可, 数字以这里为准。")

            Dim title = TitleOf(o)
            If title = "" Then title = If(target IsNot Nothing, picked(0).Name & " 完成情况", String.Join("、", picked.Select(Function(p) p.Name)))
            Dim chart As New JObject From {
                {"type", "kpi"}, {"types", New JArray("kpi")}, {"title", title},
                {"x", "指标"}, {"categories", New JArray()}, {"series", New JArray()}, {"kpis", kpis}}
            If src.PartialRows OrElse tsrc.PartialRows Then chart("note") = "只统计了已加载的行"
            Return New ChartOutcome With {.Chart = chart, .Message = msg.ToString()}
        End Function

        ''' 指标卡能取的数: 只有一行的结果取各数字列; 项目/值 两列的结果 (如业务工具返回的几个数) 取各行。
        Private Shared Function KpiItems(src As ChartSource) As List(Of KpiItem)
            Dim numeric = src.Columns.Where(Function(c) IsNumericColumn(src, c)).ToList()
            Dim res As New List(Of KpiItem)
            If src.Rows.Count = 1 Then
                For Each c In numeric
                    res.Add(New KpiItem With {.Name = c.Display, .Value = src.Rows(0)(src.Columns.IndexOf(c))})
                Next
                Return res
            End If
            Dim names = src.Columns.Where(Function(c) Not numeric.Contains(c)).ToList()
            If names.Count = 1 AndAlso numeric.Count >= 1 AndAlso src.Rows.Count <= 20 Then
                Dim ni = src.Columns.IndexOf(names(0))
                Dim vi = src.Columns.IndexOf(numeric(0))
                For Each r In src.Rows
                    res.Add(New KpiItem With {.Name = Label(r(ni)), .Value = r(vi)})
                Next
                Return res
            End If
            Throw New ToolArgumentException("指标卡要只有一行的结果 (如不分组的 aggregate), 或 项目/值 两列的结果; 结果 " & src.Id & " 有 " & src.Rows.Count & " 行。")
        End Function

        Private Shared Function FindKpi(items As List(Of KpiItem), name As String, src As ChartSource) As KpiItem
            ' 一行的结果: 按列名 (含别名) 找
            If src.Rows.Count = 1 Then
                Dim numeric = src.Columns.Where(Function(x) IsNumericColumn(src, x)).ToList()
                Dim c = Resolve(New JValue(name), src.Columns, "y")
                If Not numeric.Contains(c) Then Throw New ToolArgumentException(c.Label & " 不是数字, 不能放进指标卡。")
                Return items(numeric.IndexOf(c))
            End If
            Dim hit = items.FirstOrDefault(Function(it) String.Equals(it.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            If hit Is Nothing Then Throw New ToolArgumentException("结果 " & src.Id & " 里没有 """ & name & """, 可用: " & String.Join("、", items.Select(Function(it) it.Name)) & "。")
            Return hit
        End Function

        Private Shared Function Num(v As Object) As String
            If v Is Nothing Then Return "(空)"
            Return Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString("#,0.##", CultureInfo.InvariantCulture)
        End Function

        '==============================================================
        ' 公用
        '==============================================================

        Private Shared Function TitleOf(o As JObject) As String
            Dim title = If(CStr(o("title")), "").Trim()
            If title.Length > 40 Then title = title.Substring(0, 40)
            Return title
        End Function

        Private Shared Function NormalizeType(t As String) As String
            Dim s = If(t, "").Trim().ToLowerInvariant().Replace("-", "_")
            Select Case s
                Case "auto" : Return s
                Case "hbar", "horizontal_bar", "bar_h", "barh" : s = "bar_horizontal"
                Case "stacked_bar", "stack", "stacked" : s = "bar_stacked"
                Case "column" : s = "bar"
                Case "doughnut", "ring" : s = "donut"
                Case "mixed", "bar_line", "dual" : s = "combo"
                Case "heat", "heat_map" : s = "heatmap"
                Case "scatter_plot", "bubble", "point" : s = "scatter"
                Case "tree_map", "rect_tree" : s = "treemap"
                Case "spider" : s = "radar"
                Case "bridge" : s = "waterfall"
                Case "pareto_chart", "8020", "abc" : s = "pareto"
                Case "card", "metric", "number", "indicator", "gauge" : s = "kpi"
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
            Throw New ToolArgumentException("结果里没有 """ & name & """ 这一列, " & what & " 可用: " & avail & "。")
        End Function

        Private Shared Function TextList(t As JToken) As List(Of String)
            Dim res As New List(Of String)
            If t Is Nothing OrElse t.Type = JTokenType.Null Then Return res
            If t.Type <> JTokenType.Array Then t = New JArray(t)
            For Each x In DirectCast(t, JArray)
                Dim s = If(x.Type = JTokenType.Null, "", x.ToString()).Trim()
                If s <> "" Then res.Add(s)
            Next
            Return res
        End Function

        ''' 在给定顺序里的位置; 没列出的排在后面 (OrderBy 稳定, 保持原来的先后)。
        Private Shared Function RankOf(order As List(Of String), label As String) As Integer
            Dim i = order.FindIndex(Function(s) String.Equals(s, label, StringComparison.OrdinalIgnoreCase))
            Return If(i < 0, order.Count, i)
        End Function

        ''' 分组值显示成文字: 日期按 yyyy-MM-dd, 空值为 (空)。
        Friend Shared Function Label(v As Object) As String
            If v Is Nothing Then Return "(空)"
            Dim j = QueryEngine.ToJValue(v)
            If j.Type = JTokenType.Null Then Return "(空)"
            Return Convert.ToString(DirectCast(j, JValue).Value, CultureInfo.InvariantCulture)
        End Function

        ''' 这一列的值都是数字 (空值除外), 且至少有一个。
        Friend Shared Function IsNumericColumn(src As ChartSource, c As ChartColumn) As Boolean
            Dim i = src.Columns.IndexOf(c)
            Dim any = False
            For Each r In src.Rows
                Dim v = r(i)
                If v Is Nothing Then Continue For
                If Not IsNumber(v) Then Return False
                any = True
            Next
            Return any
        End Function

        Friend Shared Function IsNumber(v As Object) As Boolean
            Select Case Type.GetTypeCode(v.GetType())
                Case TypeCode.Byte, TypeCode.SByte, TypeCode.Int16, TypeCode.Int32, TypeCode.Int64,
                     TypeCode.UInt16, TypeCode.UInt32, TypeCode.UInt64, TypeCode.Decimal, TypeCode.Double, TypeCode.Single
                    Return True
            End Select
            Return False
        End Function
    End Class
End Namespace
