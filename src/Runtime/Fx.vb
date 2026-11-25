Imports System.Collections.Concurrent
Imports System.Reflection
Imports System.Windows.Forms

Namespace AI

    ''' 按名字取狐表对象的属性和全局对象。
    '''
    ''' 狐表每版重跑混淆: 属性名不变, 访问器方法名整批换掉。编译期直接写 dt.Name 绑的是那一版的访问器名,
    ''' 换一版狐表就报"找不到方法"。这里一律运行时按名字找, 同一份 DLL 各版通用。方法名不混淆, 可以直接调。
    Friend Module Fx

        Private Const INST As BindingFlags = BindingFlags.Public Or BindingFlags.Instance
        Private Const STAT As BindingFlags = BindingFlags.Public Or BindingFlags.Static Or BindingFlags.DeclaredOnly
        Private ReadOnly _props As New ConcurrentDictionary(Of String, PropertyInfo)

        ''' 狐表主程序集 (foxtable.exe)。
        Public ReadOnly Property FoxtableAssembly As Assembly
            Get
                Return GetType(Foxtable.DataRow).Assembly
            End Get
        End Property

        Private Function FindProperty(t As Type, name As String, indexType As Type) As PropertyInfo
            Dim key = t.AssemblyQualifiedName & "|" & name & "|" & If(indexType Is Nothing, "", indexType.FullName)
            Return _props.GetOrAdd(key,
                Function(k)
                    If indexType IsNot Nothing Then
                        Dim ip = t.GetProperty(name, INST, Nothing, Nothing, {indexType}, Nothing)
                        If ip Is Nothing Then Throw New MissingMemberException(t.FullName, name)
                        Return ip
                    End If
                    ' 从最派生的类型往上找, 避开同名属性被遮蔽时的 AmbiguousMatchException
                    Dim cur = t
                    While cur IsNot Nothing
                        For Each p In cur.GetProperties(INST Or BindingFlags.DeclaredOnly)
                            If p.Name = name AndAlso p.GetIndexParameters().Length = 0 Then Return p
                        Next
                        cur = cur.BaseType
                    End While
                    Throw New MissingMemberException(t.FullName, name)
                End Function)
        End Function

        ''' 读实例属性。
        Public Function [Get](obj As Object, name As String) As Object
            If obj Is Nothing Then Throw New NullReferenceException("取属性 " & name & " 时对象为空。")
            Return FindProperty(obj.GetType(), name, Nothing).GetValue(obj, Nothing)
        End Function

        Public Function GetStr(obj As Object, name As String) As String
            Dim v = [Get](obj, name)
            Return If(v Is Nothing, "", v.ToString())
        End Function

        ''' 读属性, 出错时返回默认值 (用于各版可能没有的属性)。
        Public Function TryGet(obj As Object, name As String) As Object
            Try
                Return [Get](obj, name)
            Catch
                Return Nothing
            End Try
        End Function

        Public Sub [Set](obj As Object, name As String, value As Object)
            FindProperty(obj.GetType(), name, Nothing).SetValue(obj, value, Nothing)
        End Sub

        ''' 按字符串索引取 Item(名字)。狐表集合的默认属性 Item2 访问器名也是混淆的, 这里按参数类型找 Item。
        Public Function Item(obj As Object, key As String) As Object
            Return FindProperty(obj.GetType(), "Item", GetType(String)).GetValue(obj, New Object() {key})
        End Function

        ''' 按字符串索引写 Item(名字) = 值, 如给狐表行的某列赋值 (会触发表事件)。
        Public Sub SetItem(obj As Object, key As String, value As Object)
            FindProperty(obj.GetType(), "Item", GetType(String)).SetValue(obj, value, New Object() {key})
        End Sub

        ''' 按整数索引取 Item(i) 或 Item(i, j)。
        Public Function ItemAt(obj As Object, ParamArray index() As Integer) As Object
            Dim types = index.Select(Function(x) GetType(Integer)).ToArray()
            Dim p = obj.GetType().GetProperty("Item", INST, Nothing, Nothing, types, Nothing)
            If p Is Nothing Then Throw New MissingMemberException(obj.GetType().FullName, "Item")
            Return p.GetValue(obj, index.Cast(Of Object)().ToArray())
        End Function

        Public Function HasProperty(t As Type, name As String) As Boolean
            Try
                FindProperty(t, name, Nothing)
                Return True
            Catch ex As MissingMemberException
                Return False
            End Try
        End Function

        '------------------------------------------------------------
        ' 全局对象 (事件代码里直接写的 DataTables、Tables、User ...)
        '------------------------------------------------------------

        Private ReadOnly _globalModule As New Lazy(Of Type)(AddressOf FindGlobalModule)

        Private Function FindGlobalModule() As Type
            Dim types() As Type
            Try
                types = FoxtableAssembly.GetTypes()
            Catch ex As ReflectionTypeLoadException
                types = ex.Types
            End Try
            Dim found As New List(Of Type)
            For Each t In types
                If t Is Nothing OrElse t.Namespace <> "Foxtable" OrElse Not t.IsPublic Then Continue For
                If Not t.IsDefined(GetType(Microsoft.VisualBasic.CompilerServices.StandardModuleAttribute), False) Then Continue For
                If t.GetProperty("Forms", STAT) IsNot Nothing AndAlso t.GetProperty("DataTables", STAT) IsNot Nothing AndAlso
                   t.GetProperty("ProjectFile", STAT) IsNot Nothing Then found.Add(t)
            Next
            If found.Count = 1 Then Return found(0)
            Throw New NotSupportedException("在当前狐表里认不出全局对象模块 (找到 " & found.Count & " 个), 请反馈狐表版本号。")
        End Function

        ''' 读全局对象, 如 Fx.Global("DataTables")。
        Public Function [Global](name As String) As Object
            Dim p = _globalModule.Value.GetProperty(name, STAT)
            If p Is Nothing Then Throw New MissingMemberException("狐表全局对象", name)
            Return p.GetValue(Nothing, Nothing)
        End Function

        ''' 写全局对象, 如 Fx.SetGlobal("MainTable", t)。
        Public Sub SetGlobal(name As String, value As Object)
            Dim p = _globalModule.Value.GetProperty(name, STAT)
            If p Is Nothing OrElse Not p.CanWrite Then Throw New MissingMemberException("狐表全局对象", name)
            p.SetValue(Nothing, value, Nothing)
        End Sub

        Public Function DataTables() As Object
            Return [Global]("DataTables")
        End Function

        ''' 按名字取数据表; 没有时返回 Nothing。
        Public Function DataTable(name As String) As Foxtable.DataTable
            Dim dts = DirectCast(DataTables(), Foxtable.DataTableCollection)
            If Not dts.Contains(name) Then Return Nothing
            Return DirectCast(Item(dts, name), Foxtable.DataTable)
        End Function

        ''' 狐表主窗体; 取不到时返回 Nothing。
        Public Function MainForm() As Form
            Try
                Return TryCast([Global]("BaseMainForm"), Form)
            Catch
                Return Nothing
            End Try
        End Function

        ''' 狐表版本, 如 "2026.11.16.1"; 取不到时为空。
        Public Function FoxtableVersion() As String
            Try
                Dim m = _globalModule.Value.GetMethod("ApplicationVertion", STAT, Nothing, Type.EmptyTypes, Nothing)
                Return If(m Is Nothing, "", CStr(m.Invoke(Nothing, Nothing)))
            Catch
                Return ""
            End Try
        End Function
    End Module

    ''' 界面线程。狐表对象只在界面线程上读写; 后台线程上的工具和流式片段经这里编组回来。
    Friend NotInheritable Class UiThread
        Private Shared _anchor As Control

        ''' 在界面线程上调用一次 (ProAI 构造时)。
        Public Shared Sub Init()
            If _anchor IsNot Nothing AndAlso Not _anchor.IsDisposed Then Return
            Dim c As New Control()
            Dim h = c.Handle
            _anchor = c
        End Sub

        Public Shared ReadOnly Property IsUiThread As Boolean
            Get
                Return _anchor Is Nothing OrElse Not _anchor.InvokeRequired
            End Get
        End Property

        ''' 在界面线程上执行并等结果。
        Public Shared Function Run(Of T)(f As Func(Of T)) As T
            If IsUiThread Then Return f()
            Return DirectCast(_anchor.Invoke(f), T)
        End Function

        Public Shared Sub Run(a As Action)
            If IsUiThread Then
                a()
            Else
                _anchor.Invoke(a)
            End If
        End Sub

        ''' 投递到界面线程, 不等。
        Public Shared Sub Post(a As Action)
            If _anchor Is Nothing OrElse _anchor.IsDisposed Then Return
            Try
                _anchor.BeginInvoke(a)
            Catch ex As InvalidOperationException
                ' 句柄已销毁 (狐表正在退出)
            End Try
        End Sub
    End Class
End Namespace
