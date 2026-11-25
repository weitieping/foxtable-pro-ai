Imports FoxtablePro.AI

''' AI 能填哪些窗口 (第 3 档)。AI 打开开发者现有的录入窗口, 把内容填进去但不保存, 用户核对后点窗口里原有的保存按钮。
''' 适合拍照入表: 用户发一张单据照片, AI 认出内容填进录入窗口。
Public NotInheritable Class AIForms

    Private ReadOnly _rules As New List(Of AIFormRule)

    Friend Sub New()
    End Sub

    ''' 放行这些窗口。返回的规则可以再加说明和角色, 如 .Describe("录入销售单").Roles("销售")。
    Public Function Allow(ParamArray forms() As String) As AIFormRule
        If forms Is Nothing OrElse forms.Length = 0 Then Throw New ArgumentException("至少给一个窗口名。")
        Dim rule As New AIFormRule()
        For Each name In forms
            Dim real = FormReader.RealName(name)
            If real Is Nothing Then Throw New ArgumentException("没有名为 """ & name & """ 的窗口。")
            If Not rule.Forms.Contains(real) Then rule.Forms.Add(real)
        Next
        _rules.Add(rule)
        Return rule
    End Function

    ''' 收回某些窗口的放行。
    Public Sub Remove(ParamArray forms() As String)
        For Each name In forms
            For Each r In _rules
                r.Forms.RemoveAll(Function(f) String.Equals(f, name, StringComparison.OrdinalIgnoreCase))
            Next
        Next
        _rules.RemoveAll(Function(r) r.Forms.Count = 0)
    End Sub

    ''' 放行过的窗口名 (不分角色)。
    Public ReadOnly Property Names As String()
        Get
            Return _rules.SelectMany(Function(r) r.Forms).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        End Get
    End Property

    ''' 这个用户能填的窗口和说明。
    Friend Function VisibleTo(who As Who) As List(Of FormAccess)
        Dim res As New List(Of FormAccess)
        For Each r In _rules
            If Not r.AppliesTo(who) Then Continue For
            For Each f In r.Forms
                Dim cur = res.FirstOrDefault(Function(x) String.Equals(x.Name, f, StringComparison.OrdinalIgnoreCase))
                If cur Is Nothing Then
                    res.Add(New FormAccess With {.Name = f, .Description = r.Description})
                ElseIf r.Description <> "" AndAlso Not cur.Description.Contains(r.Description) Then
                    cur.Description = (cur.Description & " " & r.Description).Trim()
                End If
            Next
        Next
        Return res
    End Function
End Class

''' 一次 Allow 放行的窗口的说明和适用角色。
Public NotInheritable Class AIFormRule

    Friend ReadOnly Forms As New List(Of String)
    Private _roles As HashSet(Of String)

    Friend Sub New()
    End Sub

    Friend Property Description As String = ""

    ''' 给 AI 看的窗口说明, 如"录入销售单: 先填客户, 明细里一行一个产品"。
    Public Function Describe(text As String) As AIFormRule
        Description = If(text, "").Trim()
        Return Me
    End Function

    ''' 只对有这些角色之一的用户生效。
    Public Function Roles(ParamArray names() As String) As AIFormRule
        Dim list = Who.SplitRoles(If(names, New String() {}))
        If list.Count = 0 Then Throw New ArgumentException("至少给一个角色名。")
        _roles = New HashSet(Of String)(list, StringComparer.OrdinalIgnoreCase)
        Return Me
    End Function

    Friend Function AppliesTo(w As Who) As Boolean
        Return _roles Is Nothing OrElse w.HasAnyRole(_roles)
    End Function
End Class

Namespace AI

    Friend NotInheritable Class FormAccess
        Public Name As String
        Public Description As String = ""
    End Class
End Namespace
