' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Standalone Minecraft Server List Ping (SLP 1.7+) TCP probe engine (no PCL UI/core dependencies).

Imports System.Diagnostics
Imports System.IO
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

Public Module SlpProbeEngine

    Public Class ServerProbeResult
        Public Property Online As Boolean = False
        Public Property Address As String = ""
        Public Property Host As String = ""
        Public Property Port As Integer = 25565
        Public Property LatencyMs As Long = -1
        Public Property VersionName As String = ""
        Public Property ProtocolVersion As String = ""
        Public Property OnlinePlayers As Integer = 0
        Public Property MaxPlayers As Integer = 0
        Public Property SamplePlayers As New List(Of String)
        Public Property Motd As String = ""
        Public Property ErrorMessage As String = ""
    End Class

    Public Function ParseHostAndPort(RawAddr As String, ByRef OutHost As String, ByRef OutPort As Integer) As Boolean
        OutHost = ""
        OutPort = 25565
        Dim TrimAddr = If(RawAddr, "").Trim()
        If String.IsNullOrWhiteSpace(TrimAddr) Then Return False
        If TrimAddr.Contains("://") Then
            Dim SchemeIdx = TrimAddr.IndexOf("://", StringComparison.Ordinal)
            TrimAddr = TrimAddr.Substring(SchemeIdx + 3)
        End If
        TrimAddr = TrimAddr.TrimEnd("/"c)

        If TrimAddr.StartsWith("[") Then
            Dim EndBracket = TrimAddr.IndexOf("]"c)
            If EndBracket <= 1 Then Return False
            OutHost = TrimAddr.Substring(1, EndBracket - 1)
            If EndBracket + 1 < TrimAddr.Length AndAlso TrimAddr(EndBracket + 1) = ":"c Then
                Dim PortPart = TrimAddr.Substring(EndBracket + 2)
                Dim ParsedPort As Integer
                If Not Integer.TryParse(PortPart, ParsedPort) OrElse ParsedPort < 1 OrElse ParsedPort > 65535 Then Return False
                OutPort = ParsedPort
            End If
            Return Not String.IsNullOrWhiteSpace(OutHost)
        End If

        Dim FirstColon = TrimAddr.IndexOf(":"c)
        Dim LastColon = TrimAddr.LastIndexOf(":"c)
        If FirstColon > 0 AndAlso FirstColon = LastColon Then
            OutHost = TrimAddr.Substring(0, FirstColon).Trim()
            Dim PortPart = TrimAddr.Substring(FirstColon + 1).Trim()
            Dim ParsedPort As Integer
            If Not Integer.TryParse(PortPart, ParsedPort) OrElse ParsedPort < 1 OrElse ParsedPort > 65535 Then Return False
            OutPort = ParsedPort
        Else
            OutHost = TrimAddr
        End If
        Return Not String.IsNullOrWhiteSpace(OutHost)
    End Function

    Public Function FormatHostAndPort(Host As String, Port As Integer, Optional OmitDefaultPort As Boolean = False) As String
        Dim CleanHost = If(Host, "").Trim()
        Dim WrappedHost = If(CleanHost.Contains(":"c) AndAlso Not CleanHost.StartsWith("["), $"[{CleanHost}]", CleanHost)
        If OmitDefaultPort AndAlso Port = 25565 Then Return WrappedHost
        Return $"{WrappedHost}:{Port}"
    End Function

    Private Sub WriteVarInt(Ms As Stream, Value As Integer)
        Dim UVal As UInteger = BitConverter.ToUInt32(BitConverter.GetBytes(Value), 0)
        While True
            If (UVal And Not &H7FUI) = 0UI Then
                Ms.WriteByte(CByte(UVal))
                Return
            End If
            Ms.WriteByte(CByte((UVal And &H7FUI) Or &H80UI))
            UVal >>= 7
        End While
    End Sub

    Private Sub WriteMcString(Ms As Stream, Text As String)
        Dim Bytes = Encoding.UTF8.GetBytes(If(Text, ""))
        WriteVarInt(Ms, Bytes.Length)
        Ms.Write(Bytes, 0, Bytes.Length)
    End Sub

    Private Function ReadVarInt(StreamObj As Stream) As Integer
        Dim NumRead As Integer = 0
        Dim Result As Integer = 0
        While True
            Dim RawByte = StreamObj.ReadByte()
            If RawByte < 0 Then Throw New EndOfStreamException("服务器在传输过程中关闭了连接")
            Dim B = CByte(RawByte)
            Result = Result Or (CInt(B And &H7F) << (7 * NumRead))
            NumRead += 1
            If NumRead > 5 Then Throw New InvalidDataException("无效的 VarInt 长度")
            If (B And &H80) = 0 Then Exit While
        End While
        Return Result
    End Function

    Private Function ReadExactBytes(StreamObj As Stream, Count As Integer) As Byte()
        If Count < 0 OrElse Count > 2097152 Then Throw New InvalidDataException($"响应数据包异常过长 ({Count} B)")
        Dim Buf(Count - 1) As Byte
        Dim Offset As Integer = 0
        While Offset < Count
            Dim Got = StreamObj.Read(Buf, Offset, Count - Offset)
            If Got <= 0 Then Throw New EndOfStreamException("读取服务器状态响应不完整")
            Offset += Got
        End While
        Return Buf
    End Function

    Private Function ExtractChatComponentText(Token As JToken) As String
        If Token Is Nothing Then Return ""
        If Token.Type = JTokenType.String Then Return Token.Value(Of String)()
        Dim Sb As New StringBuilder()
        If Token.Type = JTokenType.Object Then
            Dim Obj = CType(Token, JObject)
            Dim TextVal = Obj("text")?.Value(Of String)()
            If Not String.IsNullOrEmpty(TextVal) Then Sb.Append(TextVal)
            Dim Extra = TryCast(Obj("extra"), JArray)
            If Extra IsNot Nothing Then
                For Each ChildToken As JToken In Extra
                    Sb.Append(ExtractChatComponentText(ChildToken))
                Next
            End If
        ElseIf Token.Type = JTokenType.Array Then
            For Each ChildToken As JToken In CType(Token, JArray)
                Sb.Append(ExtractChatComponentText(ChildToken))
            Next
        End If
        Return Sb.ToString()
    End Function

    Private Function CleanMotdText(Raw As String) As String
        If String.IsNullOrEmpty(Raw) Then Return ""
        Dim NoColor = Regex.Replace(Raw, "§[0-9a-fklmnorx]", "", RegexOptions.IgnoreCase)
        Return Regex.Replace(NoColor.Replace(vbCr, "").Replace(vbLf, "  "), "\s{3,}", "  ").Trim()
    End Function

    Public Function ProbeMinecraftServer(RawAddr As String, Optional TimeoutMs As Integer = 4500) As ServerProbeResult
        Dim Res As New ServerProbeResult With {.Address = If(RawAddr, "").Trim()}
        Dim Host As String = ""
        Dim Port As Integer = 25565
        If Not ParseHostAndPort(Res.Address, Host, Port) Then
            Res.ErrorMessage = "服务器地址格式无效"
            Return Res
        End If
        Res.Host = Host
        Res.Port = Port

        Try
            Using Client As New TcpClient()
                Dim SwConnect = Stopwatch.StartNew()
                Dim AsyncRes = Client.BeginConnect(Host, Port, Nothing, Nothing)
                Using Wh = AsyncRes.AsyncWaitHandle
                    If Not Wh.WaitOne(TimeoutMs, False) Then
                        Res.ErrorMessage = $"连接超时（>{TimeoutMs} ms）"
                        Return Res
                    End If
                End Using
                Client.EndConnect(AsyncRes)
                SwConnect.Stop()
                Dim ConnectMs = Math.Max(1L, SwConnect.ElapsedMilliseconds)

                Client.ReceiveTimeout = TimeoutMs
                Client.SendTimeout = TimeoutMs
                Dim Ns = Client.GetStream()

                '1. Handshake (Packet ID 0x00, NextState = 1 Status)
                Using PayloadMs As New MemoryStream()
                    WriteVarInt(PayloadMs, &H0)
                    WriteVarInt(PayloadMs, 767)
                    WriteMcString(PayloadMs, Host)
                    PayloadMs.WriteByte(CByte((Port >> 8) And &HFF))
                    PayloadMs.WriteByte(CByte(Port And &HFF))
                    WriteVarInt(PayloadMs, 1)

                    Using FrameMs As New MemoryStream()
                        Dim Body = PayloadMs.ToArray()
                        WriteVarInt(FrameMs, Body.Length)
                        FrameMs.Write(Body, 0, Body.Length)
                        Dim FrameBytes = FrameMs.ToArray()
                        Ns.Write(FrameBytes, 0, FrameBytes.Length)
                    End Using
                End Using

                '2. Status Request (Length = 1, ID = 0x00)
                Dim ReqBytes As Byte() = {&H1, &H0}
                Ns.Write(ReqBytes, 0, ReqBytes.Length)
                Ns.Flush()

                '3. Status Response JSON
                Dim _PacketLen = ReadVarInt(Ns)
                Dim PacketId = ReadVarInt(Ns)
                If PacketId <> 0 Then Throw New InvalidDataException($"预期状态包 0x00，实际收到 0x{PacketId:X2}")
                Dim JsonLen = ReadVarInt(Ns)
                Dim JsonBytes = ReadExactBytes(Ns, JsonLen)
                Dim JsonText = Encoding.UTF8.GetString(JsonBytes)

                Dim Root = JObject.Parse(JsonText)
                Dim VerObj = TryCast(Root("version"), JObject)
                Res.VersionName = If(VerObj?("name")?.ToString(), "未知版本")
                Res.ProtocolVersion = If(VerObj?("protocol")?.ToString(), "?")
                Dim PlayersObj = TryCast(Root("players"), JObject)
                If PlayersObj IsNot Nothing Then
                    Res.OnlinePlayers = If(PlayersObj("online")?.Value(Of Integer)(), 0)
                    Res.MaxPlayers = If(PlayersObj("max")?.Value(Of Integer)(), 0)
                    Dim SampleArr = TryCast(PlayersObj("sample"), JArray)
                    If SampleArr IsNot Nothing Then
                        For Each SampleObj In SampleArr.Take(12)
                            Dim PlayerName = SampleObj?("name")?.ToString()
                            If Not String.IsNullOrWhiteSpace(PlayerName) Then Res.SamplePlayers.Add(PlayerName)
                        Next
                    End If
                End If
                Res.Motd = CleanMotdText(ExtractChatComponentText(Root("description")))

                '4. 0x01 Ping RTT measurement (falls back to TCP connect RTT on proxies)
                Res.LatencyMs = ConnectMs
                Try
                    Using PingMs As New MemoryStream()
                        PingMs.WriteByte(&H9)
                        PingMs.WriteByte(&H1)
                        Dim TimestampBytes = BitConverter.GetBytes(DateTime.UtcNow.Ticks)
                        PingMs.Write(TimestampBytes, 0, 8)
                        Dim PingFrame = PingMs.ToArray()
                        Dim SwPing = Stopwatch.StartNew()
                        Ns.Write(PingFrame, 0, PingFrame.Length)
                        Ns.Flush()
                        Dim PongLen = ReadVarInt(Ns)
                        Dim PongId = ReadVarInt(Ns)
                        If PongId = 1 AndAlso PongLen >= 9 Then
                            ReadExactBytes(Ns, 8)
                            SwPing.Stop()
                            Res.LatencyMs = Math.Max(1L, SwPing.ElapsedMilliseconds)
                        End If
                    End Using
                Catch
                End Try

                Res.Online = True
                Return Res
            End Using
        Catch ex As Exception
            Res.Online = False
            Res.ErrorMessage = ex.Message
            Return Res
        End Try
    End Function

End Module
