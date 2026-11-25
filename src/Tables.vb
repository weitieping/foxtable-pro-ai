Imports FoxtablePro.AI

''' AI 能读哪些表和列。没放行的表和列, AI 既看不到结构也查不到数据。
''' 可以按角色放行: 同一张表对不同角色放行不同的列, 对某个用户生效的各条规则取并集。
Public NotInheritable Class AITables

    Private ReadOnly _rules As New List(Of AITableRule)
    Private ReadOnly _order As New List(Of String)

    Friend Sub New()
    End Sub

    ''' 放行这些表的全部列 (只读: 查询、统计、看结构)。返回的规则可以再收窄,
    ''' 如 .Hide("身份证号")、.Roles("经理")。
    ''' 同一张表再次 Allow 且角色相同时, 后一条替换前一条。
    Public Function Allow(ParamArray tables() As String) As AITableRule
        If tables Is Nothing OrElse tables.Length = 0 Then Throw New ArgumentException("至少给一个表名。")
        Dim rule As New AITableRule()
        For Each name In tables
            Dim dt = Fx.DataTable(name)
            If dt Is Nothing Then Throw New ArgumentException("没有名为 """ & name & """ 的表。")
            Dim real = Fx.GetStr(dt, "Name")
            If Not _order.Contains(real) Then _order.Add(real)
            If Not rule.Tables.Contains(real) Then rule.Tables.Add(real)
        Next
        _rules.Add(rule)
        Return rule
    End Function

    ''' 放行读写: 除了查询, AI 还能新增、修改这张表的行 (不能删除)。每次写入前都弹确认卡片, 用户确认后才执行并保存。
    ''' 表事件里的校验照常生效: 被拦下的写入会告诉 AI 和用户。
    ''' 明细: 明细表名, 两表之间要有关联; 新增主表行时可以带上明细行, 关联列自动填。
    ''' 如 ai.Tables.AllowWrite("订单", 明细:="订单明细").Required("客户编号", "日期")
    Public Function AllowWrite(table As String, Optional 明细 As String = Nothing) As AITableRule
        Dim names = If(String.IsNullOrWhiteSpace(明细), New String() {table}, New String() {table, 明细})
        Dim rule = Allow(names)
        Dim detail As String = Nothing
        If rule.Tables.Count > 1 Then
            detail = rule.Tables(1)
            If RelationReader.Find(rule.Tables(0), detail) Is Nothing Then
                _rules.Remove(rule)
                Throw New ArgumentException("表 """ & rule.Tables(0) & """ 和 """ & detail & """ 之间没有关联, 不能作为明细表。")
            End If
        End If
        rule.MarkWritable(detail)
        Return rule
    End Function

    ''' 收回某些表的放行 (所有角色)。
    Public Sub Remove(ParamArray tables() As String)
        For Each name In tables
            Dim key = Resolve(name)
            If key Is Nothing Then Continue For
            For Each r In _rules
                r.Tables.Remove(key)
            Next
            _rules.RemoveAll(Function(r) r.Tables.Count = 0)
            _order.Remove(key)
        Next
    End Sub

    ''' 放行过的表名 (不分角色), 按放行顺序。
    Public ReadOnly Property Names As String()
        Get
            Return _order.ToArray()
        End Get
    End Property

    ''' 放行过的表个数 (不分角色)。
    Public ReadOnly Property Count As Integer
        Get
            Return _order.Count
        End Get
    End Property

    ''' 这个用户能看的表和各表的有效规则, 按放行顺序。
    Friend Function VisibleTo(who As Who) As List(Of TableAccess)
        Dim res As New List(Of TableAccess)
        For Each table In _order
            Dim applied As New List(Of AITableRule)
            Dim seen As New HashSet(Of String)
            ' 倒着看: 同一角色组合只认最后一条
            For i = _rules.Count - 1 To 0 Step -1
                Dim r = _rules(i)
                If Not r.Tables.Contains(table) OrElse Not seen.Add(r.RoleKey) Then Continue For
                If r.AppliesTo(who) Then applied.Insert(0, r)
            Next
            If applied.Count > 0 Then res.Add(New TableAccess(table, applied))
        Next
        Return res
    End Function

    ''' 规范成放行时的写法; 没放行时返回 Nothing。
    Friend Function Resolve(table As String) As String
        If table Is Nothing Then Return Nothing
        Return _order.FirstOrDefault(Function(n) String.Equals(n, table.Trim(), StringComparison.OrdinalIgnoreCase))
    End Function
End Class

''' 一次 Allow 放行的那几张表的列规则、说明和适用角色。
Public NotInheritable Class AITableRule

    Friend ReadOnly Tables As New List(Of String)
    Private _only As HashSet(Of String)
    Private ReadOnly _hidden As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private _roles As HashSet(Of String)
    Private _description As String = ""
    Private _writable As Boolean
    Private _detail As String
    Private ReadOnly _required As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _protected As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

    Friend Sub New()
    End Sub

    Friend Sub MarkWritable(detail As String)
        _writable = True
        _detail = detail
    End Sub

    ''' 新增时必须填的列 (狐表的列没有"必填"属性, 在这里声明)。只对 AllowWrite 的表有用。
    Public Function Required(ParamArray cols() As String) As AITableRule
        CheckColumns(cols)
        For Each c In cols
            _required.Add(c)
        Next
        Return Me
    End Function

    ''' AI 能看但不能改的列 (如订单号由系统生成)。只对 AllowWrite 的表有用。表达式列本来就不能改。
    Public Function Protect(ParamArray cols() As String) As AITableRule
        CheckColumns(cols)
        For Each c In cols
            _protected.Add(c)
        Next
        Return Me
    End Function

    Friend ReadOnly Property Writable As Boolean
        Get
            Return _writable
        End Get
    End Property

    ''' 明细表名; 没有时为 Nothing。
    Friend ReadOnly Property Detail As String
        Get
            Return _detail
        End Get
    End Property

    Friend Function CanWrite(col As String) As Boolean
        Return _writable AndAlso IsAllowed(col) AndAlso Not _protected.Contains(col)
    End Function

    Friend Function IsRequired(col As String) As Boolean
        Return _writable AndAlso _required.Contains(col)
    End Function

    ''' 只放行这些列, 其余列 AI 看不到。
    Public Function Columns(ParamArray cols() As String) As AITableRule
        CheckColumns(cols)
        _only = New HashSet(Of String)(cols, StringComparer.OrdinalIgnoreCase)
        Return Me
    End Function

    ''' 不放行这些列 (如身份证号、成本价)。
    Public Function Hide(ParamArray cols() As String) As AITableRule
        CheckColumns(cols)
        For Each c In cols
            _hidden.Add(c)
        Next
        Return Me
    End Function

    ''' 给 AI 看的表说明, 如"每张订单一行, 金额含税"。
    Public Function Describe(text As String) As AITableRule
        _description = If(text, "")
        Return Me
    End Function

    ''' 只对有这些角色之一的用户生效。不调用时对所有人生效。
    ''' 角色默认取狐表登录用户的 Roles, 也可以用 ProAI.SetUser 指定。
    Public Function Roles(ParamArray names() As String) As AITableRule
        Dim list = Who.SplitRoles(If(names, New String() {}))
        If list.Count = 0 Then Throw New ArgumentException("至少给一个角色名。")
        _roles = New HashSet(Of String)(list, StringComparer.OrdinalIgnoreCase)
        Return Me
    End Function

    ''' 给某张表的说明: AllowWrite 带的明细表不套用主表的说明。
    Friend Function DescriptionFor(table As String) As String
        If _detail IsNot Nothing AndAlso String.Equals(table, _detail, StringComparison.OrdinalIgnoreCase) Then Return ""
        Return _description
    End Function

    Friend Function IsAllowed(col As String) As Boolean
        If _hidden.Contains(col) Then Return False
        Return _only Is Nothing OrElse _only.Contains(col)
    End Function

    Friend Function AppliesTo(w As Who) As Boolean
        Return _roles Is Nothing OrElse w.HasAnyRole(_roles)
    End Function

    ''' 角色组合的键, 不限角色时为空。
    Friend ReadOnly Property RoleKey As String
        Get
            If _roles Is Nothing Then Return ""
            Return String.Join(",", _roles.Select(Function(r) r.ToLowerInvariant()).OrderBy(Function(r) r, StringComparer.Ordinal))
        End Get
    End Property

    ' 列名写错时当场报错, 免得开发者以为藏住了。一次放行多张表时, 列在其中任一张表里就算对
    Private Sub CheckColumns(cols() As String)
        For Each c In cols
            Dim found = False
            For Each t In Tables
                Dim dcs = DirectCast(Fx.Get(Fx.DataTable(t), "DataCols"), Foxtable.DataColCollection)
                If dcs.Contains(c) Then found = True : Exit For
            Next
            If Not found Then Throw New ArgumentException("表 """ & String.Join("、", Tables) & """ 里没有名为 """ & c & """ 的列。")
        Next
    End Sub
End Class

Namespace AI

    ''' 某个用户对一张表的有效放行: 对他生效的各条规则取并集。
    Friend NotInheritable Class TableAccess
        Public ReadOnly Name As String
        Private ReadOnly _rules As List(Of AITableRule)

        Public Sub New(name As String, rules As List(Of AITableRule))
            Me.Name = name
            _rules = rules
        End Sub

        Public Function IsAllowed(col As String) As Boolean
            Return _rules.Any(Function(r) r.IsAllowed(col))
        End Function

        ''' 有没有一条对他生效的规则放行了写入。
        Public ReadOnly Property Writable As Boolean
            Get
                Return _rules.Any(Function(r) r.Writable)
            End Get
        End Property

        Public Function CanWrite(col As String) As Boolean
            Return _rules.Any(Function(r) r.CanWrite(col))
        End Function

        Public Function IsRequired(col As String) As Boolean
            Return _rules.Any(Function(r) r.IsRequired(col))
        End Function

        ''' 这张表作为主表时的明细表 (去重)。
        Public ReadOnly Property Details As List(Of String)
            Get
                Return _rules.Where(Function(r) r.Writable AndAlso r.Detail IsNot Nothing AndAlso r.Tables(0) = Name).
                              Select(Function(r) r.Detail).Distinct().ToList()
            End Get
        End Property

        ''' 各条规则的说明, 去重后合并。
        Public ReadOnly Property Description As String
            Get
                Return String.Join(" ", _rules.Select(Function(r) r.DescriptionFor(Name).Trim()).Where(Function(d) d <> "").Distinct())
            End Get
        End Property
    End Class
End Namespace
