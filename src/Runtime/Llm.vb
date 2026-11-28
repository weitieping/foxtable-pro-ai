Imports System.Reflection
Imports System.Threading.Tasks
Imports Foxtable

Namespace AI

    ''' 狐表官方 LLMCaller 的薄封装。全部按名字反射调用: 不在编译期绑定 LLMCaller,
    ''' 低于 2026-11-16 的狐表上也能加载本 DLL 并给出升级提示, 而不是直接报类型找不到。
    Friend NotInheritable Class Llm

        Private Shared ReadOnly _type As New Lazy(Of Type)(Function() Fx.FoxtableAssembly.GetType("Foxtable.LLMCaller"))

        ''' 当前狐表带不带 LLMCaller (2026-11-16 起)。
        Public Shared ReadOnly Property Available As Boolean
            Get
                Return _type.Value IsNot Nothing
            End Get
        End Property

        ''' 新建一个 LLMCaller 对象。
        Public Shared Function Create(url As String, model As String, key As String) As LLMCaller
            If Not Available Then Throw New NotSupportedException("AI 面板需要狐表 2026-11-16 或更新的版本, 请先升级狐表。")

            Return New LLMCaller(url, model, key)
        End Function

        ' 每轮新建 caller 时不从模板复制的属性: 由 ProAI 自己管, 或是上一次调用的结果
        Private Shared ReadOnly NotCopied As New HashSet(Of String)(StringComparer.Ordinal) From {
            "Messages", "UserMessage", "SystemPrompt", "Tools", "ToolExecutor", "SSEStreamHandler", "stream", "RunOnServer",
            "Result", "Think", "Error", "ResultJObject"}

        ''' 按模板新建一个 caller: 复制模板上开发者设的模型参数 (model、temperature、Headers ...)。
        ''' 每轮都用新的 caller, 被作废的上一轮即使还在跑也不会和这一轮抢同一个对象。
        Public Shared Function CloneConfig(template As Object) As LLMCaller
            Dim c = New LLMCaller()
            For Each p In _type.Value.GetProperties(BindingFlags.Public Or BindingFlags.Instance)
                If NotCopied.Contains(p.Name) OrElse Not p.CanRead OrElse Not p.CanWrite OrElse p.GetIndexParameters().Length > 0 Then Continue For
                Try
                    Dim v = p.GetValue(template, Nothing)
                    Dim tok = TryCast(v, JToken)
                    If tok IsNot Nothing Then v = tok.DeepClone()
                    p.SetValue(c, v, Nothing)
                Catch
                    ' 个别属性读写抛异常 (随版本变化) 时跳过
                End Try
            Next
            Return c
        End Function

        Public Shared Sub AddJsonTool(caller As LLMCaller, json As String)
            caller.AddJsonTool(json)
        End Sub

        Public Shared Function GetResultAsync(caller As LLMCaller) As Task(Of Boolean)
            Return caller.GetResultAsync()
        End Function

        Public Shared Sub Cancel(caller As LLMCaller)
            Try
                caller.Cancel()
            Catch
            End Try
        End Sub
    End Class
End Namespace
