' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Standalone authenticated & AES-256-CBC encrypted TCP side-channel for EasyTier P2P mod-manifest exchange and large file / world-save relay (no PCL dependencies).

Imports System.Collections.Concurrent
Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Public Module EasyTierSideChannelCore

    Public Const DefaultSideChannelPort As Integer = 41729
    Private Const MAX_CONTROL_FRAME_BYTES As Integer = 16 * 1024 * 1024

    Public Enum SideMsgType As Byte
        ReqModManifest = 1
        RespModManifest = 2
        ReqOfferList = 3
        RespOfferList = 4
        ReqPullOffer = 5
        RespPullMeta = 6
        ReqPushFile = 7
        RespPushDecision = 8
        TransferResultAck = 9
        ErrorFrame = 255
    End Enum

    Public Class SideChannelOffer
        Public Property OfferId As String = ""
        Public Property Kind As String = "save" ' "save" | "file"
        Public Property Name As String = ""
        Public Property FileName As String = ""
        <JsonIgnore>
        Public Property FilePath As String = ""
        Public Property SizeBytes As Long = 0
        Public Property Sha256 As String = ""
        Public Property Sender As String = ""
        Public Property CreatedUtc As DateTime = DateTime.UtcNow
    End Class

    Public Class SideChannelHostCallbacks
        Public Property GetModManifestJson As Func(Of String) = Function() "{}"
        ''' <summary>
        ''' 收到加入者推送文件/存档请求时触发（在工作线程调用，UI 需自行切到主线程询问用户）。
        ''' 返回值: Accepted=True 时 TempDestPath 为待接收的临时文件路径；Declined 时 FinalSaveDirOrAction 可忽略，Message 为拒绝原因。
        ''' 返回对象中的 ActionKind 为 "extract_save" 时，接收完临时 zip 后会自动安全解压到 FinalDestPath。
        ''' </summary>
        Public Property ConfirmIncomingPush As Func(Of SideChannelOffer, (Accepted As Boolean, TempReceivePath As String, FinalDestPath As String, ActionKind As String, Message As String))
        Public Property OnPushCompleted As Action(Of SideChannelOffer, Boolean, String, String)
        Public Property OnLog As Action(Of String)
    End Class

    Public Function CryptographicEquals(A As Byte(), B As Byte()) As Boolean
        If A Is Nothing OrElse B Is Nothing OrElse A.Length <> B.Length Then Return False
        Dim Diff As Integer = 0
        For i As Integer = 0 To A.Length - 1
            Diff = Diff Or (CInt(A(i)) Xor CInt(B(i)))
        Next
        Return Diff = 0
    End Function

    Public Function ComputeSha256HexFile(FilePath As String) As String
        Using Fs = File.OpenRead(FilePath), Sha = SHA256.Create()
            Return BitConverter.ToString(Sha.ComputeHash(Fs)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Public Function FormatByteSizeDisplay(SizeBytes As Long) As String
        If SizeBytes >= 1024L * 1024 * 1024 Then Return $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):0.00} GB"
        If SizeBytes >= 1024L * 1024 Then Return $"{SizeBytes / (1024.0 * 1024.0):0.0} MB"
        If SizeBytes >= 1024L Then Return $"{SizeBytes / 1024.0:0.0} KB"
        Return $"{SizeBytes} B"
    End Function

    Public Function PackWorldSaveToZip(WorldDir As String, DestZipPath As String, Optional OnFilePacked As Action(Of Integer, Integer) = Nothing) As (SizeBytes As Long, Sha256 As String)
        If String.IsNullOrWhiteSpace(WorldDir) OrElse Not Directory.Exists(WorldDir) Then
            Throw New DirectoryNotFoundException($"存档目录不存在：{WorldDir}")
        End If
        Dim RootFull = Path.GetFullPath(WorldDir).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
        Dim Parent = Path.GetDirectoryName(DestZipPath)
        If Not String.IsNullOrEmpty(Parent) Then Directory.CreateDirectory(Parent)
        Dim TmpZip = DestZipPath & ".tmp"
        If File.Exists(TmpZip) Then File.Delete(TmpZip)

        Dim Files = Directory.GetFiles(RootFull, "*", SearchOption.AllDirectories).
            Where(Function(f)
                      Dim Fn = Path.GetFileName(f)
                      If Fn.Equals("session.lock", StringComparison.OrdinalIgnoreCase) Then Return False
                      If Fn.EndsWith(".lmltmp", StringComparison.OrdinalIgnoreCase) OrElse Fn.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) Then Return False
                      Return True
                  End Function).ToList()

        Using Archive = ZipFile.Open(TmpZip, ZipArchiveMode.Create)
            Dim Buf(65535) As Byte
            For i As Integer = 0 To Files.Count - 1
                Dim FilePath = Files(i)
                Dim Rel = Path.GetFullPath(FilePath).Substring(RootFull.Length).Replace("\"c, "/"c)
                Dim Entry = Archive.CreateEntry(Rel, CompressionLevel.Fastest)
                Using Src = New FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Dst = Entry.Open()
                    Do
                        Dim N = Src.Read(Buf, 0, Buf.Length)
                        If N <= 0 Then Exit Do
                        Dst.Write(Buf, 0, N)
                    Loop
                End Using
                If OnFilePacked IsNot Nothing AndAlso ((i And 15) = 0 OrElse i = Files.Count - 1) Then
                    OnFilePacked(i + 1, Files.Count)
                End If
            Next
        End Using

        If File.Exists(DestZipPath) Then File.Delete(DestZipPath)
        File.Move(TmpZip, DestZipPath)
        Dim Info = New FileInfo(DestZipPath)
        Return (Info.Length, ComputeSha256HexFile(DestZipPath))
    End Function

    Public Sub ExtractWorldSaveZipSafely(ZipPath As String, DestSaveDir As String, Optional DeleteZipAfter As Boolean = False)
        Const MAX_ENTRIES As Integer = 100000
        Const MAX_TOTAL_BYTES As Long = 4L * 1024 * 1024 * 1024
        Dim StagingDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DestSaveDir)), ".pcl_staging_" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory(StagingDir)
        Try
            Dim StagingRoot = Path.GetFullPath(StagingDir).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
            Dim TotalBytes As Long = 0
            Dim EntryCount As Integer = 0
            Using Archive = ZipFile.OpenRead(ZipPath)
                Dim Buf(65535) As Byte
                For Each Entry In Archive.Entries
                    EntryCount += 1
                    If EntryCount > MAX_ENTRIES Then Throw New InvalidDataException("存档压缩包内文件数量超过 100,000 安全上限！")
                    Dim Rel = Entry.FullName.Replace("/"c, "\"c).TrimStart("\"c)
                    If String.IsNullOrEmpty(Rel) Then Continue For
                    Dim FullDest = Path.GetFullPath(Path.Combine(StagingRoot, Rel))
                    If Not FullDest.StartsWith(StagingRoot, StringComparison.OrdinalIgnoreCase) Then
                        Throw New InvalidDataException($"检测到不安全的路径穿越条目：{Entry.FullName}")
                    End If
                    If Entry.FullName.EndsWith("/") OrElse Entry.FullName.EndsWith("\") Then
                        Directory.CreateDirectory(FullDest)
                        Continue For
                    End If
                    Dim PDir = Path.GetDirectoryName(FullDest)
                    If Not String.IsNullOrEmpty(PDir) Then Directory.CreateDirectory(PDir)
                    Using Src = Entry.Open(), Dst As New FileStream(FullDest, FileMode.Create, FileAccess.Write, FileShare.None)
                        Do
                            Dim N = Src.Read(Buf, 0, Buf.Length)
                            If N <= 0 Then Exit Do
                            TotalBytes += N
                            If TotalBytes > MAX_TOTAL_BYTES Then Throw New InvalidDataException("存档解压体积超过 4 GB 安全上限！")
                            Dst.Write(Buf, 0, N)
                        Loop
                    End Using
                Next
            End Using

            Dim SourceRoot As String = StagingRoot.TrimEnd(Path.DirectorySeparatorChar)
            If Not File.Exists(Path.Combine(SourceRoot, "level.dat")) Then
                Dim LevelFiles = Directory.GetFiles(SourceRoot, "level.dat", SearchOption.AllDirectories)
                If LevelFiles.Any() Then
                    Dim Shallowest = LevelFiles.OrderBy(Function(p) p.Count(Function(c) c = "\"c)).First()
                    SourceRoot = Path.GetDirectoryName(Shallowest)
                Else
                    Dim TopFiles = Directory.GetFiles(SourceRoot)
                    Dim TopDirs = Directory.GetDirectories(SourceRoot)
                    If TopFiles.Length = 0 AndAlso TopDirs.Length = 1 Then SourceRoot = TopDirs(0)
                End If
            End If

            If Directory.Exists(DestSaveDir) Then Throw New IOException($"目标存档目录已存在：{DestSaveDir}")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DestSaveDir)))
            Dim SrcVol = Path.GetPathRoot(Path.GetFullPath(SourceRoot))
            Dim DstVol = Path.GetPathRoot(Path.GetFullPath(DestSaveDir))
            If String.Equals(SrcVol, DstVol, StringComparison.OrdinalIgnoreCase) Then
                Directory.Move(SourceRoot, DestSaveDir)
            Else
                ZipSafetyCore.CopyDirectorySafe(SourceRoot, DestSaveDir)
            End If
        Finally
            If DeleteZipAfter Then
                Try
                    If File.Exists(ZipPath) Then File.Delete(ZipPath)
                Catch
                End Try
            End If
            Try
                If Directory.Exists(StagingDir) Then Directory.Delete(StagingDir, True)
            Catch
            End Try
        End Try
    End Sub

#Region "加密帧与流式传输底层原语"

    Private Function DeriveAuthKey(Secret As String) As Byte()
        Using Sha = SHA256.Create()
            Return Sha.ComputeHash(Encoding.UTF8.GetBytes("PCLLK-SIDE-AUTH-V1|" & If(Secret, "")))
        End Using
    End Function

    Private Function DeriveAesKey(Secret As String, ServerNonce As Byte(), ClientNonce As Byte()) As Byte()
        Using Sha = SHA256.Create()
            Dim Material = "PCLLK-SIDE-AES-V1|" & If(Secret, "") & "|" & Convert.ToBase64String(ServerNonce) & "|" & Convert.ToBase64String(ClientNonce)
            Return Sha.ComputeHash(Encoding.UTF8.GetBytes(Material))
        End Using
    End Function

    Private Function RandomBytes(Count As Integer) As Byte()
        Dim Buf(Count - 1) As Byte
        Using Rng = RandomNumberGenerator.Create()
            Rng.GetBytes(Buf)
        End Using
        Return Buf
    End Function

    Private Function ComputeHmac(Key As Byte(), ParamArray Parts As Byte()()) As Byte()
        Using H = New HMACSHA256(Key)
            For i As Integer = 0 To Parts.Length - 1
                Dim P = Parts(i)
                If P Is Nothing Then Continue For
                If i < Parts.Length - 1 Then
                    H.TransformBlock(P, 0, P.Length, Nothing, 0)
                Else
                    H.TransformFinalBlock(P, 0, P.Length)
                    Return H.Hash
                End If
            Next
            H.TransformFinalBlock(Array.Empty(Of Byte)(), 0, 0)
            Return H.Hash
        End Using
    End Function

    Private Sub WriteUInt32BE(S As Stream, Value As UInteger)
        Dim B(3) As Byte
        B(0) = CByte((Value >> 24) And &HFFUI)
        B(1) = CByte((Value >> 16) And &HFFUI)
        B(2) = CByte((Value >> 8) And &HFFUI)
        B(3) = CByte(Value And &HFFUI)
        S.Write(B, 0, 4)
    End Sub

    Private Function ReadUInt32BE(S As Stream) As UInteger
        Dim B = ReadExact(S, 4)
        Return (CUInt(B(0)) << 24) Or (CUInt(B(1)) << 16) Or (CUInt(B(2)) << 8) Or CUInt(B(3))
    End Function

    Private Sub WriteInt64BE(S As Stream, Value As Long)
        Dim B(7) As Byte
        For i As Integer = 7 To 0 Step -1
            B(7 - i) = CByte((Value >> (i * 8)) And &HFFL)
        Next
        S.Write(B, 0, 8)
    End Sub

    Private Function ReadInt64BE(S As Stream) As Long
        Dim B = ReadExact(S, 8)
        Dim Val As Long = 0
        For i As Integer = 0 To 7
            Val = (Val << 8) Or CLng(B(i))
        Next
        Return Val
    End Function

    Private Function ReadExact(S As Stream, Count As Integer) As Byte()
        Dim Buf(Count - 1) As Byte
        Dim Offset As Integer = 0
        While Offset < Count
            Dim N = S.Read(Buf, Offset, Count - Offset)
            If N <= 0 Then Throw New EndOfStreamException("对端连接提前关闭")
            Offset += N
        End While
        Return Buf
    End Function

    Private Function PerformServerHandshake(Stream As NetworkStream, Secret As String) As (AuthKey As Byte(), AesKey As Byte())
        Dim AuthKey = DeriveAuthKey(Secret)
        Dim ServerNonce = RandomBytes(32)
        Stream.Write(ServerNonce, 0, 32)
        Stream.Flush()

        Dim ClientNonce = ReadExact(Stream, 32)
        Dim ClientMac = ReadExact(Stream, 32)
        Dim ExpectedClientMac = ComputeHmac(AuthKey, Encoding.ASCII.GetBytes("C|"), ServerNonce, ClientNonce)
        If Not CryptographicEquals(ClientMac, ExpectedClientMac) Then
            Throw New UnauthorizedAccessException("房间密钥校验失败（HMAC 不匹配）")
        End If

        Dim ServerMac = ComputeHmac(AuthKey, Encoding.ASCII.GetBytes("S|"), ServerNonce, ClientNonce)
        Stream.Write(ServerMac, 0, 32)
        Stream.Flush()
        Return (AuthKey, DeriveAesKey(Secret, ServerNonce, ClientNonce))
    End Function

    Private Function PerformClientHandshake(Stream As NetworkStream, Secret As String) As (AuthKey As Byte(), AesKey As Byte())
        Dim AuthKey = DeriveAuthKey(Secret)
        Dim ServerNonce = ReadExact(Stream, 32)
        Dim ClientNonce = RandomBytes(32)
        Dim ClientMac = ComputeHmac(AuthKey, Encoding.ASCII.GetBytes("C|"), ServerNonce, ClientNonce)
        Stream.Write(ClientNonce, 0, 32)
        Stream.Write(ClientMac, 0, 32)
        Stream.Flush()

        Dim ServerMac = ReadExact(Stream, 32)
        Dim ExpectedServerMac = ComputeHmac(AuthKey, Encoding.ASCII.GetBytes("S|"), ServerNonce, ClientNonce)
        If Not CryptographicEquals(ServerMac, ExpectedServerMac) Then
            Throw New UnauthorizedAccessException("服务端密钥校验失败（HMAC 不匹配）")
        End If
        Return (AuthKey, DeriveAesKey(Secret, ServerNonce, ClientNonce))
    End Function

    Private Sub SendEncryptedFrame(Stream As Stream, AuthKey As Byte(), AesKey As Byte(), MsgType As SideMsgType, JsonPayload As String)
        Dim PlainBytes = Encoding.UTF8.GetBytes(If(JsonPayload, "{}"))
        Dim Iv = RandomBytes(16)
        Dim CipherBytes As Byte()
        Using AesAlg = Aes.Create()
            AesAlg.KeySize = 256
            AesAlg.Mode = CipherMode.CBC
            AesAlg.Padding = PaddingMode.PKCS7
            AesAlg.Key = AesKey
            AesAlg.IV = Iv
            Using Ms As New MemoryStream(), Cs As New CryptoStream(Ms, AesAlg.CreateEncryptor(), CryptoStreamMode.Write)
                Cs.Write(PlainBytes, 0, PlainBytes.Length)
                Cs.FlushFinalBlock()
                CipherBytes = Ms.ToArray()
            End Using
        End Using

        Dim LenBytes = New Byte() {
            CByte((CipherBytes.Length >> 24) And &HFF),
            CByte((CipherBytes.Length >> 16) And &HFF),
            CByte((CipherBytes.Length >> 8) And &HFF),
            CByte(CipherBytes.Length And &HFF)
        }
        Dim TypeArr = New Byte() {CByte(MsgType)}
        Dim Mac = ComputeHmac(AuthKey, TypeArr, LenBytes, Iv, CipherBytes)

        Stream.WriteByte(CByte(MsgType))
        Stream.Write(LenBytes, 0, 4)
        Stream.Write(Iv, 0, 16)
        Stream.Write(CipherBytes, 0, CipherBytes.Length)
        Stream.Write(Mac, 0, 32)
        Stream.Flush()
    End Sub

    Private Function ReadEncryptedFrame(Stream As Stream, AuthKey As Byte(), AesKey As Byte()) As (MsgType As SideMsgType, JsonPayload As String)
        Dim TypeByte = Stream.ReadByte()
        If TypeByte < 0 Then Throw New EndOfStreamException("对端连接已断开")
        Dim MsgType = CType(CByte(TypeByte), SideMsgType)
        Dim LenBytes = ReadExact(Stream, 4)
        Dim CipherLen As Long = (CLng(LenBytes(0)) << 24) Or (CLng(LenBytes(1)) << 16) Or (CLng(LenBytes(2)) << 8) Or CLng(LenBytes(3))
        If CipherLen <= 0 OrElse CipherLen > MAX_CONTROL_FRAME_BYTES Then
            Throw New InvalidDataException($"控制帧长度异常：{CipherLen}")
        End If
        Dim Iv = ReadExact(Stream, 16)
        Dim CipherBytes = ReadExact(Stream, CInt(CipherLen))
        Dim Mac = ReadExact(Stream, 32)
        Dim ExpectedMac = ComputeHmac(AuthKey, New Byte() {CByte(TypeByte)}, LenBytes, Iv, CipherBytes)
        If Not CryptographicEquals(Mac, ExpectedMac) Then
            Throw New UnauthorizedAccessException("控制帧 HMAC 校验失败！")
        End If
        Using AesAlg = Aes.Create()
            AesAlg.KeySize = 256
            AesAlg.Mode = CipherMode.CBC
            AesAlg.Padding = PaddingMode.PKCS7
            AesAlg.Key = AesKey
            AesAlg.IV = Iv
            Using Ms As New MemoryStream(CipherBytes), Cs As New CryptoStream(Ms, AesAlg.CreateDecryptor(), CryptoStreamMode.Read), Sr As New StreamReader(Cs, Encoding.UTF8)
                Return (MsgType, Sr.ReadToEnd())
            End Using
        End Using
    End Function

    Private Sub SendEncryptedFileStream(Stream As Stream, AuthKey As Byte(), AesKey As Byte(), FilePath As String, ExpectedPlainSize As Long, Optional OnProgress As Action(Of Long, Long) = Nothing)
        Dim FileIv = RandomBytes(16)
        Dim CipherLen As Long = ((ExpectedPlainSize \ 16L) + 1L) * 16L
        Stream.Write(FileIv, 0, 16)
        WriteInt64BE(Stream, CipherLen)

        Using Hmac = New HMACSHA256(AuthKey), AesAlg = Aes.Create()
            Hmac.TransformBlock(FileIv, 0, FileIv.Length, Nothing, 0)
            AesAlg.KeySize = 256
            AesAlg.Mode = CipherMode.CBC
            AesAlg.Padding = PaddingMode.PKCS7
            AesAlg.Key = AesKey
            AesAlg.IV = FileIv

            Using OutSink As New HmacTeeStream(Stream, Hmac),
                  Cs As New CryptoStream(OutSink, AesAlg.CreateEncryptor(), CryptoStreamMode.Write),
                  Src As New FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Dim Buf(65535) As Byte
                Dim SentPlain As Long = 0
                Do
                    Dim N = Src.Read(Buf, 0, Buf.Length)
                    If N <= 0 Then Exit Do
                    Cs.Write(Buf, 0, N)
                    SentPlain += N
                    OnProgress?.Invoke(SentPlain, ExpectedPlainSize)
                Loop
                Cs.FlushFinalBlock()
            End Using

            Hmac.TransformFinalBlock(Array.Empty(Of Byte)(), 0, 0)
            Dim FinalMac = Hmac.Hash
            Stream.Write(FinalMac, 0, 32)
            Stream.Flush()
        End Using
    End Sub

    Private Sub ReceiveEncryptedFileStream(Stream As Stream, AuthKey As Byte(), AesKey As Byte(), DestPath As String, ExpectedPlainSize As Long, ExpectedSha256Hex As String, Optional OnProgress As Action(Of Long, Long) = Nothing)
        Dim FileIv = ReadExact(Stream, 16)
        Dim CipherLen = ReadInt64BE(Stream)
        If CipherLen <= 0 OrElse (CipherLen Mod 16L) <> 0 Then
            Throw New InvalidDataException($"加密文件流长度不合法：{CipherLen}")
        End If
        Dim ExpectedCipherLen = ((ExpectedPlainSize \ 16L) + 1L) * 16L
        If CipherLen <> ExpectedCipherLen Then
            Throw New InvalidDataException($"加密文件长度与元数据不匹配：{CipherLen} <> {ExpectedCipherLen}")
        End If

        Dim Parent = Path.GetDirectoryName(DestPath)
        If Not String.IsNullOrEmpty(Parent) Then Directory.CreateDirectory(Parent)
        Dim TmpPath = DestPath & ".part_" & Guid.NewGuid().ToString("N").Substring(0, 6)

        Try
            Using Hmac = New HMACSHA256(AuthKey), PlainSha = SHA256.Create(), AesAlg = Aes.Create()
                Hmac.TransformBlock(FileIv, 0, FileIv.Length, Nothing, 0)
                AesAlg.KeySize = 256
                AesAlg.Mode = CipherMode.CBC
                AesAlg.Padding = PaddingMode.PKCS7
                AesAlg.Key = AesKey
                AesAlg.IV = FileIv

                Using BoundIn As New BoundedHmacReadStream(Stream, CipherLen, Hmac),
                      Cs As New CryptoStream(BoundIn, AesAlg.CreateDecryptor(), CryptoStreamMode.Read),
                      Dst As New FileStream(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None)
                    Dim Buf(65535) As Byte
                    Dim TotalPlain As Long = 0
                    Do
                        Dim N = Cs.Read(Buf, 0, Buf.Length)
                        If N <= 0 Then Exit Do
                        Dst.Write(Buf, 0, N)
                        PlainSha.TransformBlock(Buf, 0, N, Nothing, 0)
                        TotalPlain += N
                        OnProgress?.Invoke(TotalPlain, ExpectedPlainSize)
                    Loop
                    If TotalPlain <> ExpectedPlainSize Then
                        Throw New InvalidDataException($"接收字节数不完整：{TotalPlain} / {ExpectedPlainSize}")
                    End If
                End Using

                Hmac.TransformFinalBlock(Array.Empty(Of Byte)(), 0, 0)
                PlainSha.TransformFinalBlock(Array.Empty(Of Byte)(), 0, 0)
                Dim RemoteMac = ReadExact(Stream, 32)
                If Not CryptographicEquals(Hmac.Hash, RemoteMac) Then
                    Throw New UnauthorizedAccessException("文件流 HMAC 完整性校验失败！")
                End If
                Dim ActualShaHex = BitConverter.ToString(PlainSha.Hash).Replace("-", "").ToLowerInvariant()
                If Not String.IsNullOrEmpty(ExpectedSha256Hex) AndAlso
                   Not ActualShaHex.Equals(ExpectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase) Then
                    Throw New InvalidDataException($"文件 SHA-256 校验不匹配（预期 {ExpectedSha256Hex}，实际 {ActualShaHex}）")
                End If
            End Using

            If File.Exists(DestPath) Then File.Delete(DestPath)
            File.Move(TmpPath, DestPath)
        Catch
            Try
                If File.Exists(TmpPath) Then File.Delete(TmpPath)
            Catch
            End Try
            Throw
        End Try
    End Sub

    Private Class HmacTeeStream
        Inherits Stream
        Private ReadOnly _Inner As Stream
        Private ReadOnly _Hmac As HMACSHA256
        Public Sub New(Inner As Stream, Hmac As HMACSHA256)
            _Inner = Inner
            _Hmac = Hmac
        End Sub
        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return False
            End Get
        End Property
        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return False
            End Get
        End Property
        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return True
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
            _Inner.Flush()
        End Sub
        Public Overrides Sub Write(buffer() As Byte, offset As Integer, count As Integer)
            If count > 0 Then
                _Hmac.TransformBlock(buffer, offset, count, Nothing, 0)
                _Inner.Write(buffer, offset, count)
            End If
        End Sub
        Public Overrides Function Read(buffer() As Byte, offset As Integer, count As Integer) As Integer
            Throw New NotSupportedException()
        End Function
        Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
            Throw New NotSupportedException()
        End Function
        Public Overrides Sub SetLength(value As Long)
            Throw New NotSupportedException()
        End Sub
    End Class

    Private Class BoundedHmacReadStream
        Inherits Stream
        Private ReadOnly _Inner As Stream
        Private ReadOnly _Hmac As HMACSHA256
        Private _Remaining As Long
        Public Sub New(Inner As Stream, TotalLength As Long, Hmac As HMACSHA256)
            _Inner = Inner
            _Remaining = TotalLength
            _Hmac = Hmac
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
        Public Overrides Function Read(buffer() As Byte, offset As Integer, count As Integer) As Integer
            If _Remaining <= 0 OrElse count <= 0 Then Return 0
            Dim ToRead = CInt(Math.Min(CLng(count), _Remaining))
            Dim N = _Inner.Read(buffer, offset, ToRead)
            If N <= 0 Then Throw New EndOfStreamException("文件加密流提前结束")
            _Hmac.TransformBlock(buffer, offset, N, Nothing, 0)
            _Remaining -= N
            Return N
        End Function
        Public Overrides Sub Write(buffer() As Byte, offset As Integer, count As Integer)
            Throw New NotSupportedException()
        End Sub
        Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
            Throw New NotSupportedException()
        End Function
        Public Overrides Sub SetLength(value As Long)
            Throw New NotSupportedException()
        End Sub
    End Class

#End Region

#Region "房主侧监听宿主 (Host)"

    Public Class EasyTierSideChannelHost
        Implements IDisposable

        Private ReadOnly _Port As Integer
        Private ReadOnly _Secret As String
        Private ReadOnly _Callbacks As SideChannelHostCallbacks
        Private ReadOnly _Offers As New ConcurrentDictionary(Of String, SideChannelOffer)(StringComparer.OrdinalIgnoreCase)
        Private _Listener As TcpListener = Nothing
        Private _ListenThread As Thread = Nothing
        Private _IsStopped As Integer = 0
        Private _BoundPort As Integer = 0

        Public ReadOnly Property BoundPort As Integer
            Get
                Return _BoundPort
            End Get
        End Property

        Public Sub New(ListenPort As Integer, Secret As String, Callbacks As SideChannelHostCallbacks)
            _Port = ListenPort
            _Secret = If(Secret, "")
            _Callbacks = If(Callbacks, New SideChannelHostCallbacks())
        End Sub

        Public Sub AddOffer(Offer As SideChannelOffer)
            If Offer Is Nothing OrElse String.IsNullOrWhiteSpace(Offer.OfferId) Then Return
            _Offers(Offer.OfferId) = Offer
        End Sub

        Public Function RemoveOffer(OfferId As String) As Boolean
            Dim Removed As SideChannelOffer = Nothing
            Return _Offers.TryRemove(OfferId, Removed)
        End Function

        Public Function GetOffersSnapshot() As List(Of SideChannelOffer)
            Return _Offers.Values.OrderByDescending(Function(o) o.CreatedUtc).ToList()
        End Function

        Public Sub ClearOffers()
            _Offers.Clear()
        End Sub

        Public Sub Start()
            _Listener = New TcpListener(IPAddress.Loopback, _Port)
            _Listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, True)
            _Listener.Start()
            _BoundPort = CType(_Listener.LocalEndpoint, IPEndPoint).Port
            _ListenThread = New Thread(AddressOf AcceptLoop) With {.IsBackground = True, .Name = "ET-SideChannel-Host"}
            _ListenThread.Start()
        End Sub

        Private Sub AcceptLoop()
            While Volatile.Read(_IsStopped) = 0
                Try
                    Dim Client = _Listener.AcceptTcpClient()
                    Dim Worker As New Thread(Sub() HandleClientConnection(Client)) With {.IsBackground = True, .Name = "ET-SideChannel-Worker"}
                    Worker.Start()
                Catch ex As SocketException
                    If Volatile.Read(_IsStopped) <> 0 Then Exit While
                Catch ex As ObjectDisposedException
                    Exit While
                Catch ex As Exception
                    If Volatile.Read(_IsStopped) <> 0 Then Exit While
                    _Callbacks.OnLog?.Invoke("SideChannel Accept 异常：" & ex.Message)
                End Try
            End While
        End Sub

        Private Sub HandleClientConnection(Client As TcpClient)
            Using Client
                Try
                    Client.ReceiveTimeout = 120000
                    Client.SendTimeout = 120000
                    Client.NoDelay = True
                    Using Ns = Client.GetStream()
                        Dim Keys = PerformServerHandshake(Ns, _Secret)
                        Dim Frame = ReadEncryptedFrame(Ns, Keys.AuthKey, Keys.AesKey)
                        Select Case Frame.MsgType
                            Case SideMsgType.ReqModManifest
                                Dim ManifestJson As String = If(_Callbacks.GetModManifestJson?.Invoke(), "{}")
                                SendEncryptedFrame(Ns, Keys.AuthKey, Keys.AesKey, SideMsgType.RespModManifest, ManifestJson)

                            Case SideMsgType.ReqOfferList
                                Dim ListJson = JsonConvert.SerializeObject(_Offers.Values.OrderByDescending(Function(o) o.CreatedUtc).ToList())
                                SendEncryptedFrame(Ns, Keys.AuthKey, Keys.AesKey, SideMsgType.RespOfferList, ListJson)

                            Case SideMsgType.ReqPullOffer
                                Dim ReqObj = JObject.Parse(Frame.JsonPayload)
                                Dim OfferId = If(ReqObj("offerId")?.ToString(), "")
                                Dim TargetOffer As SideChannelOffer = Nothing
                                If Not _Offers.TryGetValue(OfferId, TargetOffer) OrElse TargetOffer Is Nothing OrElse Not File.Exists(TargetOffer.FilePath) Then
                                    SendEncryptedFrame(Ns, Keys.AuthKey, Keys.AesKey, SideMsgType.ErrorFrame, New JObject From {{"error", "共享文件已下线或不存在"}}.ToString(Formatting.None))
                                    Return
                                End If
                                Dim MetaJson = JsonConvert.SerializeObject(TargetOffer)
                                SendEncryptedFrame(Ns, Keys.AuthKey, Keys.AesKey, SideMsgType.RespPullMeta, MetaJson)
                                SendEncryptedFileStream(Ns, Keys.AuthKey, Keys.AesKey, TargetOffer.FilePath, TargetOffer.SizeBytes)

                            Case SideMsgType.ReqPushFile
                                Dim ReqOffer = JsonConvert.DeserializeObject(Of SideChannelOffer)(Frame.JsonPayload)
                                If ReqOffer Is Nothing OrElse ReqOffer.SizeBytes <= 0 Then
                                    SendEncryptedFrame(Ns, Keys.AuthKey, Keys.AesKey, SideMsgType.RespPushDecision, New JObject From {{"accepted", False}, {"reason", "无效的文件元数据"}}.ToString(Formatting.None))
                                    Return
                                End If
                                Dim Decision As (Accepted As Boolean, TempReceivePath As String, FinalDestPath As String, ActionKind As String, Message As String) =
                                    If(_Callbacks.ConfirmIncomingPush IsNot Nothing,
                                        _Callbacks.ConfirmIncomingPush(ReqOffer),
                                        (Accepted:=False, TempReceivePath:="", FinalDestPath:="", ActionKind:="", Message:="房主未开启接收"))
                                If Not Decision.Accepted OrElse String.IsNullOrWhiteSpace(Decision.TempReceivePath) Then
                                    SendEncryptedFrame(Ns, Keys.AuthKey, Keys.AesKey, SideMsgType.RespPushDecision,
                                        New JObject From {{"accepted", False}, {"reason", If(String.IsNullOrWhiteSpace(Decision.Message), "房主已拒绝接收", Decision.Message)}}.ToString(Formatting.None))
                                    Return
                                End If
                                SendEncryptedFrame(Ns, Keys.AuthKey, Keys.AesKey, SideMsgType.RespPushDecision,
                                    New JObject From {{"accepted", True}, {"reason", ""}}.ToString(Formatting.None))

                                Try
                                    ReceiveEncryptedFileStream(Ns, Keys.AuthKey, Keys.AesKey, Decision.TempReceivePath, ReqOffer.SizeBytes, ReqOffer.Sha256)
                                    Dim FinalPath = If(String.IsNullOrWhiteSpace(Decision.FinalDestPath), Decision.TempReceivePath, Decision.FinalDestPath)
                                    If Decision.ActionKind = "extract_save" Then
                                        ExtractWorldSaveZipSafely(Decision.TempReceivePath, FinalPath, DeleteZipAfter:=True)
                                    ElseIf Not FinalPath.Equals(Decision.TempReceivePath, StringComparison.OrdinalIgnoreCase) Then
                                        Directory.CreateDirectory(Path.GetDirectoryName(FinalPath))
                                        If File.Exists(FinalPath) Then File.Delete(FinalPath)
                                        File.Move(Decision.TempReceivePath, FinalPath)
                                    End If
                                    SendEncryptedFrame(Ns, Keys.AuthKey, Keys.AesKey, SideMsgType.TransferResultAck, New JObject From {{"ok", True}}.ToString(Formatting.None))
                                    _Callbacks.OnPushCompleted?.Invoke(ReqOffer, True, FinalPath, "")
                                Catch recvEx As Exception
                                    Try
                                        SendEncryptedFrame(Ns, Keys.AuthKey, Keys.AesKey, SideMsgType.TransferResultAck, New JObject From {{"ok", False}, {"error", recvEx.Message}}.ToString(Formatting.None))
                                    Catch
                                    End Try
                                    _Callbacks.OnPushCompleted?.Invoke(ReqOffer, False, "", recvEx.Message)
                                End Try
                        End Select
                    End Using
                Catch ex As Exception
                    _Callbacks.OnLog?.Invoke("SideChannel Worker：" & ex.Message)
                End Try
            End Using
        End Sub

        Public Sub StopHost()
            If Interlocked.Exchange(_IsStopped, 1) = 1 Then Return
            Try
                _Listener?.Stop()
            Catch
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            StopHost()
        End Sub
    End Class

#End Region

#Region "客户端侧请求 (Client)"

    Private Function ConnectAndHandshake(Port As Integer, Secret As String, TimeoutMs As Integer) As (Client As TcpClient, Stream As NetworkStream, AuthKey As Byte(), AesKey As Byte())
        Dim Tcp As New TcpClient()
        Dim AsyncRes = Tcp.BeginConnect(IPAddress.Loopback, Port, Nothing, Nothing)
        If Not AsyncRes.AsyncWaitHandle.WaitOne(TimeoutMs) Then
            Tcp.Close()
            Throw New TimeoutException($"连接虚网辅助通道 (127.0.0.1:{Port}) 超时")
        End If
        Tcp.EndConnect(AsyncRes)
        Tcp.ReceiveTimeout = TimeoutMs
        Tcp.SendTimeout = TimeoutMs
        Tcp.NoDelay = True
        Dim Ns = Tcp.GetStream()
        Try
            Dim Keys = PerformClientHandshake(Ns, Secret)
            Return (Tcp, Ns, Keys.AuthKey, Keys.AesKey)
        Catch
            Tcp.Close()
            Throw
        End Try
    End Function

    Public Function RequestModManifest(LocalPort As Integer, Secret As String, Optional TimeoutMs As Integer = 8000) As String
        Dim Conn = ConnectAndHandshake(LocalPort, Secret, TimeoutMs)
        Using TcpCli As TcpClient = Conn.Client, Ns As NetworkStream = Conn.Stream
            SendEncryptedFrame(Ns, Conn.AuthKey, Conn.AesKey, SideMsgType.ReqModManifest, "{}")
            Dim Resp = ReadEncryptedFrame(Ns, Conn.AuthKey, Conn.AesKey)
            If Resp.MsgType = SideMsgType.ErrorFrame Then Throw New InvalidOperationException(Resp.JsonPayload)
            Return Resp.JsonPayload
        End Using
    End Function

    Public Function RequestOfferList(LocalPort As Integer, Secret As String, Optional TimeoutMs As Integer = 8000) As List(Of SideChannelOffer)
        Dim Conn = ConnectAndHandshake(LocalPort, Secret, TimeoutMs)
        Using TcpCli As TcpClient = Conn.Client, Ns As NetworkStream = Conn.Stream
            SendEncryptedFrame(Ns, Conn.AuthKey, Conn.AesKey, SideMsgType.ReqOfferList, "{}")
            Dim Resp = ReadEncryptedFrame(Ns, Conn.AuthKey, Conn.AesKey)
            If Resp.MsgType = SideMsgType.ErrorFrame Then Throw New InvalidOperationException(Resp.JsonPayload)
            Dim List = JsonConvert.DeserializeObject(Of List(Of SideChannelOffer))(Resp.JsonPayload)
            Return If(List, New List(Of SideChannelOffer)())
        End Using
    End Function

    Public Sub PullOfferToFile(LocalPort As Integer, Secret As String, OfferId As String, DestPath As String,
                               Optional OnProgress As Action(Of Long, Long) = Nothing,
                               Optional TransferTimeoutMs As Integer = 120000)
        Dim Conn = ConnectAndHandshake(LocalPort, Secret, TransferTimeoutMs)
        Using TcpCli As TcpClient = Conn.Client, Ns As NetworkStream = Conn.Stream
            SendEncryptedFrame(Ns, Conn.AuthKey, Conn.AesKey, SideMsgType.ReqPullOffer,
                New JObject From {{"offerId", OfferId}}.ToString(Formatting.None))
            Dim MetaResp = ReadEncryptedFrame(Ns, Conn.AuthKey, Conn.AesKey)
            If MetaResp.MsgType = SideMsgType.ErrorFrame Then
                Dim ErrObj = JObject.Parse(MetaResp.JsonPayload)
                Throw New InvalidOperationException(If(ErrObj("error")?.ToString(), "远端返回错误"))
            End If
            Dim Meta = JsonConvert.DeserializeObject(Of SideChannelOffer)(MetaResp.JsonPayload)
            If Meta Is Nothing OrElse Meta.SizeBytes <= 0 Then Throw New InvalidDataException("无效的共享文件元数据")
            ReceiveEncryptedFileStream(Ns, Conn.AuthKey, Conn.AesKey, DestPath, Meta.SizeBytes, Meta.Sha256, OnProgress)
        End Using
    End Sub

    Public Sub PushFileToHost(LocalPort As Integer, Secret As String, Kind As String, DisplayName As String, FileName As String,
                              LocalFilePath As String, SenderName As String,
                              Optional PrecomputedSha256 As String = Nothing,
                              Optional OnProgress As Action(Of Long, Long) = Nothing,
                              Optional TransferTimeoutMs As Integer = 120000)
        Dim Fi As New FileInfo(LocalFilePath)
        If Not Fi.Exists OrElse Fi.Length <= 0 Then Throw New FileNotFoundException("待发送文件不存在或为空", LocalFilePath)
        Dim ShaHex = If(String.IsNullOrWhiteSpace(PrecomputedSha256), ComputeSha256HexFile(LocalFilePath), PrecomputedSha256)

        Dim Conn = ConnectAndHandshake(LocalPort, Secret, TransferTimeoutMs)
        Using TcpCli As TcpClient = Conn.Client, Ns As NetworkStream = Conn.Stream
            Dim OfferMeta As New SideChannelOffer With {
                .OfferId = Guid.NewGuid().ToString("N").Substring(0, 10),
                .Kind = Kind,
                .Name = DisplayName,
                .FileName = FileName,
                .SizeBytes = Fi.Length,
                .Sha256 = ShaHex,
                .Sender = If(String.IsNullOrWhiteSpace(SenderName), "玩家", SenderName)
            }
            SendEncryptedFrame(Ns, Conn.AuthKey, Conn.AesKey, SideMsgType.ReqPushFile, JsonConvert.SerializeObject(OfferMeta))
            Dim DecResp = ReadEncryptedFrame(Ns, Conn.AuthKey, Conn.AesKey)
            If DecResp.MsgType = SideMsgType.ErrorFrame Then Throw New InvalidOperationException(DecResp.JsonPayload)
            Dim DecObj = JObject.Parse(DecResp.JsonPayload)
            If DecObj("accepted")?.ToObject(Of Boolean)() <> True Then
                Throw New OperationCanceledException(If(DecObj("reason")?.ToString(), "房主拒绝接收"))
            End If
            SendEncryptedFileStream(Ns, Conn.AuthKey, Conn.AesKey, LocalFilePath, Fi.Length, OnProgress)
            Dim AckFrame = ReadEncryptedFrame(Ns, Conn.AuthKey, Conn.AesKey)
            Dim AckObj = JObject.Parse(AckFrame.JsonPayload)
            If AckObj("ok")?.ToObject(Of Boolean)() <> True Then
                Throw New IOException(If(AckObj("error")?.ToString(), "对端校验接收文件失败"))
            End If
        End Using
    End Sub

#End Region

End Module
