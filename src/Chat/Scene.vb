Imports System.Globalization
Imports System.Text

Namespace AI

    ''' 谁在问: 用户名和角色。默认取狐表登录用户, ProAI.SetUser 可以指定 (如移动端代答)。
    Friend NotInheritable Class Who
        Public ReadOnly Name As String
        Public ReadOnly Roles As List(Of String)

        Public Sub New(name As String, roles As IEnumerable(Of String))
            Me.Name = If(name, "")
            Me.Roles = SplitRoles(roles)
        End Sub

        ''' 换人判定用: 用户名或角色变了就是另一个人。
        Public ReadOnly Property Key As String
            Get
                Return Name & "|" & String.Join(",", Roles.Select(Function(r) r.ToLowerInvariant()).OrderBy(Function(r) r, StringComparer.Ordinal))
            End Get
        End Property

        Public Function HasAnyRole(roles As IEnumerable(Of String)) As Boolean
            Return roles.Any(Function(r) Me.Roles.Contains(r, StringComparer.OrdinalIgnoreCase))
        End Function

        ''' 角色名拆开去重: 每项里还可以用逗号、分号分隔 (狐表的 User.Roles 就是 "经理,财务")。
        Public Shared Function SplitRoles(items As IEnumerable(Of String)) As List(Of String)
            Dim res As New List(Of String)
            If items Is Nothing Then Return res
            For Each entry In items
                If entry Is Nothing Then Continue For
                For Each r In entry.Split({","c, "，"c, ";"c, "；"c, "|"c}, StringSplitOptions.RemoveEmptyEntries)
                    Dim t = r.Trim()
                    If t <> "" AndAlso Not res.Contains(t, StringComparer.OrdinalIgnoreCase) Then res.Add(t)
                Next
            Next
            Return res
        End Function

        ''' 狐表当前登录用户。只在界面线程上调用。
        Public Shared Function FromFoxtable() As Who
            Try
                Dim u = Fx.Global("User")
                Return New Who(Fx.GetStr(u, "Name"), {TryCast(Fx.TryGet(u, "Roles"), String)})
            Catch
                Return New Who("", Nothing)
            End Try
        End Function
    End Class

    ''' 当前现场: 面板在哪个窗口、当前表的当前行和选中行。每轮开始时在界面线程上读。
    ''' 行数据只给这个用户放行的表和列。
    ''' 现场随用户点选随时会变, 放在用户消息里而不放进系统提示词: 系统提示词不变, 模型服务的前缀缓存才能命中整段历史。
    Friend NotInheritable Class Scene

        Private Const MAX_SELECTED_ROWS As Integer = 10

        ''' 谁在问, 只给用户名 (放进系统提示词: 换人时对话会清空, 一段对话里不变)。
        ''' 不给角色: 权限已经体现在能用的表、列、工具和技能里, 模型不需要知道角色。
        Public Shared Function WhoText(who As Who) As String
            Return If(who.Name = "", "(未登录)", who.Name)
        End Function

        ''' 面板所在窗口和用户正在看的表、行; 都没有时为空串。
        Public Shared Function Describe(windowName As String, tables As List(Of TableInfo)) As String
            Dim sb As New StringBuilder()
            If Not String.IsNullOrEmpty(windowName) Then sb.Append("- 面板所在窗口: ").AppendLine(windowName)
            Try
                AppendCurrentTable(sb, tables)
            Catch
                ' 主界面没有表或读不到时略过
            End Try
            Return sb.ToString()
        End Function

        Private Shared Sub AppendCurrentTable(sb As StringBuilder, tables As List(Of TableInfo))
            Dim t = Fx.Global("CurrentTable")
            If t Is Nothing Then Return
            Dim dt = Fx.Get(t, "DataTable")
            If dt Is Nothing Then Return
            Dim info = tables.FirstOrDefault(Function(x) x.Name = Fx.GetStr(dt, "Name"))
            If info Is Nothing Then Return ' 没放行的表, 连表名也不说
            Dim tableName = Fx.GetStr(t, "Name")
            sb.Append("- 用户正在看的表: ").Append(info.Display)
            If tableName <> info.Name Then sb.Append(" (窗口里的表 ").Append(tableName).Append(")")
            sb.AppendLine()
            Dim rows = Fx.Get(t, "Rows")
            Dim count = CInt(Fx.Get(rows, "Count"))
            If count = 0 Then
                sb.AppendLine("  (表里没有行)")
                Return
            End If
            Dim top = CInt(Fx.Get(t, "TopPosition"))
            Dim bottom = CInt(Fx.Get(t, "BottomPosition"))
            Dim pos = CInt(Fx.Get(t, "Position"))
            If top > bottom Then
                Dim x = top : top = bottom : bottom = x
            End If
            top = Math.Max(0, top) : bottom = Math.Min(count - 1, bottom)
            Dim selected = bottom - top + 1
            If selected > 1 Then
                sb.Append("  选中了 ").Append(selected).Append(" 行")
                If selected > MAX_SELECTED_ROWS Then sb.Append(", 下面只列前 ").Append(MAX_SELECTED_ROWS).Append(" 行")
                sb.AppendLine(":")
                For i = top To Math.Min(bottom, top + MAX_SELECTED_ROWS - 1)
                    sb.Append("  - ").AppendLine(RowText(RowAt(rows, i), info))
                Next
            ElseIf pos >= 0 AndAlso pos < count Then
                sb.Append("  当前行: ").AppendLine(RowText(RowAt(rows, pos), info))
            End If
        End Sub

        Private Shared Function RowAt(rows As Object, index As Integer) As Object
            Return rows.GetType().GetProperty("Item", {GetType(Integer)}).GetValue(rows, New Object() {index})
        End Function

        ' 只列放行的列, 空值略去
        Private Shared Function RowText(row As Object, info As TableInfo) As String
            Dim dr = Fx.Get(row, "DataRow")
            Dim parts As New List(Of String)
            For Each c In info.Columns
                Dim v As Object
                Try
                    v = Fx.Item(dr, c.Name)
                Catch
                    Continue For
                End Try
                If v Is Nothing OrElse TypeOf v Is DBNull Then Continue For
                Dim s As String
                If TypeOf v Is Date Then
                    Dim d = DirectCast(v, Date)
                    s = d.ToString(If(d.TimeOfDay = TimeSpan.Zero, "yyyy-MM-dd", "yyyy-MM-dd HH:mm"), CultureInfo.InvariantCulture)
                ElseIf TypeOf v Is IFormattable Then
                    s = DirectCast(v, IFormattable).ToString(Nothing, CultureInfo.InvariantCulture)
                Else
                    s = v.ToString()
                End If
                ' 值是用户录的数据: 换行会打乱现场的格式, 尖括号可能拼出 </当前现场> 冒充用户的话
                s = s.Replace(vbCrLf, " ").Replace(vbLf, " ").Replace(vbCr, " ").Replace("<", "‹").Replace(">", "›").Trim()
                If s = "" Then Continue For
                If s.Length > 60 Then s = s.Substring(0, 60) & "…"
                parts.Add(c.Name & "=" & s)
            Next
            Return String.Join(", ", parts)
        End Function
    End Class
End Namespace
