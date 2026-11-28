Imports System.Data
Imports System.Reflection
Imports System.Text

Namespace AI

    ''' 第 3 档: 把内容填进开发者现有的录入窗口, 不保存, 由用户核对后点窗口里原有的保存按钮。
    ''' 绑定了列的控件写到窗口当前行 (stage0: 给绑定控件赋 Value 不会写回数据), 表事件照常触发;
    ''' 没绑定的控件直接赋值。新单用户没保存就关了窗口时, 删掉这张新单 (见 FormFillTracker)。
    Friend NotInheritable Class FormTools
        Implements IToolSet

        Public Const LIST_FORMS As String = "list_forms"
        Public Const FILL As String = "fill_form"

        Private ReadOnly _forms As List(Of FormAccess)
        Private ReadOnly _tracker As FormFillTracker

        Public Sub New(forms As List(Of FormAccess), tracker As FormFillTracker)
            _forms = forms
            _tracker = tracker
        End Sub

        ''' 这个用户有可填写的窗口。
        Public ReadOnly Property HasAny As Boolean
            Get
                Return _forms.Count > 0
            End Get
        End Property

        Public Function Owns(name As String) As Boolean Implements IToolSet.Owns
            Return (name = LIST_FORMS OrElse name = FILL) AndAlso _forms.Count > 0
        End Function

        ''' 有哪些窗口、窗口有哪些字段都不放进系统提示词, 由模型用 list_forms、只给 form 的 fill_form 去查。
        ''' form 参数也不列枚举, 给错时报错信息里列出可填写的窗口。
        Public Function Definitions() As List(Of JObject) Implements IToolSet.Definitions
            Dim list As New List(Of JObject)
            If _forms.Count = 0 Then Return list
            list.Add(ReadTools.Fn(LIST_FORMS, "列出当前用户可以填写的录入窗口: 窗口名和说明。", New JObject()))
            list.Add(ReadTools.Fn(FILL,
                "打开系统里的录入窗口, 把内容填进去 (不保存), 由用户核对后自己点窗口里的保存按钮。只给 form 不给 values 时, 只打开窗口并返回有哪些字段。",
                New JObject From {
                    {"form", New JObject From {{"type", "string"}, {"description", "窗口名 (list_forms 列出的)"}}},
                    {"mode", New JObject From {{"type", "string"}, {"enum", New JArray("new", "current")}, {"description", "new: 新建一张单再填; current: 填到窗口当前这张单。不给时: 当前这张是本次对话新建、还没保存的单就接着填它, 否则新建"}}},
                    {"values", New JObject From {{"type", "object"}, {"description", "{字段名: 值}, 字段名用只给 form 调用时返回的; 日期写 2026-03-05"}}},
                    {"details", New JObject From {{"type", "array"}, {"items", New JObject From {{"type", "object"}}}, {"description", "明细表格的行, 每项 {列名: 值} (窗口里有明细表格时)"}}}},
                "form"))
            Return list
        End Function

        Public Function Label(name As String, args As JObject) As String Implements IToolSet.Label
            If name = LIST_FORMS Then Return "查看可填写的窗口"
            Return "填写窗口 " & If(args Is Nothing, "", CStr(args("form")))
        End Function

        ''' list_forms 的结果: 每个窗口一行, 如 "订单录入: 新建销售订单"。
        Private Function Listing() As List(Of String)
            Return _forms.Select(Function(f) If(f.Description <> "", f.Name & ": " & f.Description, f.Name)).ToList()
        End Function

        Private Shared Function DetailColumns(layout As FormLayout) As IEnumerable(Of ColumnInfo)
            Return layout.Detail.Columns.Where(Function(c) c.Expression = "" AndAlso Not layout.Detail.LinkCols.Contains(c.Name) AndAlso Not c.Name.StartsWith("_", StringComparison.Ordinal))
        End Function

        Public Function Execute(name As String, args As JObject, ctx As ToolContext) As ToolOutcome Implements IToolSet.Execute
            If name = LIST_FORMS Then
                Dim lines = Listing()
                Return New ToolOutcome(New JObject From {{"forms", New JArray(lines.Cast(Of Object)().ToArray())}}, lines.Count & " 个窗口")
            End If
            Dim formName = CStr(args("form"))
            Dim f = _forms.FirstOrDefault(Function(x) String.Equals(x.Name, If(formName, "").Trim(), StringComparison.OrdinalIgnoreCase))
            If f Is Nothing Then Throw New ToolArgumentException(If(formName, "(未给)") & " 不是可填写的窗口。可填写的窗口: " & String.Join("、", _forms.Select(Function(x) x.Name)))
            Dim res = UiThread.Run(Function() DoFill(f.Name, args))
            If res.Log IsNot Nothing Then ctx.Wrote(res.Log)
            Return res.Outcome
        End Function

        Private NotInheritable Class FillResult
            Public Outcome As ToolOutcome
            Public Log As JObject
        End Class

        Private Function DoFill(formName As String, args As JObject) As FillResult
            Dim form = FormReader.Open(formName)
            Dim layout = FormReader.Inspect(formName)
            If layout Is Nothing Then Throw New ToolArgumentException("窗口 " & formName & " 没能打开。")
            Dim values = TryCast(args("values"), JObject)
            Dim details = TryCast(args("details"), JArray)
            Dim fieldsJson = New JArray(layout.Fields.Select(Function(x) x.Brief()).Cast(Of Object)().ToArray())

            If (values Is Nothing OrElse Not values.HasValues) AndAlso (details Is Nothing OrElse details.Count = 0) Then
                Dim info As New JObject From {{"form", formName}, {"opened", True}, {"fields", fieldsJson}}
                If layout.Detail IsNot Nothing Then info("detail_columns") = New JArray(DetailColumns(layout).Select(Function(c) c.Brief()).Cast(Of Object)().ToArray())
                info("message") = "窗口已打开, 还没有填。再调用 fill_form 带上 values (和 details) 填写。"
                Return New FillResult With {.Outcome = New ToolOutcome(info, layout.Fields.Count & " 个字段")}
            End If
            If layout.BindTable Is Nothing AndAlso layout.Fields.All(Function(x) x.Bound) Then
                Throw New ToolArgumentException("窗口 " & formName & " 里没有能填的控件。")
            End If

            Dim filled As New JArray()
            Dim skipped As New List(Of String)
            Dim problems As New List(Of String)
            Dim newRows As New List(Of Object)
            ' 不给 mode 时, 窗口当前是本次对话刚新建、还没保存的单就接着填它:
            ' 填完用户说 "客户改成李四", 模型再调用时常不带 mode, 默认新建会多出一张单。
            Dim mode = If(CStr(args("mode")), "").Trim().ToLowerInvariant()
            Dim dr As Object = Nothing
            If layout.BindTable IsNot Nothing Then
                Dim tbl = DirectCast(Fx.Item(Fx.Global("Tables"), layout.BindTable), Foxtable.Table)
                Dim cur = Fx.Get(tbl, "Current")
                If mode <> "new" AndAlso mode <> "current" Then
                    mode = If(cur IsNot Nothing AndAlso _tracker.IsPending(formName, Fx.Get(cur, "DataRow")), "current", "new")
                End If
                If mode = "current" Then
                    If cur Is Nothing Then Throw New ToolArgumentException("窗口 " & formName & " 现在没有当前单据, 用 mode=new。")
                    dr = Fx.Get(cur, "DataRow")
                Else
                    Dim row = tbl.AddNew()
                    dr = Fx.Get(row, "DataRow")
                    newRows.Add(dr)
                End If
            End If

            If values IsNot Nothing Then
                For Each p In values.Properties()
                    Dim fld = layout.Find(p.Name)
                    If fld Is Nothing Then
                        skipped.Add(p.Name)
                        Continue For
                    End If
                    Dim v As Object
                    Try
                        v = AI.Values.ToColumn(fld.Column, p.Value)
                    Catch ex As ToolArgumentException
                        problems.Add(fld.Key & ": " & ex.Message)
                        Continue For
                    End Try
                    If fld.Bound Then
                        Dim blocked = WriteTools.SetValues(dr, {New KeyValuePair(Of ColumnInfo, Object)(fld.Column, v)})
                        If blocked.Count > 0 Then
                            problems.Add(fld.Key & ": 没填进去 (被系统的校验拦下)")
                            Continue For
                        End If
                    Else
                        Try
                            Fx.Set(fld.Control, "Value", If(TypeOf v Is DBNull, Nothing, v))
                        Catch ex As Exception
                            problems.Add(fld.Key & ": " & ex.GetBaseException().Message)
                            Continue For
                        End Try
                    End If
                    filled.Add(New JObject From {{"field", fld.Key}, {"value", AI.Values.Display(v)}})
                Next
            End If

            Dim detailCount = 0
            If details IsNot Nothing AndAlso details.Count > 0 Then
                If layout.Detail Is Nothing Then
                    problems.Add("窗口里没有明细表格, details 没有填")
                ElseIf dr Is Nothing Then
                    problems.Add("窗口没有绑定主表, details 没有填")
                Else
                    Dim dtbl = DirectCast(Fx.Item(Fx.Global("Tables"), layout.Detail.TableName), Foxtable.Table)
                    For i = 0 To details.Count - 1
                        Dim o = TryCast(details(i), JObject)
                        If o Is Nothing Then Continue For
                        Dim vals As New List(Of KeyValuePair(Of ColumnInfo, Object))
                        Try
                            For Each p In o.Properties()
                                Dim c = layout.Detail.Column(p.Name)
                                If c Is Nothing Then
                                    skipped.Add("明细." & p.Name)
                                    Continue For
                                End If
                                If c.Expression <> "" OrElse layout.Detail.LinkCols.Contains(c.Name) Then Continue For
                                vals.Add(New KeyValuePair(Of ColumnInfo, Object)(c, AI.Values.ToColumn(c, p.Value)))
                            Next
                        Catch ex As ToolArgumentException
                            problems.Add("明细第 " & (i + 1) & " 行: " & ex.Message)
                            Continue For
                        End Try
                        Dim r = dtbl.AddNew()
                        Dim d = Fx.Get(r, "DataRow")
                        newRows.Add(d)
                        ' 窗口里的子表一般自动带上关联列; 没带上的补上
                        For k = 0 To layout.Detail.LinkCols.Count - 1
                            Dim cur = Fx.Item(d, layout.Detail.LinkCols(k))
                            If cur Is Nothing OrElse TypeOf cur Is DBNull Then Fx.SetItem(d, layout.Detail.LinkCols(k), Fx.Item(dr, layout.Detail.ParentCols(k)))
                        Next
                        Dim blocked = WriteTools.SetValues(d, vals)
                        If blocked.Count > 0 Then problems.Add("明细第 " & (i + 1) & " 行: " & String.Join("、", blocked) & " 没填进去 (被系统的校验拦下)")
                        detailCount += 1
                    Next
                End If
            End If
            If newRows.Count > 0 Then _tracker.Track(formName, newRows)
            FormReader.Activate(form)

            Dim res As New JObject From {{"form", formName}, {"filled", filled}}
            If detailCount > 0 Then res("details_filled") = detailCount
            If skipped.Count > 0 Then
                res("skipped") = New JArray(skipped.Cast(Of Object)().ToArray())
                res("fields") = fieldsJson
            End If
            If problems.Count > 0 Then res("problems") = New JArray(problems.Cast(Of Object)().ToArray())
            res("message") = "已填入窗口「" & formName & "」, 还没有保存。请用户核对, 确认无误后点窗口里的保存按钮; 你不能替用户保存。" &
                             "接着改这张单就再调用 fill_form (不给 mode 会填到这张单上), 另录一张才用 mode=new。" &
                             If(skipped.Count > 0, "窗口里没有这些字段, 没填: " & String.Join("、", skipped) & "。", "") &
                             If(problems.Count > 0, "有些字段没填成, 见 problems, 告诉用户手动补上。", "")
            Dim log As New JObject From {{"action", "fill_form"}, {"form", formName}, {"fields", filled.Count}}
            If layout.BindTable IsNot Nothing Then log("table") = layout.BindDataTable
            If detailCount > 0 Then log("details") = detailCount
            Return New FillResult With {
                .Outcome = New ToolOutcome(res, "已填 " & filled.Count & " 项" & If(detailCount > 0, ", 明细 " & detailCount & " 行", "") & If(problems.Count + skipped.Count > 0, ", " & (problems.Count + skipped.Count) & " 项未填", "")),
                .Log = log}
        End Function
    End Class

    ''' 窗口里能填的东西: 绑定了列或可输入的控件, 以及关联到主表的明细表格。
    Friend NotInheritable Class FormLayout
        Public Fields As New List(Of FormField)
        ''' 绑定的表 (Tables 里的名字, 可能是窗口表如 订单录入_Table1); 没有绑定时为 Nothing。
        Public BindTable As String
        Public BindDataTable As String
        Public Detail As FormDetail

        Public Function Find(key As String) As FormField
            If key Is Nothing Then Return Nothing
            Dim k = key.Trim()
            Return If(Fields.FirstOrDefault(Function(f) String.Equals(f.Key, k, StringComparison.OrdinalIgnoreCase)),
                   If(Fields.FirstOrDefault(Function(f) f.Column IsNot Nothing AndAlso String.Equals(f.Column.Caption, k, StringComparison.OrdinalIgnoreCase)),
                      Fields.FirstOrDefault(Function(f) String.Equals(f.ControlName, k, StringComparison.OrdinalIgnoreCase))))
        End Function
    End Class

    Friend NotInheritable Class FormField
        ''' 给模型的字段名: 绑定的列名, 没绑定时用控件名。
        Public Key As String
        Public ControlName As String
        Public Control As Object
        Public Bound As Boolean
        Public Column As ColumnInfo

        Public Function Brief() As String
            Dim c = Column.Brief()
            Return Key & c.Substring(Column.Name.Length)
        End Function
    End Class

    Friend NotInheritable Class FormDetail
        Public TableName As String
        Public DataTable As String
        Public Columns As List(Of ColumnInfo)
        Public LinkCols As New List(Of String)
        Public ParentCols As New List(Of String)

        Public Function Column(name As String) As ColumnInfo
            If name Is Nothing Then Return Nothing
            Dim k = name.Trim()
            Return If(Columns.FirstOrDefault(Function(c) String.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase)),
                      Columns.FirstOrDefault(Function(c) String.Equals(c.Caption, k, StringComparison.OrdinalIgnoreCase)))
        End Function
    End Class

    ''' 读狐表窗口。只在界面线程上调用。
    Friend Module FormReader

        Private ReadOnly InputTypes As New HashSet(Of String)(StringComparer.Ordinal) From {
            "TextBox", "ComboBox", "NumericComboBox", "DateTimePicker", "CheckBox", "MemoEditBox", "CheckedComboBox", "DropDownBox"}

        Private Function FormsObj() As Object
            Return Fx.Global("Forms")
        End Function

        ''' 规范成窗口的真名; 没有时返回 Nothing。
        Public Function RealName(name As String) As String
            If String.IsNullOrWhiteSpace(name) Then Return Nothing
            For Each f As Object In DirectCast(FormsObj(), IEnumerable)
                Dim n = Fx.GetStr(f, "Name")
                If String.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase) Then Return n
            Next
            Return Nothing
        End Function

        Public Function FormOf(name As String) As Object
            Return Fx.Item(FormsObj(), name)
        End Function

        ''' 打开窗口 (已经开着就不动)。
        Public Function Open(name As String) As Object
            Dim f = FormOf(name)
            If Not Equals(Fx.TryGet(f, "Opened"), True) Then
                Dim m = f.GetType().GetMethod("Open", BindingFlags.Public Or BindingFlags.Instance, Nothing, {GetType(Object())}, Nothing)
                If m Is Nothing Then Throw New MissingMethodException("Form", "Open")
                m.Invoke(f, New Object() {New Object() {}})
                Windows.Forms.Application.DoEvents()
            End If
            Return f
        End Function

        Public Sub Activate(f As Object)
            Try
                Dim bf = TryCast(Fx.TryGet(f, "BaseForm"), Windows.Forms.Form)
                If bf IsNot Nothing AndAlso bf.Visible Then
                    bf.Activate()
                Else
                    f.GetType().GetMethod("Select", Type.EmptyTypes)?.Invoke(f, Nothing)
                End If
            Catch
            End Try
        End Sub

        ''' 窗口里的字段; 窗口没打开 (还没有控件) 时返回 Nothing。
        Public Function Inspect(name As String) As FormLayout
            Dim f = FormOf(name)
            Dim controls = TryCast(Fx.TryGet(f, "Controls"), IEnumerable)
            If controls Is Nothing Then Return Nothing
            Dim layout As New FormLayout()
            Dim bound As New List(Of Tuple(Of Object, String, String)) ' 控件, 表, 列
            Dim tables As New List(Of Object)
            For Each c As Object In controls
                Dim typeName = c.GetType().Name
                If typeName = "Table" Then
                    Dim t = Fx.TryGet(c, "Table")
                    If t IsNot Nothing Then tables.Add(t)
                    Continue For
                End If
                If Not InputTypes.Contains(typeName) Then Continue For
                Dim bf = TryCast(Fx.TryGet(c, "BindingField"), String)
                If Not String.IsNullOrEmpty(bf) AndAlso bf.Contains(".") Then
                    Dim i = bf.LastIndexOf("."c)
                    bound.Add(Tuple.Create(c, bf.Substring(0, i), bf.Substring(i + 1)))
                Else
                    layout.Fields.Add(New FormField With {
                        .Key = Fx.GetStr(c, "Name"), .ControlName = Fx.GetStr(c, "Name"), .Control = c, .Bound = False,
                        .Column = New ColumnInfo With {.Name = Fx.GetStr(c, "Name"), .Kind = KindOfControl(typeName), .DataType = TypeOfControl(typeName),
                                                       .ComboList = If(TryCast(Fx.TryGet(c, "ComboList"), String), "")}})
                End If
            Next
            ' 主表: 绑定最多的那张
            If bound.Count > 0 Then
                layout.BindTable = bound.GroupBy(Function(b) b.Item2).OrderByDescending(Function(g) g.Count()).First().Key
                Dim tbl = Fx.Item(Fx.Global("Tables"), layout.BindTable)
                layout.BindDataTable = Fx.GetStr(Fx.Get(tbl, "DataTable"), "Name")
                Dim cols = SchemaReader.AllColumns(layout.BindDataTable)
                For Each b In bound.Where(Function(x) x.Item2 = layout.BindTable)
                    Dim col = cols.FirstOrDefault(Function(x) String.Equals(x.Name, b.Item3, StringComparison.OrdinalIgnoreCase))
                    If col Is Nothing OrElse col.Expression <> "" OrElse layout.Fields.Any(Function(x) x.Key = col.Name) Then Continue For
                    layout.Fields.Insert(layout.Fields.Where(Function(x) x.Bound).Count(),
                        New FormField With {.Key = col.Name, .ControlName = Fx.GetStr(b.Item1, "Name"), .Control = b.Item1, .Bound = True, .Column = col})
                Next
                ' 明细: 和主表有关联的表格
                For Each t In tables
                    Dim childDt = Fx.GetStr(Fx.Get(t, "DataTable"), "Name")
                    Dim link = RelationReader.Find(layout.BindDataTable, childDt)
                    If link Is Nothing Then Continue For
                    layout.Detail = New FormDetail With {
                        .TableName = Fx.GetStr(t, "Name"), .DataTable = childDt, .Columns = SchemaReader.AllColumns(childDt),
                        .LinkCols = link.ChildCols, .ParentCols = link.ParentCols}
                    Exit For
                Next
            End If
            Return layout
        End Function

        Private Function KindOfControl(typeName As String) As ColumnKind
            Select Case typeName
                Case "DateTimePicker" : Return ColumnKind.Date
                Case "NumericComboBox" : Return ColumnKind.Number
                Case "CheckBox" : Return ColumnKind.Boolean
                Case Else : Return ColumnKind.Text
            End Select
        End Function

        Private Function TypeOfControl(typeName As String) As Type
            Select Case typeName
                Case "DateTimePicker" : Return GetType(Date)
                Case "NumericComboBox" : Return GetType(Double)
                Case "CheckBox" : Return GetType(Boolean)
                Case Else : Return GetType(String)
            End Select
        End Function
    End Module

    ''' 填窗口时新建的行: 用户没保存就关了窗口, 就删掉 (stage0: 用户放弃时要撤销)。
    Friend NotInheritable Class FormFillTracker
        Implements IDisposable

        Private ReadOnly _items As New List(Of Tuple(Of String, List(Of Object)))
        Private _timer As Windows.Forms.Timer

        ''' 在界面线程上调用。
        Public Sub Track(form As String, rows As List(Of Object))
            _items.Add(Tuple.Create(form, rows))
            If _timer Is Nothing Then
                _timer = New Windows.Forms.Timer With {.Interval = 1000}
                AddHandler _timer.Tick, AddressOf Tick
            End If
            _timer.Start()
        End Sub

        Private Sub Tick(sender As Object, e As EventArgs)
            For Each entry In _items.ToList()
                Dim pending = entry.Item2.Where(Function(r) IsAdded(r)).ToList()
                Dim opened = True
                Try
                    opened = Equals(Fx.TryGet(FormReader.FormOf(entry.Item1), "Opened"), True)
                Catch
                    opened = False
                End Try
                If pending.Count = 0 Then
                    _items.Remove(entry)
                ElseIf Not opened Then
                    ' 先删明细再删主表
                    For i = pending.Count - 1 To 0 Step -1
                        Try
                            DirectCast(pending(i), Foxtable.DataRow).Delete()
                        Catch
                        End Try
                    Next
                    _items.Remove(entry)
                End If
            Next
            If _items.Count = 0 Then _timer.Stop()
        End Sub

        ''' row 是不是在这个窗口里新建、还没保存的行。在界面线程上调用。
        Public Function IsPending(form As String, row As Object) As Boolean
            Dim target = BaseOf(row)
            If target Is Nothing Then Return False
            Return _items.Any(Function(e) String.Equals(e.Item1, form, StringComparison.OrdinalIgnoreCase) AndAlso
                                          e.Item2.Any(Function(r) BaseOf(r) Is target AndAlso IsAdded(r)))
        End Function

        Private Shared Function BaseOf(r As Object) As DataRow
            If r Is Nothing Then Return Nothing
            Try
                Return TryCast(Fx.Get(r, "BaseRow"), DataRow)
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function IsAdded(r As Object) As Boolean
            Try
                Return DirectCast(Fx.Get(r, "BaseRow"), DataRow).RowState = DataRowState.Added
            Catch
                Return False
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _timer IsNot Nothing Then
                _timer.Stop()
                _timer.Dispose()
                _timer = Nothing
            End If
        End Sub
    End Class
End Namespace
