Imports System.Globalization

Namespace AI

    ''' 把模型或文件给的值转成列的类型, 以及写入后的回读比较。
    Friend Module Values

        Private ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Private ReadOnly DateFormats As String() = {
            "yyyy-M-d", "yyyy-MM-dd", "yyyy/M/d", "yyyy.M.d", "yyyyMMdd", "yyyy年M月d日",
            "yyyy-M-d H:m", "yyyy-M-d H:m:s", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy/M/d H:m", "yyyy/M/d H:m:s",
            "yyyy年M月d日 H:m", "yyyy年M月d日 H时m分"}

        ''' 转成列的类型。空值返回 DBNull.Value。转不了抛 ToolArgumentException。
        Public Function ToColumn(c As ColumnInfo, v As JToken) As Object
            If v Is Nothing OrElse v.Type = JTokenType.Null OrElse v.Type = JTokenType.Undefined Then Return DBNull.Value
            If v.Type = JTokenType.Object OrElse v.Type = JTokenType.Array Then
                Throw New ToolArgumentException("列 " & c.Name & " 的值应是单个值, 不能是对象或数组。")
            End If
            If v.Type = JTokenType.Date Then Return FromObject(c, v.ToObject(Of Date)())
            Return FromObject(c, DirectCast(v, JValue).Value)
        End Function

        ''' 转成列的类型 (值来自文件单元格或 JSON)。
        Public Function FromObject(c As ColumnInfo, v As Object) As Object
            If v Is Nothing OrElse TypeOf v Is DBNull Then Return DBNull.Value
            Dim s = If(TypeOf v Is String, DirectCast(v, String).Trim(), Nothing)
            If s IsNot Nothing AndAlso s = "" Then Return DBNull.Value
            Select Case c.Kind
                Case ColumnKind.Text
                    Dim text = If(TypeOf v Is Date, FormatDate(DirectCast(v, Date)), If(TypeOf v Is IFormattable, DirectCast(v, IFormattable).ToString(Nothing, Inv), v.ToString()))
                    If c.MaxLength > 0 AndAlso text.Length > c.MaxLength Then
                        Throw New ToolArgumentException("列 " & c.Name & " 最多 " & c.MaxLength & " 个字, 给的值有 " & text.Length & " 个字。")
                    End If
                    Return text
                Case ColumnKind.Integer, ColumnKind.Number
                    Dim d As Decimal
                    If s IsNot Nothing Then
                        If Not TryNumber(s, d) Then Throw New ToolArgumentException("列 " & c.Name & " 要数字, """ & s & """ 不是数字。")
                    ElseIf TypeOf v Is Boolean Then
                        Throw New ToolArgumentException("列 " & c.Name & " 要数字。")
                    Else
                        Try
                            d = Convert.ToDecimal(v, Inv)
                        Catch ex As Exception
                            Throw New ToolArgumentException("列 " & c.Name & " 要数字。")
                        End Try
                    End If
                    If c.Kind = ColumnKind.Integer AndAlso d <> Decimal.Truncate(d) Then
                        Throw New ToolArgumentException("列 " & c.Name & " 要整数, 给的是 " & d.ToString(Inv) & "。")
                    End If
                    Try
                        Return Convert.ChangeType(d, If(c.DataType, GetType(Decimal)), Inv)
                    Catch ex As OverflowException
                        Throw New ToolArgumentException("列 " & c.Name & " 的值 " & d.ToString(Inv) & " 超出范围。")
                    End Try
                Case ColumnKind.Date
                    If TypeOf v Is Date Then Return v
                    If s Is Nothing Then
                        ' Excel 里的日期序号
                        Try
                            Dim n = Convert.ToDouble(v, Inv)
                            If n > 0 AndAlso n < 2958466 Then Return Date.FromOADate(n)
                        Catch
                        End Try
                        Throw New ToolArgumentException("列 " & c.Name & " 要日期。")
                    End If
                    Dim dt As Date
                    If Date.TryParseExact(s, DateFormats, Inv, DateTimeStyles.AllowWhiteSpaces, dt) OrElse
                       Date.TryParse(s, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.AllowWhiteSpaces, dt) Then Return dt
                    Throw New ToolArgumentException("列 " & c.Name & " 要日期 (如 2026-03-05), """ & s & """ 认不出。")
                Case ColumnKind.Boolean
                    If TypeOf v Is Boolean Then Return v
                    Dim b = ParseBool(If(s, Convert.ToString(v, Inv)))
                    If Not b.HasValue Then Throw New ToolArgumentException("列 " & c.Name & " 要 是/否 (true/false)。")
                    Return b.Value
                Case Else
                    Throw New ToolArgumentException("列 " & c.Name & " 的类型不支持写入。")
            End Select
        End Function

        Private Function TryNumber(s As String, ByRef d As Decimal) As Boolean
            Dim t = s.Replace(",", "").Replace("，", "").Replace("￥", "").Replace("¥", "").Replace("元", "").Replace(" ", "").Trim()
            Dim pct = t.EndsWith("%", StringComparison.Ordinal)
            If pct Then t = t.TrimEnd("%"c)
            If Not Decimal.TryParse(t, NumberStyles.Number Or NumberStyles.AllowExponent, Inv, d) Then Return False
            If pct Then d /= 100D
            Return True
        End Function

        Public Function ParseBool(s As String) As Boolean?
            Select Case If(s, "").Trim().ToLowerInvariant()
                Case "true", "1", "是", "y", "yes", "对", "√", "已", "t" : Return True
                Case "false", "0", "否", "n", "no", "不", "×", "未", "f" : Return False
            End Select
            Return Nothing
        End Function

        Public Function FormatDate(d As Date) As String
            Return d.ToString(If(d.TimeOfDay = TimeSpan.Zero, "yyyy-MM-dd", "yyyy-MM-dd HH:mm"), Inv)
        End Function

        ''' 显示用 (确认卡片、给模型的结果)。
        Public Function Display(v As Object) As JToken
            If v Is Nothing OrElse TypeOf v Is DBNull Then Return JValue.CreateNull()
            If TypeOf v Is Date Then Return FormatDate(DirectCast(v, Date))
            If TypeOf v Is Boolean Then Return CBool(v)
            Select Case Type.GetTypeCode(v.GetType())
                Case TypeCode.Byte, TypeCode.SByte, TypeCode.Int16, TypeCode.Int32, TypeCode.Int64, TypeCode.UInt16, TypeCode.UInt32
                    Return Convert.ToInt64(v)
                Case TypeCode.Decimal, TypeCode.Double, TypeCode.Single
                    Return Math.Round(Convert.ToDecimal(v), 6)
            End Select
            Return v.ToString()
        End Function

        ''' 显示用文本。
        Public Function Text(v As Object) As String
            Dim t = Display(v)
            If t.Type = JTokenType.Null Then Return ""
            If t.Type = JTokenType.Boolean Then Return If(CBool(t), "是", "否")
            Return CStr(t)
        End Function

        ''' 回读比较: 写进去的值和读出来的是不是一回事。
        Public Function Same(wanted As Object, actual As Object) As Boolean
            Dim wn = wanted Is Nothing OrElse TypeOf wanted Is DBNull
            Dim an = actual Is Nothing OrElse TypeOf actual Is DBNull
            If wn OrElse an Then Return wn AndAlso an
            If TypeOf wanted Is String OrElse TypeOf actual Is String Then Return String.Equals(Convert.ToString(wanted, Inv), Convert.ToString(actual, Inv), StringComparison.Ordinal)
            If TypeOf wanted Is Date AndAlso TypeOf actual Is Date Then Return DirectCast(wanted, Date) = DirectCast(actual, Date)
            Try
                Return Convert.ToDecimal(wanted, Inv) = Convert.ToDecimal(actual, Inv)
            Catch
                Return Equals(wanted, actual)
            End Try
        End Function
    End Module
End Namespace
