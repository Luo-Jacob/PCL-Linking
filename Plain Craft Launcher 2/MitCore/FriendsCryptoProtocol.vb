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

    Public Structure ChannelKeys
        Public TopicName As String
        Public EncKey As Byte()
        Public MacKey As Byte()
    End Structure

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
        Dim BaseRelayUri As Uri = Nothing
        If Not Uri.TryCreate(BaseRelay, UriKind.Absolute, BaseRelayUri) OrElse
           Not String.Equals(Parsed.Scheme, BaseRelayUri.Scheme, StringComparison.OrdinalIgnoreCase) OrElse
           Not String.Equals(Parsed.Host, BaseRelayUri.Host, StringComparison.OrdinalIgnoreCase) OrElse
           Parsed.Port <> BaseRelayUri.Port Then
            Return "附件地址与当前配置的中继服务器不同源，出于安全策略已拦截自动请求！"
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

End Module
