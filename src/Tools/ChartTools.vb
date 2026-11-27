Namespace AI

    ''' 本次对话里查出的、可以画图的结果, 按 result_id (r1、r2…) 引用。
    ''' 只在这段对话里有效: 新对话、换了用户时清空 (ChatSession.ClearLocked), 编号不复用。线程安全。
    Friend NotInheritable Class ResultStore
        ''' 最多留几份; 更早的要重新查询。
        Private Const MAX_KEEP As Integer = 30

        Private ReadOnly _lock As New Object
        Private ReadOnly _items As New List(Of ChartSource)
        Private _seq As Integer

        ''' 登记一份结果, 返回它的编号。
        Public Function Add(src As ChartSource) As String
            SyncLock _lock
                _seq += 1
                src.Id = "r" & _seq
                _items.Add(src)
                If _items.Count > MAX_KEEP Then _items.RemoveAt(0)
                Return src.Id
            End SyncLock
        End Function

        Public Function Find(id As String) As ChartSource
            If String.IsNullOrWhiteSpace(id) Then Return Nothing
            SyncLock _lock
                Return _items.FirstOrDefault(Function(s) String.Equals(s.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
            End SyncLock
        End Function

        Public Sub Clear()
            SyncLock _lock
                _items.Clear()
            End SyncLock
        End Sub

        ''' 现在能引用的结果, 如 "r3 统计 订单、r4 查询 订单" (最近的在前, 最多 8 份)。
        Public Function Describe() As String
            SyncLock _lock
                Return String.Join("、", Enumerable.Reverse(_items).Take(8).Select(Function(s) s.Id & " " & s.Title))
            End SyncLock
        End Function
    End Class

    ''' 画图前复核: 结果来自的表、列和业务工具, 当前用户这一轮还能不能用。图表只用已经查出的结果, 不扩大数据权限。
    Friend NotInheritable Class ChartAccess
        Private ReadOnly _tables As List(Of TableInfo)
        Private ReadOnly _tools As HashSet(Of String)

        ''' tables: 这个用户本轮能查的表和列; tools: 本轮能用的业务工具名。
        Public Sub New(tables As List(Of TableInfo), tools As IEnumerable(Of String))
            _tables = tables
            _tools = New HashSet(Of String)(tools, StringComparer.OrdinalIgnoreCase)
        End Sub

        ''' 按编号取结果并复核。取不到或不能用时抛 ToolArgumentException, 原因交给模型。
        Public Function [Get](results As ResultStore, id As String) As ChartSource
            If String.IsNullOrWhiteSpace(id) Then Throw New ToolArgumentException("要给 result: 工具结果里的 result_id, 如 r2。")
            Dim src = If(results Is Nothing, Nothing, results.Find(id))
            If src Is Nothing Then
                Dim avail = If(results Is Nothing, "", results.Describe())
                Throw New ToolArgumentException("本次对话里没有结果 " & id.Trim() & " (只能引用这段对话里查出的结果; 新对话或换了用户后, 之前的结果都不能再用)。" &
                                                If(avail = "", "现在没有可以画图的结果, 先用工具查询。", "可以用的: " & avail & "; 都不合适就先重新查询。"))
            End If
            Check(src)
            Return src
        End Function

        Public Sub Check(src As ChartSource)
            If src.Table IsNot Nothing Then
                Dim t = _tables.FirstOrDefault(Function(x) String.Equals(x.Name, src.Table, StringComparison.OrdinalIgnoreCase))
                If t Is Nothing Then Throw New ToolArgumentException("结果 " & src.Id & " 来自表 " & src.Table & ", 当前用户已不能查看这张表, 不能画图。")
                For Each c In src.Columns
                    If c.Source IsNot Nothing AndAlso t.Column(c.Source) Is Nothing Then
                        Throw New ToolArgumentException("结果 " & src.Id & " 用到的列 " & c.Source & " 当前用户已不能查看, 不能画图。")
                    End If
                Next
            ElseIf src.Tool IsNot Nothing Then
                If Not _tools.Contains(src.Tool) Then Throw New ToolArgumentException("结果 " & src.Id & " 来自的业务工具当前用户已不能使用, 不能画图。")
            End If
        End Sub
    End Class

    ''' 出图工具: 用本次对话里任意一份查出的结果画图 (明细、业务工具的表格、换一种图时不用重新统计)。
    Friend NotInheritable Class ChartTools
        Implements IToolSet

        Public Const MAKE_CHART As String = "make_chart"

        Private ReadOnly _access As ChartAccess

        ''' access: 开发者关掉图表时为 Nothing, 没有这个工具。
        Public Sub New(access As ChartAccess)
            _access = access
        End Sub

        Public Function Owns(name As String) As Boolean Implements IToolSet.Owns
            Return name = MAKE_CHART AndAlso _access IsNot Nothing
        End Function

        Public Function Definitions() As List(Of JObject) Implements IToolSet.Definitions
            Dim list As New List(Of JObject)
            If Not Owns(MAKE_CHART) Then Return list
            Dim props = ChartBuilder.SpecProperties()
            Dim all As New JObject From {{"result", New JObject From {{"type", "string"}, {"description", "要画的结果: 工具结果里的 result_id, 如 r2"}}}}
            For Each p In props.Properties()
                all.Add(p.Name, p.Value)
            Next
            list.Add(ReadTools.Fn(MAKE_CHART,
                "用本次对话里已经查出的结果画图, 图上的数字由系统从那份结果里取。用于: 明细查询、业务工具返回的表格; " &
                "用户要换一种图 (如 ""换成饼图"") 时直接引用原来的结果, 不用重新统计。统计时可以直接在 aggregate 里附上 chart, 不必再调它。" & ChartBuilder.RULES,
                all, "result", "type"))
            Return list
        End Function

        Public Function Label(name As String, args As JObject) As String Implements IToolSet.Label
            Dim t = If(args Is Nothing, "", CStr(args("type")))
            Return "画图" & If(String.IsNullOrWhiteSpace(t), "", " · " & ChartBuilder.TypeName(t.Trim().ToLowerInvariant()))
        End Function

        Public Function Execute(name As String, args As JObject, ctx As ToolContext) As ToolOutcome Implements IToolSet.Execute
            If Not Owns(name) Then Throw New ToolArgumentException("当前不支持出图, 用表格和文字回答。")
            Dim results = If(ctx Is Nothing, Nothing, ctx.Results)
            Dim src = _access.Get(results, CStr(args("result")))
            Dim c = ChartBuilder.Make(args, src, Function(id) _access.Get(results, id))
            Dim res As New JObject From {{"result", src.Id}, {"chart", c.Message}}
            Return New ToolOutcome(res, ChartBuilder.TypeName(CStr(c.Chart("type"))) & " · " & src.Title) With {.Chart = c.Chart}
        End Function
    End Class
End Namespace
