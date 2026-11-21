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

End Module
