Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Threading.Tasks

Namespace AI

    ''' 语音转文字: multipart 上传 file (和 model, 有时), 返回 {"text": ...}。
    ''' DeepSeek 的 /v1/tools/asr 和 OpenAI 兼容的 /audio/transcriptions 都是这个格式。
    Friend NotInheritable Class SpeechClient

        Private Const MAX_BASE64 As Integer = 16 * 1024 * 1024
        Private Shared ReadOnly Http As New HttpClient With {.Timeout = TimeSpan.FromSeconds(60)}

        Private ReadOnly _url As String
        Private ReadOnly _model As String
        Private ReadOnly _key As String

        Public Sub New(url As String, model As String, key As String)
            _url = url
            _model = If(model, "")
            _key = If(key, "")
        End Sub

        ''' 识别网页发来的录音 (WAV 的 base64), 结果作为 transcript 消息放进发件箱。在界面线程上调用, 识别在后台进行。
        Public Sub Transcribe(id As String, data As String, outbox As Outbox)
            Dim reply = Sub(text As String, err As String)
                            Dim evt As New JObject From {{"type", "transcript"}, {"id", id}}
                            If err IsNot Nothing Then evt("error") = err Else evt("text") = If(text, "")
                            outbox.Enqueue(evt)
                        End Sub
            Dim wav As Byte()
            Try
                If data Is Nothing OrElse data.Length > MAX_BASE64 Then Throw New FormatException()
                wav = Convert.FromBase64String(data)
            Catch ex As FormatException
                reply(Nothing, "录音数据有误。")
                Return
            End Try
            Task.Run(Async Function()
                         Try
                             reply(Await PostAsync(wav).ConfigureAwait(False), Nothing)
                         Catch ex As TaskCanceledException
                             reply(Nothing, "语音识别超时, 请再试一次。")
                         Catch ex As Exception
                             reply(Nothing, "语音识别失败: " & ex.GetBaseException().Message)
                         End Try
                     End Function)
        End Sub

        Private Async Function PostAsync(wav As Byte()) As Task(Of String)
            ' 狐表按老版本 .NET 启动时默认不开 TLS 1.2
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol Or SecurityProtocolType.Tls12
            Using form As New MultipartFormDataContent()
                Dim file As New ByteArrayContent(wav)
                file.Headers.ContentType = New MediaTypeHeaderValue("audio/wav")
                ' 自己写 Content-Disposition: 默认会带 filename*, 个别服务认不出
                file.Headers.ContentDisposition = New ContentDispositionHeaderValue("form-data") With {.Name = """file""", .FileName = """voice.wav"""}
                form.Add(file)
                ' DeepSeek 的 /v1/tools/asr 不要 model
                If _model <> "" Then
                    Dim model As New StringContent(_model)
                    model.Headers.ContentDisposition = New ContentDispositionHeaderValue("form-data") With {.Name = """model"""}
                    form.Add(model)
                End If
                Using req As New HttpRequestMessage(HttpMethod.Post, _url)
                    req.Content = form
                    If _key <> "" Then req.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _key)
                    Using resp = Await Http.SendAsync(req).ConfigureAwait(False)
                        Dim body = Await resp.Content.ReadAsStringAsync().ConfigureAwait(False)
                        Dim json As JObject = Nothing
                        Try
                            json = TryCast(JToken.Parse(body), JObject)
                        Catch
                        End Try
                        If Not resp.IsSuccessStatusCode Then
                            Dim msg = If(CStr(json?.SelectToken("error.message")), If(CStr(json?("message")), Left(body, 200)))
                            Throw New HttpRequestException(CInt(resp.StatusCode) & " " & msg)
                        End If
                        If json Is Nothing OrElse json("text") Is Nothing Then Throw New HttpRequestException("接口返回的不是识别结果: " & Left(body, 200))
                        Return CStr(json("text"))
                    End Using
                End Using
            End Using
        End Function

        Private Shared Function Left(s As String, n As Integer) As String
            If s Is Nothing Then Return ""
            Return If(s.Length > n, s.Substring(0, n) & "…", s)
        End Function
    End Class
End Namespace
