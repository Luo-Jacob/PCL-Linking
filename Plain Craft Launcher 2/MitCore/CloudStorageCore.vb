' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' WebDAV (Alist / Nextcloud / Jianguoyun) and AWS SigV4 S3-compatible (Cloudflare R2 / MinIO / Aliyun OSS)
' streaming chunk uploaders and presigned URL generator (no PCL UI dependencies).

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text

Public Module CloudStorageCore

    Public Class WebDavStorageConfig
        Public Property EndpointUrl As String = ""
        Public Property Username As String = ""
        Public Property Password As String = ""
        ''' <summary>可选：好友免账号直接下载的前缀（如 Alist 直链前缀 https://alist.example.com/d/pcllk/）。</summary>
        Public Property PublicBaseUrl As String = ""
    End Class

    Public Class S3StorageConfig
        Public Property EndpointUrl As String = ""
        Public Property Region As String = "auto"
        Public Property Bucket As String = ""
        Public Property AccessKeyId As String = ""
        Public Property SecretAccessKey As String = ""
        Public Property KeyPrefix As String = "pcllk/"
        Public Property UsePathStyle As Boolean = True
        Public Property PresignDays As Integer = 7
    End Class

    Public Function ValidateWebDavConfig(Cfg As WebDavStorageConfig, ByRef OutErr As String) As Boolean
        OutErr = Nothing
        If Cfg Is Nothing OrElse String.IsNullOrWhiteSpace(Cfg.EndpointUrl) Then
            OutErr = "请填写 WebDAV 服务地址！"
            Return False
        End If
        Dim EpUri As Uri = Nothing
        If Not Uri.TryCreate(Cfg.EndpointUrl.Trim(), UriKind.Absolute, EpUri) OrElse
           (EpUri.Scheme <> Uri.UriSchemeHttp AndAlso EpUri.Scheme <> Uri.UriSchemeHttps) OrElse
           Not String.IsNullOrEmpty(EpUri.UserInfo) Then
            OutErr = "WebDAV 地址格式无效（需为 http(s):// 开头且不能在 URL 中直接内嵌账号密码）！"
            Return False
        End If
        If Not String.IsNullOrWhiteSpace(Cfg.PublicBaseUrl) Then
            Dim PubUri As Uri = Nothing
            If Not Uri.TryCreate(Cfg.PublicBaseUrl.Trim(), UriKind.Absolute, PubUri) OrElse
               Not SecurityGuard.IsPublicSafeHttpsUri(PubUri, ResolveDns:=False) Then
                OutErr = "公开直链前缀必须为有效的公网 HTTPS 地址！"
                Return False
            End If
        End If
        Return True
    End Function

    Public Function ValidateS3Config(Cfg As S3StorageConfig, ByRef OutErr As String) As Boolean
        OutErr = Nothing
        If Cfg Is Nothing OrElse String.IsNullOrWhiteSpace(Cfg.EndpointUrl) Then
            OutErr = "请填写 S3 / R2 Endpoint 地址！"
            Return False
        End If
        Dim EpUri As Uri = Nothing
        If Not Uri.TryCreate(Cfg.EndpointUrl.Trim(), UriKind.Absolute, EpUri) OrElse
           (EpUri.Scheme <> Uri.UriSchemeHttp AndAlso EpUri.Scheme <> Uri.UriSchemeHttps) OrElse
           Not String.IsNullOrEmpty(EpUri.UserInfo) Then
            OutErr = "S3 Endpoint 地址无效！"
            Return False
        End If
        If String.IsNullOrWhiteSpace(Cfg.Bucket) OrElse Cfg.Bucket.Trim().Contains("/") OrElse Cfg.Bucket.Trim().Contains("..") Then
            OutErr = "Bucket 名称格式无效！"
            Return False
        End If
        If String.IsNullOrWhiteSpace(Cfg.AccessKeyId) OrElse String.IsNullOrWhiteSpace(Cfg.SecretAccessKey) Then
            OutErr = "请完整填写 AccessKey ID 与 SecretAccessKey！"
            Return False
        End If
        Return True
    End Function

    Public Function UploadFileToWebDav(Cfg As WebDavStorageConfig, UserAgent As String, LocalFilePath As String, ByRef OutDownloadUrl As String) As String
        OutDownloadUrl = ""
        Dim ErrMsg As String = Nothing
        If Not ValidateWebDavConfig(Cfg, ErrMsg) Then Return ErrMsg
        If String.IsNullOrWhiteSpace(LocalFilePath) OrElse Not File.Exists(LocalFilePath) Then Return "待上传密文文件不存在！"

        Dim ObjectName = $"pcllk_{Guid.NewGuid().ToString("N").Substring(0, 12)}.bin"
        Dim PutBase = Cfg.EndpointUrl.Trim().TrimEnd("/"c)
        Dim PutUrl = $"{PutBase}/{ObjectName}"

        Dim AuthHeader As String = Nothing
        If Not String.IsNullOrEmpty(Cfg.Username) OrElse Not String.IsNullOrEmpty(Cfg.Password) Then
            AuthHeader = "Basic " & Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Cfg.Username}:{Cfg.Password}"))
        End If

        Dim LastErr As String = "WebDAV 上传失败"
        For Attempt = 1 To 3
            Try
                Using Handler As New HttpClientHandler() With {.AllowAutoRedirect = False},
                      Client As New HttpClient(Handler) With {.Timeout = TimeSpan.FromSeconds(300)}
                    If Not String.IsNullOrWhiteSpace(UserAgent) Then Client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent)
                    Using Fs As New FileStream(LocalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read),
                          Req As New HttpRequestMessage(HttpMethod.Put, PutUrl)
                        If AuthHeader IsNot Nothing Then Req.Headers.TryAddWithoutValidation("Authorization", AuthHeader)
                        Req.Content = New StreamContent(Fs, 65536)
                        Using Resp = Client.SendAsync(Req).GetAwaiter().GetResult()
                            Dim Code = CInt(Resp.StatusCode)
                            If Code = 401 OrElse Code = 403 Then
                                Return "WebDAV 鉴权失败（HTTP " & Code & "），请检查用户名/应用密码及目录写权限！"
                            End If
                            If Resp.IsSuccessStatusCode Then
                                If Not String.IsNullOrWhiteSpace(Cfg.PublicBaseUrl) Then
                                    OutDownloadUrl = Cfg.PublicBaseUrl.Trim().TrimEnd("/"c) & "/" & ObjectName
                                Else
                                    OutDownloadUrl = PutUrl
                                End If
                                AppEventBus.Publish("storage.webdav.uploaded", "CloudStorageCore",
                                    New Newtonsoft.Json.Linq.JObject From {{"object", ObjectName}})
                                Return Nothing
                            End If
                            Dim Body = Resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                            LastErr = $"WebDAV 上传失败（HTTP {Code}）：{Body}"
                            If Code >= 400 AndAlso Code < 500 AndAlso Code <> 429 Then Return LastErr
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                LastErr = "WebDAV 上传失败：" & ex.Message
            End Try
            If Attempt < 3 Then Threading.Thread.Sleep(700 * Attempt)
        Next
        Return LastErr
    End Function

    Private Function ComputeFileSha256HexStream(FilePath As String) As String
        Using Sha = SHA256.Create(), Fs As New FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)
            Return BitConverter.ToString(Sha.ComputeHash(Fs)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Function Sha256HexUtf8(Text As String) As String
        Using Sha = SHA256.Create()
            Return BitConverter.ToString(Sha.ComputeHash(Encoding.UTF8.GetBytes(Text))).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Private Function HmacSha256(Key As Byte(), Data As String) As Byte()
        Using Hmac As New HMACSHA256(Key)
            Return Hmac.ComputeHash(Encoding.UTF8.GetBytes(Data))
        End Using
    End Function

    Private Function GetSigV4SigningKey(SecretKey As String, DateStamp As String, Region As String) As Byte()
        Dim kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" & SecretKey), DateStamp)
        Dim kRegion = HmacSha256(kDate, Region)
        Dim kService = HmacSha256(kRegion, "s3")
        Return HmacSha256(kService, "aws4_request")
    End Function

    Private Function AwsUrlEncode(Raw As String, EncodeSlash As Boolean) As String
        If String.IsNullOrEmpty(Raw) Then Return ""
        Dim Sb As New StringBuilder()
        For Each B As Byte In Encoding.UTF8.GetBytes(Raw)
            Dim Ch = ChrW(B)
            If (Ch >= "a"c AndAlso Ch <= "z"c) OrElse (Ch >= "A"c AndAlso Ch <= "Z"c) OrElse
               (Ch >= "0"c AndAlso Ch <= "9"c) OrElse Ch = "-"c OrElse Ch = "_"c OrElse
               Ch = "."c OrElse Ch = "~"c Then
                Sb.Append(Ch)
            ElseIf Ch = "/"c AndAlso Not EncodeSlash Then
                Sb.Append("/"c)
            Else
                Sb.AppendFormat("%{0:X2}", B)
            End If
        Next
        Return Sb.ToString()
    End Function

    Private Sub ResolveS3Target(Cfg As S3StorageConfig, ObjectKey As String, ByRef OutRequestUrl As String, ByRef OutHostHeader As String, ByRef OutCanonicalUri As String)
        Dim EpUri As New Uri(Cfg.EndpointUrl.Trim().TrimEnd("/"c))
        Dim Bucket = Cfg.Bucket.Trim()
        Dim CleanKey = ObjectKey.TrimStart("/"c)
        Dim BasePath = EpUri.AbsolutePath.TrimEnd("/"c)
        Dim PortPart = If(EpUri.IsDefaultPort, "", ":" & EpUri.Port.ToString())

        If Cfg.UsePathStyle Then
            OutHostHeader = EpUri.Host & PortPart
            Dim FullPath = $"{BasePath}/{Bucket}/{CleanKey}"
            OutCanonicalUri = AwsUrlEncode(FullPath, EncodeSlash:=False)
            OutRequestUrl = $"{EpUri.Scheme}://{OutHostHeader}{OutCanonicalUri}"
        Else
            OutHostHeader = $"{Bucket}.{EpUri.Host}{PortPart}"
            Dim FullPath = $"{BasePath}/{CleanKey}"
            OutCanonicalUri = AwsUrlEncode(FullPath, EncodeSlash:=False)
            OutRequestUrl = $"{EpUri.Scheme}://{OutHostHeader}{OutCanonicalUri}"
        End If
    End Sub

    Public Function BuildS3PresignedGetUrl(Cfg As S3StorageConfig, ObjectKey As String, Optional UtcNowOverride As DateTime? = Nothing) As String
        Dim ErrMsg As String = Nothing
        If Not ValidateS3Config(Cfg, ErrMsg) Then Throw New ArgumentException(ErrMsg)

        Dim NowUtc = If(UtcNowOverride.HasValue, UtcNowOverride.Value.ToUniversalTime(), DateTime.UtcNow)
        Dim AmzDate = NowUtc.ToString("yyyyMMddTHHmmssZ")
        Dim DateStamp = NowUtc.ToString("yyyyMMdd")
        Dim Region = If(String.IsNullOrWhiteSpace(Cfg.Region), "auto", Cfg.Region.Trim())
        Dim ExpireSec = CLng(Math.Max(1, Math.Min(7, If(Cfg.PresignDays <= 0, 7, Cfg.PresignDays)))) * 86400L

        Dim RequestUrl As String = "", HostHeader As String = "", CanonicalUri As String = ""
        ResolveS3Target(Cfg, ObjectKey, RequestUrl, HostHeader, CanonicalUri)

        Dim CredentialScope = $"{DateStamp}/{Region}/s3/aws4_request"
        Dim QueryParams As New SortedDictionary(Of String, String)(StringComparer.Ordinal) From {
            {"X-Amz-Algorithm", "AWS4-HMAC-SHA256"},
            {"X-Amz-Credential", $"{Cfg.AccessKeyId.Trim()}/{CredentialScope}"},
            {"X-Amz-Date", AmzDate},
            {"X-Amz-Expires", ExpireSec.ToString()},
            {"X-Amz-SignedHeaders", "host"}
        }
        Dim CanonicalQs = String.Join("&", QueryParams.Select(Function(kv) $"{AwsUrlEncode(kv.Key, True)}={AwsUrlEncode(kv.Value, True)}"))
        Dim CanonicalReq = $"GET{vbLf}{CanonicalUri}{vbLf}{CanonicalQs}{vbLf}host:{HostHeader}{vbLf}{vbLf}host{vbLf}UNSIGNED-PAYLOAD"
        Dim StringToSign = $"AWS4-HMAC-SHA256{vbLf}{AmzDate}{vbLf}{CredentialScope}{vbLf}{Sha256HexUtf8(CanonicalReq)}"
        Dim SigningKey = GetSigV4SigningKey(Cfg.SecretAccessKey.Trim(), DateStamp, Region)
        Dim Signature = BitConverter.ToString(HmacSha256(SigningKey, StringToSign)).Replace("-", "").ToLowerInvariant()

        Return $"{RequestUrl}?{CanonicalQs}&X-Amz-Signature={Signature}"
    End Function

    Public Function UploadFileToS3(Cfg As S3StorageConfig, UserAgent As String, LocalFilePath As String, ByRef OutPresignedGetUrl As String) As String
        OutPresignedGetUrl = ""
        Dim ErrMsg As String = Nothing
        If Not ValidateS3Config(Cfg, ErrMsg) Then Return ErrMsg
        If String.IsNullOrWhiteSpace(LocalFilePath) OrElse Not File.Exists(LocalFilePath) Then Return "待上传密文文件不存在！"

        Dim Prefix = If(Cfg.KeyPrefix, "pcllk/").Trim().Trim("/"c)
        Dim ObjectKey = If(String.IsNullOrEmpty(Prefix), "", Prefix & "/") & $"pcllk_{Guid.NewGuid().ToString("N").Substring(0, 12)}.bin"
        Dim PayloadHash = ComputeFileSha256HexStream(LocalFilePath)
        Dim Region = If(String.IsNullOrWhiteSpace(Cfg.Region), "auto", Cfg.Region.Trim())

        Dim RequestUrl As String = "", HostHeader As String = "", CanonicalUri As String = ""
        ResolveS3Target(Cfg, ObjectKey, RequestUrl, HostHeader, CanonicalUri)

        Dim LastErr As String = "S3 上传失败"
        For Attempt = 1 To 3
            Try
                Dim NowUtc = DateTime.UtcNow
                Dim AmzDate = NowUtc.ToString("yyyyMMddTHHmmssZ")
                Dim DateStamp = NowUtc.ToString("yyyyMMdd")
                Dim CredentialScope = $"{DateStamp}/{Region}/s3/aws4_request"
                Dim CanonicalHeaders = $"host:{HostHeader}{vbLf}x-amz-content-sha256:{PayloadHash}{vbLf}x-amz-date:{AmzDate}{vbLf}"
                Dim SignedHeaders = "host;x-amz-content-sha256;x-amz-date"
                Dim CanonicalReq = $"PUT{vbLf}{CanonicalUri}{vbLf}{vbLf}{CanonicalHeaders}{vbLf}{SignedHeaders}{vbLf}{PayloadHash}"
                Dim StringToSign = $"AWS4-HMAC-SHA256{vbLf}{AmzDate}{vbLf}{CredentialScope}{vbLf}{Sha256HexUtf8(CanonicalReq)}"
                Dim SigningKey = GetSigV4SigningKey(Cfg.SecretAccessKey.Trim(), DateStamp, Region)
                Dim Signature = BitConverter.ToString(HmacSha256(SigningKey, StringToSign)).Replace("-", "").ToLowerInvariant()
                Dim AuthValue = $"AWS4-HMAC-SHA256 Credential={Cfg.AccessKeyId.Trim()}/{CredentialScope}, SignedHeaders={SignedHeaders}, Signature={Signature}"

                Using Handler As New HttpClientHandler() With {.AllowAutoRedirect = False},
                      Client As New HttpClient(Handler) With {.Timeout = TimeSpan.FromSeconds(300)}
                    If Not String.IsNullOrWhiteSpace(UserAgent) Then Client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent)
                    Using Fs As New FileStream(LocalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read),
                          Req As New HttpRequestMessage(HttpMethod.Put, RequestUrl)
                        Req.Headers.TryAddWithoutValidation("x-amz-date", AmzDate)
                        Req.Headers.TryAddWithoutValidation("x-amz-content-sha256", PayloadHash)
                        Req.Headers.TryAddWithoutValidation("Authorization", AuthValue)
                        Req.Content = New StreamContent(Fs, 65536)
                        Req.Content.Headers.ContentType = New Headers.MediaTypeHeaderValue("application/octet-stream")
                        Using Resp = Client.SendAsync(Req).GetAwaiter().GetResult()
                            Dim Code = CInt(Resp.StatusCode)
                            If Code = 401 OrElse Code = 403 Then
                                Dim ErrBody = Resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                                Return $"S3 鉴权失败（HTTP {Code}），请检查 AccessKey / SecretKey / Region / Bucket 权限：{ErrBody}"
                            End If
                            If Resp.IsSuccessStatusCode Then
                                OutPresignedGetUrl = BuildS3PresignedGetUrl(Cfg, ObjectKey)
                                AppEventBus.Publish("storage.s3.uploaded", "CloudStorageCore",
                                    New Newtonsoft.Json.Linq.JObject From {{"bucket", Cfg.Bucket}, {"object", ObjectKey}})
                                Return Nothing
                            End If
                            Dim Body = Resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                            LastErr = $"S3 上传失败（HTTP {Code}）：{Body}"
                            If Code >= 400 AndAlso Code < 500 AndAlso Code <> 429 Then Return LastErr
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                LastErr = "S3 上传失败：" & ex.Message
            End Try
            If Attempt < 3 Then Threading.Thread.Sleep(700 * Attempt)
        Next
        Return LastErr
    End Function

End Module
