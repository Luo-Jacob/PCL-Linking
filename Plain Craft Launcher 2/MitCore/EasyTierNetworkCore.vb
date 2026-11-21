' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Standalone EasyTier network control helpers: architecture download/SHA-256 verification,
' peer normalization & concurrent TCP RTT benchmark, latency/jitter statistics, LAN 4445 scan,
' and invite code extraction (no PCL UI/core dependencies).

Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json.Linq

Public Module EasyTierNetworkCore

    Public Const LinkDownloadVersion As String = "2.6.4"

    Public Class LanBroadcastInfo
        Public Property Port As Integer
        Public Property Motd As String
        Public Property SourceLabel As String
    End Class

    Public Function GetEasyTierDownloadUrls(Optional Arch As String = Nothing) As List(Of String)
        If Arch Is Nothing Then Arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE")
        Dim ArchName = If(Arch IsNot Nothing AndAlso Arch.IndexOf("ARM", StringComparison.OrdinalIgnoreCase) >= 0, "arm64", "x86_64")
        Dim FileName = $"easytier-windows-{ArchName}-v{LinkDownloadVersion}.zip"
        Return New List(Of String) From {
            $"https://github.com/EasyTier/EasyTier/releases/download/v{LinkDownloadVersion}/{FileName}"
        }
    End Function

    Public Function GetEasyTierExpectedSha256(Optional Arch As String = Nothing) As String
        If Arch Is Nothing Then Arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE")
        Dim IsArm = Arch IsNot Nothing AndAlso Arch.IndexOf("ARM", StringComparison.OrdinalIgnoreCase) >= 0
        Return If(IsArm,
            "37023f8a3451c9234b17ee2089a03dc344ce90d803b5b359cb6c46682b0549b4",
            "27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84")
    End Function

    Public Function ComputeFileSha256Hex(FilePath As String) As String
        Using Fs = File.OpenRead(FilePath), Sha = SHA256.Create()
            Return BitConverter.ToString(Sha.ComputeHash(Fs)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Public Function VerifyFileSha256(FilePath As String, ExpectedHex As String) As Boolean
        If String.IsNullOrWhiteSpace(FilePath) OrElse Not File.Exists(FilePath) Then Return False
        Dim Actual = ComputeFileSha256Hex(FilePath)
        Return String.Equals(Actual, If(ExpectedHex, "").Trim(), StringComparison.OrdinalIgnoreCase)
    End Function

    Public Function NormalizePeerUri(Address As String) As String
        If String.IsNullOrWhiteSpace(Address) Then Return Nothing
        Address = Address.Trim()
        If Address.StartsWith("-") Then Return Nothing
        If Address.Any(Function(c) Char.IsWhiteSpace(c) OrElse Char.IsControl(c) OrElse c = """"c OrElse c = "'"c OrElse c = "`"c) Then Return Nothing
        Dim AllowedSchemes As String() = {"tcp", "udp", "ws", "wss", "http", "https"}
        If Address.Contains("://") Then
            Dim SchemeIdx = Address.IndexOf("://", StringComparison.Ordinal)
            Dim RawScheme = Address.Substring(0, SchemeIdx).ToLowerInvariant()
            If Not AllowedSchemes.Contains(RawScheme) Then Return Nothing
        Else
            Address = "tcp://" & Address
        End If
        Dim Parsed As Uri = Nothing
        If Not Uri.TryCreate(Address, UriKind.Absolute, Parsed) Then Return Nothing
        If Not AllowedSchemes.Contains(Parsed.Scheme.ToLowerInvariant()) Then Return Nothing
        If Not String.IsNullOrEmpty(Parsed.UserInfo) Then Return Nothing
        If String.IsNullOrEmpty(Parsed.Host) OrElse Parsed.Port <= 0 OrElse Parsed.Port > 65535 Then Return Nothing
        Return Address
    End Function

    Public Function ParseCommunityNodesJson(RawJson As String) As List(Of String)
        Dim Result As New List(Of String)
        If String.IsNullOrWhiteSpace(RawJson) Then Return Result
        Dim Data = JObject.Parse(RawJson)("data")
        If Data Is Nothing OrElse Data("items") Is Nothing Then Return Result
        For Each Item As JToken In CType(Data("items"), JArray)
            Try
                If Item("is_active") IsNot Nothing AndAlso Not Item("is_active").Value(Of Boolean)() Then Continue For
                If Item("allow_relay") IsNot Nothing AndAlso Not Item("allow_relay").Value(Of Boolean)() Then Continue For
                Dim Address As String = If(Item("address") Is Nothing, "", Item("address").ToString())
                Dim Norm = NormalizePeerUri(Address)
                If Norm IsNot Nothing AndAlso Not Result.Contains(Norm) Then Result.Add(Norm)
            Catch
            End Try
        Next
        Return Result
    End Function

    Public Function ProbeTcpPeersConcurrent(Candidates As IEnumerable(Of String), Optional TimeoutMs As Integer = 1500) As List(Of (Peer As String, LatencyMs As Integer, Reachable As Boolean))
        Dim Results As New ConcurrentBag(Of (Peer As String, LatencyMs As Integer, Reachable As Boolean))
        Dim Workers As New List(Of Thread)
        For Each PeerAddr In Candidates
            Dim LocalPeer = PeerAddr
            Dim T As New Thread(
            Sub()
                Dim Sw As Stopwatch = Stopwatch.StartNew()
                Dim Ok As Boolean = False
                Dim Elapsed As Integer = TimeoutMs
                Try
                    Dim Parsed As Uri = Nothing
                    If Uri.TryCreate(LocalPeer, UriKind.Absolute, Parsed) AndAlso Not String.IsNullOrEmpty(Parsed.Host) AndAlso Parsed.Port > 0 Then
                        Using Client As New TcpClient()
                            Dim ConnTask = Client.ConnectAsync(Parsed.Host, Parsed.Port)
                            ConnTask.ContinueWith(Sub(faultedTask)
                                                      Dim _ex = faultedTask.Exception
                                                  End Sub, Tasks.TaskContinuationOptions.OnlyOnFaulted)
                            If ConnTask.Wait(TimeoutMs) AndAlso Client.Connected Then
                                Ok = True
                                Elapsed = Math.Max(1, CInt(Sw.ElapsedMilliseconds))
                            End If
                        End Using
                    End If
                Catch
                    Ok = False
                End Try
                Results.Add((LocalPeer, If(Ok, Elapsed, TimeoutMs), Ok))
            End Sub) With {.IsBackground = True, .Name = $"PeerPing {LocalPeer}"}
            T.Start()
            Workers.Add(T)
        Next
        For Each T In Workers
            T.Join()
        Next
        Return Results.OrderByDescending(Function(r) r.Reachable).ThenBy(Function(r) r.LatencyMs).ToList()
    End Function

    Public Structure LatencySeriesSummary
        Public SampleCount As Integer
        Public AvgMs As Double
        Public MinMs As Double
        Public MaxMs As Double
        Public JitterMs As Double
    End Structure

    Public Function ComputeLatencySeriesStats(Samples As IEnumerable(Of Double)) As LatencySeriesSummary
        Dim Valid = If(Samples, Array.Empty(Of Double)()).Where(Function(v) v > 0).ToList()
        If Valid.Count = 0 Then Return New LatencySeriesSummary()
        Dim Avg = Valid.Average()
        Dim Jitter As Double = 0
        If Valid.Count >= 2 Then
            Dim DiffSum As Double = 0
            For I = 1 To Valid.Count - 1
                DiffSum += Math.Abs(Valid(I) - Valid(I - 1))
            Next
            Jitter = DiffSum / (Valid.Count - 1)
        End If
        Return New LatencySeriesSummary With {
            .SampleCount = Valid.Count,
            .AvgMs = Avg,
            .MinMs = Valid.Min(),
            .MaxMs = Valid.Max(),
            .JitterMs = Jitter
        }
    End Function

    Public Function TryExtractInviteFromText(RawText As String, ByRef OutCode As String, ByRef OutVerify As String) As Boolean
        OutCode = ""
        OutVerify = ""
        If String.IsNullOrWhiteSpace(RawText) Then Return False
        Dim MCode = Regex.Match(RawText, "邀请码[：:\s【\[]+([A-Za-z0-9\-]{10,})")
        If Not MCode.Success Then
            MCode = Regex.Match(RawText, "\b(P[0-9A-Fa-f]{4}-[A-Za-z0-9]{4,8}-[A-Za-z0-9]{4,8}(?:-[A-Za-z0-9]{3,8})*)\b")
        End If
        If Not MCode.Success Then Return False
        OutCode = MCode.Groups(1).Value.Trim()
        Dim MBracketVer = Regex.Match(RawText, "(?:验证码|密码)[：:\s]*(?:【([^】]+)】|\[([^\]]+)\])")
        If MBracketVer.Success Then
            OutVerify = If(MBracketVer.Groups(1).Success, MBracketVer.Groups(1).Value, MBracketVer.Groups(2).Value).Trim()
        Else
            Dim MVer = Regex.Match(RawText, "(?:验证码|密码)[：:\s]+([^\s】\]，。,！!]+)")
            If MVer.Success Then OutVerify = MVer.Groups(1).Value.Trim()
        End If
        Return OutCode.Length >= 10
    End Function

    Public Function ScanLocalLanBroadcasts(Optional LocalServerPortHint As Integer = 0, Optional TimeoutMs As Integer = 2200) As List(Of LanBroadcastInfo)
        Dim Found As New Dictionary(Of Integer, LanBroadcastInfo)
        If LocalServerPortHint >= 1 AndAlso LocalServerPortHint <= 65535 Then
            Try
                Using TcpProbe As New TcpClient()
                    Dim Ar = TcpProbe.BeginConnect("127.0.0.1", LocalServerPortHint, Nothing, Nothing)
                    Using Wh = Ar.AsyncWaitHandle
                        If Wh.WaitOne(180, False) Then
                            TcpProbe.EndConnect(Ar)
                            Found(LocalServerPortHint) = New LanBroadcastInfo With {
                                .Port = LocalServerPortHint,
                                .Motd = "本地开服服务端",
                                .SourceLabel = "本地开服"
                            }
                        End If
                    End Using
                End Using
            Catch
            End Try
        End If

        Dim LocalIps As New HashSet(Of IPAddress)({IPAddress.Loopback, IPAddress.IPv6Loopback})
        Try
            For Each Nic In NetworkInterface.GetAllNetworkInterfaces()
                If Nic.OperationalStatus <> OperationalStatus.Up Then Continue For
                For Each Ua In Nic.GetIPProperties().UnicastAddresses
                    LocalIps.Add(Ua.Address)
                Next
            Next
        Catch
        End Try

        Try
            Dim MulticastIp = IPAddress.Parse("224.0.2.60")
            Using Udp As New UdpClient()
                Udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, True)
                Udp.ExclusiveAddressUse = False
                Udp.Client.Bind(New IPEndPoint(IPAddress.Any, 4445))
                Try : Udp.JoinMulticastGroup(MulticastIp) : Catch : End Try
                Udp.Client.ReceiveTimeout = 250

                Dim Sw = Stopwatch.StartNew()
                While Sw.ElapsedMilliseconds < TimeoutMs
                    Try
                        Dim RemoteEp As IPEndPoint = Nothing
                        Dim Bytes = Udp.Receive(RemoteEp)
                        If RemoteEp IsNot Nothing Then
                            Dim SrcIp = If(RemoteEp.Address.IsIPv4MappedToIPv6, RemoteEp.Address.MapToIPv4(), RemoteEp.Address)
                            If Not IPAddress.IsLoopback(SrcIp) AndAlso Not LocalIps.Contains(SrcIp) Then Continue While
                        End If
                        Dim PayloadText = Encoding.UTF8.GetString(Bytes)
                        If PayloadText.Contains("LML 联机") OrElse PayloadText.Contains("LML Pro 联机") OrElse PayloadText.Contains("PCL Linking 联机") Then Continue While
                        Dim M = Regex.Match(PayloadText, "\[MOTD\](.*?)\[/MOTD\].*?\[AD\](\d+)\[/AD\]")
                        If M.Success Then
                            Dim ParsedPort As Integer
                            If Integer.TryParse(M.Groups(2).Value, ParsedPort) AndAlso ParsedPort >= 1 AndAlso ParsedPort <= 65535 Then
                                Dim CleanMotd = Regex.Replace(M.Groups(1).Value, "§[0-9a-fklmnorx]", "", RegexOptions.IgnoreCase).Trim()
                                If String.IsNullOrWhiteSpace(CleanMotd) Then CleanMotd = "局域网世界"
                                If Not Found.ContainsKey(ParsedPort) Then
                                    Found(ParsedPort) = New LanBroadcastInfo With {
                                        .Port = ParsedPort,
                                        .Motd = CleanMotd,
                                        .SourceLabel = "MC 局域网"
                                    }
                                End If
                            End If
                        End If
                    Catch
                    End Try
                End While

                Try : Udp.DropMulticastGroup(MulticastIp) : Catch : End Try
            End Using
        Catch
        End Try

        Return Found.Values.OrderBy(Function(w) w.Port).ToList()
    End Function

End Module
