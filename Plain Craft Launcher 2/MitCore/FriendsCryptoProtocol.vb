' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Standalone end-to-end encrypted messaging, domain-separated v2 key derivation,
' HMAC-SHA256 verification, and encrypted attachment relay protocol (no PCL UI/core dependencies).

Imports System.IO
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

Public Module FriendsCryptoProtocol

    Public Const NtfyChunkPlainBytes As Long = 8L * 1024L * 1024L
    Public Const NtfyLegacySingleMaxBytes As Long = 10L * 1024L * 1024L
    Public Const WebDavChunkPlainBytes As Long = 32L * 1024L * 1024L
    Public Const PixeldrainChunkPlainBytes As Long = 64L * 1024L * 1024L
    Public Const S3ChunkPlainBytes As Long = 64L * 1024L * 1024L
    Public Const ManifestUrlPrefix As String = "pcllk-manifest:"
    Public Const PickupCodePrefix As String = "PCLLK-FILE-V1:"
    Public Const MaxManifestPlainBytes As Long = 2L * 1024L * 1024L
    Public Const MaxManifestWireBytes As Long = 2L * 1024L * 1024L + 1024L
    Private Const MaxPixeldrainSingleWireBytes As Long = 21L * 1024L * 1024L * 1024L
    Private Const MaxNtfySingleWireBytes As Long = 16L * 1024L * 1024L
    Private Const MaxCloudSingleWireBytes As Long = 512L * 1024L * 1024L

    Public Structure ChannelKeys
        Public TopicName As String
        Public EncKey As Byte()
        Public MacKey As Byte()
    End Structure

    Private NotInheritable Class BoundedReadStream
        Inherits Stream

        Private ReadOnly Inner As Stream
        Private ReadOnly MaxBytes As Long
        Private TotalRead As Long

        Public Sub New(InnerStream As Stream, LimitBytes As Long)
            Inner = InnerStream
            MaxBytes = LimitBytes
        End Sub

        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return True
            End Get
        End Property
        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return False
            End Get
        End Property
        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return False
            End Get
        End Property
        Public Overrides ReadOnly Property Length As Long
            Get
                Throw New NotSupportedException()
            End Get
        End Property
        Public Overrides Property Position As Long
            Get
                Throw New NotSupportedException()
            End Get
            Set(value As Long)
                Throw New NotSupportedException()
            End Set
        End Property
        Public Overrides Sub Flush()
        End Sub
        Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
            Throw New NotSupportedException()
        End Function
        Public Overrides Sub SetLength(value As Long)
            Throw New NotSupportedException()
        End Sub
        Public Overrides Sub Write(buffer() As Byte, offset As Integer, count As Integer)
            Throw New NotSupportedException()
        End Sub
        Public Overrides Function Read(buffer() As Byte, offset As Integer, count As Integer) As Integer
            Dim n = Inner.Read(buffer, offset, count)
            If n > 0 Then
                TotalRead += n
                If TotalRead > MaxBytes Then
                    Throw New InvalidDataException("云端附件流体积超过安全上限，已中止接收！")
                End If
            End If
            Return n
        End Function
    End Class

    Public Function Sha256Utf8(Text As String) As Byte()
        Using Sha = SHA256.Create()
            Return Sha.ComputeHash(Encoding.UTF8.GetBytes(If(Text, "")))
        End Using
    End Function

    Public Function ComputeSha256Hex(RawBytes As Byte()) As String
        Using Sha = SHA256.Create()
            Return BitConverter.ToString(Sha.ComputeHash(RawBytes)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Public Function DerivePairKeysCore(MyId As String, MyPassword As String, FriendId As String, FriendPassword As String) As ChannelKeys
        Dim A As String = MyId & ":" & MyPassword
        Dim B As String = FriendId & ":" & FriendPassword
        Dim Pair As String = If(String.CompareOrdinal(A, B) < 0, A & "|" & B, B & "|" & A)
        Dim TopicHash = Sha256Utf8("LML-TOPIC-V2|" & Pair)
        Return New ChannelKeys With {
            .TopicName = "lml-v2-" & BitConverter.ToString(TopicHash).Replace("-", "").Substring(0, 24).ToLowerInvariant(),
            .EncKey = Sha256Utf8("LML-ENC-V2|" & Pair),
            .MacKey = Sha256Utf8("LML-MAC-V2|" & Pair)
        }
    End Function

    Public Function DeriveGroupKeysCore(GroupId As String, GroupSecret As String) As ChannelKeys
        Dim Pair As String = $"{GroupId}|{GroupSecret}"
        Dim TopicHash = Sha256Utf8("LML-GRP-TOPIC-V2|" & Pair)
        Return New ChannelKeys With {
            .TopicName = "lml-grp-v2-" & BitConverter.ToString(TopicHash).Replace("-", "").Substring(0, 24).ToLowerInvariant(),
            .EncKey = Sha256Utf8("LML-GRP-ENC-V2|" & Pair),
            .MacKey = Sha256Utf8("LML-GRP-MAC-V2|" & Pair)
        }
    End Function

    Public Function FixedTimeEquals(ExpectedHex As String, ActualHex As String) As Boolean
        If String.IsNullOrEmpty(ExpectedHex) OrElse String.IsNullOrEmpty(ActualHex) Then Return False
        Dim A = Encoding.ASCII.GetBytes(ExpectedHex.Trim().ToUpperInvariant())
        Dim B = Encoding.ASCII.GetBytes(ActualHex.Trim().ToUpperInvariant())
        If A.Length <> B.Length Then Return False
        Dim Diff As Integer = 0
        For i = 0 To A.Length - 1
            Diff = Diff Or (CInt(A(i)) Xor CInt(B(i)))
        Next
        Return Diff = 0
    End Function

    Public Function EncryptBytes(Plain As Byte(), Key As Byte()) As Byte()
        Using Aes As New AesManaged With {.KeySize = 256, .Mode = CipherMode.CBC, .Padding = PaddingMode.PKCS7}
            Aes.Key = Key
            Aes.GenerateIV()
            Using Enc = Aes.CreateEncryptor()
                Dim Cipher = Enc.TransformFinalBlock(Plain, 0, Plain.Length)
                Dim Result(Aes.IV.Length + Cipher.Length - 1) As Byte
                Array.Copy(Aes.IV, Result, Aes.IV.Length)
                Array.Copy(Cipher, 0, Result, Aes.IV.Length, Cipher.Length)
                Return Result
            End Using
        End Using
    End Function

    Public Function DecryptBytes(Raw As Byte(), Key As Byte()) As Byte()
        If Raw Is Nothing OrElse Raw.Length < 17 Then Throw New Exception("密文数据长度异常")
        Using Aes As New AesManaged With {.KeySize = 256, .Mode = CipherMode.CBC, .Padding = PaddingMode.PKCS7}
            Aes.Key = Key
            Dim IV(15) As Byte
            Array.Copy(Raw, IV, 16)
            Aes.IV = IV
            Using Dec = Aes.CreateDecryptor()
                Return Dec.TransformFinalBlock(Raw, 16, Raw.Length - 16)
            End Using
        End Using
    End Function

    Public Function EncryptText(Plain As String, Key As Byte()) As String
        Using Aes As New AesManaged With {.KeySize = 256, .Mode = CipherMode.CBC, .Padding = PaddingMode.PKCS7}
            Aes.Key = Key
            Aes.GenerateIV()
            Using Enc = Aes.CreateEncryptor()
                Dim Raw = Encoding.UTF8.GetBytes(Plain)
                Dim Cipher = Enc.TransformFinalBlock(Raw, 0, Raw.Length)
                Dim Result(Aes.IV.Length + Cipher.Length - 1) As Byte
                Array.Copy(Aes.IV, Result, Aes.IV.Length)
                Array.Copy(Cipher, 0, Result, Aes.IV.Length, Cipher.Length)
                Return Convert.ToBase64String(Result)
            End Using
        End Using
    End Function

    Public Function DecryptText(Encoded As String, Key As Byte()) As String
        Dim Raw = Convert.FromBase64String(Encoded)
        If Raw.Length < 17 Then Throw New Exception("密文长度异常")
        Using Aes As New AesManaged With {.KeySize = 256, .Mode = CipherMode.CBC, .Padding = PaddingMode.PKCS7}
            Aes.Key = Key
            Dim IV(15) As Byte
            Array.Copy(Raw, IV, 16)
            Aes.IV = IV
            Using Dec = Aes.CreateDecryptor()
                Dim Plain = Dec.TransformFinalBlock(Raw, 16, Raw.Length - 16)
                Return Encoding.UTF8.GetString(Plain)
            End Using
        End Using
    End Function

    Public Function MacOf(Text As String, Key As Byte()) As String
        Using Hmac As New HMACSHA256(Key)
            Return BitConverter.ToString(Hmac.ComputeHash(Encoding.UTF8.GetBytes(Text))).Replace("-", "").ToUpperInvariant()
        End Using
    End Function

    Public Function UploadEncryptedBlobCore(BaseRelay As String, UserAgent As String, MaxBytes As Long, RawBytes As Byte(),
                                            ByRef OutUrl As String, ByRef OutKeyB64 As String, ByRef OutSha256Hex As String) As String
        OutUrl = ""
        OutKeyB64 = ""
        OutSha256Hex = ""
        If RawBytes Is Nothing OrElse RawBytes.Length = 0 Then Return "文件内容为空，无法上传！"
        If RawBytes.LongLength > MaxBytes Then
            Return $"文件大小超过 {MaxBytes \ (1024 * 1024)} MB 上限（当前 {RawBytes.LongLength / 1048576.0:0.1} MB），无法通过公共中继传输！"
        End If
        OutSha256Hex = ComputeSha256Hex(RawBytes)
        Dim KeyBytes(31) As Byte
        Using Rng = RandomNumberGenerator.Create()
            Rng.GetBytes(KeyBytes)
        End Using
        OutKeyB64 = Convert.ToBase64String(KeyBytes)
        Dim Encrypted = EncryptBytes(RawBytes, KeyBytes)

        Dim AttTopic = "lml-att-" & Guid.NewGuid().ToString("N").Substring(0, 16)
        Dim CleanRelay = BaseRelay.TrimEnd("/"c)
        Dim UploadUrl = CleanRelay & "/" & AttTopic
        Using Client As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(45)}
            If Not String.IsNullOrWhiteSpace(UserAgent) Then Client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent)
            Using Req As New HttpRequestMessage(HttpMethod.Put, UploadUrl)
                Req.Headers.TryAddWithoutValidation("Filename", $"lml_{Guid.NewGuid().ToString("N").Substring(0, 8)}.lmlbin")
                Req.Content = New ByteArrayContent(Encrypted)
                Using Resp = Client.SendAsync(Req).GetAwaiter().GetResult()
                    Dim Body = Resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    If Not Resp.IsSuccessStatusCode Then
                        Return $"上传加密文件失败（HTTP {CInt(Resp.StatusCode)}）：{Body}"
                    End If
                    Dim Obj = JObject.Parse(Body)
                    Dim AttUrl = Obj("attachment")?("url")?.ToString()
                    If String.IsNullOrWhiteSpace(AttUrl) Then Return "中继服务器未返回附件下载链接（该中继可能关闭了附件支持）。"
                    If AttUrl.StartsWith("/") Then AttUrl = CleanRelay & AttUrl
                    OutUrl = AttUrl
                End Using
            End Using
        End Using
        Return Nothing
    End Function

    Public Function DownloadAndDecryptBlobCore(BaseRelay As String, UserAgent As String, MaxBytes As Long,
                                               FileUrl As String, KeyB64 As String, ExpectedSha256Hex As String,
                                               ByRef OutBytes As Byte()) As String
        OutBytes = Nothing
        If String.IsNullOrWhiteSpace(FileUrl) OrElse String.IsNullOrWhiteSpace(KeyB64) Then Return "缺少文件下载地址或解密密钥！"
        Dim CleanHash = If(ExpectedSha256Hex, "").Trim()
        If Not Regex.IsMatch(CleanHash, "^[0-9a-fA-F]{64}$") Then
            Return "缺少有效的文件 SHA-256 校验摘要，已拒绝加载该附件！"
        End If
        Dim Parsed As Uri = Nothing
        If Not Uri.TryCreate(FileUrl, UriKind.Absolute, Parsed) OrElse (Parsed.Scheme <> Uri.UriSchemeHttp AndAlso Parsed.Scheme <> Uri.UriSchemeHttps) Then
            Return "非法的附件下载地址！"
        End If
        If Not IsAllowedDownloadUri(BaseRelay, Parsed) Then
            Return "附件地址与当前配置的中继/大文件托管源不同源，出于安全策略已拦截自动请求！"
        End If

        Dim KeyBytes = Convert.FromBase64String(KeyB64)
        If KeyBytes Is Nothing OrElse KeyBytes.Length <> 32 Then Return "解密密钥长度无效！"
        Dim MaxWireBytes As Long = MaxBytes + 64L
        Dim Encrypted As Byte()
        Using Handler As New HttpClientHandler() With {.AllowAutoRedirect = False},
              Client As New HttpClient(Handler) With {.Timeout = TimeSpan.FromSeconds(45)}
            If Not String.IsNullOrWhiteSpace(UserAgent) Then Client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent)
            Using Resp = Client.GetAsync(Parsed, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()
                If Resp.StatusCode = Net.HttpStatusCode.NotFound OrElse CInt(Resp.StatusCode) = 410 Then
                    Return "云端加密文件已过期（公共中继通常缓存 3 小时）或已被清理，请让对方重发！"
                End If
                Dim Code = CInt(Resp.StatusCode)
                If Code >= 300 AndAlso Code < 400 Then
                    Return "中继附件接口返回重定向，出于安全策略已拦截跨源跳转！"
                End If
                Resp.EnsureSuccessStatusCode()
                If Resp.Content.Headers.ContentLength.HasValue AndAlso Resp.Content.Headers.ContentLength.Value > MaxWireBytes Then
                    Return $"云端附件体积（{Resp.Content.Headers.ContentLength.Value / 1048576.0:0.1} MB）超过上限，已拒绝下载！"
                End If
                Using NetStream = Resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult(), Ms As New MemoryStream()
                    Dim Buf(16383) As Byte
                    Do
                        Dim n = NetStream.Read(Buf, 0, Buf.Length)
                        If n <= 0 Then Exit Do
                        If Ms.Length + n > MaxWireBytes Then
                            Return "云端附件流体积超过安全上限，已中止接收！"
                        End If
                        Ms.Write(Buf, 0, n)
                    Loop
                    Encrypted = Ms.ToArray()
                End Using
            End Using
        End Using
        Dim Decrypted = DecryptBytes(Encrypted, KeyBytes)
        Dim ActualHash = ComputeSha256Hex(Decrypted)
        If Not FixedTimeEquals(CleanHash, ActualHash) Then
            Return "文件完整性校验失败（SHA-256 不匹配），文件可能已损坏或被篡改！"
        End If
        OutBytes = Decrypted
        Return Nothing
    End Function

    Public Function IsAllowedDownloadUri(BaseRelay As String, TargetUri As Uri) As Boolean
        If TargetUri Is Nothing OrElse (TargetUri.Scheme <> Uri.UriSchemeHttp AndAlso TargetUri.Scheme <> Uri.UriSchemeHttps) Then
            Return False
        End If
        If TargetUri.Scheme = Uri.UriSchemeHttps AndAlso
           String.Equals(TargetUri.Host, "pixeldrain.com", StringComparison.OrdinalIgnoreCase) AndAlso
           TargetUri.Port = 443 AndAlso
           TargetUri.AbsolutePath.StartsWith("/api/file/", StringComparison.OrdinalIgnoreCase) Then
            Return True
        End If
        Dim BaseRelayUri As Uri = Nothing
        If Uri.TryCreate(BaseRelay, UriKind.Absolute, BaseRelayUri) AndAlso
           String.Equals(TargetUri.Scheme, BaseRelayUri.Scheme, StringComparison.OrdinalIgnoreCase) AndAlso
           String.Equals(TargetUri.Host, BaseRelayUri.Host, StringComparison.OrdinalIgnoreCase) AndAlso
           TargetUri.Port = BaseRelayUri.Port Then
            Return True
        End If
        Return False
    End Function

    Private Function Base64UrlEncode(Raw As Byte()) As String
        Return Convert.ToBase64String(Raw).TrimEnd("="c).Replace("+"c, "-"c).Replace("/"c, "_"c)
    End Function

    Private Function Base64UrlDecode(Encoded As String) As Byte()
        Dim S = Encoded.Replace("-"c, "+"c).Replace("_"c, "/"c)
        Dim Mod4 = S.Length Mod 4
        If Mod4 > 0 Then S &= New String("="c, 4 - Mod4)
        Return Convert.FromBase64String(S)
    End Function

    Public Function EncodeFilePickupCode(FileName As String, FileSize As Long, FileUrl As String, KeyB64 As String, Sha256Hex As String) As String
        Dim Obj As New JObject From {
            {"v", 1},
            {"n", If(FileName, "file.bin")},
            {"s", FileSize},
            {"u", If(FileUrl, "")},
            {"k", If(KeyB64, "")},
            {"h", If(Sha256Hex, "")}
        }
        Dim Raw = Encoding.UTF8.GetBytes(Obj.ToString(Newtonsoft.Json.Formatting.None))
        Return PickupCodePrefix & Base64UrlEncode(Raw)
    End Function

    Public Function TryDecodeFilePickupCode(Code As String, ByRef OutFileName As String, ByRef OutFileSize As Long,
                                            ByRef OutFileUrl As String, ByRef OutKeyB64 As String, ByRef OutSha256Hex As String,
                                            ByRef OutErr As String) As Boolean
        OutFileName = ""
        OutFileSize = 0L
        OutFileUrl = ""
        OutKeyB64 = ""
        OutSha256Hex = ""
        OutErr = Nothing
        Dim Clean = If(Code, "").Trim()
        If String.IsNullOrEmpty(Clean) Then
            OutErr = "请输入取件码！"
            Return False
        End If
        If Not Clean.StartsWith(PickupCodePrefix, StringComparison.OrdinalIgnoreCase) Then
            OutErr = $"取件码格式无效（应以 {PickupCodePrefix} 开头）！"
            Return False
        End If
        Try
            Dim BodyB64 = Clean.Substring(PickupCodePrefix.Length).Trim()
            Dim JsonText = Encoding.UTF8.GetString(Base64UrlDecode(BodyB64))
            Dim Obj = JObject.Parse(JsonText)
            Dim Ver = If(Obj("v") IsNot Nothing, Obj.Value(Of Integer)("v"), 0)
            If Ver <> 1 Then
                OutErr = "不支持的取件码版本！"
                Return False
            End If
            OutFileName = If(Obj.Value(Of String)("n"), "pcl_linking_file.bin").Trim()
            OutFileSize = If(Obj("s") IsNot Nothing, Obj.Value(Of Long)("s"), 0L)
            OutFileUrl = If(Obj.Value(Of String)("u"), "").Trim()
            OutKeyB64 = If(Obj.Value(Of String)("k"), "").Trim()
            OutSha256Hex = If(Obj.Value(Of String)("h"), "").Trim()
            If String.IsNullOrWhiteSpace(OutFileUrl) OrElse String.IsNullOrWhiteSpace(OutKeyB64) OrElse
               Not Regex.IsMatch(OutSha256Hex, "^[0-9a-fA-F]{64}$") OrElse OutFileSize <= 0 Then
                OutErr = "取件码内容不完整或校验摘要无效！"
                Return False
            End If
            Return True
        Catch ex As Exception
            OutErr = "无法解析取件码：" & ex.Message
            Return False
        End Try
    End Function

    Private Function EncryptStreamToTempFile(SourceStream As Stream, MaxPlainBytes As Long, KeyBytes As Byte(),
                                             ByRef OutPlainBytesRead As Long, ByRef OutChunkSha256Hex As String,
                                             FullHasher As SHA256) As String
        OutPlainBytesRead = 0L
        OutChunkSha256Hex = ""
        Dim TempPath = Path.Combine(Path.GetTempPath(), $"pcllk_enc_{Guid.NewGuid():N}.tmp")
        Dim Buf(65535) As Byte
        Try
            Using ChunkHasher = SHA256.Create(),
                  Aes As New AesManaged With {.KeySize = 256, .Mode = CipherMode.CBC, .Padding = PaddingMode.PKCS7}
                Aes.Key = KeyBytes
                Aes.GenerateIV()
                Using FsOut As New FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None)
                    FsOut.Write(Aes.IV, 0, Aes.IV.Length)
                    Using Enc = Aes.CreateEncryptor(),
                          Cs As New CryptoStream(FsOut, Enc, CryptoStreamMode.Write)
                        While OutPlainBytesRead < MaxPlainBytes
                            Dim ToRead = CInt(Math.Min(CLng(Buf.Length), MaxPlainBytes - OutPlainBytesRead))
                            Dim n = SourceStream.Read(Buf, 0, ToRead)
                            If n <= 0 Then Exit While
                            ChunkHasher.TransformBlock(Buf, 0, n, Buf, 0)
                            If FullHasher IsNot Nothing Then FullHasher.TransformBlock(Buf, 0, n, Buf, 0)
                            Cs.Write(Buf, 0, n)
                            OutPlainBytesRead += n
                        End While
                        Cs.FlushFinalBlock()
                    End Using
                End Using
                ChunkHasher.TransformFinalBlock(New Byte() {}, 0, 0)
                OutChunkSha256Hex = BitConverter.ToString(ChunkHasher.Hash).Replace("-", "").ToLowerInvariant()
            End Using
            Return TempPath
        Catch
            Try
                If File.Exists(TempPath) Then File.Delete(TempPath)
            Catch
            End Try
            Throw
        End Try
    End Function

    Private Function UploadEncryptedTempFileToNtfy(BaseRelay As String, UserAgent As String, EncryptedFilePath As String, ByRef OutUrl As String) As String
        OutUrl = ""
        Dim CleanRelay = BaseRelay.TrimEnd("/"c)
        Dim LastErr As String = "上传失败"
        For Attempt = 1 To 3
            Try
                Dim AttTopic = "lml-att-" & Guid.NewGuid().ToString("N").Substring(0, 16)
                Dim UploadUrl = CleanRelay & "/" & AttTopic
                Using Client As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(180)}
                    If Not String.IsNullOrWhiteSpace(UserAgent) Then Client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent)
                    Using Fs As New FileStream(EncryptedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read),
                          Req As New HttpRequestMessage(HttpMethod.Put, UploadUrl)
                        Req.Headers.TryAddWithoutValidation("Filename", $"lml_{Guid.NewGuid().ToString("N").Substring(0, 8)}.lmlbin")
                        Req.Content = New StreamContent(Fs, 65536)
                        Using Resp = Client.SendAsync(Req).GetAwaiter().GetResult()
                            Dim Body = Resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                            If Resp.IsSuccessStatusCode Then
                                Dim Obj = JObject.Parse(Body)
                                Dim AttUrl = Obj("attachment")?("url")?.ToString()
                                If String.IsNullOrWhiteSpace(AttUrl) Then Return "中继服务器未返回附件下载链接（该中继可能关闭了附件支持）。"
                                If AttUrl.StartsWith("/") Then AttUrl = CleanRelay & AttUrl
                                OutUrl = AttUrl
                                Return Nothing
                            End If
                            Dim Code = CInt(Resp.StatusCode)
                            LastErr = $"上传加密文件失败（HTTP {Code}）：{Body}"
                            If Code >= 400 AndAlso Code < 500 AndAlso Code <> 429 Then Return LastErr
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                LastErr = "上传加密文件失败：" & ex.Message
            End Try
            If Attempt < 3 Then Threading.Thread.Sleep(600 * Attempt)
        Next
        Return LastErr
    End Function

    Private Function UploadEncryptedTempFileToPixeldrain(ApiKey As String, UserAgent As String, EncryptedFilePath As String, ByRef OutUrl As String) As String
        OutUrl = ""
        Dim AuthToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" & ApiKey.Trim()))
        Dim LastErr As String = "Pixeldrain 上传失败"
        For Attempt = 1 To 3
            Try
                Dim RemoteName = $"pcllk_{Guid.NewGuid().ToString("N").Substring(0, 10)}.bin"
                Dim UploadUrl = "https://pixeldrain.com/api/file/" & RemoteName
                Using Client As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(300)}
                    If Not String.IsNullOrWhiteSpace(UserAgent) Then Client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent)
                    Using Fs As New FileStream(EncryptedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read),
                          Req As New HttpRequestMessage(HttpMethod.Put, UploadUrl)
                        Req.Headers.TryAddWithoutValidation("Authorization", "Basic " & AuthToken)
                        Req.Content = New StreamContent(Fs, 65536)
                        Using Resp = Client.SendAsync(Req).GetAwaiter().GetResult()
                            Dim Body = Resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                            Dim Code = CInt(Resp.StatusCode)
                            If Code = 401 OrElse Code = 403 Then
                                Return "Pixeldrain API Key 无效或权限不足，请在左下角「中继 → 大文件托管」重新填写！"
                            End If
                            If Resp.IsSuccessStatusCode Then
                                Dim Obj = JObject.Parse(Body)
                                Dim FileId = Obj("id")?.ToString()
                                If String.IsNullOrWhiteSpace(FileId) Then
                                    Return "Pixeldrain 未返回文件 ID：" & Body
                                End If
                                OutUrl = "https://pixeldrain.com/api/file/" & FileId.Trim()
                                Return Nothing
                            End If
                            LastErr = $"Pixeldrain 上传失败（HTTP {Code}）：{Body}"
                            If Code >= 400 AndAlso Code < 500 AndAlso Code <> 429 Then Return LastErr
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                LastErr = "Pixeldrain 上传失败：" & ex.Message
            End Try
            If Attempt < 3 Then Threading.Thread.Sleep(800 * Attempt)
        Next
        Return LastErr
    End Function

    Private Function UploadEncryptedTempFile(Provider As String, BaseRelay As String, PixeldrainApiKey As String,
                                             WebDavCfg As WebDavStorageConfig, S3Cfg As S3StorageConfig,
                                             UserAgent As String, EncryptedFilePath As String, ByRef OutUrl As String) As String
        Select Case If(Provider, "ntfy").ToLowerInvariant()
            Case "s3"
                Return CloudStorageCore.UploadFileToS3(S3Cfg, UserAgent, EncryptedFilePath, OutUrl)
            Case "webdav"
                Return CloudStorageCore.UploadFileToWebDav(WebDavCfg, UserAgent, EncryptedFilePath, OutUrl)
            Case "pixeldrain"
                Return UploadEncryptedTempFileToPixeldrain(PixeldrainApiKey, UserAgent, EncryptedFilePath, OutUrl)
            Case Else
                Return UploadEncryptedTempFileToNtfy(BaseRelay, UserAgent, EncryptedFilePath, OutUrl)
        End Select
    End Function

    Public Function UploadEncryptedFileStreamCore(BaseRelay As String, PixeldrainApiKey As String, UserAgent As String, FilePath As String,
                                                  ByRef OutUrl As String, ByRef OutKeyB64 As String, ByRef OutSha256Hex As String,
                                                  ByRef OutProvider As String, Optional OnProgress As Action(Of String, Integer, Integer) = Nothing) As String
        Return UploadEncryptedFileWithBackendCore(BaseRelay, "auto", PixeldrainApiKey, Nothing, Nothing, UserAgent, FilePath,
                                                  OutUrl, OutKeyB64, OutSha256Hex, OutProvider, OnProgress)
    End Function

    Public Function UploadEncryptedFileWithBackendCore(BaseRelay As String, StorageMode As String, PixeldrainApiKey As String,
                                                       WebDavCfg As WebDavStorageConfig, S3Cfg As S3StorageConfig,
                                                       UserAgent As String, FilePath As String,
                                                       ByRef OutUrl As String, ByRef OutKeyB64 As String, ByRef OutSha256Hex As String,
                                                       ByRef OutProvider As String, Optional OnProgress As Action(Of String, Integer, Integer) = Nothing) As String
        OutUrl = ""
        OutKeyB64 = ""
        OutSha256Hex = ""
        OutProvider = ""
        If String.IsNullOrWhiteSpace(FilePath) OrElse Not File.Exists(FilePath) Then Return "找不到要发送的文件！"
        Dim Info As New FileInfo(FilePath)
        If Info.Length <= 0 Then Return "文件内容为空，无法上传！"

        Dim Mode = If(StorageMode, "auto").Trim().ToLowerInvariant()
        Select Case Mode
            Case "s3"
                Dim S3Err As String = Nothing
                If Not CloudStorageCore.ValidateS3Config(S3Cfg, S3Err) Then Return S3Err
                OutProvider = "s3"
            Case "webdav"
                Dim DavErr As String = Nothing
                If Not CloudStorageCore.ValidateWebDavConfig(WebDavCfg, DavErr) Then Return DavErr
                OutProvider = "webdav"
            Case "pixeldrain"
                If String.IsNullOrWhiteSpace(PixeldrainApiKey) Then Return "尚未配置 Pixeldrain API Key！"
                OutProvider = "pixeldrain"
            Case "ntfy"
                OutProvider = "ntfy"
            Case Else
                If Not String.IsNullOrWhiteSpace(PixeldrainApiKey) Then
                    OutProvider = "pixeldrain"
                Else
                    OutProvider = "ntfy"
                End If
        End Select

        Dim ChunkPlainLimit As Long
        Select Case OutProvider
            Case "s3"
                ChunkPlainLimit = S3ChunkPlainBytes
            Case "webdav"
                ChunkPlainLimit = WebDavChunkPlainBytes
            Case "pixeldrain"
                ChunkPlainLimit = PixeldrainChunkPlainBytes
            Case Else
                ChunkPlainLimit = If(Info.Length <= NtfyLegacySingleMaxBytes, NtfyLegacySingleMaxBytes, NtfyChunkPlainBytes)
        End Select
        Dim TotalChunks = CInt(Math.Max(1L, CLng(Math.Ceiling(Info.Length / CDbl(ChunkPlainLimit)))))

        Dim KeyBytes(31) As Byte
        Using Rng = RandomNumberGenerator.Create()
            Rng.GetBytes(KeyBytes)
        End Using
        OutKeyB64 = Convert.ToBase64String(KeyBytes)

        Dim ChunkEntries As New JArray()
        Using FullHasher = SHA256.Create(),
              Fs As New FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)
            Dim ChunkIndex = 0
            While Fs.Position < Fs.Length
                ChunkIndex += 1
                OnProgress?.Invoke("加密上传", ChunkIndex, TotalChunks)
                Dim PlainRead As Long = 0
                Dim ChunkSha As String = ""
                Dim TempEncPath = EncryptStreamToTempFile(Fs, ChunkPlainLimit, KeyBytes, PlainRead, ChunkSha, FullHasher)
                Try
                    If PlainRead <= 0 Then Exit While
                    Dim ChunkUrl As String = ""
                    Dim UpErr = UploadEncryptedTempFile(OutProvider, BaseRelay, PixeldrainApiKey, WebDavCfg, S3Cfg, UserAgent, TempEncPath, ChunkUrl)
                    If UpErr IsNot Nothing Then Return UpErr
                    ChunkEntries.Add(New JObject From {
                        {"u", ChunkUrl},
                        {"h", ChunkSha},
                        {"s", PlainRead}
                    })
                Finally
                    Try
                        If File.Exists(TempEncPath) Then File.Delete(TempEncPath)
                    Catch
                    End Try
                End Try
            End While
            FullHasher.TransformFinalBlock(New Byte() {}, 0, 0)
            OutSha256Hex = BitConverter.ToString(FullHasher.Hash).Replace("-", "").ToLowerInvariant()
        End Using

        If ChunkEntries.Count = 0 Then Return "文件内容为空，无法上传！"
        If ChunkEntries.Count = 1 Then
            OutUrl = ChunkEntries(0).Value(Of String)("u")
            Return Nothing
        End If

        OnProgress?.Invoke("写入索引", TotalChunks, TotalChunks)
        Dim ManifestObj As New JObject From {
            {"v", 2},
            {"fn", Info.Name},
            {"fs", Info.Length},
            {"fh", OutSha256Hex},
            {"p", OutProvider},
            {"chunks", ChunkEntries}
        }
        Dim ManifestBytes = Encoding.UTF8.GetBytes(ManifestObj.ToString(Newtonsoft.Json.Formatting.None))
        Dim ManifestEnc = EncryptBytes(ManifestBytes, KeyBytes)
        Dim TempManifestPath = Path.Combine(Path.GetTempPath(), $"pcllk_man_{Guid.NewGuid():N}.tmp")
        Try
            File.WriteAllBytes(TempManifestPath, ManifestEnc)
            Dim ManifestUrl As String = ""
            Dim ManErr = UploadEncryptedTempFile(OutProvider, BaseRelay, PixeldrainApiKey, WebDavCfg, S3Cfg, UserAgent, TempManifestPath, ManifestUrl)
            If ManErr IsNot Nothing Then Return ManErr
            OutUrl = ManifestUrlPrefix & ManifestUrl
            Return Nothing
        Finally
            Try
                If File.Exists(TempManifestPath) Then File.Delete(TempManifestPath)
            Catch
            End Try
        End Try
    End Function

    Private Function ReadExactBytes(Stream As Stream, Buffer As Byte(), Count As Integer) As Integer
        Dim Offset = 0
        While Offset < Count
            Dim n = Stream.Read(Buffer, Offset, Count - Offset)
            If n <= 0 Then Exit While
            Offset += n
        End While
        Return Offset
    End Function

    Private Function DownloadAndDecryptSingleStream(BaseRelay As String, UserAgent As String, TargetUrl As String,
                                                  KeyBytes As Byte(), OutStream As Stream, FullHasher As SHA256,
                                                  ExpectedChunkSha256OrNull As String, MaxWireBytes As Long,
                                                  Optional AllowExternalPublicHttps As Boolean = False,
                                                  Optional CancelToken As Threading.CancellationToken = Nothing) As String
        Dim Parsed As Uri = Nothing
        If Not Uri.TryCreate(TargetUrl, UriKind.Absolute, Parsed) OrElse (Parsed.Scheme <> Uri.UriSchemeHttp AndAlso Parsed.Scheme <> Uri.UriSchemeHttps) Then
            Return "非法的附件下载地址！"
        End If
        Dim IsSameOrPixeldrain = IsAllowedDownloadUri(BaseRelay, Parsed)
        Dim IsSafeExternal = AllowExternalPublicHttps AndAlso SecurityGuard.IsPublicSafeHttpsUri(Parsed, ResolveDns:=True)
        If Not IsSameOrPixeldrain AndAlso Not IsSafeExternal Then
            AppEventBus.Publish("security.ssrf_blocked", "FriendsCryptoProtocol",
                New JObject From {{"host", Parsed.Host}, {"scheme", Parsed.Scheme}})
            Return "附件地址未通过安全同源/公网 HTTPS 白名单校验（已拦截内网或非 HTTPS 链接）！"
        End If

        Using Handler As New HttpClientHandler() With {.AllowAutoRedirect = False},
              Client As New HttpClient(Handler) With {.Timeout = TimeSpan.FromSeconds(300)}
            If Not String.IsNullOrWhiteSpace(UserAgent) Then Client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent)
            Using Resp = Client.GetAsync(Parsed, HttpCompletionOption.ResponseHeadersRead, CancelToken).GetAwaiter().GetResult()
                If Resp.StatusCode = Net.HttpStatusCode.NotFound OrElse CInt(Resp.StatusCode) = 410 Then
                    Return "云端加密文件已过期（公共中继约 3 小时 / S3 预签名默认 7 天 / Pixeldrain 120 天）或已被清理，请让对方重发！"
                End If
                Dim Code = CInt(Resp.StatusCode)
                If Code >= 300 AndAlso Code < 400 Then
                    Return "附件接口返回重定向，出于安全策略已拦截跨源跳转！"
                End If
                If Not Resp.IsSuccessStatusCode Then
                    Return $"下载云端附件失败（HTTP {Code}）！"
                End If
                If Resp.Content.Headers.ContentLength.HasValue AndAlso Resp.Content.Headers.ContentLength.Value > MaxWireBytes Then
                    Return $"云端附件体积（{Resp.Content.Headers.ContentLength.Value / 1048576.0:0.1} MB）超过上限，已拒绝下载！"
                End If
                Using NetStream = Resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult(),
                      Bounded As New BoundedReadStream(NetStream, MaxWireBytes)
                    Dim IV(15) As Byte
                    If ReadExactBytes(Bounded, IV, 16) <> 16 Then
                        Return "云端密文数据头损坏或不完整！"
                    End If
                    Dim CheckChunkHash = Not String.IsNullOrWhiteSpace(ExpectedChunkSha256OrNull)
                    Using ChunkHasher = If(CheckChunkHash, SHA256.Create(), Nothing),
                          Aes As New AesManaged With {.KeySize = 256, .Mode = CipherMode.CBC, .Padding = PaddingMode.PKCS7}
                        Aes.Key = KeyBytes
                        Aes.IV = IV
                        Using Dec = Aes.CreateDecryptor(),
                              Cs As New CryptoStream(Bounded, Dec, CryptoStreamMode.Read)
                            Dim Buf(65535) As Byte
                            Do
                                CancelToken.ThrowIfCancellationRequested()
                                Dim n = Cs.Read(Buf, 0, Buf.Length)
                                If n <= 0 Then Exit Do
                                OutStream.Write(Buf, 0, n)
                                If FullHasher IsNot Nothing Then FullHasher.TransformBlock(Buf, 0, n, Buf, 0)
                                If ChunkHasher IsNot Nothing Then ChunkHasher.TransformBlock(Buf, 0, n, Buf, 0)
                            Loop
                        End Using
                        If CheckChunkHash Then
                            ChunkHasher.TransformFinalBlock(New Byte() {}, 0, 0)
                            Dim ActualChunkHex = BitConverter.ToString(ChunkHasher.Hash).Replace("-", "").ToLowerInvariant()
                            If Not FixedTimeEquals(ExpectedChunkSha256OrNull, ActualChunkHex) Then
                                Return "分片完整性校验失败（SHA-256 不匹配），分片可能已损坏或被篡改！"
                            End If
                        End If
                    End Using
                End Using
            End Using
        End Using
        Return Nothing
    End Function

    Public Sub CleanupResumeTempFiles(SavePath As String)
        If String.IsNullOrWhiteSpace(SavePath) Then Return
        Try
            Dim PartPath = SavePath & ".pcllk.part"
            Dim ResumePath = SavePath & ".pcllk.resume.json"
            If File.Exists(PartPath) Then File.Delete(PartPath)
            If File.Exists(ResumePath) Then File.Delete(ResumePath)
        Catch
        End Try
    End Sub

    Private Function TryResumePartFile(PartPath As String, ResumePath As String, FileUrl As String, CleanHash As String,
                                       Chunks As JArray, FullHasher As SHA256, ByRef FsOut As FileStream) As Integer
        Try
            If Not File.Exists(PartPath) OrElse Not File.Exists(ResumePath) Then Return 0
            Dim Obj = JObject.Parse(File.ReadAllText(ResumePath, Encoding.UTF8))
            If Not String.Equals(If(Obj.Value(Of String)("url"), ""), FileUrl, StringComparison.Ordinal) Then Return 0
            If Not FixedTimeEquals(CleanHash, If(Obj.Value(Of String)("sha256"), "")) Then Return 0
            Dim Completed = If(Obj("completed") IsNot Nothing, Obj.Value(Of Integer)("completed"), 0)
            If Completed <= 0 OrElse Completed >= Chunks.Count Then Return 0

            Dim ExpectedPrefixBytes As Long = 0
            For k = 0 To Completed - 1
                Dim Item = TryCast(Chunks(k), JObject)
                Dim Sz = If(Item?("s") IsNot Nothing, Item.Value(Of Long)("s"), 0L)
                If Sz <= 0 Then Return 0
                ExpectedPrefixBytes += Sz
            Next
            If ExpectedPrefixBytes <= 0 OrElse New FileInfo(PartPath).Length < ExpectedPrefixBytes Then Return 0

            FsOut = New FileStream(PartPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            FsOut.SetLength(ExpectedPrefixBytes)
            FsOut.Position = 0
            Dim Buf(65535) As Byte
            Dim Remaining = ExpectedPrefixBytes
            While Remaining > 0
                Dim n = FsOut.Read(Buf, 0, CInt(Math.Min(CLng(Buf.Length), Remaining)))
                If n <= 0 Then
                    FsOut.Dispose()
                    FsOut = Nothing
                    Return 0
                End If
                FullHasher.TransformBlock(Buf, 0, n, Buf, 0)
                Remaining -= n
            End While
            FsOut.Position = ExpectedPrefixBytes
            Return Completed
        Catch
            Try
                FsOut?.Dispose()
            Catch
            End Try
            FsOut = Nothing
            Return 0
        End Try
    End Function

    Public Function DownloadAndDecryptFileToPathCore(BaseRelay As String, UserAgent As String, FileUrl As String,
                                                     KeyB64 As String, ExpectedSha256Hex As String, SavePath As String,
                                                     Optional OnProgress As Action(Of String, Integer, Integer) = Nothing,
                                                     Optional AllowExternalPublicHttps As Boolean = True,
                                                     Optional CancelToken As Threading.CancellationToken = Nothing) As String
        If String.IsNullOrWhiteSpace(FileUrl) OrElse String.IsNullOrWhiteSpace(KeyB64) Then Return "缺少文件下载地址或解密密钥！"
        If String.IsNullOrWhiteSpace(SavePath) Then Return "无效的本地保存路径！"
        Dim CleanHash = If(ExpectedSha256Hex, "").Trim()
        If Not Regex.IsMatch(CleanHash, "^[0-9a-fA-F]{64}$") Then
            Return "缺少有效的文件 SHA-256 校验摘要，已拒绝加载该附件！"
        End If
        Dim KeyBytes As Byte()
        Try
            KeyBytes = Convert.FromBase64String(KeyB64)
        Catch
            Return "解密密钥格式无效！"
        End Try
        If KeyBytes Is Nothing OrElse KeyBytes.Length <> 32 Then Return "解密密钥长度无效！"

        Dim SaveDir = Path.GetDirectoryName(SavePath)
        If Not String.IsNullOrWhiteSpace(SaveDir) Then Directory.CreateDirectory(SaveDir)
        Dim PartPath = SavePath & ".pcllk.part"
        Dim ResumePath = SavePath & ".pcllk.resume.json"
        Dim PreserveResumeOnError As Boolean = False
        Dim FsOut As FileStream = Nothing

        Try
            Using FullHasher = SHA256.Create()
                If FileUrl.StartsWith(ManifestUrlPrefix, StringComparison.OrdinalIgnoreCase) Then
                    Dim ManifestUrl = FileUrl.Substring(ManifestUrlPrefix.Length).Trim()
                    OnProgress?.Invoke("读取索引", 0, 1)
                    Dim ManifestObj As JObject
                    Using ManMs As New MemoryStream()
                        Dim ManErr = DownloadAndDecryptSingleStream(BaseRelay, UserAgent, ManifestUrl, KeyBytes, ManMs, Nothing, Nothing, MaxManifestWireBytes, AllowExternalPublicHttps, CancelToken)
                        If ManErr IsNot Nothing Then Return ManErr
                        ManifestObj = JObject.Parse(Encoding.UTF8.GetString(ManMs.ToArray()))
                    End Using
                    Dim Chunks = TryCast(ManifestObj("chunks"), JArray)
                    If Chunks Is Nothing OrElse Chunks.Count = 0 Then
                        Return "大文件分片索引为空或格式无效！"
                    End If
                    Dim TotalChunks = Chunks.Count
                    Dim StartIdx = TryResumePartFile(PartPath, ResumePath, FileUrl, CleanHash, Chunks, FullHasher, FsOut)
                    If FsOut Is Nothing Then
                        StartIdx = 0
                        FsOut = New FileStream(PartPath, FileMode.Create, FileAccess.Write, FileShare.None)
                    Else
                        PreserveResumeOnError = True
                    End If

                    For i = StartIdx To TotalChunks - 1
                        CancelToken.ThrowIfCancellationRequested()
                        OnProgress?.Invoke("下载解密", i + 1, TotalChunks)
                        Dim Item = TryCast(Chunks(i), JObject)
                        Dim ChunkUrl = If(Item?.Value(Of String)("u"), "").Trim()
                        Dim ChunkHash = If(Item?.Value(Of String)("h"), "").Trim()
                        If String.IsNullOrWhiteSpace(ChunkUrl) Then Return $"第 {i + 1} 个分片缺少下载地址！"
                        Dim ChunkMaxWire = If(ChunkUrl.IndexOf("pixeldrain.com", StringComparison.OrdinalIgnoreCase) >= 0,
                                              MaxPixeldrainSingleWireBytes, MaxCloudSingleWireBytes)
                        Dim PosBeforeChunk = FsOut.Position
                        Dim ChunkErr = DownloadAndDecryptSingleStream(BaseRelay, UserAgent, ChunkUrl, KeyBytes, FsOut, FullHasher, ChunkHash, ChunkMaxWire, AllowExternalPublicHttps, CancelToken)
                        If ChunkErr IsNot Nothing Then
                            Try
                                FsOut.SetLength(PosBeforeChunk)
                                FsOut.Flush()
                            Catch
                            End Try
                            If i > 0 Then PreserveResumeOnError = True
                            Return ChunkErr
                        End If
                        FsOut.Flush()
                        Dim ResJson As New JObject From {
                            {"url", FileUrl},
                            {"sha256", CleanHash},
                            {"total", TotalChunks},
                            {"completed", i + 1}
                        }
                        File.WriteAllText(ResumePath, ResJson.ToString(Newtonsoft.Json.Formatting.None), New UTF8Encoding(False))
                        PreserveResumeOnError = True
                    Next
                Else
                    FsOut = New FileStream(PartPath, FileMode.Create, FileAccess.Write, FileShare.None)
                    OnProgress?.Invoke("下载解密", 1, 1)
                    Dim SingleMaxWire = If(FileUrl.IndexOf("pixeldrain.com", StringComparison.OrdinalIgnoreCase) >= 0,
                                           MaxPixeldrainSingleWireBytes, MaxCloudSingleWireBytes)
                    Dim SingleErr = DownloadAndDecryptSingleStream(BaseRelay, UserAgent, FileUrl, KeyBytes, FsOut, FullHasher, Nothing, SingleMaxWire, AllowExternalPublicHttps, CancelToken)
                    If SingleErr IsNot Nothing Then Return SingleErr
                End If

                FullHasher.TransformFinalBlock(New Byte() {}, 0, 0)
                Dim ActualHash = BitConverter.ToString(FullHasher.Hash).Replace("-", "").ToLowerInvariant()
                If Not FixedTimeEquals(CleanHash, ActualHash) Then
                    PreserveResumeOnError = False
                    Return "文件完整性校验失败（SHA-256 不匹配），文件可能已损坏或被篡改！"
                End If
                FsOut.Dispose()
                FsOut = Nothing
                PreserveResumeOnError = False
            End Using

            If File.Exists(ResumePath) Then File.Delete(ResumePath)
            If File.Exists(SavePath) Then File.Delete(SavePath)
            File.Move(PartPath, SavePath)
            Return Nothing
        Catch oce As OperationCanceledException
            Return "下载已取消（若已完成部分分片，可从同一保存位置断点续传）。"
        Catch ex As Exception
            Return "下载或解密文件失败：" & ex.Message
        Finally
            Try
                FsOut?.Dispose()
            Catch
            End Try
            If Not PreserveResumeOnError Then
                CleanupResumeTempFiles(SavePath)
            End If
        End Try
    End Function

End Module
