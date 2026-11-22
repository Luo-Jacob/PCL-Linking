' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors

Imports System.IO
Imports System.IO.Compression
Imports System.Text

''' <summary>
''' 轻量级、无外部依赖的 Minecraft NBT（Big-Endian / GZip）读写器，用于世界存档解析与编辑。
''' </summary>
Public Module ModNbt

    Public Enum NbtTagType As Byte
        [End] = 0
        [Byte] = 1
        [Short] = 2
        [Int] = 3
        [Long] = 4
        [Single] = 5
        [Double] = 6
        ByteArray = 7
        [String] = 8
        List = 9
        Compound = 10
        IntArray = 11
        LongArray = 12
    End Enum

    Public Class NbtTag
        Public Property Type As NbtTagType
        Public Property Name As String
        Public Property Value As Object
        Public Property ListElementType As NbtTagType = NbtTagType.[End]

        Public Function AsCompound() As Dictionary(Of String, NbtTag)
            Return TryCast(Value, Dictionary(Of String, NbtTag))
        End Function

        Public Function AsList() As List(Of NbtTag)
            Return TryCast(Value, List(Of NbtTag))
        End Function

        Public Function GetList(Key As String) As List(Of NbtTag)
            Dim Child = GetChild(Key)
            If Child Is Nothing OrElse Child.Type <> NbtTagType.List Then Return Nothing
            Return Child.AsList()
        End Function

        Public Function GetChild(Key As String) As NbtTag
            Dim Map = AsCompound()
            If Map Is Nothing Then Return Nothing
            Dim Out As NbtTag = Nothing
            If Map.TryGetValue(Key, Out) Then Return Out
            Return Nothing
        End Function

        Public Function GetCompound(Key As String) As NbtTag
            Dim Child = GetChild(Key)
            If Child IsNot Nothing AndAlso Child.Type = NbtTagType.Compound Then Return Child
            Return Nothing
        End Function

        Public Function GetString(Key As String, Optional Fallback As String = Nothing) As String
            Dim Child = GetChild(Key)
            If Child Is Nothing OrElse Child.Value Is Nothing Then Return Fallback
            Return Child.Value.ToString()
        End Function

        Public Function GetLong(Key As String) As Long?
            Dim Child = GetChild(Key)
            If Child Is Nothing OrElse Child.Value Is Nothing Then Return Nothing
            Try
                Return Convert.ToInt64(Child.Value)
            Catch
                Return Nothing
            End Try
        End Function

        Public Function GetInt(Key As String) As Integer?
            Dim Child = GetChild(Key)
            If Child Is Nothing OrElse Child.Value Is Nothing Then Return Nothing
            Try
                Return Convert.ToInt32(Child.Value)
            Catch
                Return Nothing
            End Try
        End Function

        Public Function GetByte(Key As String) As Byte?
            Dim Child = GetChild(Key)
            If Child Is Nothing OrElse Child.Value Is Nothing Then Return Nothing
            Try
                Return CByte(Convert.ToInt32(Child.Value) And &HFF)
            Catch
                Return Nothing
            End Try
        End Function

        Public Function GetFloat(Key As String) As Single?
            Dim Child = GetChild(Key)
            If Child Is Nothing OrElse Child.Value Is Nothing Then Return Nothing
            Try
                Return Convert.ToSingle(Child.Value)
            Catch
                Return Nothing
            End Try
        End Function

        Public Function GetIntArray(Key As String) As Integer()
            Dim Child = GetChild(Key)
            If Child Is Nothing Then Return Nothing
            Return TryCast(Child.Value, Integer())
        End Function

        Public Sub SetByte(Key As String, Val As Byte)
            Dim Map = AsCompound()
            If Map Is Nothing Then Return
            Map(Key) = New NbtTag With {.Type = NbtTagType.[Byte], .Name = Key, .Value = CSByte(If(Val > 127, CInt(Val) - 256, CInt(Val)))}
        End Sub

        Public Sub SetString(Key As String, Val As String)
            Dim Map = AsCompound()
            If Map Is Nothing Then Return
            Map(Key) = New NbtTag With {.Type = NbtTagType.[String], .Name = Key, .Value = If(Val, "")}
        End Sub
    End Class

    Public Function DecompressZlibBytes(RawBytes As Byte()) As Byte()
        If RawBytes Is Nothing OrElse RawBytes.Length < 6 Then Throw New InvalidDataException("Zlib 数据长度不足")
        Dim Cmf = CInt(RawBytes(0))
        Dim Flg = CInt(RawBytes(1))
        If (Cmf And &HF) <> 8 OrElse ((Cmf * 256 + Flg) Mod 31) <> 0 Then
            Throw New InvalidDataException("无效的 Zlib 头")
        End If
        Dim Offset = 2
        If (Flg And &H20) <> 0 Then Offset += 4 ' FDICT
        If Offset >= RawBytes.Length Then Throw New InvalidDataException("Zlib 负载为空")
        Using MsIn As New MemoryStream(RawBytes, Offset, RawBytes.Length - Offset),
              Df As New DeflateStream(MsIn, CompressionMode.Decompress),
              MsOut As New MemoryStream()
            Df.CopyTo(MsOut)
            Return MsOut.ToArray()
        End Using
    End Function

    Public Function ReadNbtStream(Stream As Stream) As NbtTag
        If Stream Is Nothing Then Throw New ArgumentNullException(NameOf(Stream))
        Dim RootTypeByte = Stream.ReadByte()
        If RootTypeByte < 0 Then Throw New InvalidDataException("无法读取 NBT 根标签类型")
        Dim RootType As NbtTagType = CType(RootTypeByte, NbtTagType)
        If RootType <> NbtTagType.Compound Then Throw New InvalidDataException("NBT 根标签不是 Compound")
        Dim Name = ReadNbtString(Stream)
        Return New NbtTag With {
            .Type = RootType,
            .Name = Name,
            .Value = ReadPayload(Stream, RootType, Nothing)
        }
    End Function

    Public Function ReadNbtBytes(RawBytes As Byte()) As NbtTag
        If RawBytes Is Nothing OrElse RawBytes.Length < 2 Then Throw New InvalidDataException("NBT 数据为空或已损坏")
        Dim PlainBytes As Byte()
        If RawBytes(0) = &H1F AndAlso RawBytes(1) = &H8B Then
            Using MsIn As New MemoryStream(RawBytes), Gz As New GZipStream(MsIn, CompressionMode.Decompress), MsOut As New MemoryStream()
                Gz.CopyTo(MsOut)
                PlainBytes = MsOut.ToArray()
            End Using
        ElseIf RawBytes.Length >= 6 AndAlso (RawBytes(0) And &HF) = 8 AndAlso ((CInt(RawBytes(0)) * 256 + CInt(RawBytes(1))) Mod 31) = 0 Then
            PlainBytes = DecompressZlibBytes(RawBytes)
        Else
            PlainBytes = RawBytes
        End If
        Using Ms As New MemoryStream(PlainBytes)
            Return ReadNbtStream(Ms)
        End Using
    End Function

    Public Function ReadNbtFile(FilePath As String) As NbtTag
        Return ReadNbtBytes(File.ReadAllBytes(FilePath))
    End Function

    Public Sub WriteNbtFile(FilePath As String, Root As NbtTag, Optional BackupOld As Boolean = True, Optional Compress As Boolean = True)
        If Root Is Nothing OrElse Root.Type <> NbtTagType.Compound Then Throw New ArgumentException("根标签必须为 Compound")
        Dim ParentDir = Path.GetDirectoryName(FilePath)
        If Not String.IsNullOrEmpty(ParentDir) Then Directory.CreateDirectory(ParentDir)
        Dim TempPath = FilePath & ".lmltmp"
        Using Ms As New MemoryStream()
            Ms.WriteByte(CByte(Root.Type))
            WriteNbtString(Ms, If(Root.Name, ""))
            WritePayload(Ms, Root)
            Dim Raw = Ms.ToArray()
            If Compress Then
                Using Fs As New FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None), Gz As New GZipStream(Fs, CompressionLevel.Optimal)
                    Gz.Write(Raw, 0, Raw.Length)
                End Using
            Else
                File.WriteAllBytes(TempPath, Raw)
            End If
        End Using
        If BackupOld AndAlso File.Exists(FilePath) Then
            Dim OldPath = FilePath & "_old"
            Try
                File.Copy(FilePath, OldPath, True)
            Catch
            End Try
        End If
        File.Copy(TempPath, FilePath, True)
        File.Delete(TempPath)
    End Sub

    ''' <summary>
    ''' 将服务端地址写入 Minecraft 客户端的无压缩 NBT 文件 servers.dat；若同 IP 已存在则更新名称并返回 False，否则新增并返回 True。
    ''' </summary>
    Public Function EnsureServerInServersDat(FilePath As String, ServerName As String, ServerIp As String) As Boolean
        Dim Root As NbtTag = Nothing
        If File.Exists(FilePath) Then
            Try
                Root = ReadNbtFile(FilePath)
            Catch
                Root = Nothing
            End Try
        End If
        If Root Is Nothing OrElse Root.AsCompound() Is Nothing Then
            Root = New NbtTag With {
                .Type = NbtTagType.Compound,
                .Name = "",
                .Value = New Dictionary(Of String, NbtTag)(StringComparer.Ordinal)
            }
        End If

        Dim RootMap = Root.AsCompound()
        Dim ServersTag As NbtTag = Nothing
        If Not RootMap.TryGetValue("servers", ServersTag) OrElse ServersTag.Type <> NbtTagType.List OrElse ServersTag.AsList() Is Nothing Then
            ServersTag = New NbtTag With {
                .Type = NbtTagType.List,
                .Name = "servers",
                .ListElementType = NbtTagType.Compound,
                .Value = New List(Of NbtTag)()
            }
            RootMap("servers") = ServersTag
        Else
            ServersTag.ListElementType = NbtTagType.Compound
        End If

        Dim List = ServersTag.AsList()
        Dim TargetName = If(String.IsNullOrWhiteSpace(ServerName), "PCL Linking 本地服务端", ServerName.Trim())
        Dim TargetIp = If(ServerIp, "").Trim()
        If String.IsNullOrEmpty(TargetIp) Then Throw New ArgumentException("服务端地址不能为空")

        For Each Entry In List
            Dim EntryIp = Entry.GetString("ip", "")
            If String.Equals(EntryIp, TargetIp, StringComparison.OrdinalIgnoreCase) Then
                Entry.SetString("name", TargetName)
                WriteNbtFile(FilePath, Root, BackupOld:=True, Compress:=False)
                Return False
            End If
        Next

        Dim NewMap As New Dictionary(Of String, NbtTag)(StringComparer.Ordinal)
        Dim NewEntry As New NbtTag With {.Type = NbtTagType.Compound, .Name = "", .Value = NewMap}
        NewEntry.SetString("name", TargetName)
        NewEntry.SetString("ip", TargetIp)
        NewEntry.SetByte("hidden", 0)
        List.Insert(0, NewEntry)

        WriteNbtFile(FilePath, Root, BackupOld:=True, Compress:=False)
        Return True
    End Function

    Private Function ReadExactly(S As Stream, Count As Integer) As Byte()
        Dim Buf(Count - 1) As Byte
        Dim Offset As Integer = 0
        While Offset < Count
            Dim ReadCount = S.Read(Buf, Offset, Count - Offset)
            If ReadCount <= 0 Then Throw New EndOfStreamException("NBT 数据流提前结束")
            Offset += ReadCount
        End While
        Return Buf
    End Function

    Private Function ReadBEInt16(S As Stream) As Short
        Dim B = ReadExactly(S, 2)
        Return BitConverter.ToInt16({B(1), B(0)}, 0)
    End Function

    Private Function ReadBEUInt16(S As Stream) As UShort
        Dim B = ReadExactly(S, 2)
        Return BitConverter.ToUInt16({B(1), B(0)}, 0)
    End Function

    Private Function ReadBEInt32(S As Stream) As Integer
        Dim B = ReadExactly(S, 4)
        Return BitConverter.ToInt32({B(3), B(2), B(1), B(0)}, 0)
    End Function

    Private Function ReadBEInt64(S As Stream) As Long
        Dim B = ReadExactly(S, 8)
        Return BitConverter.ToInt64({B(7), B(6), B(5), B(4), B(3), B(2), B(1), B(0)}, 0)
    End Function

    Private Function ReadBESingle(S As Stream) As Single
        Dim B = ReadExactly(S, 4)
        Return BitConverter.ToSingle({B(3), B(2), B(1), B(0)}, 0)
    End Function

    Private Function ReadBEDouble(S As Stream) As Double
        Dim B = ReadExactly(S, 8)
        Return BitConverter.ToDouble({B(7), B(6), B(5), B(4), B(3), B(2), B(1), B(0)}, 0)
    End Function

    Private Function ReadNbtString(S As Stream) As String
        Dim Len = ReadBEUInt16(S)
        If Len = 0 Then Return ""
        Dim B = ReadExactly(S, Len)
        Return Encoding.UTF8.GetString(B)
    End Function

    Private Function ReadPayload(S As Stream, TagType As NbtTagType, ByRef ListElemType As NbtTagType) As Object
        Select Case TagType
            Case NbtTagType.[Byte]
                Dim B = S.ReadByte()
                If B < 0 Then Throw New EndOfStreamException()
                Return CSByte(If(B > 127, B - 256, B))
            Case NbtTagType.[Short] : Return ReadBEInt16(S)
            Case NbtTagType.[Int] : Return ReadBEInt32(S)
            Case NbtTagType.[Long] : Return ReadBEInt64(S)
            Case NbtTagType.[Single] : Return ReadBESingle(S)
            Case NbtTagType.[Double] : Return ReadBEDouble(S)
            Case NbtTagType.ByteArray
                Dim Count = ReadBEInt32(S)
                If Count < 0 Then Throw New InvalidDataException($"无效的 NBT ByteArray 长度: {Count}")
                Return ReadExactly(S, Count)
            Case NbtTagType.[String]
                Return ReadNbtString(S)
            Case NbtTagType.List
                Dim ElemByte = S.ReadByte()
                If ElemByte < 0 Then Throw New EndOfStreamException()
                ListElemType = CType(ElemByte, NbtTagType)
                Dim Count = ReadBEInt32(S)
                If Count < 0 OrElse Count > 4194304 Then Throw New InvalidDataException($"无效的 NBT List 长度: {Count}")
                Dim Items As New List(Of NbtTag)(Math.Min(Count, 1024))
                For i As Integer = 0 To Count - 1
                    Dim SubElemType As NbtTagType = NbtTagType.[End]
                    Dim Val = ReadPayload(S, ListElemType, SubElemType)
                    Items.Add(New NbtTag With {.Type = ListElemType, .Value = Val, .ListElementType = SubElemType})
                Next
                Return Items
            Case NbtTagType.Compound
                Dim Map As New Dictionary(Of String, NbtTag)(StringComparer.Ordinal)
                While True
                    Dim NextByte = S.ReadByte()
                    If NextByte < 0 Then Throw New EndOfStreamException()
                    Dim NextType = CType(NextByte, NbtTagType)
                    If NextType = NbtTagType.[End] Then Exit While
                    Dim Key = ReadNbtString(S)
                    Dim SubElemType As NbtTagType = NbtTagType.[End]
                    Dim Val = ReadPayload(S, NextType, SubElemType)
                    Map(Key) = New NbtTag With {.Type = NextType, .Name = Key, .Value = Val, .ListElementType = SubElemType}
                End While
                Return Map
            Case NbtTagType.IntArray
                Dim Count = ReadBEInt32(S)
                If Count < 0 OrElse (S.CanSeek AndAlso CLng(Count) * 4 > S.Length - S.Position) OrElse Count > 67108864 Then Throw New InvalidDataException($"无效的 NBT IntArray 长度: {Count}")
                Dim Arr(Count - 1) As Integer
                For i As Integer = 0 To Count - 1
                    Arr(i) = ReadBEInt32(S)
                Next
                Return Arr
            Case NbtTagType.LongArray
                Dim Count = ReadBEInt32(S)
                If Count < 0 OrElse (S.CanSeek AndAlso CLng(Count) * 8 > S.Length - S.Position) OrElse Count > 33554432 Then Throw New InvalidDataException($"无效的 NBT LongArray 长度: {Count}")
                Dim Arr(Count - 1) As Long
                For i As Integer = 0 To Count - 1
                    Arr(i) = ReadBEInt64(S)
                Next
                Return Arr
            Case Else
                Throw New NotSupportedException($"未知 NBT 标签类型: {CByte(TagType)}")
        End Select
    End Function

    Private Sub WriteBE(S As Stream, Bytes As Byte())
        For i As Integer = Bytes.Length - 1 To 0 Step -1
            S.WriteByte(Bytes(i))
        Next
    End Sub

    Private Sub WriteNbtString(S As Stream, Str As String)
        Dim Bytes = Encoding.UTF8.GetBytes(If(Str, ""))
        WriteBE(S, BitConverter.GetBytes(CUShort(Bytes.Length)))
        If Bytes.Length > 0 Then S.Write(Bytes, 0, Bytes.Length)
    End Sub

    Private Sub WritePayload(S As Stream, Tag As NbtTag)
        Select Case Tag.Type
            Case NbtTagType.[Byte]
                Dim Sb As SByte = Convert.ToSByte(Tag.Value)
                S.WriteByte(CByte(If(Sb < 0, CInt(Sb) + 256, CInt(Sb))))
            Case NbtTagType.[Short] : WriteBE(S, BitConverter.GetBytes(Convert.ToInt16(Tag.Value)))
            Case NbtTagType.[Int] : WriteBE(S, BitConverter.GetBytes(Convert.ToInt32(Tag.Value)))
            Case NbtTagType.[Long] : WriteBE(S, BitConverter.GetBytes(Convert.ToInt64(Tag.Value)))
            Case NbtTagType.[Single] : WriteBE(S, BitConverter.GetBytes(Convert.ToSingle(Tag.Value)))
            Case NbtTagType.[Double] : WriteBE(S, BitConverter.GetBytes(Convert.ToDouble(Tag.Value)))
            Case NbtTagType.ByteArray
                Dim Arr = DirectCast(Tag.Value, Byte())
                WriteBE(S, BitConverter.GetBytes(Arr.Length))
                S.Write(Arr, 0, Arr.Length)
            Case NbtTagType.[String]
                WriteNbtString(S, CStr(Tag.Value))
            Case NbtTagType.List
                Dim Items = DirectCast(Tag.Value, List(Of NbtTag))
                S.WriteByte(CByte(Tag.ListElementType))
                WriteBE(S, BitConverter.GetBytes(Items.Count))
                For Each Child In Items
                    WritePayload(S, Child)
                Next
            Case NbtTagType.Compound
                Dim Map = DirectCast(Tag.Value, Dictionary(Of String, NbtTag))
                For Each Kvp In Map
                    S.WriteByte(CByte(Kvp.Value.Type))
                    WriteNbtString(S, Kvp.Key)
                    WritePayload(S, Kvp.Value)
                Next
                S.WriteByte(0)
            Case NbtTagType.IntArray
                Dim Arr = DirectCast(Tag.Value, Integer())
                WriteBE(S, BitConverter.GetBytes(Arr.Length))
                For Each V In Arr
                    WriteBE(S, BitConverter.GetBytes(V))
                Next
            Case NbtTagType.LongArray
                Dim Arr = DirectCast(Tag.Value, Long())
                WriteBE(S, BitConverter.GetBytes(Arr.Length))
                For Each V In Arr
                    WriteBE(S, BitConverter.GetBytes(V))
                Next
        End Select
    End Sub

    Public Class McaTrimResult
        Public Property TotalChunks As Integer
        Public Property PrunableChunks As Integer
        Public Property BytesBefore As Long
        Public Property BytesAfter As Long
        Public ReadOnly Property BytesSaved As Long
            Get
                Return Math.Max(0L, BytesBefore - BytesAfter)
            End Get
        End Property
    End Class

    Private Function ExtractInhabitedTime(ChunkRoot As NbtTag) As Long?
        If ChunkRoot Is Nothing Then Return Nothing
        Dim DirectVal = ChunkRoot.GetLong("InhabitedTime")
        If DirectVal.HasValue Then Return DirectVal
        Dim LevelTag = ChunkRoot.GetCompound("Level")
        If LevelTag IsNot Nothing Then Return LevelTag.GetLong("InhabitedTime")
        Return Nothing
    End Function

    Public Function CompactMcaRegionFile(FilePath As String, MaxInhabitedTicks As Long, DryRun As Boolean) As McaTrimResult
        Dim Res As New McaTrimResult()
        If String.IsNullOrWhiteSpace(FilePath) OrElse Not File.Exists(FilePath) Then Return Res
        Dim Raw = File.ReadAllBytes(FilePath)
        Res.BytesBefore = Raw.LongLength
        Res.BytesAfter = Raw.LongLength
        If Raw.Length < 8192 Then Return Res

        Dim KeepChunk(1023) As Boolean
        Dim ChunkStart(1023) As Integer
        Dim ChunkTotalBytes(1023) As Integer ' 4-byte length prefix + length payload

        For i = 0 To 1023
            Dim LocOff = i * 4
            Dim SectorOffset = (CInt(Raw(LocOff)) << 16) Or (CInt(Raw(LocOff + 1)) << 8) Or CInt(Raw(LocOff + 2))
            Dim SectorCount = CInt(Raw(LocOff + 3))
            If SectorOffset < 2 OrElse SectorCount <= 0 Then Continue For

            Dim ByteOffset = CLng(SectorOffset) * 4096L
            If ByteOffset + 5 > Raw.LongLength Then Continue For
            Dim IntOff = CInt(ByteOffset)
            Dim PayloadLen = (CInt(Raw(IntOff)) << 24) Or (CInt(Raw(IntOff + 1)) << 16) Or (CInt(Raw(IntOff + 2)) << 8) Or CInt(Raw(IntOff + 3))
            If PayloadLen < 1 OrElse ByteOffset + 4L + CLng(PayloadLen) > Raw.LongLength Then Continue For

            Res.TotalChunks += 1
            KeepChunk(i) = True
            ChunkStart(i) = IntOff
            ChunkTotalBytes(i) = 4 + PayloadLen

            Dim CompType = Raw(IntOff + 4)
            If CompType <> 1 AndAlso CompType <> 2 AndAlso CompType <> 3 Then Continue For
            Dim DataLen = PayloadLen - 1
            If DataLen <= 0 Then Continue For

            Try
                Dim ChunkData(DataLen - 1) As Byte
                Buffer.BlockCopy(Raw, IntOff + 5, ChunkData, 0, DataLen)
                Dim Tag As NbtTag = Nothing
                If CompType = 1 Then
                    Using MsIn As New MemoryStream(ChunkData), Gz As New GZipStream(MsIn, CompressionMode.Decompress), MsOut As New MemoryStream()
                        Gz.CopyTo(MsOut)
                        Using MsNbt As New MemoryStream(MsOut.ToArray())
                            Tag = ReadNbtStream(MsNbt)
                        End Using
                    End Using
                ElseIf CompType = 2 Then
                    Dim Plain = DecompressZlibBytes(ChunkData)
                    Using MsNbt As New MemoryStream(Plain)
                        Tag = ReadNbtStream(MsNbt)
                    End Using
                ElseIf CompType = 3 Then
                    Using MsNbt As New MemoryStream(ChunkData)
                        Tag = ReadNbtStream(MsNbt)
                    End Using
                End If
                Dim Inhabited = ExtractInhabitedTime(Tag)
                If Inhabited.HasValue AndAlso Inhabited.Value <= MaxInhabitedTicks Then
                    Res.PrunableChunks += 1
                    KeepChunk(i) = False
                End If
            Catch
                ' 保守策略：解析失败的区块一律保留，防止损坏存档
                KeepChunk(i) = True
            End Try
        Next

        If DryRun OrElse Res.PrunableChunks = 0 Then Return Res

        Dim Header(8191) As Byte
        Using MsBody As New MemoryStream()
            Dim NextSector As Integer = 2
            For i = 0 To 1023
                If Not KeepChunk(i) Then Continue For
                Dim TotalLen = ChunkTotalBytes(i)
                Dim SectorsNeeded = (TotalLen + 4095) \ 4096
                If SectorsNeeded <= 0 OrElse SectorsNeeded > 255 OrElse NextSector > &HFFFFFF Then
                    ' 超出 Anvil 单区块 1MB 限制则跳过重排，保守回退
                    Return Res
                End If
                Dim LocOff = i * 4
                Header(LocOff) = CByte((NextSector >> 16) And &HFF)
                Header(LocOff + 1) = CByte((NextSector >> 8) And &HFF)
                Header(LocOff + 2) = CByte(NextSector And &HFF)
                Header(LocOff + 3) = CByte(SectorsNeeded And &HFF)
                ' 保留原始时间戳
                Buffer.BlockCopy(Raw, 4096 + LocOff, Header, 4096 + LocOff, 4)

                MsBody.Write(Raw, ChunkStart(i), TotalLen)
                Dim PadLen = SectorsNeeded * 4096 - TotalLen
                If PadLen > 0 Then
                    Dim Pad(PadLen - 1) As Byte
                    MsBody.Write(Pad, 0, PadLen)
                End If
                NextSector += SectorsNeeded
            Next

            Dim TempPath = FilePath & ".lmltmp"
            Using Fs As New FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None)
                Fs.Write(Header, 0, Header.Length)
                MsBody.Position = 0
                MsBody.CopyTo(Fs)
                Res.BytesAfter = Fs.Length
            End Using
            If File.Exists(FilePath) Then
                File.Replace(TempPath, FilePath, Nothing, True)
            Else
                File.Move(TempPath, FilePath)
            End If
        End Using
        Return Res
    End Function

#Region "世界规则与卡服玩家救援扩展"

    Public Class GameRuleDescriptor
        Public Property Key As String = ""
        Public Property Title As String = ""
        Public Property Description As String = ""
        Public Property IsBoolean As Boolean = True
        Public Property DefaultValue As String = "false"
    End Class

    Public Class WorldPlayerNbtInfo
        Public Property TargetId As String = ""
        Public Property DisplayName As String = ""
        Public Property FilePath As String = ""
        Public Property IsLevelDatPlayer As Boolean = False
        Public Property Dimension As String = "minecraft:overworld"
        Public Property PosX As Double = 0
        Public Property PosY As Double = 64
        Public Property PosZ As Double = 0
        Public Property Health As Single = 20.0F
        Public Property FoodLevel As Integer = 20
        Public Property XpLevel As Integer = 0
        Public Property IsStuckRisk As Boolean = False
    End Class

    Public ReadOnly CommonWorldGameRules As IReadOnlyList(Of GameRuleDescriptor) = New List(Of GameRuleDescriptor) From {
        New GameRuleDescriptor With {.Key = "keepInventory", .Title = "死亡保留物品栏 (keepInventory)", .Description = "死亡后保留背包物品与经验等级", .IsBoolean = True, .DefaultValue = "false"},
        New GameRuleDescriptor With {.Key = "doDaylightCycle", .Title = "昼夜交替 (doDaylightCycle)", .Description = "关闭后游戏时间固定停留在当前时刻", .IsBoolean = True, .DefaultValue = "true"},
        New GameRuleDescriptor With {.Key = "doWeatherCycle", .Title = "天气变化 (doWeatherCycle)", .Description = "关闭后不再自动下雨或雷暴", .IsBoolean = True, .DefaultValue = "true"},
        New GameRuleDescriptor With {.Key = "mobGriefing", .Title = "生物破坏地形 (mobGriefing)", .Description = "苦力怕炸坑、末影人搬方块与村民收割开关", .IsBoolean = True, .DefaultValue = "true"},
        New GameRuleDescriptor With {.Key = "doFireTick", .Title = "火焰蔓延 (doFireTick)", .Description = "关闭后火不会引燃或烧毁周围方块", .IsBoolean = True, .DefaultValue = "true"},
        New GameRuleDescriptor With {.Key = "pvp", .Title = "允许玩家间伤害 (pvp)", .Description = "局域网合作防误伤可关闭此项", .IsBoolean = True, .DefaultValue = "true"},
        New GameRuleDescriptor With {.Key = "naturalRegeneration", .Title = "饱食度自然回血", .Description = "关闭即开启 UHC 极限回血机制", .IsBoolean = True, .DefaultValue = "true"},
        New GameRuleDescriptor With {.Key = "announceAdvancements", .Title = "进度播报 (announceAdvancements)", .Description = "是否在聊天栏全服播报玩家获得的进度", .IsBoolean = True, .DefaultValue = "true"},
        New GameRuleDescriptor With {.Key = "doImmediateRespawn", .Title = "死亡立即重生 (doImmediateRespawn)", .Description = "跳过死亡界面直接复活", .IsBoolean = True, .DefaultValue = "false"}
    }

    Private Function GetGameRulesCompound(DataTag As NbtTag, Optional CreateIfMissing As Boolean = False) As NbtTag
        If DataTag Is Nothing OrElse DataTag.AsCompound() Is Nothing Then Return Nothing
        Dim GrTag = DataTag.GetCompound("GameRules")
        If GrTag Is Nothing Then GrTag = DataTag.GetCompound("game_rules")
        If GrTag Is Nothing AndAlso CreateIfMissing Then
            GrTag = New NbtTag With {
                .Type = NbtTagType.Compound,
                .Name = "GameRules",
                .Value = New Dictionary(Of String, NbtTag)(StringComparer.Ordinal)
            }
            DataTag.AsCompound()("GameRules") = GrTag
        End If
        Return GrTag
    End Function

    ''' <summary>
    ''' 从 level.dat 读取游戏规则键值对（兼容传统 GameRules 与 game_rules 命名）。
    ''' </summary>
    Public Function ReadWorldGameRules(LevelDatPath As String) As Dictionary(Of String, String)
        Dim Result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        If String.IsNullOrWhiteSpace(LevelDatPath) OrElse Not File.Exists(LevelDatPath) Then Return Result
        Dim Root = ReadNbtFile(LevelDatPath)
        Dim DataTag = If(Root.GetCompound("Data"), Root)
        Dim GrTag = GetGameRulesCompound(DataTag, CreateIfMissing:=False)
        If GrTag Is Nothing OrElse GrTag.AsCompound() Is Nothing Then Return Result
        For Each Kv In GrTag.AsCompound()
            If Kv.Value IsNot Nothing AndAlso Kv.Value.Value IsNot Nothing Then
                Result(Kv.Key) = Kv.Value.Value.ToString()
            End If
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 从 level.dat 解析世界出生点坐标，优先读取 26.1+ spawn.pos，其次读取 SpawnX/SpawnY/SpawnZ。
    ''' </summary>
    Public Function ResolveWorldSpawnCoords(DataTag As NbtTag) As (X As Integer, Y As Integer, Z As Integer)
        If DataTag IsNot Nothing Then
            Dim SpawnTag = DataTag.GetCompound("spawn")
            Dim SpawnPos = If(SpawnTag IsNot Nothing, SpawnTag.GetIntArray("pos"), Nothing)
            If SpawnPos IsNot Nothing AndAlso SpawnPos.Length >= 3 Then
                Return (SpawnPos(0), SpawnPos(1), SpawnPos(2))
            End If
            Dim Sx = DataTag.GetInt("SpawnX")
            Dim Sy = DataTag.GetInt("SpawnY")
            Dim Sz = DataTag.GetInt("SpawnZ")
            If Sx.HasValue AndAlso Sy.HasValue AndAlso Sz.HasValue Then
                Return (Sx.Value, Sy.Value, Sz.Value)
            End If
        End If
        Return (0, 80, 0)
    End Function

    ''' <summary>
    ''' 安全更新 level.dat 中的基础字段、GameRules 或世界种子，并在覆盖前保留 level.dat_old 备份。
    ''' </summary>
    Public Sub UpdateLevelDatFieldsAndRules(LevelDatPath As String,
                                            BasicUpdater As Action(Of NbtTag),
                                            Optional GameRuleUpdates As IDictionary(Of String, String) = Nothing,
                                            Optional NewSeed As Long? = Nothing)
        If String.IsNullOrWhiteSpace(LevelDatPath) OrElse Not File.Exists(LevelDatPath) Then
            Throw New FileNotFoundException("找不到 level.dat 文件", LevelDatPath)
        End If
        Dim Root = ReadNbtFile(LevelDatPath)
        Dim DataTag = If(Root.GetCompound("Data"), Root)
        BasicUpdater?.Invoke(DataTag)

        If GameRuleUpdates IsNot Nothing AndAlso GameRuleUpdates.Count > 0 Then
            Dim GrTag = GetGameRulesCompound(DataTag, CreateIfMissing:=True)
            Dim GrMap = GrTag.AsCompound()
            For Each Kv In GameRuleUpdates
                If String.IsNullOrWhiteSpace(Kv.Key) Then Continue For
                Dim ExistingKey As String = Nothing
                For Each Existing In GrMap.Keys
                    If Existing.Equals(Kv.Key.Trim(), StringComparison.OrdinalIgnoreCase) Then
                        ExistingKey = Existing
                        Exit For
                    End If
                Next
                Dim TargetKey = If(ExistingKey, Kv.Key.Trim())
                Dim ExistingTag As NbtTag = Nothing
                If GrMap.TryGetValue(TargetKey, ExistingTag) AndAlso ExistingTag.Type = NbtTagType.[Byte] Then
                    Dim BoolVal As Boolean
                    If Boolean.TryParse(Kv.Value, BoolVal) Then
                        GrTag.SetByte(TargetKey, If(BoolVal, CByte(1), CByte(0)))
                        Continue For
                    End If
                End If
                GrTag.SetString(TargetKey, If(Kv.Value, "").Trim())
            Next
        End If

        If NewSeed.HasValue Then
            Dim DataMap = DataTag.AsCompound()
            If DataMap IsNot Nothing Then
                If DataMap.ContainsKey("RandomSeed") OrElse DataTag.GetCompound("WorldGenSettings") Is Nothing Then
                    DataMap("RandomSeed") = New NbtTag With {.Type = NbtTagType.[Long], .Name = "RandomSeed", .Value = NewSeed.Value}
                End If
                Dim Wgs = DataTag.GetCompound("WorldGenSettings")
                If Wgs IsNot Nothing AndAlso Wgs.AsCompound() IsNot Nothing Then
                    Wgs.AsCompound()("seed") = New NbtTag With {.Type = NbtTagType.[Long], .Name = "seed", .Value = NewSeed.Value}
                End If
            End If
        End If

        WriteNbtFile(LevelDatPath, Root, BackupOld:=True)
    End Sub

    Private Function BuildPlayerNbtSummary(PlayerTag As NbtTag, TargetId As String, DisplayName As String, FilePath As String, IsLevelDat As Boolean) As WorldPlayerNbtInfo
        Dim DimTag = PlayerTag.GetChild("Dimension")
        Dim DimRaw = If(DimTag IsNot Nothing AndAlso DimTag.Value IsNot Nothing, DimTag.Value.ToString(), "minecraft:overworld")
        Dim Px As Double = 0, Py As Double = 64, Pz As Double = 0
        Dim PosList = PlayerTag.GetList("Pos")
        If PosList IsNot Nothing AndAlso PosList.Count >= 3 Then
            Double.TryParse(Convert.ToString(PosList(0).Value, Globalization.CultureInfo.InvariantCulture), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, Px)
            Double.TryParse(Convert.ToString(PosList(1).Value, Globalization.CultureInfo.InvariantCulture), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, Py)
            Double.TryParse(Convert.ToString(PosList(2).Value, Globalization.CultureInfo.InvariantCulture), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, Pz)
        End If
        Dim Health = PlayerTag.GetFloat("Health").GetValueOrDefault(20.0F)
        Dim Food = PlayerTag.GetInt("foodLevel").GetValueOrDefault(20)
        Dim Xp = PlayerTag.GetInt("XpLevel").GetValueOrDefault(0)
        Dim Risk As Boolean = Double.IsNaN(Px) OrElse Double.IsInfinity(Px) OrElse
                              Double.IsNaN(Py) OrElse Double.IsInfinity(Py) OrElse Py < -64 OrElse Py > 360 OrElse
                              Double.IsNaN(Pz) OrElse Double.IsInfinity(Pz) OrElse
                              Math.Abs(Px) > 29999900 OrElse Math.Abs(Pz) > 29999900 OrElse Health <= 0.0F
        Return New WorldPlayerNbtInfo With {
            .TargetId = TargetId,
            .DisplayName = DisplayName,
            .FilePath = FilePath,
            .IsLevelDatPlayer = IsLevelDat,
            .Dimension = DimRaw,
            .PosX = Px,
            .PosY = Py,
            .PosZ = Pz,
            .Health = Health,
            .FoodLevel = Food,
            .XpLevel = Xp,
            .IsStuckRisk = Risk
        }
    End Function

    ''' <summary>
    ''' 枚举存档内全部可识别玩家状态（含 level.dat 单人主机玩家与 playerdata/*.dat 多人档案）。
    ''' </summary>
    Public Function InspectWorldPlayers(WorldDir As String) As List(Of WorldPlayerNbtInfo)
        Dim List As New List(Of WorldPlayerNbtInfo)
        If String.IsNullOrWhiteSpace(WorldDir) OrElse Not Directory.Exists(WorldDir) Then Return List

        Dim LevelDat = Path.Combine(WorldDir, "level.dat")
        If File.Exists(LevelDat) Then
            Try
                Dim Root = ReadNbtFile(LevelDat)
                Dim DataTag = If(Root.GetCompound("Data"), Root)
                Dim PlayerTag = DataTag.GetCompound("Player")
                If PlayerTag IsNot Nothing Then
                    List.Add(BuildPlayerNbtSummary(PlayerTag, "level_dat_player", "单人房主玩家 (level.dat)", LevelDat, True))
                End If
            Catch
            End Try
        End If

        Dim PlayerDataDir = Path.Combine(WorldDir, "playerdata")
        If Directory.Exists(PlayerDataDir) Then
            For Each DatFile In Directory.GetFiles(PlayerDataDir, "*.dat", SearchOption.TopDirectoryOnly).OrderByDescending(Function(f) File.GetLastWriteTimeUtc(f))
                Try
                    Dim PTag = ReadNbtFile(DatFile)
                    Dim Uuid = Path.GetFileNameWithoutExtension(DatFile)
                    Dim ShortId = If(Uuid.Length > 12, Uuid.Substring(0, 8) & "...", Uuid)
                    List.Add(BuildPlayerNbtSummary(PTag, Uuid, $"玩家档案 ({ShortId})", DatFile, False))
                Catch
                End Try
            Next
        End If
        Return List
    End Function

    Private Sub ResetPlayerTagToSpawn(PlayerTag As NbtTag, SpawnX As Double, SpawnY As Double, SpawnZ As Double)
        Dim Map = PlayerTag.AsCompound()
        If Map Is Nothing Then Throw New InvalidDataException("玩家 NBT 数据结构无效")

        Map("Pos") = New NbtTag With {
            .Type = NbtTagType.List,
            .Name = "Pos",
            .ListElementType = NbtTagType.[Double],
            .Value = New List(Of NbtTag) From {
                New NbtTag With {.Type = NbtTagType.[Double], .Value = SpawnX},
                New NbtTag With {.Type = NbtTagType.[Double], .Value = SpawnY},
                New NbtTag With {.Type = NbtTagType.[Double], .Value = SpawnZ}
            }
        }
        Map("Motion") = New NbtTag With {
            .Type = NbtTagType.List,
            .Name = "Motion",
            .ListElementType = NbtTagType.[Double],
            .Value = New List(Of NbtTag) From {
                New NbtTag With {.Type = NbtTagType.[Double], .Value = 0.0R},
                New NbtTag With {.Type = NbtTagType.[Double], .Value = 0.0R},
                New NbtTag With {.Type = NbtTagType.[Double], .Value = 0.0R}
            }
        }
        Dim ExistingDim = PlayerTag.GetChild("Dimension")
        If ExistingDim IsNot Nothing AndAlso ExistingDim.Type = NbtTagType.[Int] Then
            Map("Dimension") = New NbtTag With {.Type = NbtTagType.[Int], .Name = "Dimension", .Value = 0}
        Else
            PlayerTag.SetString("Dimension", "minecraft:overworld")
        End If
        Map("FallDistance") = New NbtTag With {.Type = NbtTagType.[Single], .Name = "FallDistance", .Value = 0.0F}
        Map("Fire") = New NbtTag With {.Type = NbtTagType.[Short], .Name = "Fire", .Value = CShort(-20)}
        Dim Health = PlayerTag.GetFloat("Health").GetValueOrDefault(20.0F)
        If Single.IsNaN(Health) OrElse Health <= 0.0F Then
            Map("Health") = New NbtTag With {.Type = NbtTagType.[Single], .Name = "Health", .Value = 20.0F}
            Map("DeathTime") = New NbtTag With {.Type = NbtTagType.[Short], .Name = "DeathTime", .Value = CShort(0)}
        End If
    End Sub

    ''' <summary>
    ''' 将指定卡死/掉虚空/崩溃实体区的玩家安全传送回主世界出生点（清空速度与摔落高度），并生成 _old 备份。
    ''' </summary>
    Public Function RescueWorldPlayerToSpawn(WorldDir As String, TargetPlayerFilePath As String, IsLevelDatPlayer As Boolean) As (SpawnX As Double, SpawnY As Double, SpawnZ As Double)
        Dim LevelDat = Path.Combine(WorldDir, "level.dat")
        If Not File.Exists(LevelDat) Then Throw New FileNotFoundException("找不到 level.dat，无法定位世界出生点", LevelDat)
        Dim LevelRoot = ReadNbtFile(LevelDat)
        Dim LevelDataTag = If(LevelRoot.GetCompound("Data"), LevelRoot)
        Dim Spawn = ResolveWorldSpawnCoords(LevelDataTag)
        Dim SafeX As Double = Spawn.X + 0.5R
        Dim SafeY As Double = Math.Max(Spawn.Y, -60) + 1.0R
        Dim SafeZ As Double = Spawn.Z + 0.5R

        If IsLevelDatPlayer Then
            Dim PTag = LevelDataTag.GetCompound("Player")
            If PTag Is Nothing Then Throw New InvalidDataException("level.dat 中不存在单人 Player 数据")
            ResetPlayerTagToSpawn(PTag, SafeX, SafeY, SafeZ)
            WriteNbtFile(LevelDat, LevelRoot, BackupOld:=True)
        Else
            If String.IsNullOrWhiteSpace(TargetPlayerFilePath) OrElse Not File.Exists(TargetPlayerFilePath) Then
                Throw New FileNotFoundException("找不到目标玩家 .dat 文件", TargetPlayerFilePath)
            End If
            Dim PlayerRoot = ReadNbtFile(TargetPlayerFilePath)
            ResetPlayerTagToSpawn(PlayerRoot, SafeX, SafeY, SafeZ)
            WriteNbtFile(TargetPlayerFilePath, PlayerRoot, BackupOld:=True)
        End If

        Return (SafeX, SafeY, SafeZ)
    End Function

#End Region

End Module
