Imports System.Globalization

Namespace AI

    ''' 把工具的结构化筛选条件翻成狐表的筛选表达式 (Table.Filter), 用于"在狐表中打开"。
    ''' 判断口径和 QueryEngine 一致; 翻不了的条件返回 Nothing, 改按行筛。
    Friend Module FilterBuilder

        Private ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Public Function Build(info As TableInfo, where As JToken) As String
            Try
                If where Is Nothing OrElse where.Type = JTokenType.Null Then Return ""
                If where.Type = JTokenType.Object Then where = New JArray(where)
                Dim parts = DirectCast(where, JArray).Select(Function(c) Cond(info, c)).ToList()
                Return String.Join(" And ", parts)
            Catch
                Return Nothing
            End Try
        End Function

        ''' 某列取这些值之一, 如 [订单号] In ('T001','T002')。
        Public Function InList(c As ColumnInfo, values As IEnumerable(Of Object)) As String
            Dim items = values.Where(Function(v) v IsNot Nothing AndAlso Not TypeOf v Is DBNull).Select(Function(v) Literal(c, v)).Distinct().ToList()
            If items.Count = 0 Then Return Nothing
            Return Col(c) & " In (" & String.Join(",", items) & ")"
        End Function

        Private Function Cond(info As TableInfo, tok As JToken) As String
            Dim o = DirectCast(tok, JObject)
            Dim orList = TryCast(o("or"), JArray)
            If orList IsNot Nothing Then Return "(" & String.Join(" Or ", orList.Select(Function(x) Cond(info, x))) & ")"
            Dim c = info.Column(CStr(o("column")))
            If c Is Nothing Then Throw New ArgumentException()
            Dim op = If(CStr(o("op")), "=").Trim().ToLowerInvariant()
            Select Case op
                Case "==", "eq" : op = "="
                Case "<>", "ne" : op = "!="
                Case "like" : op = "contains"
            End Select
            Dim value = o("value")
            Dim values = TryCast(o("values"), JArray)
            If values Is Nothing AndAlso TypeOf value Is JArray Then values = DirectCast(value, JArray)
            Select Case op
                Case "is_empty" : Return Empty(c)
                Case "not_empty" : Return "Not " & Empty(c)
                Case "in", "not_in"
                    If values Is Nothing Then values = New JArray(value)
                    Dim any = "(" & String.Join(" Or ", values.Select(Function(v) Compare(c, "=", v))) & ")"
                    Return If(op = "in", any, "Not " & any)
                Case "between"
                    Return "(" & Compare(c, ">=", values(0)) & " And " & Compare(c, "<=", values(1)) & ")"
                Case Else
                    Return Compare(c, op, value)
            End Select
        End Function

        Private Function Col(c As ColumnInfo) As String
            Return "[" & c.Name.Replace("]", "\]") & "]"
        End Function

        Private Function Empty(c As ColumnInfo) As String
            If c.Kind = ColumnKind.Text Then Return "(" & Col(c) & " Is Null Or Trim(" & Col(c) & ") = '')"
            Return "(" & Col(c) & " Is Null)"
        End Function

        Private Function Quote(s As String) As String
            Return "'" & s.Replace("'", "''") & "'"
        End Function

        ' Like 里的通配符要用方括号转义
        Private Function LikeText(s As String) As String
            Dim sb As New Text.StringBuilder()
            For Each ch In s
                If ch = "*"c OrElse ch = "%"c OrElse ch = "["c OrElse ch = "]"c Then sb.Append("[").Append(ch).Append("]") Else sb.Append(ch)
            Next
            Return sb.ToString().Replace("'", "''")
        End Function

        Private Function DateLit(d As Date) As String
            Return "#" & d.ToString("MM/dd/yyyy HH:mm:ss", Inv) & "#"
        End Function

        Private Function Literal(c As ColumnInfo, v As Object) As String
            Select Case c.Kind
                Case ColumnKind.Integer, ColumnKind.Number : Return Convert.ToDecimal(v, Inv).ToString(Inv)
                Case ColumnKind.Date : Return DateLit(Convert.ToDateTime(v, Inv))
                Case ColumnKind.Boolean : Return If(Convert.ToBoolean(v, Inv), "True", "False")
                Case Else : Return Quote(Convert.ToString(v, Inv))
            End Select
        End Function

        Private Function Compare(c As ColumnInfo, op As String, value As JToken) As String
            If value Is Nothing OrElse value.Type = JTokenType.Null Then Throw New ArgumentException()
            Dim text = value.ToString()
            Dim fld = Col(c)
            Select Case op
                Case "contains" : Return fld & " Like '%" & LikeText(text) & "%'"
                Case "not_contains" : Return "(" & fld & " Is Null Or " & fld & " Not Like '%" & LikeText(text) & "%')"
                Case "starts_with" : Return fld & " Like '" & LikeText(text) & "%'"
                Case "ends_with" : Return fld & " Like '%" & LikeText(text) & "'"
            End Select
            Select Case c.Kind
                Case ColumnKind.Integer, ColumnKind.Number
                    Dim d = Decimal.Parse(text, NumberStyles.Any, Inv).ToString(Inv)
                    If op = "!=" Then Return "(" & fld & " Is Null Or " & fld & " <> " & d & ")"
                    Return fld & " " & op & " " & d
                Case ColumnKind.Date
                    Dim range = QueryEngine.ParseDateRange(text)
                    If range Is Nothing Then Throw New ArgumentException()
                    Dim s = DateLit(range.Item1), e = DateLit(range.Item2)
                    Select Case op
                        Case "=" : Return "(" & fld & " >= " & s & " And " & fld & " < " & e & ")"
                        Case "!=" : Return "(" & fld & " Is Null Or " & fld & " < " & s & " Or " & fld & " >= " & e & ")"
                        Case ">" : Return fld & " >= " & e
                        Case ">=" : Return fld & " >= " & s
                        Case "<" : Return fld & " < " & s
                        Case Else : Return fld & " < " & e
                    End Select
                Case ColumnKind.Boolean
                    Dim b = Values.ParseBool(text)
                    If Not b.HasValue Then Throw New ArgumentException()
                    Dim eq = If(b.Value, fld & " = True", "(" & fld & " = False Or " & fld & " Is Null)")
                    Return If(op = "!=", "Not " & eq, eq)
                Case Else
                    Dim q = Quote(text.Trim())
                    If op = "!=" Then Return "(" & fld & " Is Null Or " & fld & " <> " & q & ")"
                    Return fld & " " & op & " " & q
            End Select
        End Function
    End Module
End Namespace
