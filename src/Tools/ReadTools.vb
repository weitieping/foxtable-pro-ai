Namespace AI

    ''' 内置只读工具: 列出可查的表、看表结构、查询明细、统计、列出和读技能库。只能碰这个用户放行的表、列和技能文件。
    ''' 有哪些表、表有哪些列、技能目录都不放进系统提示词, 由模型用 list_tables、describe_table、list_skills 去查。
    Friend NotInheritable Class ReadTools
        Implements IToolSet

        Public Const LIST_TABLES As String = "list_tables"
        Public Const DESCRIBE As String = "describe_table"
        Public Const QUERY As String = "query_rows"
        Public Const AGGREGATE As String = "aggregate"
        Public Const LIST_SKILLS As String = "list_skills"
        Public Const READ_SKILL As String = "read_skill"
        Private Const MAX_SKILL_CHARS As Integer = 20000

        Private ReadOnly _tables As List(Of TableInfo)
        Private ReadOnly _maxRows As Integer
        Private ReadOnly _skills As SkillCatalog
        Private ReadOnly _charts As Boolean
        Private ReadOnly _access As ChartAccess

        ''' tables: 本轮开始时读出的放行表结构; skills: 本轮这个用户能看的技能库; charts: 统计时能不能附带出图;
        ''' access: 指标卡引用别的结果 (target_result) 时复核权限用。
        Public Sub New(tables As List(Of TableInfo), maxRows As Integer, skills As SkillCatalog, Optional charts As Boolean = False,
                       Optional access As ChartAccess = Nothing)
            _tables = tables
            _maxRows = Math.Max(1, maxRows)
            _skills = skills
            _charts = charts
            _access = access
        End Sub

        ''' 本轮有没有这个工具: 没有可查的表就没有表工具, 没有技能文件就没有 read_skill, 技能目录为空就没有 list_skills。
        Public Function Owns(name As String) As Boolean Implements IToolSet.Owns
            If name = LIST_SKILLS Then Return _skills IsNot Nothing AndAlso _skills.Entries.Count > 0
            If name = READ_SKILL Then Return _skills IsNot Nothing AndAlso Not _skills.IsEmpty
            Return _tables.Count > 0 AndAlso (name = LIST_TABLES OrElse name = DESCRIBE OrElse name = QUERY OrElse name = AGGREGATE)
        End Function

        '==============================================================
        ' 定义 (OpenAI 函数格式, 原样交给 LLMCaller.AddJsonTool)
        '==============================================================

        ''' 筛选条件的参数定义 (查询、统计、修改共用)。
        Public Shared Function WhereSchema(description As String) As JObject
            Dim cond = New JObject From {
                {"type", "object"},
                {"properties", New JObject From {
                    {"column", New JObject From {{"type", "string"}, {"description", "列名"}}},
                    {"op", New JObject From {{"type", "string"}, {"enum", New JArray(QueryEngine.OPS.Split(" "c).Cast(Of Object)().ToArray())}}},
                    {"value", New JObject From {{"type", New JArray("string", "number", "boolean")},
                                                {"description", "比较值。日期写 2026-03-05; 写 2026-03 或 2026 表示整月、整年"}}},
                    {"values", New JObject From {{"type", "array"}, {"items", New JObject From {{"type", New JArray("string", "number")}}},
                                                 {"description", "in / not_in 的候选值; between 的 [下限, 上限] (含两端)"}}},
                    {"or", New JObject From {{"type", "array"}, {"items", New JObject From {{"type", "object"}}},
                                             {"description", "任一满足即可的一组条件, 每项格式同本条件。用了 or 就不填 column/op"}}}}}}
            Return New JObject From {{"type", "array"}, {"items", cond}, {"description", description}}
        End Function

        ''' 表名参数。不列枚举: 表名由 list_tables 给出, 不在每个工具定义里重复一遍; 给错时报错信息里列出可用的表。
        Public Shared Function TableParam(description As String) As JObject
            Return New JObject From {{"type", "string"}, {"description", description}}
        End Function

        Public Function Definitions() As List(Of JObject) Implements IToolSet.Definitions
            Dim tableProp = TableParam("表名 (list_tables 列出的)")
            Dim where = WhereSchema("筛选条件, 须全部满足。文本比较不分大小写。")

            Dim list As New List(Of JObject)
            If Owns(LIST_SKILLS) Then
                list.Add(Fn(LIST_SKILLS, "列出技能库的目录: 各业务模块口径、术语、流程文件的路径和简介。", New JObject()))
            End If
            If Owns(READ_SKILL) Then
                list.Add(Fn(READ_SKILL,
                    "读项目技能库里的文件 (业务口径、术语、流程)。",
                    New JObject From {{"path", New JObject From {{"type", "string"}, {"description", "list_skills 列出的路径, 如 销售/SKILL.md; 也可以是技能正文里引用的文件"}}}},
                    "path"))
            End If
            If _tables.Count = 0 Then Return list
            list.Add(Fn(LIST_TABLES, "列出当前用户可以查询的表: 表名(标题)、是否可写、明细表、说明。", New JObject()))

            list.Add(Fn(DESCRIBE,
                "查看一张表的列 (类型、标题、可选值、表达式) 和关联。",
                New JObject From {{"table", tableProp}}, "table"))

            list.Add(Fn(QUERY,
                "查明细行, 最多返回 " & _maxRows & " 行。",
                New JObject From {
                    {"table", tableProp},
                    {"columns", New JObject From {{"type", "array"}, {"items", New JObject From {{"type", "string"}}}, {"description", "要返回的列, 不填返回全部列"}}},
                    {"where", where},
                    {"order_by", New JObject From {{"type", "array"}, {"description", "排序, 按先后"},
                        {"items", New JObject From {{"type", "object"}, {"properties", New JObject From {
                            {"column", New JObject From {{"type", "string"}}},
                            {"desc", New JObject From {{"type", "boolean"}, {"description", "true 为降序"}}}}}}}}},
                    {"limit", New JObject From {{"type", "integer"}, {"description", "最多返回几行, 默认 20"}}}},
                "table"))

            Dim aggProps = New JObject From {
                    {"table", tableProp},
                    {"group_by", New JObject From {{"type", "array"}, {"description", "分组列, 最多 3 个"},
                        {"items", New JObject From {{"type", "object"}, {"properties", New JObject From {
                            {"column", New JObject From {{"type", "string"}}},
                            {"date_part", New JObject From {{"type", "string"}, {"enum", New JArray("year", "quarter", "month", "day", "weekday")},
                                                            {"description", "日期列按年/季/月/日/星期分组"}}}}}, {"required", New JArray("column")}}}}},
                    {"metrics", New JObject From {{"type", "array"}, {"description", "统计项, 不填为计数"},
                        {"items", New JObject From {{"type", "object"}, {"properties", New JObject From {
                            {"func", New JObject From {{"type", "string"}, {"enum", New JArray("count", "count_distinct", "sum", "avg", "min", "max")}}},
                            {"column", New JObject From {{"type", "string"}, {"description", "count 可不填 (数行数)"}}}}}, {"required", New JArray("func")}}}}},
                    {"where", where},
                    {"order_by", New JObject From {{"type", "object"}, {"description", "排序"}, {"properties", New JObject From {
                        {"by", New JObject From {{"type", "string"}, {"description", "分组列名, 或统计项如 sum(金额)、count"}}},
                        {"desc", New JObject From {{"type", "boolean"}}}}}}},
                    {"limit", New JObject From {{"type", "integer"}, {"description", "最多返回几组, 默认 50。不给时图表显示全部组"}}}}
            If _charts Then aggProps("chart") = ChartBuilder.Schema()
            list.Add(Fn(AGGREGATE,
                "分组统计: 计数、去重计数、合计、平均、最小、最大。不分组时对全部符合条件的行统计。" &
                If(_charts, "附上 chart 时统计完直接出图。", "") &
                "按日期的年/季/月/日/星期分组时, 首尾之间没有数据的时间会补上 (计数、合计为 0, 平均等为空)。",
                aggProps, "table"))
            Return list
        End Function

        Friend Shared Function Fn(name As String, description As String, props As JObject, ParamArray required() As String) As JObject
            Dim parameters As New JObject From {{"type", "object"}, {"properties", props}}
            ' 没有必填参数时不给 required: 有的模型服务不认空数组
            If required.Length > 0 Then parameters("required") = New JArray(required.Cast(Of Object)().ToArray())
            Return New JObject From {
                {"type", "function"},
                {"function", New JObject From {
                    {"name", name},
                    {"description", description},
                    {"parameters", parameters}}}}
        End Function

        '==============================================================
        ' 执行
        '==============================================================

        ''' 界面上显示的进度, 如 "查询 订单"。
        Public Function Label(name As String, args As JObject) As String Implements IToolSet.Label
            If name = LIST_SKILLS Then Return "查看技能目录"
            If name = READ_SKILL Then Return "查阅业务说明 " & If(args Is Nothing, "", CStr(args("path")))
            Dim t = TableOf(args, throwIfMissing:=False)
            Dim target = If(t Is Nothing, "", " " & t.Display)
            Select Case name
                Case LIST_TABLES : Return "查看可查询的表"
                Case DESCRIBE : Return "查看表结构" & target
                Case QUERY : Return "查询" & target
                Case AGGREGATE : Return "统计" & target
                Case Else : Return name
            End Select
        End Function

        Public Function Execute(name As String, args As JObject, ctx As ToolContext) As ToolOutcome Implements IToolSet.Execute
            Dim lookup As Func(Of String, ChartSource) = Nothing
            If _access IsNot Nothing AndAlso ctx IsNot Nothing AndAlso ctx.Results IsNot Nothing Then lookup = Function(id) _access.Get(ctx.Results, id)
            Return UiThread.Run(Function() ExecuteOnUi(name, args, lookup))
        End Function

        ''' 执行工具。只在界面线程上调用。参数错误抛 ToolArgumentException。
        Private Function ExecuteOnUi(name As String, args As JObject, lookup As Func(Of String, ChartSource)) As ToolOutcome
            If Not Owns(name) Then Throw New ToolArgumentException("没有名为 " & name & " 的工具。")
            If name = LIST_SKILLS Then
                Dim skills = SkillLibrary.Listing(_skills)
                Return New ToolOutcome(New JObject From {{"skills", New JArray(skills.Cast(Of Object)().ToArray())}}, skills.Count & " 个技能")
            End If
            If name = READ_SKILL Then Return ReadSkill(If(args Is Nothing, Nothing, CStr(args("path"))))
            If name = LIST_TABLES Then
                Dim lines = SchemaReader.Listing(_tables)
                Return New ToolOutcome(New JObject From {{"tables", New JArray(lines.Cast(Of Object)().ToArray())}}, lines.Count & " 张表")
            End If
            Dim t = TableOf(args, throwIfMissing:=True)
            Select Case name
                Case DESCRIBE
                    Return New ToolOutcome(t.ToJson(), t.Columns.Count & " 列")
                Case QUERY
                    Return New QueryEngine(t).Query(args, _maxRows)
                Case AGGREGATE
                    Return New QueryEngine(t).Aggregate(args, Math.Max(_maxRows, 200), _charts, lookup)
                Case Else
                    Throw New ToolArgumentException("没有工具 " & name)
            End Select
        End Function

        Private Function ReadSkill(path As String) As ToolOutcome
            Dim text = SkillLibrary.Read(_skills, path)
            Dim res As New JObject From {{"path", path}}
            If text.Length > MAX_SKILL_CHARS Then
                res("content") = text.Substring(0, MAX_SKILL_CHARS)
                res("note") = "文件太长, 只给了前 " & MAX_SKILL_CHARS & " 字。"
            Else
                res("content") = text
            End If
            Return New ToolOutcome(res, text.Length & " 字")
        End Function

        Private Function TableOf(args As JObject, throwIfMissing As Boolean) As TableInfo
            Dim name = If(args Is Nothing, Nothing, CStr(args("table")))
            Dim t = SchemaReader.FindTable(_tables, name)
            If t Is Nothing AndAlso throwIfMissing Then
                Throw New ToolArgumentException(If(name, "(未给)") & " 不是可查询的表。可查询的表: " & String.Join("、", _tables.Select(Function(x) x.Name)))
            End If
            Return t
        End Function
    End Class
End Namespace
