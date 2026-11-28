Imports System.IO
Imports System.Text

Namespace AI

    ''' 技能库里的一个文件。
    Friend NotInheritable Class SkillFile
        ''' 相对技能库目录的路径, 用 / 分隔, 如 "销售/SKILL.md"。
        Public Path As String
        Public Name As String
        Public Description As String
        ''' 只给这些角色看; 为空时所有人可看。目录下的 SKILL.md 限定了角色, 同目录及子目录的文件一并限定。
        Public Roles As New List(Of String)
    End Class

    ''' 本轮可用的技能库: 系统概况 (ai/SKILL.md 正文) 和这个用户能看的技能目录。
    Friend NotInheritable Class SkillCatalog
        Public Folder As String
        Public Overview As String = ""
        Public Entries As New List(Of SkillFile)
        ''' 这个用户能读的全部文件 (含没有 description、不进目录的参考文件)。
        Public Readable As New Dictionary(Of String, SkillFile)(StringComparer.OrdinalIgnoreCase)

        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return Readable.Count = 0
            End Get
        End Property
    End Class

    ''' 项目技能库: 工程目录下的 ai/ 目录, 格式同 Claude Code 的技能 (SKILL.md + frontmatter)。
    '''
    ''' - ai/SKILL.md: 系统概况、术语、红线, 正文每轮放进提示词。
    ''' - 其余 .md: 有 description 的进技能目录 (AI 用 list_skills 查看), AI 按需用 read_skill 读; 没有的只能被引用后读。
    ''' - frontmatter 里 roles: 经理, 财务 表示只给这些角色看。
    ''' 只读 .md / .txt, 只读技能库目录里的文件。
    Friend Module SkillLibrary

        Public Const OVERVIEW_FILE As String = "SKILL.md"
        Private Const MAX_FILES As Integer = 500
        Private Const MAX_DEPTH As Integer = 4
        Private Const MAX_FILE_BYTES As Long = 1024 * 1024
        Private ReadOnly Extensions As String() = {".md", ".txt"}

        ''' 扫描技能库, 只留这个用户能看的。目录不存在时返回空目录。
        Public Function Scan(folder As String, who As Who) As SkillCatalog
            Dim cat As New SkillCatalog With {.Folder = folder}
            If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then Return cat

            Dim files As New List(Of SkillFile)
            Dim dirRoles As New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)
            Collect(folder, "", 0, files, dirRoles)

            For Each f In files
                ' 继承所在目录及上级目录 SKILL.md 的角色限定
                Dim dir = DirOf(f.Path)
                While True
                    Dim r As List(Of String) = Nothing
                    If dirRoles.TryGetValue(dir, r) AndAlso f.Roles.Count = 0 Then f.Roles.AddRange(r)
                    If dir = "" Then Exit While
                    dir = DirOf(dir)
                End While
                If f.Roles.Count > 0 AndAlso Not who.HasAnyRole(f.Roles) Then Continue For
                cat.Readable(f.Path) = f
                If String.Equals(f.Path, OVERVIEW_FILE, StringComparison.OrdinalIgnoreCase) Then Continue For
                If Not String.IsNullOrWhiteSpace(f.Description) Then cat.Entries.Add(f)
            Next

            Dim ov As SkillFile = Nothing
            If cat.Readable.TryGetValue(OVERVIEW_FILE, ov) Then cat.Overview = Body(ReadText(Path.Combine(folder, OVERVIEW_FILE))).Trim()
            Return cat
        End Function

        Private Sub Collect(root As String, rel As String, depth As Integer, files As List(Of SkillFile),
                            dirRoles As Dictionary(Of String, List(Of String)))
            If depth > MAX_DEPTH OrElse files.Count >= MAX_FILES Then Return
            Dim full = If(rel = "", root, Path.Combine(root, rel.Replace("/"c, Path.DirectorySeparatorChar)))
            Dim di As New DirectoryInfo(full)
            ' 不跟随目录联接 / 符号链接, 免得读到技能库以外
            If depth > 0 AndAlso (di.Attributes And FileAttributes.ReparsePoint) <> 0 Then Return
            For Each fi In di.GetFiles().OrderBy(Function(x) x.Name, StringComparer.OrdinalIgnoreCase)
                If files.Count >= MAX_FILES Then Return
                If Not Extensions.Contains(fi.Extension.ToLowerInvariant()) Then Continue For
                If (fi.Attributes And (FileAttributes.ReparsePoint Or FileAttributes.Hidden)) <> 0 OrElse fi.Length > MAX_FILE_BYTES Then Continue For
                Dim relPath = If(rel = "", fi.Name, rel & "/" & fi.Name)
                Dim meta = FrontMatter(ReadHead(fi.FullName))
                Dim f As New SkillFile With {.Path = relPath}
                meta.TryGetValue("name", f.Name)
                meta.TryGetValue("description", f.Description)
                Dim roles As String = Nothing
                If meta.TryGetValue("roles", roles) Then f.Roles = Who.SplitRoles({roles.Trim("["c, "]"c)})
                If f.Roles.Count > 0 AndAlso String.Equals(fi.Name, OVERVIEW_FILE, StringComparison.OrdinalIgnoreCase) AndAlso rel <> "" Then
                    dirRoles(rel) = f.Roles
                End If
                files.Add(f)
            Next
            For Each sub_ In di.GetDirectories().OrderBy(Function(x) x.Name, StringComparer.OrdinalIgnoreCase)
                If sub_.Name.StartsWith(".") Then Continue For
                Collect(root, If(rel = "", sub_.Name, rel & "/" & sub_.Name), depth + 1, files, dirRoles)
            Next
        End Sub

        Private Function DirOf(relPath As String) As String
            Dim i = relPath.LastIndexOf("/"c)
            Return If(i < 0, "", relPath.Substring(0, i))
        End Function

        ''' 读技能库里的一个文件。路径不合法、不在目录里、这个用户看不到时抛 ToolArgumentException。
        Public Function Read(cat As SkillCatalog, relPath As String) As String
            Dim key = Normalize(relPath)
            Dim f As SkillFile = Nothing
            If key Is Nothing OrElse Not cat.Readable.TryGetValue(key, f) Then
                Throw New ToolArgumentException("技能库里没有 """ & relPath & """, 或你无权查看。可读的文件: " &
                                                String.Join("、", cat.Readable.Keys.Take(40)))
            End If
            Dim full = Path.GetFullPath(Path.Combine(cat.Folder, f.Path.Replace("/"c, Path.DirectorySeparatorChar)))
            Dim root = Path.GetFullPath(cat.Folder).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
            If Not full.StartsWith(root, StringComparison.OrdinalIgnoreCase) Then Throw New ToolArgumentException("只能读技能库目录里的文件。")
            Return ReadText(full)
        End Function

        ''' 规范成 a/b.md; 绝对路径、带 .. 或盘符的一律不认。
        Private Function Normalize(p As String) As String
            If String.IsNullOrWhiteSpace(p) Then Return Nothing
            Dim s = p.Trim().Replace("\"c, "/"c)
            If s.StartsWith("ai/", StringComparison.OrdinalIgnoreCase) Then s = s.Substring(3)
            s = s.TrimStart("/"c)
            If s.Contains(":") Then Return Nothing
            Dim parts = s.Split({"/"c}, StringSplitOptions.RemoveEmptyEntries).Where(Function(x) x <> ".").ToArray()
            If parts.Length = 0 OrElse parts.Any(Function(x) x = "..") Then Return Nothing
            Return String.Join("/", parts)
        End Function

        Private Function ReadText(full As String) As String
            Return File.ReadAllText(full, Encoding.UTF8)
        End Function

        Private Function ReadHead(full As String) As String
            Using r As New StreamReader(full, Encoding.UTF8, True)
                Dim buf(4095) As Char
                Dim n = r.ReadBlock(buf, 0, buf.Length)
                Return New String(buf, 0, n)
            End Using
        End Function

        ''' 文件开头 --- 之间的 key: value。值可以续行 (缩进), 支持 description: > / | 的写法。
        Public Function FrontMatter(text As String) As Dictionary(Of String, String)
            Dim res As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim lines = text.TrimStart(ChrW(&HFEFF)).Replace(vbCrLf, vbLf).Split(ChrW(10))
            If lines.Length = 0 OrElse lines(0).Trim() <> "---" Then Return res
            Dim key As String = Nothing
            For i = 1 To lines.Length - 1
                Dim line = lines(i)
                If line.Trim() = "---" Then Exit For
                If line.Length > 0 AndAlso Not Char.IsWhiteSpace(line(0)) AndAlso line.Contains(":") Then
                    Dim k = line.Substring(0, line.IndexOf(":"c)).Trim()
                    Dim v = line.Substring(line.IndexOf(":"c) + 1).Trim()
                    If v = ">" OrElse v = "|" OrElse v = ">-" OrElse v = "|-" Then v = ""
                    key = k
                    res(k) = Unquote(v)
                ElseIf key IsNot Nothing AndAlso line.Trim() <> "" Then
                    res(key) = (res(key) & " " & line.Trim()).Trim()
                End If
            Next
            Return res
        End Function

        Private Function Unquote(v As String) As String
            If v.Length >= 2 AndAlso ((v.StartsWith("""") AndAlso v.EndsWith("""")) OrElse (v.StartsWith("'") AndAlso v.EndsWith("'"))) Then
                Return v.Substring(1, v.Length - 2)
            End If
            Return v
        End Function

        ''' 去掉 frontmatter 后的正文。
        Public Function Body(text As String) As String
            Dim t = text.TrimStart(ChrW(&HFEFF)).Replace(vbCrLf, vbLf)
            If Not t.StartsWith("---" & vbLf) Then Return t
            Dim endAt = t.IndexOf(vbLf & "---", 4, StringComparison.Ordinal)
            If endAt < 0 Then Return t
            Dim after = t.IndexOf(vbLf, endAt + 4, StringComparison.Ordinal)
            Return If(after < 0, "", t.Substring(after + 1))
        End Function

        ''' list_skills 的结果: 每个技能一行, 如 "销售/SKILL.md (销售): 销售额口径、退货处理"。
        Public Function Listing(cat As SkillCatalog) As List(Of String)
            Dim res As New List(Of String)
            For Each e In cat.Entries
                Dim sb As New StringBuilder(e.Path)
                If Not String.IsNullOrWhiteSpace(e.Name) Then sb.Append(" (").Append(e.Name.Trim()).Append(")")
                sb.Append(": ").Append(e.Description.Trim())
                res.Add(sb.ToString())
            Next
            Return res
        End Function
    End Module
End Namespace
