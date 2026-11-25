Imports System.Globalization
Imports System.IO
Imports System.Text

Namespace AI

    ''' 用户上传的一个文件: 图片 (交给模型看) 或表格 (Excel / CSV, 由我们读出来交给模型, 导入时按映射逐行处理)。
    Friend NotInheritable Class Attachment
        Public Id As String
        Public Name As String
        ''' "image" 或 "sheet"
        Public Kind As String
        ''' 图片: data:image/...;base64,...
        Public DataUrl As String
        Public Sheets As New List(Of SheetData)

        Public ReadOnly Property Summary As String
            Get
                If Kind = "image" Then Return "图片"
                Return String.Join("; ", Sheets.Select(Function(s) s.Name & " " & s.Rows.Count & " 行"))
            End Get
        End Property

        ''' 给网页: 图片带上原图 (网页上传前已缩小), 表格只给摘要。
        Public Function ToJson() As JObject
            Dim o As New JObject From {{"id", Id}, {"name", Name}, {"kind", Kind}, {"summary", Summary}}
            If Kind = "image" Then o("thumb") = DataUrl
            Return o
        End Function

        ''' 给模型看的说明: 工作表、行数和前几行。
        Public Function Describe(Optional previewRows As Integer = 20) As String
            Dim sb As New StringBuilder()
            sb.Append("[文件 ").Append(Id).Append("「").Append(Name).Append("」")
            If Kind = "image" Then
                sb.AppendLine(" 图片, 见下]")
                Return sb.ToString()
            End If
            sb.Append(" 共 ").Append(Sheets.Count).AppendLine(" 个工作表]")
            For Each s In Sheets
                sb.Append("工作表「").Append(s.Name).Append("」共 ").Append(s.Rows.Count).Append(" 行 ").Append(s.ColCount).AppendLine(" 列。")
                If s.Rows.Count = 0 Then Continue For
                Dim n = Math.Min(previewRows, s.Rows.Count)
                sb.Append("前 ").Append(n).AppendLine(" 行 (行号: 各列用 | 分隔):")
                sb.Append(s.Format(1, n))
                If s.Rows.Count > n Then sb.AppendLine("…(更多行用 read_file 读)")
            Next
            Return sb.ToString()
        End Function

        Public Function Sheet(name As String) As SheetData
            If Sheets.Count = 0 Then Throw New ToolArgumentException("文件 " & Name & " 不是表格。")
            If String.IsNullOrWhiteSpace(name) Then Return Sheets(0)
            Dim s = Sheets.FirstOrDefault(Function(x) String.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            If s Is Nothing Then
                Dim idx As Integer
                If Integer.TryParse(name, idx) AndAlso idx >= 1 AndAlso idx <= Sheets.Count Then Return Sheets(idx - 1)
                Throw New ToolArgumentException("文件 " & Me.Name & " 里没有工作表 """ & name & """。有: " & String.Join("、", Sheets.Select(Function(x) x.Name)))
            End If
            Return s
        End Function
    End Class

    ''' 一个工作表: 单元格一律读成文本 (日期 yyyy-MM-dd, 数字不带千分位)。
    Friend NotInheritable Class SheetData
        Public Name As String
        Public Rows As New List(Of String())
        Public ColCount As Integer

        Public Function Cell(row As Integer, col As Integer) As String
            If row < 1 OrElse row > Rows.Count Then Return ""
            Dim r = Rows(row - 1)
            Return If(col >= 0 AndAlso col < r.Length, If(r(col), ""), "")
        End Function

        ''' 第 first 行起的 count 行, 每行 "行号: a | b | c"。
        Public Function Format(first As Integer, count As Integer) As String
            Dim sb As New StringBuilder()
            Dim cols = Math.Min(ColCount, 40)
            For i = Math.Max(1, first) To Math.Min(Rows.Count, first + count - 1)
                sb.Append(i).Append(": ")
                For j = 0 To cols - 1
                    If j > 0 Then sb.Append(" | ")
                    Dim v = Cell(i, j)
                    If v.Length > 60 Then v = v.Substring(0, 60) & "…"
                    sb.Append(v.Replace(vbCr, " ").Replace(vbLf, " "))
                Next
                sb.AppendLine()
            Next
            Return sb.ToString()
        End Function
    End Class

    ''' 一段对话里上传的文件。线程安全。
    Friend NotInheritable Class AttachmentStore
        Private ReadOnly _lock As New Object
        Private ReadOnly _items As New List(Of Attachment)
        Private _seq As Integer

        Public Function Add(a As Attachment) As Attachment
            SyncLock _lock
                _seq += 1
                a.Id = "f" & _seq
                _items.Add(a)
            End SyncLock
            Return a
        End Function

        Public Function Find(idOrName As String) As Attachment
            If String.IsNullOrWhiteSpace(idOrName) Then Return Nothing
            Dim key = idOrName.Trim()
            SyncLock _lock
                Return If(_items.FirstOrDefault(Function(x) String.Equals(x.Id, key, StringComparison.OrdinalIgnoreCase)),
                          _items.LastOrDefault(Function(x) String.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase)))
            End SyncLock
        End Function

        Public Function [Get](idOrName As String) As Attachment
            Dim a = Find(idOrName)
            If a Is Nothing Then Throw New ToolArgumentException("没有文件 """ & idOrName & """。用户上传的文件: " & String.Join("、", All().Select(Function(x) x.Id & "「" & x.Name & "」")))
            Return a
        End Function

        Public Function All() As List(Of Attachment)
            SyncLock _lock
                Return _items.ToList()
            End SyncLock
        End Function

        Public ReadOnly Property HasSheets As Boolean
            Get
                SyncLock _lock
                    Return _items.Any(Function(x) x.Kind = "sheet")
                End SyncLock
            End Get
        End Property

        Public Sub Clear()
            SyncLock _lock
                _items.Clear()
                _seq = 0
            End SyncLock
        End Sub
    End Class

    ''' 解析上传的文件。
    Friend Module AttachmentReader

        Public Const MAX_BYTES As Integer = 10 * 1024 * 1024
        Private Const MAX_ROWS As Integer = 20000
        Private Const MAX_COLS As Integer = 100

        ''' 图片: 网页已缩小并转成 data URL。
        Public Function Image(name As String, dataUrl As String) As Attachment
            If dataUrl Is Nothing OrElse Not dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) OrElse Not dataUrl.Contains(";base64,") Then
                Throw New InvalidDataException("不是图片。")
            End If
            If dataUrl.Length > MAX_BYTES * 4 \ 3 Then Throw New InvalidDataException("图片太大。")
            Return New Attachment With {.Name = If(String.IsNullOrWhiteSpace(name), "图片", name), .Kind = "image", .DataUrl = dataUrl}
        End Function

        ''' 图片文件 (代码里附上的, 如移动端收到的照片): 长边缩到 1600 以内, 转成 JPEG。
        Public Function ImageFile(name As String, data As Byte()) As Attachment
            If data Is Nothing OrElse data.Length = 0 Then Throw New InvalidDataException("文件是空的。")
            Using ms As New MemoryStream(data)
                Dim img As Drawing.Image
                Try
                    img = Drawing.Image.FromStream(ms)
                Catch ex As ArgumentException
                    Throw New InvalidDataException("认不出这张图片。")
                End Try
                Using img
                    Dim scale = Math.Min(1.0, 1600.0 / Math.Max(img.Width, img.Height))
                    Dim w = Math.Max(1, CInt(img.Width * scale)), h = Math.Max(1, CInt(img.Height * scale))
                    Using bmp As New Drawing.Bitmap(w, h)
                        Using g = Drawing.Graphics.FromImage(bmp)
                            g.Clear(Drawing.Color.White)
                            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic
                            g.DrawImage(img, 0, 0, w, h)
                        End Using
                        Dim codec = Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(Function(c) c.MimeType = "image/jpeg")
                        Dim ps As New Drawing.Imaging.EncoderParameters(1)
                        ps.Param(0) = New Drawing.Imaging.EncoderParameter(Drawing.Imaging.Encoder.Quality, 85L)
                        Using outMs As New MemoryStream()
                            bmp.Save(outMs, codec, ps)
                            Return Image(name, "data:image/jpeg;base64," & Convert.ToBase64String(outMs.ToArray()))
                        End Using
                    End Using
                End Using
            End Using
        End Function

        ''' 表格: .xlsx / .xls 用狐表自带的 XLS 组件读, .csv / .txt 按文本读。在界面线程上调用。
        Public Function Sheet(name As String, data As Byte()) As Attachment
            If data Is Nothing OrElse data.Length = 0 Then Throw New InvalidDataException("文件是空的。")
            If data.Length > MAX_BYTES Then Throw New InvalidDataException("文件超过 10 MB。")
            Dim ext = Path.GetExtension(If(name, "")).ToLowerInvariant()
            Dim a As New Attachment With {.Name = name, .Kind = "sheet"}
            Select Case ext
                Case ".xlsx", ".xls"
                    a.Sheets.AddRange(ReadExcel(data, ext))
                Case ".csv", ".txt", ".tsv"
                    a.Sheets.Add(ReadCsv(data, If(ext = ".tsv", ControlChars.Tab, ","c)))
                Case Else
                    Throw New InvalidDataException("只支持 Excel (.xlsx/.xls) 和 CSV 文件。")
            End Select
            If a.Sheets.All(Function(s) s.Rows.Count = 0) Then Throw New InvalidDataException("文件里没有数据。")
            Return a
        End Function

        Private Function ReadExcel(data As Byte(), ext As String) As List(Of SheetData)
            Dim tmp = Path.Combine(Path.GetTempPath(), "proai-" & Guid.NewGuid().ToString("N") & ext)
            File.WriteAllBytes(tmp, data)
            Try
                Dim bookType = Fx.FoxtableAssembly.GetType("Foxtable.XLS.Book")
                If bookType Is Nothing Then Throw New InvalidDataException("当前狐表没有 Excel 组件。")
                Dim book As Object
                Try
                    book = Activator.CreateInstance(bookType, tmp)
                Catch ex As Exception
                    Throw New InvalidDataException("读不了这个 Excel 文件: " & ex.GetBaseException().Message)
                End Try
                Dim res As New List(Of SheetData)
                Dim sheets = Fx.Get(book, "Sheets")
                Dim n = CInt(Fx.Get(sheets, "Count"))
                For i = 0 To n - 1
                    Dim sh = Fx.ItemAt(sheets, i)
                    If Equals(Fx.TryGet(sh, "Visible"), False) Then Continue For
                    Dim sd As New SheetData With {.Name = Fx.GetStr(sh, "Name")}
                    Dim rows = Math.Min(MAX_ROWS, CInt(Fx.Get(Fx.Get(sh, "Rows"), "Count")))
                    Dim cols = Math.Min(MAX_COLS, CInt(Fx.Get(Fx.Get(sh, "Cols"), "Count")))
                    For r = 0 To rows - 1
                        Dim line(cols - 1) As String
                        For c = 0 To cols - 1
                            line(c) = CellText(Fx.TryGet(Fx.ItemAt(sh, r, c), "Value"))
                        Next
                        sd.Rows.Add(line)
                    Next
                    Normalize(sd)
                    res.Add(sd)
                Next
                Return res
            Finally
                Try
                    File.Delete(tmp)
                Catch
                End Try
            End Try
        End Function

        Private Function CellText(v As Object) As String
            If v Is Nothing OrElse TypeOf v Is DBNull Then Return ""
            If TypeOf v Is Date Then Return Values.FormatDate(DirectCast(v, Date))
            If TypeOf v Is Double OrElse TypeOf v Is Single OrElse TypeOf v Is Decimal Then
                Return Convert.ToDecimal(v).ToString("0.##########", CultureInfo.InvariantCulture)
            End If
            If TypeOf v Is Boolean Then Return If(CBool(v), "是", "否")
            Return v.ToString().Trim()
        End Function

        ' 去掉末尾的空行、空列
        Private Sub Normalize(sd As SheetData)
            While sd.Rows.Count > 0 AndAlso sd.Rows(sd.Rows.Count - 1).All(Function(x) String.IsNullOrEmpty(x))
                sd.Rows.RemoveAt(sd.Rows.Count - 1)
            End While
            Dim width = 0
            For Each r In sd.Rows
                For j = r.Length - 1 To 0 Step -1
                    If Not String.IsNullOrEmpty(r(j)) Then width = Math.Max(width, j + 1) : Exit For
                Next
            Next
            sd.ColCount = width
            For i = 0 To sd.Rows.Count - 1
                If sd.Rows(i).Length <> width Then
                    Dim r = sd.Rows(i)
                    Array.Resize(r, width)
                    sd.Rows(i) = r
                End If
            Next
        End Sub

        Private Function ReadCsv(data As Byte(), sep As Char) As SheetData
            Dim text = Decode(data)
            Dim sd As New SheetData With {.Name = "Sheet1"}
            Dim row As New List(Of String)
            Dim cell As New StringBuilder()
            Dim quoted = False
            Dim i = 0
            While i < text.Length
                Dim ch = text(i)
                If quoted Then
                    If ch = """"c Then
                        If i + 1 < text.Length AndAlso text(i + 1) = """"c Then
                            cell.Append(""""c) : i += 1
                        Else
                            quoted = False
                        End If
                    Else
                        cell.Append(ch)
                    End If
                ElseIf ch = """"c AndAlso cell.Length = 0 Then
                    quoted = True
                ElseIf ch = sep Then
                    row.Add(cell.ToString().Trim()) : cell.Clear()
                ElseIf ch = ControlChars.Cr OrElse ch = ControlChars.Lf Then
                    If ch = ControlChars.Cr AndAlso i + 1 < text.Length AndAlso text(i + 1) = ControlChars.Lf Then i += 1
                    row.Add(cell.ToString().Trim()) : cell.Clear()
                    sd.Rows.Add(row.ToArray()) : row.Clear()
                    If sd.Rows.Count >= MAX_ROWS Then Exit While
                Else
                    cell.Append(ch)
                End If
                i += 1
            End While
            If cell.Length > 0 OrElse row.Count > 0 Then
                row.Add(cell.ToString().Trim())
                sd.Rows.Add(row.ToArray())
            End If
            Normalize(sd)
            Return sd
        End Function

        ' UTF-8 (带不带 BOM) 优先, 不是合法 UTF-8 就按 GBK
        Private Function Decode(data As Byte()) As String
            If data.Length >= 3 AndAlso data(0) = &HEF AndAlso data(1) = &HBB AndAlso data(2) = &HBF Then Return Encoding.UTF8.GetString(data, 3, data.Length - 3)
            If data.Length >= 2 AndAlso data(0) = &HFF AndAlso data(1) = &HFE Then Return Encoding.Unicode.GetString(data, 2, data.Length - 2)
            Try
                Return New UTF8Encoding(False, True).GetString(data)
            Catch ex As DecoderFallbackException
                Return Encoding.GetEncoding(936).GetString(data)
            End Try
        End Function
    End Module
End Namespace
