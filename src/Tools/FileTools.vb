Namespace AI

    ''' 读用户上传的表格文件 (提问时只给了前 20 行)。
    Friend NotInheritable Class FileTools
        Implements IToolSet

        Public Const READ_FILE As String = "read_file"
        Private Const MAX_ROWS As Integer = 200

        Private ReadOnly _files As AttachmentStore

        Public Sub New(files As AttachmentStore)
            _files = files
        End Sub

        Public Function Owns(name As String) As Boolean Implements IToolSet.Owns
            Return name = READ_FILE AndAlso _files IsNot Nothing AndAlso _files.HasSheets
        End Function

        Public Function Definitions() As List(Of JObject) Implements IToolSet.Definitions
            Dim list As New List(Of JObject)
            If Not Owns(READ_FILE) Then Return list
            list.Add(ReadTools.Fn(READ_FILE,
                "读用户上传的表格文件的若干行 (提问时只附了前 20 行), 一次最多 " & MAX_ROWS & " 行。要导入时不必读完, 用 import_rows 按列对应导入。",
                New JObject From {
                    {"file", New JObject From {{"type", "string"}, {"description", "文件编号, 如 f1"}}},
                    {"sheet", New JObject From {{"type", "string"}, {"description", "工作表名, 不填为第一个"}}},
                    {"start_row", New JObject From {{"type", "integer"}, {"description", "从第几行开始, 默认 1"}}},
                    {"count", New JObject From {{"type", "integer"}, {"description", "读几行, 默认 50"}}}},
                "file"))
            Return list
        End Function

        Public Function Label(name As String, args As JObject) As String Implements IToolSet.Label
            Dim f = _files.Find(CStr(args("file")))
            Return "读取文件" & If(f Is Nothing, "", " " & f.Name)
        End Function

        Public Function Execute(name As String, args As JObject, ctx As ToolContext) As ToolOutcome Implements IToolSet.Execute
            Dim f = _files.Get(CStr(args("file")))
            Dim s = f.Sheet(CStr(args("sheet")))
            Dim start = If(args("start_row") Is Nothing OrElse args("start_row").Type = JTokenType.Null, 1, Math.Max(1, CInt(args("start_row"))))
            Dim count = If(args("count") Is Nothing OrElse args("count").Type = JTokenType.Null, 50, Math.Min(MAX_ROWS, Math.Max(1, CInt(args("count")))))
            Dim last = Math.Min(s.Rows.Count, start + count - 1)
            Dim res As New JObject From {
                {"file", f.Id}, {"sheet", s.Name}, {"total_rows", s.Rows.Count},
                {"rows", s.Format(start, count)}}
            If last < s.Rows.Count Then res("note") = "还有 " & (s.Rows.Count - last) & " 行没读。"
            Return New ToolOutcome(res, If(last >= start, "第 " & start & "–" & last & " 行", "没有更多行"))
        End Function
    End Class
End Namespace
