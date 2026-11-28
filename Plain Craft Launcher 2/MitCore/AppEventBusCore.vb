' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Lightweight protocol-enforced internal event bus, re-entrancy guard, SSRF IP guard,
' and credential/PII redaction utilities (no PCL UI dependencies).

Imports System.Collections.Concurrent
Imports System.Net
Imports System.Net.Sockets
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

Public Module AppEventBus

    Public Const ProtocolVersion As Integer = 1
    Public Const MaxHopDepth As Integer = 4
    Private Const MaxAuditEntries As Integer = 200
    Private ReadOnly TopicRegex As New Regex("^[a-z0-9_.:-]{3,64}$", RegexOptions.Compiled)
    Private ReadOnly SecretKeyRegex As New Regex("(secret|password|apikey|api_key|accesskey|token|auth|verify|credential|private)", RegexOptions.IgnoreCase Or RegexOptions.Compiled)

    Public NotInheritable Class BusEnvelope
        Public Property Protocol As Integer = ProtocolVersion
        Public Property EventId As String = ""
        Public Property Topic As String = ""
        Public Property SourceModule As String = ""
        Public Property TimestampUtc As DateTime = DateTime.UtcNow
        Public Property CorrelationId As String = ""
        Public Property HopCount As Integer = 0
        Public Property Payload As JObject = New JObject()
    End Class

    Private NotInheritable Class SubscriptionEntry
        Public ReadOnly Id As String = Guid.NewGuid().ToString("N")
        Public ReadOnly TopicPrefix As String
        Public ReadOnly SourceTag As String
        Public ReadOnly Handler As Action(Of BusEnvelope)

        Public Sub New(Prefix As String, Tag As String, Callback As Action(Of BusEnvelope))
            TopicPrefix = Prefix
            SourceTag = Tag
            Handler = Callback
        End Sub
    End Class

    Private NotInheritable Class UnsubscribeHandle
        Implements IDisposable

        Private ReadOnly SubId As String
        Private DisposedFlag As Integer = 0

        Public Sub New(Id As String)
            SubId = Id
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Threading.Interlocked.Exchange(DisposedFlag, 1) = 1 Then Return
            Dim Removed As SubscriptionEntry = Nothing
            Subscriptions.TryRemove(SubId, Removed)
        End Sub
    End Class

    Private ReadOnly Subscriptions As New ConcurrentDictionary(Of String, SubscriptionEntry)(StringComparer.Ordinal)
    Private ReadOnly AuditLock As New Object()
    Private ReadOnly AuditRing As New Queue(Of BusEnvelope)(MaxAuditEntries + 2)

    <ThreadStatic>
    Private CurrentHopDepth As Integer

    Public Function IsValidTopic(Topic As String) As Boolean
        If String.IsNullOrWhiteSpace(Topic) Then Return False
        Return TopicRegex.IsMatch(Topic.Trim())
    End Function

    Public Function SanitizePayload(Raw As JObject) As JObject
        If Raw Is Nothing Then Return New JObject()
        Dim CloneObj As New JObject()
        For Each Prop In Raw.Properties()
            If SecretKeyRegex.IsMatch(Prop.Name) Then
                CloneObj(Prop.Name) = "***REDACTED***"
            ElseIf Prop.Value IsNot Nothing AndAlso Prop.Value.Type = JTokenType.Object Then
                CloneObj(Prop.Name) = SanitizePayload(CType(Prop.Value, JObject))
            ElseIf Prop.Value IsNot Nothing AndAlso Prop.Value.Type = JTokenType.String Then
                CloneObj(Prop.Name) = SecurityGuard.RedactUserPathAndSecrets(Prop.Value.ToString())
            Else
                CloneObj(Prop.Name) = Prop.Value?.DeepClone()
            End If
        Next
        Return CloneObj
    End Function

    Public Function Subscribe(TopicOrPrefix As String, Handler As Action(Of BusEnvelope), Optional SourceTag As String = "unknown") As IDisposable
        If Handler Is Nothing Then Throw New ArgumentNullException(NameOf(Handler))
        Dim NormPrefix = If(TopicOrPrefix, "").Trim()
        If NormPrefix <> "*" AndAlso Not IsValidTopic(NormPrefix.TrimEnd("*"c, "."c)) Then
            Throw New ArgumentException($"非法总线订阅主题：{TopicOrPrefix}", NameOf(TopicOrPrefix))
        End If
        Dim Entry As New SubscriptionEntry(NormPrefix, If(SourceTag, "unknown"), Handler)
        Subscriptions(Entry.Id) = Entry
        Return New UnsubscribeHandle(Entry.Id)
    End Function

    Public Function Publish(Topic As String, SourceModule As String, Optional Payload As JObject = Nothing, Optional CorrelationId As String = Nothing) As Boolean
        Dim NormTopic = If(Topic, "").Trim()
        If Not IsValidTopic(NormTopic) Then
            Return False
        End If
        If CurrentHopDepth >= MaxHopDepth Then
            RecordAudit(New BusEnvelope With {
                .EventId = Guid.NewGuid().ToString("N").Substring(0, 12),
                .Topic = "security.bus_reentrancy_blocked",
                .SourceModule = If(SourceModule, "unknown"),
                .CorrelationId = If(CorrelationId, ""),
                .HopCount = CurrentHopDepth,
                .Payload = New JObject From {{"blocked_topic", NormTopic}}
            })
            Return False
        End If

        Dim SafeSource = If(SourceModule, "unknown").Trim()
        If SafeSource.Length > 48 Then SafeSource = SafeSource.Substring(0, 48)
        Dim Env As New BusEnvelope With {
            .EventId = Guid.NewGuid().ToString("N").Substring(0, 12),
            .Topic = NormTopic,
            .SourceModule = SafeSource,
            .TimestampUtc = DateTime.UtcNow,
            .CorrelationId = If(CorrelationId, ""),
            .HopCount = CurrentHopDepth,
            .Payload = SanitizePayload(Payload)
        }
        RecordAudit(Env)

        Dim Snapshot = Subscriptions.Values.ToArray()
        CurrentHopDepth += 1
        Try
            For Each SubItem In Snapshot
                If Not MatchesTopic(SubItem.TopicPrefix, NormTopic) Then Continue For
                Try
                    SubItem.Handler(Env)
                Catch
                    '单个订阅者异常不影响总线继续投递
                End Try
            Next
        Finally
            CurrentHopDepth = Math.Max(0, CurrentHopDepth - 1)
        End Try
        Return True
    End Function

    Private Function MatchesTopic(Pattern As String, Topic As String) As Boolean
        If Pattern = "*" Then Return True
        If Pattern.EndsWith(".*", StringComparison.Ordinal) Then
            Dim Prefix = Pattern.Substring(0, Pattern.Length - 1)
            Return Topic.StartsWith(Prefix, StringComparison.Ordinal)
        End If
        Return String.Equals(Pattern, Topic, StringComparison.Ordinal)
    End Function

    Private Sub RecordAudit(Env As BusEnvelope)
        SyncLock AuditLock
            AuditRing.Enqueue(Env)
            While AuditRing.Count > MaxAuditEntries
                AuditRing.Dequeue()
            End While
        End SyncLock
    End Sub

    Public Function GetRecentAuditEvents(Optional MaxCount As Integer = 50) As List(Of BusEnvelope)
        SyncLock AuditLock
            Return AuditRing.Reverse().Take(Math.Max(1, Math.Min(MaxAuditEntries, MaxCount))).Reverse().ToList()
        End SyncLock
    End Function

End Module

Public Module SecurityGuard

    Public Function IsSafeRelativePath(RelPath As String) As Boolean
        If String.IsNullOrWhiteSpace(RelPath) Then Return False
        If RelPath.IndexOf(ChrW(0)) >= 0 Then Return False
        Dim Normalized = RelPath.Replace("/"c, "\"c).Trim()
        If Normalized.StartsWith("\") OrElse Normalized.Contains(":") Then Return False
        Dim Parts = Normalized.Split("\"c)
        For Each Part In Parts
            If Part = ".." OrElse Part = "." Then Return False
        Next
        Return True
    End Function

    Public Function IsPrivateOrLoopbackIp(Addr As IPAddress) As Boolean
        If Addr Is Nothing Then Return True
        If Addr.IsIPv4MappedToIPv6 Then Addr = Addr.MapToIPv4()
        If IPAddress.IsLoopback(Addr) Then Return True
        Dim Bytes = Addr.GetAddressBytes()
        If Addr.AddressFamily = AddressFamily.InterNetwork AndAlso Bytes.Length = 4 Then
            Dim B0 = Bytes(0)
            Dim B1 = Bytes(1)
            If B0 = 0 Then Return True '0.0.0.0/8
            If B0 = 10 Then Return True '10.0.0.0/8
            If B0 = 127 Then Return True '127.0.0.0/8
            If B0 = 169 AndAlso B1 = 254 Then Return True '169.254.0.0/16 Link-Local / Metadata
            If B0 = 172 AndAlso B1 >= 16 AndAlso B1 <= 31 Then Return True '172.16.0.0/12
            If B0 = 192 AndAlso B1 = 168 Then Return True '192.168.0.0/16
            If B0 = 100 AndAlso B1 >= 64 AndAlso B1 <= 127 Then Return True '100.64.0.0/10 CGNAT
            If B0 >= 224 Then Return True 'Multicast / Reserved
            Return False
        End If
        If Addr.AddressFamily = AddressFamily.InterNetworkV6 AndAlso Bytes.Length = 16 Then
            If Addr.Equals(IPAddress.IPv6Any) OrElse Addr.Equals(IPAddress.IPv6Loopback) Then Return True
            If (Bytes(0) And &HFE) = &HFC Then Return True 'fc00::/7 Unique Local
            If Bytes(0) = &HFE AndAlso (Bytes(1) And &HC0) = &H80 Then Return True 'fe80::/10 Link-Local
            If Bytes(0) = &HFF Then Return True 'Multicast
            Return False
        End If
        Return True
    End Function

    Public Function IsPublicSafeHttpsUri(TargetUri As Uri, Optional ResolveDns As Boolean = True) As Boolean
        If TargetUri Is Nothing OrElse Not TargetUri.IsAbsoluteUri Then Return False
        If Not String.Equals(TargetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) Then Return False
        If Not String.IsNullOrEmpty(TargetUri.UserInfo) Then Return False
        Dim Host = If(TargetUri.DnsSafeHost, TargetUri.Host).Trim().TrimEnd("."c).ToLowerInvariant()
        If String.IsNullOrEmpty(Host) Then Return False
        If Host = "localhost" OrElse Host.EndsWith(".local") OrElse Host.EndsWith(".internal") OrElse Host.EndsWith(".localhost") Then
            Return False
        End If
        Dim LiteralIp As IPAddress = Nothing
        If IPAddress.TryParse(Host, LiteralIp) Then
            Return Not IsPrivateOrLoopbackIp(LiteralIp)
        End If
        If Not ResolveDns Then Return True
        Try
            Dim Addrs = Dns.GetHostAddresses(Host)
            If Addrs Is Nothing OrElse Addrs.Length = 0 Then Return False
            For Each Ip In Addrs
                If IsPrivateOrLoopbackIp(Ip) Then Return False
            Next
            Return True
        Catch
            Return False
        End Try
    End Function

    Public Function RedactUserPathAndSecrets(Input As String) As String
        If String.IsNullOrEmpty(Input) Then Return ""
        Dim Result = Input
        Try
            Dim UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            If Not String.IsNullOrWhiteSpace(UserProfile) Then
                Result = Regex.Replace(Result, Regex.Escape(UserProfile), "%USERPROFILE%", RegexOptions.IgnoreCase)
            End If
        Catch
        End Try
        Result = Regex.Replace(Result, "([?&](?:X-Amz-Signature|X-Amz-Credential|token|key|secret)=)[^&\s]+", "$1***", RegexOptions.IgnoreCase)
        Result = Regex.Replace(Result, "(Basic|Bearer)\s+[A-Za-z0-9+/=._-]+", "$1 ***", RegexOptions.IgnoreCase)
        Return Result
    End Function

End Module
