Imports System.Data

Namespace AI

    ''' 第 2 档: 放行写入的表自动有新增、修改、导入三个工具。不开放删除。
    ''' 每次都是三步: 界面线程上检查参数、做预览 → 后台线程等用户在确认卡片上点确认 → 界面线程上写入并保存。
    ''' 写入走狐表的 DataRow, 表事件照常触发; 写完回读, 被事件拦下的告诉模型 (stage0: e.Cancel 是静默的)。
    Friend NotInheritable Class WriteTools
        Implements IToolSet

        Public Const ADD As String = "add_rows"
        Public Const UPDATE As String = "update_rows"
        Public Const IMPORT As String = "import_rows"
        Private Const MAX_ADD As Integer = 200
        Private Const MAX_UPDATE As Integer = 100
        Private Const MAX_IMPORT As Integer = 5000
        Private Const PREVIEW_ROWS As Integer = 20
        Public Const DETAILS_KEY As String = "details"

        Private ReadOnly _tables As List(Of TableInfo)
        Private ReadOnly _writable As List(Of TableInfo)
        Private ReadOnly _files As AttachmentStore

        Public Sub New(tables As List(Of TableInfo), files As AttachmentStore)
            _tables = tables
            _writable = tables.Where(Function(t) t.CanWrite).ToList()
            _files = files
        End Sub

        Public Function Owns(name As String) As Boolean Implements IToolSet.Owns
            If _writable.Count = 0 Then Return False
            If name = IMPORT Then Return _files IsNot Nothing AndAlso _files.HasSheets
            Return name = ADD OrElse name = UPDATE
        End Function

        '==============================================================
        ' 定义
        '==============================================================

        Public Function Definitions() As List(Of JObject) Implements IToolSet.Definitions
            Dim list As New List(Of JObject)
            If _writable.Count = 0 Then Return list
            Dim tableProp = ReadTools.TableParam("表名 (list_tables 里标了可写的)")
            Dim hasDetails = _writable.Any(Function(t) t.Details.Count > 0)
            Dim rowItem As New JObject From {{"type", "object"}, {"description", "一行: {列名: 值}, 日期写 2026-03-05"}}
            If hasDetails Then
                rowItem("properties") = New JObject From {
                    {DETAILS_KEY, New JObject From {{"type", "array"}, {"items", New JObject From {{"type", "object"}}},
                                                    {"description", "这一行的明细行, 每项 {列名: 值}; 只有带明细表的主表才填, 关联列不用填"}}}}
            End If

            list.Add(ReadTools.Fn(ADD,
                "新增行并保存, 一次最多 " & MAX_ADD & " 行。执行前系统给用户看预览并等确认。",
                New JObject From {
                    {"table", tableProp},
                    {"rows", New JObject From {{"type", "array"}, {"items", rowItem}, {"description", "要新增的行"}}}},
                "table", "rows"))

            list.Add(ReadTools.Fn(UPDATE,
                "修改符合条件的行并保存, 一次最多 " & MAX_UPDATE & " 行。执行前系统给用户看改前改后的对比并等确认。",
                New JObject From {
                    {"table", tableProp},
                    {"where", ReadTools.WhereSchema("要修改哪些行, 条件须全部满足")},
                    {"set", New JObject From {{"type", "object"}, {"description", "新值: {列名: 值}, 值为 null 表示清空"}}}},
                "table", "where", "set"))

            If Owns(IMPORT) Then
                list.Add(ReadTools.Fn(IMPORT,
                    "把用户上传的表格文件按列对应导入到表里, 一次最多 " & MAX_IMPORT & " 行, 由系统逐行转换。" &
                    "执行前系统给用户看预览并等确认。转换失败的行跳过并在预览里列出。",
                    New JObject From {
                        {"file", New JObject From {{"type", "string"}, {"description", "文件编号, 如 f1"}}},
                        {"sheet", New JObject From {{"type", "string"}, {"description", "工作表名, 不填为第一个"}}},
                        {"header_row", New JObject From {{"type", "integer"}, {"description", "表头在第几行, 默认 1; 没有表头填 0"}}},
                        {"table", tableProp},
                        {"mapping", New JObject From {{"type", "object"},
                            {"description", "列对应: {表的列名: 文件的列}, 文件的列写表头文字, 没有表头时写列字母 (A、B…)"}}},
                        {"constants", New JObject From {{"type", "object"}, {"description", "每行都填的固定值: {表的列名: 值}"}}},
                        {"skip_empty", New JObject From {{"type", "array"}, {"items", New JObject From {{"type", "string"}}},
                            {"description", "这些文件列为空的行跳过 (如合计行、空行)"}}}},
                    "file", "table", "mapping"))
            End If
            Return list
        End Function

        Public Function Label(name As String, args As JObject) As String Implements IToolSet.Label
            Dim t = Find(If(args Is Nothing, Nothing, CStr(args("table"))))
            Dim target = If(t Is Nothing, "", " " & t.Display)
            Select Case name
                Case ADD : Return "新增" & target
                Case UPDATE : Return "修改" & target
                Case IMPORT
                    Dim f = If(args Is Nothing OrElse _files Is Nothing, Nothing, _files.Find(CStr(args("file"))))
                    Return "导入" & If(f Is Nothing, "", " " & f.Name) & " →" & target
                Case Else : Return name
            End Select
        End Function

        '==============================================================
        ' 执行
        '==============================================================

        Public Function Execute(name As String, args As JObject, ctx As ToolContext) As ToolOutcome Implements IToolSet.Execute
            Dim t = TableOf(args)
            Dim plan As Plan
            Select Case name
                Case ADD : plan = UiThread.Run(Function() PlanAdd(t, args("rows")))
                Case UPDATE : plan = UiThread.Run(Function() PlanUpdate(t, args))
                Case IMPORT : plan = UiThread.Run(Function() PlanImport(t, args))
                Case Else : Throw New ToolArgumentException("没有工具 " & name)
            End Select
            If ctx.Cancelled Then Return Cancelled()
            If Not ctx.Confirm(plan.Preview) Then
                If ctx.Cancelled Then Return Cancelled()
                Return New ToolOutcome(New JObject From {
                    {"confirmed", False},
                    {"message", "用户没有确认, 什么也没有写入。不要重复提交同样的内容; 问用户哪里需要改。"}}, "用户未确认")
            End If
            Dim res = UiThread.Run(Function() Apply(plan))
            ctx.Wrote(res.Log)
            Return res.Outcome
        End Function

        Private Shared Function Cancelled() As ToolOutcome
            Return New ToolOutcome(New JObject From {{"cancelled", True}, {"message", "用户已取消本次提问, 没有写入。"}}, "已取消")
        End Function

        Private Function Find(name As String) As TableInfo
            Return SchemaReader.FindTable(_writable, name)
        End Function

        Private Function TableOf(args As JObject) As TableInfo
            Dim name = CStr(args("table"))
            Dim t = Find(name)
            If t Is Nothing Then
                Throw New ToolArgumentException(If(name, "(未给)") & " 不是可写的表。可写的表: " & String.Join("、", _writable.Select(Function(x) x.Name)))
            End If
            Return t
        End Function

        '==============================================================
        ' 计划与预览 (界面线程)
        '==============================================================

        Private NotInheritable Class Plan
            Public Kind As String
            Public Table As TableInfo
            Public Rows As New List(Of PlanRow)
            Public Link As DetailLink
            Public Detail As TableInfo
            Public Source As String
            Public Skipped As New List(Of String)
            Public Warnings As New List(Of String)
            Public Preview As JObject
        End Class

        Private NotInheritable Class PlanRow
            Public Label As String
            Public Values As New List(Of KeyValuePair(Of ColumnInfo, Object))
            Public Target As DataRow
            Public Before As New Dictionary(Of String, Object)
            Public Details As New List(Of PlanRow)
        End Class

        ''' 一行 {列名: 值} → 计划行。skip: 不让模型填的列 (明细的关联列), 给了也忽略。
        Private Function ParseRow(t As TableInfo, obj As JObject, label As String, checkRequired As Boolean,
                                  Optional skip As ICollection(Of String) = Nothing) As PlanRow
            Dim pr As New PlanRow With {.Label = label}
            Try
                For Each p In obj.Properties()
                    If p.Name = DETAILS_KEY Then Continue For
                    Dim c = t.Column(p.Name)
                    If c Is Nothing Then
                        Throw New ToolArgumentException("表 " & t.Name & " 没有列 """ & p.Name & """。可写的列: " & String.Join("、", t.WritableColumns.Select(Function(x) x.Name)))
                    End If
                    If skip IsNot Nothing AndAlso skip.Contains(c.Name) Then Continue For
                    If Not c.Writable Then
                        Throw New ToolArgumentException("列 " & c.Name & " 不能写" & If(c.Expression <> "", " (表达式列, 自动计算)", "") & "。可写的列: " & String.Join("、", t.WritableColumns.Select(Function(x) x.Name)))
                    End If
                    If pr.Values.Any(Function(kv) kv.Key Is c) Then Continue For
                    pr.Values.Add(New KeyValuePair(Of ColumnInfo, Object)(c, Values.ToColumn(c, p.Value)))
                Next
                If checkRequired Then
                    Dim missing = t.Columns.Where(Function(c) c.Required AndAlso (skip Is Nothing OrElse Not skip.Contains(c.Name)) AndAlso
                                                      Not pr.Values.Any(Function(kv) kv.Key Is c AndAlso Not TypeOf kv.Value Is DBNull)).Select(Function(c) c.Name).ToList()
                    If missing.Count > 0 Then Throw New ToolArgumentException("缺少必填的列: " & String.Join("、", missing))
                End If
            Catch ex As ToolArgumentException
                Throw New ToolArgumentException(label & ": " & ex.Message)
            End Try
            Return pr
        End Function

        Private Function PlanAdd(t As TableInfo, rowsTok As JToken) As Plan
            Dim arr = TryCast(rowsTok, JArray)
            If arr Is Nothing AndAlso TypeOf rowsTok Is JObject Then arr = New JArray(rowsTok)
            If arr Is Nothing OrElse arr.Count = 0 Then Throw New ToolArgumentException("rows 要给至少一行。")
            If arr.Count > MAX_ADD Then Throw New ToolArgumentException("一次最多新增 " & MAX_ADD & " 行, 给了 " & arr.Count & " 行。分几次提交。")
            Dim plan As New Plan With {.Kind = "add", .Table = t}
            If t.Details.Count > 0 Then
                plan.Link = t.Details(0)
                plan.Detail = _tables.FirstOrDefault(Function(x) x.Name = plan.Link.Child)
            End If
            For i = 0 To arr.Count - 1
                Dim o = TryCast(arr(i), JObject)
                If o Is Nothing Then Throw New ToolArgumentException("rows 的每一项应是 {列名: 值}。")
                Dim pr = ParseRow(t, o, "第 " & (i + 1) & " 行", True)
                Dim dets = TryCast(o(DETAILS_KEY), JArray)
                If dets IsNot Nothing AndAlso dets.Count > 0 Then
                    If plan.Detail Is Nothing Then Throw New ToolArgumentException("表 " & t.Name & " 没有可写的明细表, 不能带 details。")
                    For j = 0 To dets.Count - 1
                        Dim d = TryCast(dets(j), JObject)
                        If d Is Nothing Then Throw New ToolArgumentException("details 的每一项应是 {列名: 值}。")
                        pr.Details.Add(ParseRow(plan.Detail, d, pr.Label & "的第 " & (j + 1) & " 条明细", True, plan.Link.ChildCols))
                    Next
                End If
                plan.Rows.Add(pr)
            Next
            CheckUnique(plan)
            CheckOptions(plan)
            Dim total = plan.Rows.Count
            Dim tables As New JArray(PreviewTable(t.Display, plan.Rows))
            If plan.Detail IsNot Nothing Then
                For Each pr In plan.Rows.Where(Function(r) r.Details.Count > 0).Take(10)
                    tables.Add(PreviewTable(pr.Label & "的明细 · " & plan.Detail.Display, pr.Details))
                Next
            End If
            Dim detailCount = plan.Rows.Sum(Function(r) r.Details.Count)
            plan.Preview = Preview(plan, "新增 " & total & " 行到 " & t.Display & If(detailCount > 0, " (含明细 " & detailCount & " 行)", ""), tables, "确认新增")
            Return plan
        End Function

        Private Function PlanUpdate(t As TableInfo, args As JObject) As Plan
            Dim where = args("where")
            If where Is Nothing OrElse where.Type = JTokenType.Null OrElse (TypeOf where Is JArray AndAlso DirectCast(where, JArray).Count = 0) Then
                Throw New ToolArgumentException("update_rows 必须给 where, 不能不加条件改整张表。")
            End If
            Dim setObj = TryCast(args("set"), JObject)
            If setObj Is Nothing OrElse Not setObj.HasValues Then Throw New ToolArgumentException("set 要给至少一列的新值。")
            Dim template = ParseRow(t, setObj, "set", False)
            Dim rows = New QueryEngine(t).FilterRows(where)
            If rows.Count = 0 Then Throw New ToolArgumentException("没有符合条件的行, 什么也没改。检查条件, 或先用 query_rows 查一下。")
            If rows.Count > MAX_UPDATE Then Throw New ToolArgumentException("符合条件的有 " & rows.Count & " 行, 一次最多改 " & MAX_UPDATE & " 行。缩小条件。")
            Dim plan As New Plan With {.Kind = "update", .Table = t}
            Dim keys = KeyColumns(t, template.Values.Select(Function(kv) kv.Key))
            For Each r In rows
                Dim pr As New PlanRow With {.Target = r, .Label = RowLabel(r, keys)}
                For Each kv In template.Values
                    Dim cur = If(r(kv.Key.Name) Is DBNull.Value, Nothing, r(kv.Key.Name))
                    pr.Before(kv.Key.Name) = cur
                    If Not Values.Same(kv.Value, cur) Then pr.Values.Add(kv)
                Next
                If pr.Values.Count > 0 Then plan.Rows.Add(pr)
            Next
            If plan.Rows.Count = 0 Then Throw New ToolArgumentException("这 " & rows.Count & " 行的值已经是要改的值, 不用修改。")
            CheckUnique(plan)
            CheckOptions(plan)

            Dim cols = keys.Select(Function(c) c.Display).Concat(template.Values.Select(Function(kv) kv.Key.Display)).ToList()
            Dim data As New JArray()
            For Each pr In plan.Rows.Take(PREVIEW_ROWS)
                Dim line As New JArray()
                For Each k In keys
                    line.Add(Values.Display(pr.Target(k.Name)))
                Next
                For Each kv In template.Values
                    Dim before = Values.Text(pr.Before(kv.Key.Name))
                    Dim changed = pr.Values.FirstOrDefault(Function(x) x.Key Is kv.Key)
                    If changed.Key Is Nothing Then
                        line.Add(If(before = "", "(空)", before) & " (不变)")
                    Else
                        line.Add(If(before = "", "(空)", before) & " → " & If(Values.Text(changed.Value) = "", "(空)", Values.Text(changed.Value)))
                    End If
                Next
                data.Add(line)
            Next
            Dim tbl = QueryEngine.UiTable(t.Display, cols, data, plan.Rows.Count)
            plan.Preview = Preview(plan, "修改 " & t.Display & " 的 " & plan.Rows.Count & " 行", New JArray(tbl), "确认修改")
            Return plan
        End Function

        Private Function PlanImport(t As TableInfo, args As JObject) As Plan
            Dim file = _files.Get(CStr(args("file")))
            Dim sheet = file.Sheet(CStr(args("sheet")))
            Dim headerRow = If(args("header_row") Is Nothing OrElse args("header_row").Type = JTokenType.Null, 1, CInt(args("header_row")))
            If headerRow < 0 OrElse headerRow > sheet.Rows.Count Then Throw New ToolArgumentException("header_row 超出范围 (工作表共 " & sheet.Rows.Count & " 行)。")
            Dim headers As New List(Of String)
            For j = 0 To sheet.ColCount - 1
                headers.Add(If(headerRow > 0, sheet.Cell(headerRow, j), ""))
            Next
            Dim mapping = TryCast(args("mapping"), JObject)
            If mapping Is Nothing OrElse Not mapping.HasValues Then Throw New ToolArgumentException("mapping 要给至少一列的对应。")

            Dim map As New List(Of KeyValuePair(Of ColumnInfo, Integer))
            For Each p In mapping.Properties()
                Dim c = t.Column(p.Name)
                If c Is Nothing OrElse Not c.Writable Then
                    Throw New ToolArgumentException("""" & p.Name & """ 不是表 " & t.Name & " 的可写列。可写的列: " & String.Join("、", t.WritableColumns.Select(Function(x) x.Name)))
                End If
                map.Add(New KeyValuePair(Of ColumnInfo, Integer)(c, SourceIndex(CStr(p.Value), headers)))
            Next
            Dim constants As New List(Of KeyValuePair(Of ColumnInfo, Object))
            Dim constObj = TryCast(args("constants"), JObject)
            If constObj IsNot Nothing Then
                Dim cr = ParseRow(t, constObj, "constants", False)
                constants.AddRange(cr.Values)
            End If
            Dim missing = t.Columns.Where(Function(c) c.Required AndAlso Not map.Any(Function(m) m.Key Is c) AndAlso Not constants.Any(Function(m) m.Key Is c)).Select(Function(c) c.Name).ToList()
            If missing.Count > 0 Then Throw New ToolArgumentException("必填的列没有对应: " & String.Join("、", missing))
            Dim skipCols As New List(Of Integer)
            Dim skipArr = TryCast(args("skip_empty"), JArray)
            If skipArr IsNot Nothing Then
                For Each s In skipArr
                    skipCols.Add(SourceIndex(CStr(s), headers))
                Next
            End If

            Dim plan As New Plan With {.Kind = "import", .Table = t, .Source = file.Name & If(file.Sheets.Count > 1, " · " & sheet.Name, "")}
            Dim errors As New List(Of String)
            For rowNo = headerRow + 1 To sheet.Rows.Count
                Dim r = rowNo
                If map.All(Function(m) sheet.Cell(r, m.Value) = "") Then Continue For
                If skipCols.Any(Function(j) sheet.Cell(r, j) = "") Then Continue For
                Dim pr As New PlanRow With {.Label = "文件第 " & r & " 行"}
                Try
                    For Each m In map
                        pr.Values.Add(New KeyValuePair(Of ColumnInfo, Object)(m.Key, Values.FromObject(m.Key, sheet.Cell(r, m.Value))))
                    Next
                    pr.Values.AddRange(constants.Where(Function(k) Not pr.Values.Any(Function(x) x.Key Is k.Key)))
                    Dim empty = pr.Values.Where(Function(kv) kv.Key.Required AndAlso TypeOf kv.Value Is DBNull).Select(Function(kv) kv.Key.Name).ToList()
                    If empty.Count > 0 Then Throw New ToolArgumentException("必填的列为空: " & String.Join("、", empty))
                    plan.Rows.Add(pr)
                Catch ex As ToolArgumentException
                    errors.Add(pr.Label & ": " & ex.Message)
                End Try
            Next
            If plan.Rows.Count = 0 Then
                If errors.Count > 0 Then Throw New ToolArgumentException("没有一行能导入。前几个问题: " & String.Join("; ", errors.Take(5)) & "。检查 mapping 和 header_row。")
                Throw New ToolArgumentException("文件里没有可导入的行。检查 header_row 和 skip_empty。")
            End If
            If plan.Rows.Count > MAX_IMPORT Then Throw New ToolArgumentException("可导入的有 " & plan.Rows.Count & " 行, 一次最多 " & MAX_IMPORT & " 行。")
            plan.Skipped.AddRange(errors)
            If errors.Count > 0 Then
                plan.Warnings.Add(errors.Count & " 行转换失败, 将跳过: " & String.Join("; ", errors.Take(10)) & If(errors.Count > 10, " …", ""))
            End If
            CheckUnique(plan)
            CheckOptions(plan)
            plan.Preview = Preview(plan, "从「" & plan.Source & "」导入 " & plan.Rows.Count & " 行到 " & t.Display, New JArray(PreviewTable(t.Display, plan.Rows)), "确认导入")
            Return plan
        End Function

        ' 文件的列: 表头文字, 或列字母 A、B…, 或 "第3列"
        Private Shared Function SourceIndex(spec As String, headers As List(Of String)) As Integer
            Dim s = If(spec, "").Trim()
            If s = "" Then Throw New ToolArgumentException("mapping 里有空的文件列。")
            Dim i = headers.FindIndex(Function(h) String.Equals(h.Trim(), s, StringComparison.OrdinalIgnoreCase))
            If i >= 0 Then Return i
            Dim m = System.Text.RegularExpressions.Regex.Match(s, "^(?:列)?([A-Za-z]{1,2})(?:列)?$")
            If m.Success Then
                Dim idx = 0
                For Each ch In m.Groups(1).Value.ToUpperInvariant()
                    idx = idx * 26 + (AscW(ch) - AscW("A"c) + 1)
                Next
                If idx - 1 < headers.Count Then Return idx - 1
            End If
            m = System.Text.RegularExpressions.Regex.Match(s, "^第?(\d+)列?$")
            If m.Success Then
                Dim n = CInt(m.Groups(1).Value)
                If n >= 1 AndAlso n <= headers.Count Then Return n - 1
            End If
            Throw New ToolArgumentException("文件里没有列 """ & s & """。表头: " & String.Join(" | ", headers.Select(Function(h, k) ColumnLetter(k) & "=" & h)))
        End Function

        Private Shared Function ColumnLetter(i As Integer) As String
            Dim s = ""
            Dim n = i + 1
            While n > 0
                Dim r = (n - 1) Mod 26
                s = ChrW(AscW("A"c) + r) & s
                n = (n - 1) \ 26
            End While
            Return s
        End Function

        ' 唯一列: 和表里已有的、和同一批里其他行都不能重复
        Private Sub CheckUnique(plan As Plan)
            CheckUniqueOf(plan.Table, plan.Rows, plan.Kind = "update")
            If plan.Detail IsNot Nothing Then CheckUniqueOf(plan.Detail, plan.Rows.SelectMany(Function(r) r.Details).ToList(), False)
        End Sub

        Private Shared Sub CheckUniqueOf(t As TableInfo, rows As List(Of PlanRow), isUpdate As Boolean)
            Dim uniques = t.Columns.Where(Function(c) c.Unique AndAlso rows.Any(Function(r) r.Values.Any(Function(kv) kv.Key Is c))).ToList()
            If uniques.Count = 0 Then Return
            Dim bt = DirectCast(Fx.Get(Fx.DataTable(t.Name), "BaseTable"), System.Data.DataTable)
            Dim targets = New HashSet(Of DataRow)(rows.Where(Function(r) r.Target IsNot Nothing).Select(Function(r) r.Target))
            For Each c In uniques
                Dim existing As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                For Each r As DataRow In bt.Rows
                    If r.RowState = DataRowState.Deleted OrElse r.RowState = DataRowState.Detached OrElse targets.Contains(r) Then Continue For
                    Dim v = r(c.Name)
                    If v IsNot DBNull.Value Then existing.Add(Values.Text(v))
                Next
                For Each pr In rows
                    Dim kv = pr.Values.FirstOrDefault(Function(x) x.Key Is c)
                    If kv.Key Is Nothing OrElse TypeOf kv.Value Is DBNull Then Continue For
                    Dim text = Values.Text(kv.Value)
                    If Not existing.Add(text) Then Throw New ToolArgumentException(pr.Label & ": 列 " & c.Name & " 不能重复, """ & text & """ 已经有了。")
                Next
            Next
        End Sub

        ' 列表项目不强制 (狐表本身也不强制), 不在里面的提醒一下
        Private Sub CheckOptions(plan As Plan)
            For Each pr In plan.Rows.Concat(plan.Rows.SelectMany(Function(r) r.Details))
                For Each kv In pr.Values
                    If String.IsNullOrEmpty(kv.Key.ComboList) OrElse TypeOf kv.Value Is DBNull Then Continue For
                    Dim items = kv.Key.ComboList.Split("|"c).Select(Function(x) x.Trim())
                    Dim text = Values.Text(kv.Value)
                    If Not items.Contains(text, StringComparer.OrdinalIgnoreCase) Then
                        plan.Warnings.Add(pr.Label & " 的 " & kv.Key.Display & " """ & text & """ 不在可选值里")
                        If plan.Warnings.Count >= 20 Then Return
                    End If
                Next
            Next
        End Sub

        ' 修改预览里用来认出是哪一行的列: 唯一列优先, 再取前两个不改的文本或日期列
        Private Shared Function KeyColumns(t As TableInfo, changing As IEnumerable(Of ColumnInfo)) As List(Of ColumnInfo)
            Dim ch = New HashSet(Of ColumnInfo)(changing)
            Dim res = t.Columns.Where(Function(c) c.Unique AndAlso Not ch.Contains(c)).Take(1).ToList()
            If res.Count = 0 Then
                res = t.Columns.Where(Function(c) Not ch.Contains(c) AndAlso (c.Kind = ColumnKind.Text OrElse c.Kind = ColumnKind.Date)).Take(2).ToList()
            End If
            Return res
        End Function

        Private Shared Function RowLabel(r As DataRow, keys As List(Of ColumnInfo)) As String
            If keys.Count = 0 Then Return "行"
            Return String.Join(" ", keys.Select(Function(k) Values.Text(r(k.Name))))
        End Function

        Private Shared Function PreviewTable(title As String, rows As List(Of PlanRow)) As JObject
            Dim cols = New List(Of ColumnInfo)
            For Each pr In rows
                For Each kv In pr.Values
                    If Not cols.Contains(kv.Key) Then cols.Add(kv.Key)
                Next
            Next
            Dim data As New JArray()
            For Each pr In rows.Take(PREVIEW_ROWS)
                Dim line As New JArray()
                For Each c In cols
                    Dim kv = pr.Values.FirstOrDefault(Function(x) x.Key Is c)
                    line.Add(If(kv.Key Is Nothing, JValue.CreateNull(), Values.Display(kv.Value)))
                Next
                data.Add(line)
            Next
            Return QueryEngine.UiTable(title, cols.Select(Function(c) c.Display), data, rows.Count)
        End Function

        Private Shared Function Preview(plan As Plan, title As String, tables As JArray, confirmText As String) As JObject
            Dim o As New JObject From {{"kind", plan.Kind}, {"title", title}, {"tables", tables}, {"confirmText", confirmText}}
            If plan.Warnings.Count > 0 Then o("warnings") = New JArray(plan.Warnings.Cast(Of Object)().ToArray())
            Return o
        End Function

        '==============================================================
        ' 写入 (界面线程)
        '==============================================================

        Private NotInheritable Class ApplyResult
            Public Outcome As ToolOutcome
            Public Log As JObject
        End Class

        Private Function Apply(plan As Plan) As ApplyResult
            Dim dt = Fx.DataTable(plan.Table.Name)
            If dt Is Nothing Then Throw New ToolArgumentException("表 " & plan.Table.Name & " 已不存在。")
            Dim problems As New List(Of String)
            Dim ids As New List(Of Long)
            Dim done As New List(Of PlanRow)
            Dim count = 0
            Dim detailCount = 0
            Dim changes As New JArray()

            If plan.Kind = "update" Then
                Dim map As New Dictionary(Of DataRow, Foxtable.DataRow)
                For Each fr As Foxtable.DataRow In DirectCast(Fx.Get(dt, "DataRows"), IEnumerable)
                    map(BaseRow(fr)) = fr
                Next
                For Each pr In plan.Rows
                    Dim fr As Foxtable.DataRow = Nothing
                    If Not map.TryGetValue(pr.Target, fr) Then
                        problems.Add(pr.Label & ": 这一行已经不在了")
                        Continue For
                    End If
                    Try
                        Dim blocked = SetValues(fr, pr.Values)
                        If blocked.Count > 0 Then
                            fr.RejectChanges()
                            problems.Add(pr.Label & ": " & String.Join("、", blocked) & " 没改成 (被系统的校验拦下), 这一行没有修改")
                            Continue For
                        End If
                        Dim why = SaveRow(fr)
                        If why IsNot Nothing Then
                            fr.RejectChanges()
                            problems.Add(pr.Label & ": " & why)
                            Continue For
                        End If
                        count += 1
                        done.Add(pr)
                        Dim id = QueryEngine.IdOf(BaseRow(fr))
                        If id.HasValue Then ids.Add(id.Value)
                        If changes.Count < 50 Then
                            For Each kv In pr.Values
                                changes.Add(New JObject From {{"row", pr.Label}, {"column", kv.Key.Name}, {"before", Values.Display(pr.Before(kv.Key.Name))}, {"after", Values.Display(kv.Value)}})
                            Next
                        End If
                    Catch ex As Exception
                        Try
                            fr.RejectChanges()
                        Catch
                        End Try
                        problems.Add(pr.Label & ": " & ex.GetBaseException().Message)
                    End Try
                Next
            Else
                Dim detailDt = If(plan.Link Is Nothing, Nothing, Fx.DataTable(plan.Link.Child))
                For Each pr In plan.Rows
                    Dim fr As Foxtable.DataRow = Nothing
                    Dim newDetails As New List(Of Foxtable.DataRow)
                    Try
                        fr = dt.AddNew()
                        Dim blocked = SetValues(fr, pr.Values)
                        If blocked.Count > 0 Then
                            Discard(fr)
                            problems.Add(pr.Label & ": " & String.Join("、", blocked) & " 没写进去 (被系统的校验拦下), 这一行没有新增")
                            Continue For
                        End If
                        ' 关联靠 _Identify 这类保存后才定的列时, 先存主表
                        Dim savedFirst = False
                        If pr.Details.Count > 0 AndAlso plan.Link.ParentCols.Any(Function(c) c.StartsWith("_", StringComparison.Ordinal)) Then
                            Dim why0 = SaveRow(fr)
                            If why0 IsNot Nothing Then
                                Discard(fr)
                                problems.Add(pr.Label & ": " & why0)
                                Continue For
                            End If
                            savedFirst = True
                        End If
                        Dim detailProblem As String = Nothing
                        For Each d In pr.Details
                            Dim dr = detailDt.AddNew()
                            newDetails.Add(dr)
                            For i = 0 To plan.Link.ChildCols.Count - 1
                                Fx.SetItem(dr, plan.Link.ChildCols(i), Fx.Item(fr, plan.Link.ParentCols(i)))
                            Next
                            Dim b = SetValues(dr, d.Values)
                            If b.Count > 0 Then
                                detailProblem = d.Label & ": " & String.Join("、", b) & " 没写进去 (被系统的校验拦下)"
                                Exit For
                            End If
                        Next
                        If detailProblem Is Nothing AndAlso Not savedFirst Then
                            Dim why1 = SaveRow(fr)
                            If why1 IsNot Nothing Then detailProblem = pr.Label & ": " & why1
                        End If
                        If detailProblem IsNot Nothing Then
                            For Each dr In newDetails
                                Discard(dr)
                            Next
                            Discard(fr)
                            problems.Add(detailProblem & If(pr.Details.Count > 0, ", 这一行和它的明细都没有新增", ", 这一行没有新增"))
                            Continue For
                        End If
                        For Each dr In newDetails
                            Dim why2 = SaveRow(dr)
                            If why2 Is Nothing Then
                                detailCount += 1
                            Else
                                Discard(dr)
                                problems.Add(pr.Label & "的一条明细: " & why2)
                            End If
                        Next
                        count += 1
                        done.Add(pr)
                        Dim id = QueryEngine.IdOf(BaseRow(fr))
                        If id.HasValue Then ids.Add(id.Value)
                    Catch ex As Exception
                        For Each dr In newDetails
                            Discard(dr)
                        Next
                        If fr IsNot Nothing Then Discard(fr)
                        problems.Add(pr.Label & ": " & ex.GetBaseException().Message)
                    End Try
                Next
            End If

            Dim verb = If(plan.Kind = "update", "修改", If(plan.Kind = "import", "导入", "新增"))
            Dim res As New JObject From {{"table", plan.Table.Name}, {"confirmed", True}}
            res(If(plan.Kind = "update", "updated", "added")) = count
            If detailCount > 0 Then res("details_added") = detailCount
            If plan.Skipped.Count > 0 Then res("skipped") = plan.Skipped.Count
            If problems.Count > 0 Then res("problems") = New JArray(problems.Take(30).Cast(Of Object)().ToArray())
            Dim msg = "用户已确认, " & verb & "并保存了 " & count & " 行" & If(detailCount > 0, " (明细 " & detailCount & " 行)", "") & "。"
            If plan.Skipped.Count > 0 Then msg &= "转换失败跳过了 " & plan.Skipped.Count & " 行。"
            If problems.Count > 0 Then msg &= "有 " & problems.Count & " 行没成功, 原因见 problems, 如实告诉用户。"
            res("message") = msg

            Dim summary = "已" & verb & " " & count & " 行" & If(problems.Count > 0, ", " & problems.Count & " 行未成功", "") & If(plan.Skipped.Count > 0, ", 跳过 " & plan.Skipped.Count & " 行", "")
            Dim outcome As New ToolOutcome(res, summary)
            outcome.Open = OpenOf(plan, done, ids)

            Dim log As New JObject From {{"table", plan.Table.Name}, {"action", plan.Kind}, {"count", count}}
            If detailCount > 0 Then log("details") = detailCount
            If ids.Count > 0 Then log("ids") = New JArray(ids.Take(200).Cast(Of Object)().ToArray())
            If changes.Count > 0 Then log("changes") = changes
            If plan.Source IsNot Nothing Then log("source") = plan.Source
            If problems.Count > 0 Then log("problems") = New JArray(problems.Take(30).Cast(Of Object)().ToArray())
            Return New ApplyResult With {.Outcome = outcome, .Log = log}
        End Function

        ''' 写过的行: 有 _Identify 按它筛, 否则按一个能认出行的列的值筛 (唯一列、必填文本列、第一个文本列)。
        Private Shared Function OpenOf(plan As Plan, done As List(Of PlanRow), ids As List(Of Long)) As OpenTarget
            If done.Count = 0 Then Return Nothing
            If plan.Table.HasIdentify AndAlso ids.Count = done.Count Then Return New OpenTarget(plan.Table.Name, Nothing, ids)
            Dim t = plan.Table
            Dim candidates = t.Columns.Where(Function(c) c.Unique).
                Concat(t.Columns.Where(Function(c) c.Required AndAlso c.Kind = ColumnKind.Text)).
                Concat(t.Columns.Where(Function(c) c.Kind = ColumnKind.Text))
            For Each key In candidates
                Dim vals As New List(Of Object)
                For Each pr In done
                    If plan.Kind = "update" Then
                        Dim kv = pr.Values.FirstOrDefault(Function(x) x.Key Is key)
                        vals.Add(If(kv.Key Is Nothing, pr.Target(key.Name), kv.Value))
                    Else
                        vals.Add(pr.Values.FirstOrDefault(Function(x) x.Key Is key).Value)
                    End If
                Next
                If vals.Any(Function(v) v Is Nothing OrElse TypeOf v Is DBNull) Then Continue For
                Return New OpenTarget(t.Name, FilterBuilder.InList(key, vals))
            Next
            Return Nothing
        End Function

        Private Shared Function BaseRow(fr As Foxtable.DataRow) As DataRow
            Return DirectCast(Fx.Get(fr, "BaseRow"), DataRow)
        End Function

        ''' 逐列赋值并回读。返回没写进去的列 (被 DataColChanging 拦了或赋值时报错)。
        Friend Shared Function SetValues(fr As Object, vals As IEnumerable(Of KeyValuePair(Of ColumnInfo, Object))) As List(Of String)
            Dim blocked As New List(Of String)
            For Each kv In vals
                Dim v = If(TypeOf kv.Value Is DBNull, Nothing, kv.Value)
                Try
                    Fx.SetItem(fr, kv.Key.Name, v)
                Catch ex As Exception
                    blocked.Add(kv.Key.Name & " (" & ex.GetBaseException().Message & ")")
                    Continue For
                End Try
                If Not Values.Same(kv.Value, Fx.Item(fr, kv.Key.Name)) Then blocked.Add(kv.Key.Name)
            Next
            Return blocked
        End Function

        ''' 保存一行。成功返回 Nothing, 否则返回原因 (BeforeSaveDataRow 里 e.Cancel 是静默的: 保存后仍是新增或已修改状态)。
        ''' 临时表 (DataTableBuilder、SQLJoinTableBuilder 建的) 没有后台数据库, DataRow.Save 什么也不做: 直接标记为已保存。
        Private Shared Function SaveRow(fr As Foxtable.DataRow) As String
            Try
                fr.Save()
            Catch ex As Exception
                Return "保存失败: " & ex.GetBaseException().Message
            End Try
            Dim st = BaseRow(fr).RowState
            If st = DataRowState.Added OrElse st = DataRowState.Modified Then
                If IsTemporary(fr) Then
                    fr.Accept()
                    Return Nothing
                End If
                Return "保存被系统的校验拦下"
            End If
            Return Nothing
        End Function

        Private Const TEMPORARY_TABLE As Integer = 5

        Private Shared Function IsTemporary(fr As Foxtable.DataRow) As Boolean
            Try
                Return Convert.ToInt32(Fx.Get(Fx.Get(fr, "DataTable"), "Type")) = TEMPORARY_TABLE
            Catch
                Return False
            End Try
        End Function

        ''' 撤销: 新增的删掉, 改过的还原, 已保存的新行删掉并保存。
        Private Shared Sub Discard(fr As Foxtable.DataRow)
            Try
                Dim st = BaseRow(fr).RowState
                Select Case st
                    Case DataRowState.Added
                        fr.Delete()
                    Case DataRowState.Modified
                        fr.RejectChanges()
                    Case DataRowState.Unchanged
                        fr.Delete()
                        fr.Save()
                End Select
            Catch
            End Try
        End Sub
    End Class
End Namespace
