Namespace AI

    ''' 一组工具。Execute 在后台线程上调用: 读写狐表对象由工具自己用 UiThread.Run 切回界面线程,
    ''' 等用户确认这类阻塞放在界面线程之外。
    Friend Interface IToolSet
        Function Owns(name As String) As Boolean
        Function Definitions() As List(Of JObject)
        ''' 界面上显示的进度, 如 "查询 订单"。
        Function Label(name As String, args As JObject) As String
        ''' 参数错误抛 ToolArgumentException, 原因会交给模型。
        Function Execute(name As String, args As JObject, ctx As ToolContext) As ToolOutcome
    End Interface

    ''' 本轮的全部工具。
    Friend NotInheritable Class Toolbox
        Private ReadOnly _sets As New List(Of IToolSet)

        Public Sub Add(s As IToolSet)
            If s IsNot Nothing Then _sets.Add(s)
        End Sub

        Public Function Definitions() As List(Of JObject)
            Return _sets.SelectMany(Function(s) s.Definitions()).ToList()
        End Function

        Private Function Find(name As String) As IToolSet
            Return _sets.FirstOrDefault(Function(s) s.Owns(name))
        End Function

        Public Function Owns(name As String) As Boolean
            Return Find(name) IsNot Nothing
        End Function

        Public Function Label(name As String, args As JObject) As String
            Dim s = Find(name)
            If s Is Nothing Then Return name
            Try
                Return s.Label(name, args)
            Catch
                Return name
            End Try
        End Function

        Public Function Execute(name As String, args As JObject, ctx As ToolContext) As ToolOutcome
            Dim s = Find(name)
            If s Is Nothing Then Throw New ToolArgumentException("没有名为 " & name & " 的工具。")
            Return s.Execute(name, If(args, New JObject()), ctx)
        End Function

        ''' 本轮有没有会改数据的工具 (决定提示词里怎么说)。
        Public ReadOnly Property HasWrite As Boolean
            Get
                Return _sets.Any(Function(s) TypeOf s Is WriteTools AndAlso s.Definitions().Count > 0)
            End Get
        End Property
    End Class

    ''' 一次工具调用的上下文: 请用户确认、记下改了哪些数据。
    Friend NotInheritable Class ToolContext
        Private ReadOnly _confirm As Func(Of JObject, Boolean)
        Private ReadOnly _cancelled As Func(Of Boolean)
        Private ReadOnly _onWrite As Action(Of JObject)

        Public Sub New(confirm As Func(Of JObject, Boolean), cancelled As Func(Of Boolean), onWrite As Action(Of JObject), files As AttachmentStore)
            _confirm = confirm
            _cancelled = cancelled
            _onWrite = onWrite
            Me.Files = files
        End Sub

        ''' 弹确认卡片并等用户点击 (后台线程上阻塞)。确认返回 True; 拒绝或本轮被取消返回 False。
        Public Function Confirm(preview As JObject) As Boolean
            Return _confirm(preview)
        End Function

        Public ReadOnly Property Cancelled As Boolean
            Get
                Return _cancelled()
            End Get
        End Property

        ''' 记一次写入 (进日志), 如 {"table":"订单","action":"add","rows":[...]}。
        Public Sub Wrote(entry As JObject)
            _onWrite(entry)
        End Sub

        ''' 这段对话里用户上传的文件。
        Public ReadOnly Files As AttachmentStore
    End Class

    ''' "在狐表中打开": 哪张表的哪些行。有筛选表达式就用它 (用户在狐表里看到的也是条件), 否则按 _Identify 筛。
    Friend NotInheritable Class OpenTarget
        Public ReadOnly Table As String
        Public ReadOnly Filter As String
        Public ReadOnly Ids As List(Of Long)

        Public Sub New(table As String, filter As String, Optional ids As IEnumerable(Of Long) = Nothing)
            Me.Table = table
            Me.Filter = filter
            Me.Ids = If(ids, Enumerable.Empty(Of Long)()).Distinct().ToList()
        End Sub

        Public ReadOnly Property Usable As Boolean
            Get
                Return Filter IsNot Nothing OrElse Ids.Count > 0
            End Get
        End Property
    End Class
End Namespace
