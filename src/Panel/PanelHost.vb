Imports System.Drawing
Imports System.Runtime.CompilerServices
Imports System.Windows.Forms

Namespace AI

    ''' 面板宿主: 一个常驻的根控件, 在窗口面板、弹窗、主窗口侧栏和自己的停放窗口之间挪动。
    ''' 挪动时网页不重载 (stage0 实测), 宿主窗口关闭前收回到停放窗口, 不随窗口释放。
    Friend NotInheritable Class PanelHost
        Implements IDisposable

        Private ReadOnly _ai As ProAI
        Private ReadOnly _session As ChatSession
        Private ReadOnly _outbox As Outbox
        Private ReadOnly _park As Form
        Private _root As Panel
        Private _web As WebPanel
        Private _popup As Form
        Private _side As Panel
        Private _splitter As Splitter
        Private _sideWidth As Integer = 420
        Private ReadOnly _hookedForms As New HashSet(Of Form)
        Private _hostName As String
        Private ReadOnly _timer As Timer
        Private _disposed As Boolean

        Public Sub New(ai As ProAI, session As ChatSession, outbox As Outbox)
            _ai = ai
            _session = session
            _outbox = outbox
            ' 停放窗口不显示, 但先建好句柄: 挪进来时 WebView2 的窗口句柄不会被销毁重建
            _park = New Form With {.ShowInTaskbar = False, .FormBorderStyle = FormBorderStyle.None, .Size = New Size(400, 600),
                                   .StartPosition = FormStartPosition.Manual, .Location = New Point(-32000, -32000)}
            Dim h = _park.Handle
            _timer = New Timer With {.Interval = 50}
            AddHandler _timer.Tick, AddressOf Flush
            _timer.Start()
        End Sub

        '==============================================================
        ' 根控件
        '==============================================================

        Private Function Root() As Panel
            If _root IsNot Nothing AndAlso Not _root.IsDisposed Then Return _root
            ' 第一次, 或宿主窗口没走关闭流程就把它释放了: 重建, 网页加载后要快照还原
            _root = New Panel With {.Dock = DockStyle.Fill, .BackColor = Color.White}
            AddHandler _root.ParentChanged, AddressOf OnRootParentChanged
            AddHandler _root.HandleCreated, Sub() UiThread.Post(AddressOf FixNativeParents)
            AddHandler _root.ControlRemoved, AddressOf OnRootControlRemoved
            _web = Nothing
            Dim problem = WebRuntime.Unavailable()
            If problem Is Nothing AndAlso Not Llm.Available Then problem = Notice.OldFoxtable
            If problem Is Nothing Then
                Try
                    _web = WebPanel.Create(_root, AddressOf OnWebMessage, AddressOf ShowNotice)
                Catch ex As Exception
                    problem = Notice.InitFailed(ex.GetBaseException().Message)
                End Try
            End If
            If problem IsNot Nothing Then ShowNotice(problem)
            _root.Parent = _park
            Return _root
        End Function

        ''' 网页用不了时 (没装 WebView2 运行时、狐表版本太低、初始化失败) 显示一段说明, 不报错。
        Private Sub ShowNotice(n As Notice)
            Dim r = _root
            If r Is Nothing OrElse r.IsDisposed Then Return
            r.SuspendLayout()
            _web = Nothing
            For Each c As Control In r.Controls.Cast(Of Control).ToList()
                c.Dispose()
            Next
            r.Controls.Clear()
            Dim box As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.TopDown, .WrapContents = False,
                                                 .Padding = New Padding(16), .AutoScroll = True}
            Dim font As New Font("Microsoft YaHei UI", 10.0F)
            box.Controls.Add(New Label With {.Text = n.Title, .AutoSize = True, .Font = New Font(font, FontStyle.Bold), .Margin = New Padding(0, 0, 0, 8)})
            box.Controls.Add(New Label With {.Text = n.Text, .AutoSize = True, .MaximumSize = New Size(360, 0), .Font = font, .ForeColor = Color.DimGray})
            If n.LinkUrl IsNot Nothing Then
                Dim link As New LinkLabel With {.Text = n.LinkText, .AutoSize = True, .Font = font, .Margin = New Padding(0, 12, 0, 0)}
                AddHandler link.LinkClicked, Sub() OpenExternal(n.LinkUrl)
                box.Controls.Add(link)
            End If
            r.Controls.Add(box)
            r.ResumeLayout()
        End Sub

        Private Sub OnRootParentChanged(sender As Object, e As EventArgs)
            ' 宿主把它摘下来了 (窗口关闭时狐表只摘不释放): 收回停放窗口
            If _root IsNot Nothing AndAlso _root.Parent Is Nothing AndAlso Not _disposed AndAlso Not _root.IsDisposed Then
                UiThread.Post(Sub() If _root IsNot Nothing AndAlso Not _root.IsDisposed AndAlso _root.Parent Is Nothing Then _root.Parent = _park)
            ElseIf _root IsNot Nothing AndAlso _root.Parent IsNot Nothing Then
                UiThread.Post(AddressOf FixNativeParents)
            End If
            ' 从侧栏挪去了别处 (AttachTo、弹窗、收起): 侧栏不留空壳
            If _side IsNot Nothing AndAlso _root IsNot Nothing AndAlso _root.Parent IsNot _side Then
                UiThread.Post(Sub() If _side IsNot Nothing AndAlso (_root Is Nothing OrElse _root.Parent IsNot _side) Then RemoveSidebar())
            End If
        End Sub

        ' 狐表关窗口时会把窗口里各层控件逐个摘下, 连根控件里的 WebView2 也摘走: 放回去
        Private Sub OnRootControlRemoved(sender As Object, e As ControlEventArgs)
            Dim w = _web
            If _disposed OrElse w Is Nothing OrElse e.Control IsNot w.Control Then Return
            UiThread.Post(AddressOf EnsureWebInRoot)
        End Sub

        Private Sub EnsureWebInRoot()
            Dim w = _web
            If _disposed OrElse w Is Nothing OrElse _root Is Nothing OrElse _root.IsDisposed OrElse w.Control.IsDisposed Then Return
            If w.Control.Parent IsNot _root Then
                _root.Controls.Add(w.Control)
                w.Control.Dock = DockStyle.Fill
            End If
            FixNativeParents()
        End Sub

        ''' 纠正原生父窗口。狐表打开窗口时面板的句柄可能还没建好, WinForms 会把挪进去的子控件句柄
        ''' 暂存到它内部的 ParkingWindow (不可见), 之后 WebView2 的句柄没被挪回来, 面板一片空白。
        Private Sub FixNativeParents()
            If _root Is Nothing OrElse _root.IsDisposed Then Return
            FixNativeParent(_root)
            If _web IsNot Nothing Then FixNativeParent(_web.Control)
        End Sub

        Private Shared Sub FixNativeParent(c As Control)
            If c Is Nothing OrElse c.IsDisposed OrElse Not c.IsHandleCreated Then Return
            Dim p = c.Parent
            If p Is Nothing OrElse p.IsDisposed OrElse Not p.IsHandleCreated Then Return
            If NativeMethods.GetParent(c.Handle) <> p.Handle Then NativeMethods.SetParent(c.Handle, p.Handle)
        End Sub

        '==============================================================
        ' 放进窗口 / 弹窗
        '==============================================================

        ''' 放进一个容器: 狐表窗口里的控件 (如 e.Form.Controls("Panel1")) 或 WinForms 控件。
        Public Sub AttachTo(target As Object)
            Dim host = ResolveControl(target)
            If _popup IsNot Nothing AndAlso Not _popup.IsDisposed AndAlso host.FindForm() IsNot _popup Then ClosePopup()
            Dim r = Root()
            ' 先建好宿主句柄再挪, WinForms 就直接改父窗口, 不经 ParkingWindow 中转
            If Not host.IsHandleCreated Then
                Dim h = host.Handle
            End If
            r.Parent = host
            r.Dock = DockStyle.Fill
            r.BringToFront()
            ' 狐表窗口里的控件记下窗口名 (停靠窗口没有自己的 WinForms 窗体, 取不到)
            Dim foxForm = If(target Is host, Nothing, Fx.TryGet(target, "Form"))
            _hostName = If(foxForm Is Nothing, Nothing, TryCast(Fx.TryGet(foxForm, "Name"), String))
            HookForm(host)
            EnsureWebInRoot()
            UiThread.Post(AddressOf EnsureWebInRoot)
        End Sub

        Private Shared Function ResolveControl(target As Object) As Control
            If target Is Nothing Then Throw New ArgumentNullException(NameOf(target), "要放 AI 面板的控件为空。")
            Dim c = TryCast(target, Control)
            If c Is Nothing Then c = TryCast(Fx.TryGet(target, "BaseControl"), Control)
            If c Is Nothing Then Throw New ArgumentException("AttachTo 要给窗口里的容器控件, 如 e.Form.Controls(""Panel1"")。")
            Return c
        End Function

        ' 宿主窗口关闭前收回 (FormClosing 时子控件还没释放)
        Private Sub HookForm(host As Control)
            Dim f = host.FindForm()
            If f Is Nothing Then
                ' 控件还没放进窗口 (窗口刚建好还没显示): 等它有了窗口再挂
                Dim once As EventHandler = Nothing
                once = Sub(s, e)
                           RemoveHandler host.HandleCreated, once
                           HookForm(host)
                       End Sub
                AddHandler host.HandleCreated, once
                Return
            End If
            If Not _hookedForms.Add(f) Then Return
            AddHandler f.FormClosing, AddressOf OnHostClosing
            AddHandler f.Disposed, Sub() _hookedForms.Remove(f)
        End Sub

        Private Sub OnHostClosing(sender As Object, e As FormClosingEventArgs)
            If e.Cancel Then Return
            Dim f = DirectCast(sender, Form)
            If _root IsNot Nothing AndAlso Not _root.IsDisposed AndAlso _root.FindForm() Is f Then Park()
        End Sub

        ''' 收回停放窗口 (界面上不再显示, 对话保留)。
        Public Sub Park()
            If _root Is Nothing OrElse _root.IsDisposed Then Return
            If _root.Parent IsNot _park Then _root.Parent = _park
            RemoveSidebar()
        End Sub

        ''' 停靠在狐表主窗口右侧, 宽度可拖; 顶栏有关闭按钮。已经开着就只调宽度 (width 大于 0 时)。
        Public Sub ShowSidebar(width As Integer)
            Dim main = Fx.MainForm()
            If main Is Nothing OrElse main.IsDisposed Then Throw New InvalidOperationException("取不到狐表主窗口, 没法停靠 AI 面板。")
            If width > 0 Then _sideWidth = width
            If _side Is Nothing OrElse _side.IsDisposed OrElse _side.Parent IsNot main Then
                RemoveSidebar()
                ClosePopup()
                CreateSidebar(main)
            ElseIf width > 0 Then
                _side.Width = width
            End If
            Dim r = Root()
            If Not _side.IsHandleCreated Then
                Dim h = _side.Handle
            End If
            r.Parent = _side
            r.Dock = DockStyle.Fill
            _hostName = Nothing
            HookForm(_side)
            EnsureWebInRoot()
            UiThread.Post(AddressOf EnsureWebInRoot)
            _session.PushConfig()
        End Sub

        Private Sub CreateSidebar(main As Form)
            Dim side As New Panel With {.Dock = DockStyle.Right, .Width = _sideWidth, .BackColor = Color.White}
            Dim sp As New Splitter With {.Dock = DockStyle.Right, .Width = 4, .MinSize = 280, .MinExtra = 300, .BackColor = SystemColors.ControlLight}
            AddHandler side.SizeChanged, Sub() If side Is _side AndAlso side.Width > 0 Then _sideWidth = side.Width
            main.SuspendLayout()
            Try
                main.Controls.Add(side)
                main.Controls.Add(sp)
                ' 停靠按 Z 序从后往前排: 放在铺满的主区 (MainPages 等) 后面一位, 功能区、状态栏先占满宽, 侧栏再贴右边
                Dim fill = main.Controls.Cast(Of Control)().
                    Where(Function(c) c.Dock = DockStyle.Fill AndAlso c IsNot side AndAlso c IsNot sp).
                    Select(Function(c) main.Controls.GetChildIndex(c)).DefaultIfEmpty(-1).Max()
                main.Controls.SetChildIndex(side, fill + 1)
                main.Controls.SetChildIndex(sp, fill + 1)
            Finally
                main.ResumeLayout(True)
            End Try
            _side = side
            _splitter = sp
        End Sub

        ''' 关掉侧栏: 面板收回停放窗口 (对话保留), 主窗口恢复原样。
        Public Sub RemoveSidebar()
            Dim side = _side, sp = _splitter
            If side Is Nothing AndAlso sp Is Nothing Then Return
            _side = Nothing
            _splitter = Nothing
            If side IsNot Nothing AndAlso Not side.IsDisposed Then
                If _root IsNot Nothing AndAlso Not _root.IsDisposed AndAlso _root.Parent Is side Then _root.Parent = _park
                side.Dispose()
            End If
            If sp IsNot Nothing AndAlso Not sp.IsDisposed Then sp.Dispose()
            If Not _disposed Then _session.PushConfig()
        End Sub

        ''' 是否正停靠在主窗口侧栏里。
        Public ReadOnly Property InSidebar As Boolean
            Get
                Return _side IsNot Nothing AndAlso Not _side.IsDisposed AndAlso
                       _root IsNot Nothing AndAlso Not _root.IsDisposed AndAlso _root.Parent Is _side
            End Get
        End Property

        ''' 在独立的小窗口里显示。已经开着就激活。
        Public Sub ShowPopup()
            If _popup IsNot Nothing AndAlso Not _popup.IsDisposed Then
                If _popup.WindowState = FormWindowState.Minimized Then _popup.WindowState = FormWindowState.Normal
                _popup.Activate()
                Return
            End If
            Dim owner = Fx.MainForm()
            Dim pop As New Form With {
                .Text = _ai.Title, .ShowIcon = False, .ShowInTaskbar = owner Is Nothing,
                .Size = New Size(460, 680), .MinimumSize = New Size(320, 360),
                .StartPosition = FormStartPosition.Manual}
            Dim area = If(owner IsNot Nothing, Screen.FromControl(owner).WorkingArea, Screen.PrimaryScreen.WorkingArea)
            pop.Location = New Point(area.Right - pop.Width - 24, area.Top + Math.Max(24, (area.Height - pop.Height) \ 2))
            _popup = pop
            AddHandler pop.FormClosed, Sub()
                                           If _popup Is pop Then _popup = Nothing
                                       End Sub
            Dim r = Root()
            r.Parent = pop
            r.Dock = DockStyle.Fill
            HookForm(pop)
            If owner IsNot Nothing Then pop.Show(owner) Else pop.Show()
            EnsureWebInRoot()
        End Sub

        Public Sub ClosePopup()
            If _popup IsNot Nothing AndAlso Not _popup.IsDisposed Then _popup.Close()
            _popup = Nothing
        End Sub

        Public Sub UpdateTitle()
            If _popup IsNot Nothing AndAlso Not _popup.IsDisposed Then _popup.Text = _ai.Title
        End Sub

        ''' 面板所在窗口的名字: 狐表窗口用窗口名, 其他用标题; 在弹窗里或收起时为 Nothing。
        Public ReadOnly Property HostWindowName As String
            Get
                Dim f = HostForm
                If f Is Nothing OrElse f Is _popup OrElse InSidebar Then Return Nothing
                If Not String.IsNullOrEmpty(_hostName) Then Return _hostName
                Return f.Text
            End Get
        End Property

        ''' 现在显示在哪个窗口里; 收在停放窗口时为 Nothing。
        Public ReadOnly Property HostForm As Form
            Get
                If _root Is Nothing OrElse _root.IsDisposed Then Return Nothing
                Dim f = _root.FindForm()
                Return If(f Is _park, Nothing, f)
            End Get
        End Property

        '==============================================================
        ' 消息
        '==============================================================

        Private Sub OnWebMessage(msg As JObject)
            If CStr(msg("type")) = "ready" Then
                _web?.Post(_session.Snapshot())
                Return
            End If
            If CStr(msg("type")) = "openUrl" Then
                OpenExternal(CStr(msg("url")))
                Return
            End If
            If CStr(msg("type")) = "close" Then
                ' 不在 WebView2 自己的消息回调里挪走它
                UiThread.Post(Sub() If InSidebar Then Park())
                Return
            End If
            _session.HandleWebMessage(msg)
        End Sub

        ' 约每 50 毫秒把攒下的消息一次推给网页; 网页没就绪就丢掉, 就绪时会要快照
        Private Sub Flush(sender As Object, e As EventArgs)
            Dim items = _outbox.Drain()
            If items Is Nothing Then Return
            Dim w = _web
            If w Is Nothing OrElse Not w.Ready Then Return
            If items.Count = 1 Then
                w.Post(items(0))
            Else
                w.Post(New JObject From {{"type", "batch"}, {"events", New JArray(items.Cast(Of Object)().ToArray())}})
            End If
        End Sub

        ''' 在网页里执行一段脚本 (自测用)。
        Public Function ExecuteScriptAsync(js As String) As Threading.Tasks.Task(Of String)
            If _web Is Nothing Then Return Threading.Tasks.Task.FromResult("(no web)")
            Return _web.ExecuteScriptAsync(js)
        End Function

        ''' 面板状态, 自测用: 网页是否就绪、显示在哪、说明文字。
        Public Function State() As JObject
            Dim o As New JObject From {
                {"web", _web IsNot Nothing}, {"ready", _web IsNot Nothing AndAlso _web.Ready},
                {"host", If(HostForm Is Nothing, "", HostForm.Text)},
                {"popup", _popup IsNot Nothing AndAlso Not _popup.IsDisposed},
                {"sidebar", InSidebar}}
            If _web IsNot Nothing Then o("runtime") = _web.RuntimeVersion
            If _root IsNot Nothing AndAlso _web Is Nothing Then
                o("notice") = String.Join(" ", _root.Controls.Cast(Of Control).SelectMany(Function(c) c.Controls.Cast(Of Control)).Select(Function(c) c.Text))
            End If
            Return o
        End Function

        Friend Shared Sub OpenExternal(url As String)
            If String.IsNullOrEmpty(url) Then Return
            If Not (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) OrElse url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Then Return
            Try
                Process.Start(New ProcessStartInfo(url) With {.UseShellExecute = True})
            Catch
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            _timer.Stop()
            _timer.Dispose()
            ClosePopup()
            RemoveSidebar()
            If _root IsNot Nothing AndAlso Not _root.IsDisposed Then _root.Dispose()
            _park.Dispose()
        End Sub
    End Class

    Friend NotInheritable Class NativeMethods
        <Runtime.InteropServices.DllImport("user32.dll")>
        Public Shared Function GetParent(hWnd As IntPtr) As IntPtr
        End Function

        <Runtime.InteropServices.DllImport("user32.dll")>
        Public Shared Function SetParent(hWndChild As IntPtr, hWndNewParent As IntPtr) As IntPtr
        End Function
    End Class

    ''' 面板里显示的说明 (代替网页)。
    Friend NotInheritable Class Notice
        Public Title As String
        Public Text As String
        Public LinkText As String
        Public LinkUrl As String

        Public Shared ReadOnly Property NoRuntime As Notice
            Get
                Return New Notice With {
                    .Title = "需要安装 WebView2 运行时",
                    .Text = "AI 助手的界面依赖 Microsoft Edge WebView2 运行时, 这台电脑上还没有安装。安装后重新打开本窗口即可。",
                    .LinkText = "下载 WebView2 运行时",
                    .LinkUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"}
            End Get
        End Property

        Public Shared ReadOnly Property NoAssembly As Notice
            Get
                Return New Notice With {
                    .Title = "需要升级狐表",
                    .Text = "AI 助手需要狐表 2026-11-16 或更新的版本 (当前狐表目录里没有 WebView2 组件)。"}
            End Get
        End Property

        Public Shared ReadOnly Property OldFoxtable As Notice
            Get
                Return New Notice With {
                    .Title = "需要升级狐表",
                    .Text = "AI 助手需要狐表 2026-11-16 或更新的版本。"}
            End Get
        End Property

        Public Shared Function InitFailed(message As String) As Notice
            Return New Notice With {.Title = "AI 助手界面没能打开", .Text = message}
        End Function
    End Class
End Namespace
