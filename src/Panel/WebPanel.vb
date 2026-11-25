Imports System.Drawing
Imports System.IO
Imports System.Reflection
Imports System.Runtime.CompilerServices
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Web.WebView2.Core
Imports Microsoft.Web.WebView2.WinForms

Namespace AI

    ''' 检查 WebView2 能不能用。不引用 WebView2 类型, 狐表目录里没有 WebView2 组件时也能调用。
    Friend Module WebRuntime

        Private _resolveHooked As Boolean

        ''' 能用返回 Nothing, 否则返回要显示的说明。
        Public Function Unavailable() As Notice
            HookResolve()
#If DEBUG Then
            ' 自测用: 模拟没装运行时
            If Environment.GetEnvironmentVariable("FOXTABLEPRO_AI_FAKE_NORUNTIME") = "1" Then Return Notice.NoRuntime
#End If
            Try
                If WebRuntimeProbe.Version() Is Nothing Then Return Notice.NoRuntime
                Return Nothing
            Catch ex As FileNotFoundException
                Return Notice.NoAssembly
            Catch ex As FileLoadException
                Return Notice.NoAssembly
            Catch ex As Exception
                Return Notice.NoRuntime
            End Try
        End Function

        ' 狐表自带的 AssemblyResolve 会把不同版本的 WebView2 引用接到自带那份上 (stage0)。
        ' 另挂一个同样的兜底, 防狐表以后改掉自己的处理器。
        Private Sub HookResolve()
            If _resolveHooked Then Return
            _resolveHooked = True
            AddHandler AppDomain.CurrentDomain.AssemblyResolve,
                Function(s As Object, e As ResolveEventArgs) As Assembly
                    Dim n As New AssemblyName(e.Name)
                    If n.Name Is Nothing OrElse Not n.Name.StartsWith("Microsoft.Web.WebView2.", StringComparison.Ordinal) Then Return Nothing
                    For Each a In AppDomain.CurrentDomain.GetAssemblies()
                        If a.GetName().Name = n.Name Then Return a
                    Next
                    Dim p = Path.Combine(Path.GetDirectoryName(Fx.FoxtableAssembly.Location), n.Name & ".dll")
                    Return If(File.Exists(p), Assembly.LoadFrom(p), Nothing)
                End Function
        End Sub
    End Module

    Friend NotInheritable Class WebRuntimeProbe
        ''' 已装的 WebView2 运行时版本; 没装时为 Nothing。
        <MethodImpl(MethodImplOptions.NoInlining)>
        Public Shared Function Version() As String
            Try
                Return CoreWebView2Environment.GetAvailableBrowserVersionString(Nothing)
            Catch ex As WebView2RuntimeNotFoundException
                Return Nothing
            End Try
        End Function
    End Class

    ''' 常驻的 WebView2, 加载嵌在 DLL 里的网页。网页和 DLL 之间只走 WebView2 的消息通道, 不向网页暴露狐表对象。
    Friend NotInheritable Class WebPanel

        Private Const ORIGIN As String = "https://proai.foxtable"
        Private Const RESOURCE As String = "FoxtablePro.AI.panel.html"

        Private ReadOnly _wv As WebView2
        Private ReadOnly _onMessage As Action(Of JObject)
        Private ReadOnly _onFail As Action(Of Notice)
        Private ReadOnly _startUrl As String
        Private _ready As Boolean

        Public ReadOnly Property Ready As Boolean
            Get
                Return _ready
            End Get
        End Property

        Public Property RuntimeVersion As String = ""

        ''' WebView2 控件本身 (按 Control 给出, 调用方不必引用 WebView2)。
        Public ReadOnly Property Control As Control
            Get
                Return _wv
            End Get
        End Property

        Public Shared Function Create(parent As Control, onMessage As Action(Of JObject), onFail As Action(Of Notice)) As WebPanel
            Return New WebPanel(parent, onMessage, onFail)
        End Function

        Private Sub New(parent As Control, onMessage As Action(Of JObject), onFail As Action(Of Notice))
            _onMessage = onMessage
            _onFail = onFail
            _startUrl = ORIGIN & "/index.html"
#If DEBUG Then
            ' 调试版可以指向 Vite 开发服务器, 改网页不用重编 DLL
            Dim dev = Environment.GetEnvironmentVariable("FOXTABLEPRO_AI_DEVURL")
            If Not String.IsNullOrEmpty(dev) Then _startUrl = dev
#End If
            _wv = New WebView2 With {.Dock = DockStyle.Fill, .DefaultBackgroundColor = Color.White}
            _wv.CreationProperties = New CoreWebView2CreationProperties With {
                .UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxtablePro", "WebView2")}
            parent.Controls.Add(_wv)
            InitAsync()
        End Sub

        Private Async Sub InitAsync()
            Try
                Await _wv.EnsureCoreWebView2Async(Nothing)
            Catch ex As Exception
                _onFail(Notice.InitFailed(ex.GetBaseException().Message))
                Return
            End Try
            Dim core = _wv.CoreWebView2
            RuntimeVersion = core.Environment.BrowserVersionString
            Dim st = core.Settings
#If DEBUG Then
            st.AreDevToolsEnabled = True
#Else
            st.AreDevToolsEnabled = False
#End If
            st.IsStatusBarEnabled = False
            st.AreHostObjectsAllowed = False
            st.IsWebMessageEnabled = True
            st.IsZoomControlEnabled = False
            st.AreBrowserAcceleratorKeysEnabled = False
            AddHandler core.WebResourceRequested, AddressOf OnResource
            core.AddWebResourceRequestedFilter(ORIGIN & "/*", CoreWebView2WebResourceContext.All)
            AddHandler core.NavigationStarting, AddressOf OnNavigating
            AddHandler core.NewWindowRequested, Sub(s, e)
                                                    e.Handled = True
                                                    PanelHost.OpenExternal(e.Uri)
                                                End Sub
            AddHandler core.WebMessageReceived, AddressOf OnWebMessage
            ' 自己的网页要麦克风 (语音输入) 直接给, 不弹 WebView2 的授权框
            AddHandler core.PermissionRequested, Sub(s, e)
                                                     If e.PermissionKind = CoreWebView2PermissionKind.Microphone AndAlso IsOwn(e.Uri) Then
                                                         e.State = CoreWebView2PermissionState.Allow
                                                     End If
                                                 End Sub
            AddHandler core.ProcessFailed, Sub(s, e)
                                               _ready = False
                                               Try
                                                   core.Reload()
                                               Catch
                                               End Try
                                           End Sub
            core.Navigate(_startUrl)
        End Sub

        Private Function IsOwn(url As String) As Boolean
            If url Is Nothing Then Return False
            Return url.StartsWith(ORIGIN & "/", StringComparison.OrdinalIgnoreCase) OrElse
                   url.StartsWith(_startUrl, StringComparison.OrdinalIgnoreCase) OrElse url = "about:blank"
        End Function

        ' 只允许打开自己的网页; 回答里的链接在系统浏览器里打开
        Private Sub OnNavigating(sender As Object, e As CoreWebView2NavigationStartingEventArgs)
            If IsOwn(e.Uri) Then
                _ready = False
                Return
            End If
            e.Cancel = True
            PanelHost.OpenExternal(e.Uri)
        End Sub

        Private Sub OnResource(sender As Object, e As CoreWebView2WebResourceRequestedEventArgs)
            Dim html = LoadHtml()
            Dim ms As New MemoryStream(System.Text.Encoding.UTF8.GetBytes(html))
            e.Response = _wv.CoreWebView2.Environment.CreateWebResourceResponse(
                ms, 200, "OK", "Content-Type: text/html; charset=utf-8" & vbCrLf & "Cache-Control: no-store")
        End Sub

        Private Shared _html As String

        Private Shared Function LoadHtml() As String
            If _html IsNot Nothing Then Return _html
            Using s = GetType(WebPanel).Assembly.GetManifestResourceStream(RESOURCE)
                If s Is Nothing Then
                    Return "<!doctype html><meta charset=utf-8><body style='font:14px Microsoft YaHei;padding:16px'>面板网页没有打包进 DLL, 请先在 FoxTablePro.AI/web 下执行 npm run build 再编译。"
                End If
                Using r As New StreamReader(s, System.Text.Encoding.UTF8)
                    _html = r.ReadToEnd()
                End Using
            End Using
            Return _html
        End Function

        Private Sub OnWebMessage(sender As Object, e As CoreWebView2WebMessageReceivedEventArgs)
            If Not IsOwn(e.Source) Then Return
            Dim msg As JObject
            Try
                Dim tok = JToken.Parse(e.WebMessageAsJson)
                If tok.Type = JTokenType.String Then tok = JToken.Parse(CStr(tok))
                msg = TryCast(tok, JObject)
            Catch
                Return
            End Try
            If msg Is Nothing Then Return
            If CStr(msg("type")) = "ready" Then _ready = True
            _onMessage(msg)
        End Sub

        Public Sub Post(evt As JObject)
            Dim core = _wv.CoreWebView2
            If core Is Nothing Then Return
            Try
                core.PostWebMessageAsJson(evt.ToString(Newtonsoft.Json.Formatting.None))
            Catch ex As InvalidOperationException
                _ready = False
            End Try
        End Sub

        Public Function ExecuteScriptAsync(js As String) As Task(Of String)
            If _wv.CoreWebView2 Is Nothing Then Return Task.FromResult("(not ready)")
            Return _wv.CoreWebView2.ExecuteScriptAsync(js)
        End Function
    End Class
End Namespace
