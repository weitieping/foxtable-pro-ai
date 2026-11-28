Imports System.Text

Namespace AI

    ''' 一列的信息, 从狐表公开接口读出 (只读放行的列)。
    Friend NotInheritable Class ColumnInfo
        Public Name As String
        Public Caption As String
        Public DataType As Type
        Public Kind As ColumnKind
        Public MaxLength As Integer
        Public Decimals As Integer
        Public Expression As String
        Public ComboList As String
        Public Unique As Boolean
        ''' AI 能写 (表放行了写入, 列没被保护, 不是表达式列)。
        Public Writable As Boolean
        ''' 新增时必填 (开发者用 Required 声明)。
        Public Required As Boolean

        ''' 给模型看的类型名。
        Public ReadOnly Property KindName As String
            Get
                Select Case Kind
                    Case ColumnKind.Text : Return "文本"
                    Case ColumnKind.Integer : Return "整数"
                    Case ColumnKind.Number : Return "数值"
                    Case ColumnKind.Date : Return "日期"
                    Case ColumnKind.Boolean : Return "逻辑"
                    Case Else : Return "其他"
                End Select
            End Get
        End Property

        ''' 显示用的名字: 有标题时用标题。
        Public ReadOnly Property Display As String
            Get
                Return If(String.IsNullOrEmpty(Caption), Name, Caption)
            End Get
        End Property

        ''' 一行简述, 如 "金额(数值, 标题:订单金额, 表达式列)"。
        Public Function Brief() As String
            Dim parts As New List(Of String) From {KindName}
            If Not String.IsNullOrEmpty(Caption) AndAlso Caption <> Name Then parts.Add("标题:" & Caption)
            If Not String.IsNullOrEmpty(Expression) Then parts.Add("表达式列")
            If Unique Then parts.Add("唯一")
            If Required Then parts.Add("必填")
            If Not String.IsNullOrEmpty(ComboList) Then parts.Add("可选值:" & ShortList(ComboList, 12))
            Return Name & "(" & String.Join(", ", parts) & ")"
        End Function

        ''' describe_table 里的一列, 如 "金额(数值, 标题:订单金额, 2位小数, 必填)"。比逐列的 JSON 对象省一半左右的字。
        ''' tableWritable: 表放行了写入时才标只读列 (只读表的列都不能写, 不必逐列标)。
        Public Function Detail(tableWritable As Boolean) As String
            Dim parts As New List(Of String) From {KindName}
            If Not String.IsNullOrEmpty(Caption) AndAlso Caption <> Name Then parts.Add("标题:" & Caption)
            If Kind = ColumnKind.Text AndAlso MaxLength > 0 Then parts.Add("最长" & MaxLength)
            If Kind = ColumnKind.Number AndAlso Decimals > 0 Then parts.Add(Decimals & "位小数")
            If Not String.IsNullOrEmpty(Expression) Then parts.Add("表达式:" & Expression)
            If Unique Then parts.Add("唯一")
            If Required Then parts.Add("必填")
            If tableWritable AndAlso Not Writable Then parts.Add("只读")
            If Not String.IsNullOrEmpty(ComboList) Then parts.Add("可选值:" & String.Join("/", SplitList(ComboList)))
            Return Name & "(" & String.Join(", ", parts) & ")"
        End Function

        Private Shared Function SplitList(s As String) As String()
            Return s.Split({"|"c}, StringSplitOptions.RemoveEmptyEntries)
        End Function

        Private Shared Function ShortList(s As String, max As Integer) As String
            Dim items = SplitList(s)
            If items.Length <= max Then Return String.Join("/", items)
            Return String.Join("/", items.Take(max)) & "/…(共" & items.Length & "项)"
        End Function
    End Class

    Friend Enum ColumnKind
        Text
        [Integer]
        Number
        [Date]
        [Boolean]
        Other
    End Enum

    ''' 一张放行表的结构。
    Friend NotInheritable Class TableInfo
        Public Name As String
        Public Caption As String
        Public Description As String
        Public Columns As New List(Of ColumnInfo)
        Public Relations As New List(Of String)
        ''' 设了加载条件或只加载前几行: 内存里只有部分数据。
        Public PartiallyLoaded As Boolean
        Public LoadedRows As Integer
        ''' 放行了写入。
        Public Writable As Boolean
        ''' 作为主表时的明细表 (新增时可以一起提交)。
        Public Details As New List(Of DetailLink)
        ''' 有没有 _Identify 列: 有才能"在狐表中打开"。
        Public HasIdentify As Boolean

        ''' AI 能写的列: 不含表达式列和受保护的列。
        Public ReadOnly Property WritableColumns As List(Of ColumnInfo)
            Get
                Return Columns.Where(Function(c) c.Writable).ToList()
            End Get
        End Property

        ''' 有写入工具可用: 放行了写入, 且至少有一列能写 (与 WriteTools 的判断一致)。
        Public ReadOnly Property CanWrite As Boolean
            Get
                Return Writable AndAlso Columns.Any(Function(c) c.Writable)
            End Get
        End Property

        Public Function Column(name As String) As ColumnInfo
            If name Is Nothing Then Return Nothing
            name = name.Trim()
            Return If(Columns.FirstOrDefault(Function(c) String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)),
                      Columns.FirstOrDefault(Function(c) String.Equals(c.Caption, name, StringComparison.OrdinalIgnoreCase)))
        End Function

        Public ReadOnly Property Display As String
            Get
                Return If(String.IsNullOrEmpty(Caption) OrElse Caption = Name, Name, Name & "(" & Caption & ")")
            End Get
        End Property

        Public Function ToJson() As JObject
            Dim o As New JObject From {{"table", Name}}
            If Not String.IsNullOrEmpty(Caption) AndAlso Caption <> Name Then o("caption") = Caption
            If Not String.IsNullOrEmpty(Description) Then o("description") = Description
            o("rows") = LoadedRows
            If PartiallyLoaded Then o("note") = "只加载了部分数据, 查询和统计只针对已加载的行。"
            o("columns") = New JArray(Columns.Select(Function(c) c.Detail(Writable)).Cast(Of Object)().ToArray())
            If Relations.Count > 0 Then o("relations") = New JArray(Relations.Cast(Of Object)().ToArray())
            If Writable Then
                o("writable") = True
                If Details.Count > 0 Then o("details") = New JArray(Details.Select(Function(d) d.Child).Cast(Of Object)().ToArray())
            End If
            Return o
        End Function
    End Class

    ''' 从狐表读出放行表的结构。只在界面线程上调用。
    Friend Module SchemaReader

        ''' 这个用户能看的表和列。
        Public Function Read(tables As AITables, who As Who) As List(Of TableInfo)
            Dim res As New List(Of TableInfo)
            Dim accesses = tables.VisibleTo(who)
            For Each access In accesses
                Dim info = ReadTable(access.Name, access)
                If info IsNot Nothing Then res.Add(info)
            Next
            AddRelations(res)
            ' 明细: 两张表都可写才算
            For Each access In accesses
                Dim master = Find(res, access.Name)
                If master Is Nothing OrElse Not master.Writable Then Continue For
                For Each d In access.Details
                    Dim child = Find(res, d)
                    Dim link = RelationReader.Find(master.Name, d)
                    If child IsNot Nothing AndAlso child.Writable AndAlso link IsNot Nothing Then master.Details.Add(link)
                Next
            Next
            Return res
        End Function

        Private Function ReadTable(name As String, rule As TableAccess) As TableInfo
            Dim dt = Fx.DataTable(name)
            If dt Is Nothing Then Return Nothing
            Dim info As New TableInfo With {
                .Name = Fx.GetStr(dt, "Name"),
                .Caption = Fx.GetStr(dt, "Caption"),
                .Description = rule.Description,
                .Writable = rule.Writable}
            Dim uiColumns = UiCols(info.Name)
            For Each dc As Object In DirectCast(Fx.Get(dt, "DataCols"), IEnumerable)
                Dim colName = Fx.GetStr(dc, "Name")
                If Not rule.IsAllowed(colName) Then Continue For
                Dim col = ColumnOf(dc, uiColumns)
                If col Is Nothing Then Continue For ' 二进制列对模型没用
                col.Writable = info.Writable AndAlso rule.CanWrite(colName) AndAlso col.Expression = "" AndAlso
                               Not colName.StartsWith("_", StringComparison.Ordinal) AndAlso col.Kind <> ColumnKind.Other
                col.Required = col.Writable AndAlso rule.IsRequired(colName)
                info.Columns.Add(col)
            Next
            info.LoadedRows = ToInt(Fx.TryGet(Fx.Get(dt, "DataRows"), "Count"))
            Dim bt = TryCast(Fx.TryGet(dt, "BaseTable"), System.Data.DataTable)
            info.HasIdentify = bt IsNot Nothing AndAlso bt.Columns.Contains("_Identify")
            Dim filter = TryCast(Fx.TryGet(dt, "LoadFilter"), String)
            Dim top = TryCast(Fx.TryGet(dt, "LoadTop"), String)
            info.PartiallyLoaded = Not String.IsNullOrWhiteSpace(filter) OrElse Not String.IsNullOrWhiteSpace(top)
            Return info
        End Function

        ''' 一列的信息; 二进制列返回 Nothing。uiColumns: 主界面表的 Cols (取列表项目), 可以为 Nothing。
        Public Function ColumnOf(dc As Object, uiColumns As Object) As ColumnInfo
            Dim colName = Fx.GetStr(dc, "Name")
            Dim t = TryCast(Fx.Get(dc, "DataType"), Type)
            If t Is GetType(Byte()) Then Return Nothing
            Dim col As New ColumnInfo With {
                .Name = colName,
                .Caption = Fx.GetStr(dc, "Caption"),
                .DataType = t,
                .Kind = KindOf(t),
                .MaxLength = ToInt(Fx.TryGet(dc, "MaxLength")),
                .Decimals = ToInt(Fx.TryGet(dc, "Decimals")),
                .Expression = If(TryCast(Fx.TryGet(dc, "Expression"), String), ""),
                .Unique = Equals(Fx.TryGet(dc, "Unique"), True)}
            If uiColumns IsNot Nothing Then col.ComboList = ComboListOf(uiColumns, colName)
            Return col
        End Function

        ''' 一张表全部列的信息 (不看放行规则; 填窗口用)。
        Public Function AllColumns(table As String) As List(Of ColumnInfo)
            Dim res As New List(Of ColumnInfo)
            Dim dt = Fx.DataTable(table)
            If dt Is Nothing Then Return res
            Dim ui = UiCols(table)
            For Each dc As Object In DirectCast(Fx.Get(dt, "DataCols"), IEnumerable)
                Dim c = ColumnOf(dc, ui)
                If c IsNot Nothing Then res.Add(c)
            Next
            Return res
        End Function

        Public Function KindOf(t As Type) As ColumnKind
            If t Is Nothing Then Return ColumnKind.Other
            Select Case Type.GetTypeCode(t)
                Case TypeCode.String, TypeCode.Char : Return ColumnKind.Text
                Case TypeCode.Byte, TypeCode.SByte, TypeCode.Int16, TypeCode.Int32, TypeCode.Int64,
                     TypeCode.UInt16, TypeCode.UInt32, TypeCode.UInt64 : Return ColumnKind.Integer
                Case TypeCode.Decimal, TypeCode.Double, TypeCode.Single : Return ColumnKind.Number
                Case TypeCode.DateTime : Return ColumnKind.Date
                Case TypeCode.Boolean : Return ColumnKind.Boolean
                Case Else : Return If(t Is GetType(Guid), ColumnKind.Text, ColumnKind.Other)
            End Select
        End Function

        ' 列表项目设在主界面表的列上 (Tables(表).Cols(列).ComboList); 表没在主界面打开时取不到
        Private Function UiCols(table As String) As Object
            Try
                Return Fx.Get(Fx.Item(Fx.Global("Tables"), table), "Cols")
            Catch
                Return Nothing
            End Try
        End Function

        Private Function ComboListOf(cols As Object, colName As String) As String
            Try
                Return If(TryCast(Fx.Get(Fx.Item(cols, colName), "ComboList"), String), "")
            Catch
                Return ""
            End Try
        End Function

        ' 两端都放行的关联才告诉模型, 写成 "订单.客户编号 → 客户.编号" (子表列 → 父表列)
        Private Sub AddRelations(tables As List(Of TableInfo))
            Dim rels As IEnumerable
            Try
                rels = TryCast(Fx.Global("Relations"), IEnumerable)
            Catch
                Return
            End Try
            If rels Is Nothing Then Return
            For Each r As Object In rels
                Try
                    Dim parent = Find(tables, Fx.GetStr(Fx.Get(r, "ParentTable"), "Name"))
                    Dim child = Find(tables, Fx.GetStr(Fx.Get(r, "ChildTable"), "Name"))
                    If parent Is Nothing OrElse child Is Nothing Then Continue For
                    Dim pc = ColNames(Fx.Get(r, "ParentColumns"))
                    Dim cc = ColNames(Fx.Get(r, "ChildColumns"))
                    If Not pc.All(Function(c) parent.Column(c) IsNot Nothing) OrElse Not cc.All(Function(c) child.Column(c) IsNot Nothing) Then Continue For
                    Dim text = child.Name & "." & String.Join("+", cc) & " → " & parent.Name & "." & String.Join("+", pc)
                    child.Relations.Add(text)
                    If parent IsNot child Then parent.Relations.Add(text)
                Catch
                End Try
            Next
        End Sub

        Private Function ColNames(cols As Object) As List(Of String)
            Dim res As New List(Of String)
            For Each c As Object In DirectCast(cols, IEnumerable)
                res.Add(Fx.GetStr(c, "Name"))
            Next
            Return res
        End Function

        Private Function Find(tables As List(Of TableInfo), name As String) As TableInfo
            Return tables.FirstOrDefault(Function(t) t.Name = name)
        End Function

        Private Function ToInt(v As Object) As Integer
            If v Is Nothing Then Return 0
            Try
                Return Convert.ToInt32(v)
            Catch
                Return 0
            End Try
        End Function

        ''' 按模型给的表名找表: 表名或标题; 照抄了 list_tables 的 "表名(标题)" 也认。找不到时为 Nothing。
        Public Function FindTable(tables As IEnumerable(Of TableInfo), name As String) As TableInfo
            If String.IsNullOrWhiteSpace(name) Then Return Nothing
            Dim n = name.Trim()
            Dim hit = PickTable(tables, n)
            If hit Is Nothing Then
                Dim i = n.IndexOfAny({"("c, "（"c})
                If i > 0 Then hit = PickTable(tables, n.Substring(0, i).Trim())
            End If
            Return hit
        End Function

        Private Function PickTable(tables As IEnumerable(Of TableInfo), n As String) As TableInfo
            Return If(tables.FirstOrDefault(Function(x) String.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)),
                      tables.FirstOrDefault(Function(x) String.Equals(x.Caption, n, StringComparison.OrdinalIgnoreCase)))
        End Function

        ''' list_tables 的结果: 每张表一行, 如 "订单(销售订单) [可写, 明细表: 订单明细]: 说明"。
        ''' 列、关联、只加载了部分数据由 describe_table 和查询结果给出。
        Public Function Listing(tables As List(Of TableInfo)) As List(Of String)
            Dim res As New List(Of String)
            For Each t In tables
                Dim sb As New StringBuilder(t.Display)
                If t.CanWrite Then
                    sb.Append(" [可写")
                    If t.Details.Count > 0 Then sb.Append(", 明细表: ").Append(String.Join("、", t.Details.Select(Function(d) d.Child)))
                    sb.Append("]")
                End If
                If Not String.IsNullOrWhiteSpace(t.Description) Then
                    sb.Append(": ").Append(String.Join(" ", t.Description.Split({vbCr(0), vbLf(0)}, StringSplitOptions.RemoveEmptyEntries).Select(Function(s) s.Trim())))
                End If
                res.Add(sb.ToString())
            Next
            Return res
        End Function
    End Module

    ''' 主表和明细表之间的关联: 明细的 ChildCols 取主表 ParentCols 的值。
    Friend NotInheritable Class DetailLink
        Public Parent As String
        Public Child As String
        Public ParentCols As New List(Of String)
        Public ChildCols As New List(Of String)
    End Class

    ''' 读狐表的表间关联。只在界面线程上调用。
    Friend Module RelationReader

        ''' parent → child 的关联; 没有时返回 Nothing。
        Public Function Find(parent As String, child As String) As DetailLink
            Dim rels As IEnumerable
            Try
                rels = TryCast(Fx.Global("Relations"), IEnumerable)
            Catch
                Return Nothing
            End Try
            If rels Is Nothing Then Return Nothing
            For Each r As Object In rels
                Try
                    Dim p = Fx.GetStr(Fx.Get(r, "ParentTable"), "Name")
                    Dim c = Fx.GetStr(Fx.Get(r, "ChildTable"), "Name")
                    If Not String.Equals(p, parent, StringComparison.OrdinalIgnoreCase) OrElse
                       Not String.Equals(c, child, StringComparison.OrdinalIgnoreCase) Then Continue For
                    Dim link As New DetailLink With {.Parent = p, .Child = c}
                    For Each x As Object In DirectCast(Fx.Get(r, "ParentColumns"), IEnumerable)
                        link.ParentCols.Add(Fx.GetStr(x, "Name"))
                    Next
                    For Each x As Object In DirectCast(Fx.Get(r, "ChildColumns"), IEnumerable)
                        link.ChildCols.Add(Fx.GetStr(x, "Name"))
                    Next
                    Return link
                Catch
                End Try
            Next
            Return Nothing
        End Function
    End Module
End Namespace
