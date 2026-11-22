Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Windows.Input
Imports System.Windows.Media
Imports System.Windows.Media.Imaging
Imports System.Windows.Media.Media3D

Public Class PageToolsSkin3D
    Implements IRefreshable

    Private Const ScalePx As Double = 0.1 '1 Minecraft pixel = 0.1 3D unit
    Private CurrentSkinBitmap As BitmapSource = Nothing
    Private CurrentCapeBitmap As BitmapSource = Nothing
    Private IsLegacy64x32 As Boolean = False
    Private IsUiReady As Boolean = False
    Private IsRenderingAttached As Boolean = False
    Private SuppressModelRebuild As Boolean = False
    Private IsLoadingCurrentSkin As Boolean = False

    'Camera orbit state
    Private YawDeg As Double = 22.0
    Private PitchDeg As Double = -12.0
    Private CamDistance As Double = 7.2
    Private IsDragging As Boolean = False
    Private LastMousePos As Point

    'Joint rotations
    Private HeadRot As New AxisAngleRotation3D(New Vector3D(0, 1, 0), 0)
    Private RightArmRot As New AxisAngleRotation3D(New Vector3D(1, 0, 0), 0)
    Private LeftArmRot As New AxisAngleRotation3D(New Vector3D(1, 0, 0), 0)
    Private RightLegRot As New AxisAngleRotation3D(New Vector3D(1, 0, 0), 0)
    Private LeftLegRot As New AxisAngleRotation3D(New Vector3D(1, 0, 0), 0)
    Private CapeRot As New AxisAngleRotation3D(New Vector3D(1, 0, 0), 10)
    Private LeftWingRotZ As New AxisAngleRotation3D(New Vector3D(0, 0, 1), 14)
    Private RightWingRotZ As New AxisAngleRotation3D(New Vector3D(0, 0, 1), -14)

    Private ReadOnly AnimStartTime As DateTime = DateTime.UtcNow

    Private Sub PageToolsSkin3D_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If Not IsUiReady Then
            IsUiReady = True
            CurrentSkinBitmap = BuildDefaultSteveSkin()
            IsLegacy64x32 = False
            ApplyPreviewBackground()
            RebuildModel()
            UpdateCamera()
        End If
        UpdateRenderingSubscription()
    End Sub

    Private Sub PageToolsSkin3D_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        DetachRenderingHandler()
    End Sub

    Private Sub DetachRenderingHandler()
        If IsRenderingAttached Then
            RemoveHandler CompositionTarget.Rendering, AddressOf OnFrameRender
            IsRenderingAttached = False
        End If
    End Sub

    Private Sub UpdateRenderingSubscription()
        If Not IsUiReady Then Return
        Dim Mode As Integer = Math.Max(0, If(ComboPoseMode IsNot Nothing, ComboPoseMode.SelectedIndex, 0))
        If Mode = 2 Then
            DetachRenderingHandler()
            ApplyStaticPose()
        ElseIf Not IsRenderingAttached Then
            IsRenderingAttached = True
            AddHandler CompositionTarget.Rendering, AddressOf OnFrameRender
        End If
    End Sub

    Private Sub ApplyStaticPose()
        RightArmRot.Angle = 0
        LeftArmRot.Angle = 0
        RightLegRot.Angle = 0
        LeftLegRot.Angle = 0
        HeadRot.Angle = 0
        CapeRot.Angle = 6.0
        LeftWingRotZ.Angle = 10.0
        RightWingRotZ.Angle = -10.0
    End Sub

    Private Sub ComboPoseMode_SelectionChanged() Handles ComboPoseMode.SelectionChanged
        UpdateRenderingSubscription()
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        RebuildModel()
    End Sub

    Private Shared Function BuildDefaultSteveSkin() As BitmapSource
        '生成标准 64x64 像素级默认 Steve 皮肤模板，确保离线无皮肤文件时也可直接预览
        Dim Width As Integer = 64
        Dim Height As Integer = 64
        Dim Stride As Integer = Width * 4
        Dim Pixels(Stride * Height - 1) As Byte
        Dim FillRect As Action(Of Integer, Integer, Integer, Integer, Byte, Byte, Byte, Byte) =
        Sub(x0 As Integer, y0 As Integer, w As Integer, h As Integer, r As Byte, g As Byte, b As Byte, a As Byte)
            For y As Integer = y0 To Math.Min(Height - 1, y0 + h - 1)
                For x As Integer = x0 To Math.Min(Width - 1, x0 + w - 1)
                    Dim idx = y * Stride + x * 4
                    Pixels(idx) = b
                    Pixels(idx + 1) = g
                    Pixels(idx + 2) = r
                    Pixels(idx + 3) = a
                Next
            Next
        End Sub
        '头部（皮肤色与头发）
        FillRect(0, 0, 32, 16, 200, 155, 118, 255)
        FillRect(8, 0, 8, 8, 56, 38, 24, 255)
        FillRect(0, 8, 32, 2, 56, 38, 24, 255)
        '眼睛与胡须细节（正脸 8,8 8x8）
        FillRect(9, 12, 2, 1, 255, 255, 255, 255)
        FillRect(10, 12, 1, 1, 50, 70, 160, 255)
        FillRect(13, 12, 2, 1, 255, 255, 255, 255)
        FillRect(13, 12, 1, 1, 50, 70, 160, 255)
        FillRect(11, 14, 2, 1, 128, 78, 56, 255)
        '躯干（青蓝色 T 恤）
        FillRect(16, 16, 24, 16, 0, 158, 168, 255)
        '右手臂与左手臂（皮肤+短袖）
        FillRect(40, 16, 16, 16, 200, 155, 118, 255)
        FillRect(40, 16, 16, 8, 0, 158, 168, 255)
        FillRect(32, 48, 16, 16, 200, 155, 118, 255)
        FillRect(32, 48, 16, 8, 0, 158, 168, 255)
        '双腿（深蓝牛仔裤+鞋子）
        FillRect(0, 16, 16, 16, 48, 58, 130, 255)
        FillRect(0, 30, 16, 2, 70, 70, 70, 255)
        FillRect(16, 48, 16, 16, 48, 58, 130, 255)
        FillRect(16, 62, 16, 2, 70, 70, 70, 255)

        Dim Bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, Nothing, Pixels, Stride)
        Bmp.Freeze()
        Return Bmp
    End Function

    Private Sub OnFrameRender(sender As Object, e As EventArgs)
        If Not IsUiReady OrElse ComboPoseMode Is Nothing Then Return
        Dim Elapsed As Double = (DateTime.UtcNow - AnimStartTime).TotalSeconds
        Dim Mode As Integer = Math.Max(0, ComboPoseMode.SelectedIndex)
        If Mode = 0 Then
            '行走摆臂摆腿
            Dim Swing As Double = Math.Sin(Elapsed * 4.5) * 32.0
            RightArmRot.Angle = -Swing
            LeftArmRot.Angle = Swing
            RightLegRot.Angle = Swing
            LeftLegRot.Angle = -Swing
            HeadRot.Angle = Math.Sin(Elapsed * 1.4) * 6.0
            CapeRot.Angle = 12.0 + Math.Abs(Math.Sin(Elapsed * 4.5)) * 10.0
            Dim Flutter = 14.0 + Math.Sin(Elapsed * 4.5) * 5.0
            LeftWingRotZ.Angle = Flutter
            RightWingRotZ.Angle = -Flutter
        ElseIf Mode = 1 Then
            '轻微呼吸晃动
            Dim Breath As Double = Math.Sin(Elapsed * 2.2) * 3.5
            RightArmRot.Angle = Breath
            LeftArmRot.Angle = -Breath
            RightLegRot.Angle = 0
            LeftLegRot.Angle = 0
            HeadRot.Angle = Math.Sin(Elapsed * 1.1) * 5.0
            CapeRot.Angle = 8.0 + Math.Sin(Elapsed * 2.2) * 2.5
            Dim Flutter = 12.0 + Math.Sin(Elapsed * 2.2) * 2.0
            LeftWingRotZ.Angle = Flutter
            RightWingRotZ.Angle = -Flutter
        Else
            ApplyStaticPose()
        End If
    End Sub

    Private Sub UpdateCamera()
        Dim RadYaw As Double = YawDeg * Math.PI / 180.0
        Dim RadPitch As Double = PitchDeg * Math.PI / 180.0
        Dim CosP As Double = Math.Cos(RadPitch)
        Dim Target As New Point3D(0, 0.2, 0)
        Dim Pos As New Point3D(
            Target.X + CamDistance * CosP * Math.Sin(RadYaw),
            Target.Y - CamDistance * Math.Sin(RadPitch),
            Target.Z + CamDistance * CosP * Math.Cos(RadYaw))
        MainCamera.Position = Pos
        MainCamera.LookDirection = Target - Pos
    End Sub

    Private Sub ViewportHost_MouseLeftButtonDown(sender As Object, e As MouseButtonEventArgs) Handles ViewportHost.MouseLeftButtonDown
        IsDragging = True
        LastMousePos = e.GetPosition(ViewportHost)
        ViewportHost.CaptureMouse()
    End Sub

    Private Sub ViewportHost_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs) Handles ViewportHost.MouseLeftButtonUp
        IsDragging = False
        If ViewportHost.IsMouseCaptured Then ViewportHost.ReleaseMouseCapture()
    End Sub

    Private Sub ViewportHost_LostMouseCapture(sender As Object, e As MouseEventArgs) Handles ViewportHost.LostMouseCapture
        IsDragging = False
    End Sub

    Private Sub ViewportHost_MouseMove(sender As Object, e As MouseEventArgs) Handles ViewportHost.MouseMove
        If Not IsDragging Then Return
        Dim Pos = e.GetPosition(ViewportHost)
        Dim Dx = Pos.X - LastMousePos.X
        Dim Dy = Pos.Y - LastMousePos.Y
        LastMousePos = Pos
        YawDeg -= Dx * 0.65
        PitchDeg = Math.Max(-80.0, Math.Min(80.0, PitchDeg + Dy * 0.55))
        UpdateCamera()
    End Sub

    Private Sub ViewportHost_MouseWheel(sender As Object, e As MouseWheelEventArgs) Handles ViewportHost.MouseWheel
        CamDistance = Math.Max(3.6, Math.Min(13.0, CamDistance - e.Delta * 0.0035))
        UpdateCamera()
        e.Handled = True
    End Sub

    Private Sub BtnResetCamera_Click() Handles BtnResetCamera.Click
        YawDeg = 22.0
        PitchDeg = -12.0
        CamDistance = 7.2
        UpdateCamera()
    End Sub

    Private Sub ComboSettings_Changed() Handles ComboArmModel.SelectionChanged, ChkShowOuterLayer.Change, ComboBackAccessory.SelectionChanged
        If Not IsUiReady OrElse SuppressModelRebuild Then Return
        RebuildModel()
    End Sub

    Private Sub ComboPreviewBg_SelectionChanged() Handles ComboPreviewBg.SelectionChanged
        If Not IsUiReady Then Return
        ApplyPreviewBackground()
    End Sub

    Public Shared Function BuildPreviewBackgroundBrush(TagName As String) As Brush
        Select Case If(TagName, "Checker")
            Case "White"
                Return New SolidColorBrush(Color.FromRgb(255, 255, 255))
            Case "Dark"
                Return New SolidColorBrush(Color.FromRgb(24, 28, 36))
            Case "Green"
                Return New SolidColorBrush(Color.FromRgb(0, 255, 0))
            Case "Sky"
                Return New LinearGradientBrush(Color.FromRgb(126, 200, 255), Color.FromRgb(228, 243, 255), New Point(0, 0), New Point(0, 1))
            Case Else
                Dim Group As New DrawingGroup()
                Group.Children.Add(New GeometryDrawing(New SolidColorBrush(Color.FromArgb(14, 0, 0, 0)), Nothing, New RectangleGeometry(New Rect(0, 0, 16, 16))))
                Group.Children.Add(New GeometryDrawing(New SolidColorBrush(Color.FromArgb(26, 0, 0, 0)), Nothing, New RectangleGeometry(New Rect(0, 0, 8, 8))))
                Group.Children.Add(New GeometryDrawing(New SolidColorBrush(Color.FromArgb(26, 0, 0, 0)), Nothing, New RectangleGeometry(New Rect(8, 8, 8, 8))))
                Return New DrawingBrush(Group) With {
                    .Viewport = New Rect(0, 0, 20, 20),
                    .ViewportUnits = BrushMappingMode.Absolute,
                    .TileMode = TileMode.Tile
                }
        End Select
    End Function

    Private Sub ApplyPreviewBackground()
        Dim Item = TryCast(ComboPreviewBg.SelectedItem, MyComboBoxItem)
        Dim TagName = If(Item?.Tag?.ToString(), "Checker")
        ViewportHost.Background = BuildPreviewBackgroundBrush(TagName)
    End Sub

    Private Shared Function ScalePixelArtNearest(Src As BitmapSource, Scale As Integer) As BitmapSource
        '将像素图整数倍近邻放大后冻结，防止 WPF 3D DiffuseMaterial 的硬件双线性采样导致皮肤模糊
        Dim Bgra As BitmapSource = If(Src.Format = PixelFormats.Bgra32, Src, New FormatConvertedBitmap(Src, PixelFormats.Bgra32, Nothing, 0))
        Dim W As Integer = Bgra.PixelWidth
        Dim H As Integer = Bgra.PixelHeight
        Dim SrcStride As Integer = W * 4
        Dim SrcBuf(SrcStride * H - 1) As Byte
        Bgra.CopyPixels(SrcBuf, SrcStride, 0)

        Dim DstW As Integer = W * Scale
        Dim DstH As Integer = H * Scale
        Dim DstStride As Integer = DstW * 4
        Dim DstBuf(DstStride * DstH - 1) As Byte
        For y As Integer = 0 To H - 1
            Dim SrcRowBase As Integer = y * SrcStride
            For x As Integer = 0 To W - 1
                Dim Si As Integer = SrcRowBase + x * 4
                Dim b As Byte = SrcBuf(Si), g As Byte = SrcBuf(Si + 1), r As Byte = SrcBuf(Si + 2), a As Byte = SrcBuf(Si + 3)
                For dy As Integer = 0 To Scale - 1
                    Dim DstRowBase As Integer = (y * Scale + dy) * DstStride + (x * Scale) * 4
                    For dx As Integer = 0 To Scale - 1
                        Dim Di As Integer = DstRowBase + dx * 4
                        DstBuf(Di) = b
                        DstBuf(Di + 1) = g
                        DstBuf(Di + 2) = r
                        DstBuf(Di + 3) = a
                    Next
                Next
            Next
        Next
        Dim Result = BitmapSource.Create(DstW, DstH, 96, 96, PixelFormats.Bgra32, Nothing, DstBuf, DstStride)
        Result.Freeze()
        Return Result
    End Function

    Private Function SliceMaterial(Px As Integer, Py As Integer, Pw As Integer, Ph As Integer, Optional FlipX As Boolean = False, Optional SourceTex As BitmapSource = Nothing) As Material
        Dim Tex As BitmapSource = If(SourceTex, CurrentSkinBitmap)
        If Tex Is Nothing OrElse Pw <= 0 OrElse Ph <= 0 Then Return Nothing
        Dim TexW As Integer = Tex.PixelWidth
        Dim TexH As Integer = Tex.PixelHeight
        If Px < 0 OrElse Py < 0 OrElse Px + Pw > TexW OrElse Py + Ph > TexH Then Return Nothing
        Dim Cropped As BitmapSource = New CroppedBitmap(Tex, New Int32Rect(Px, Py, Pw, Ph))
        If FlipX Then
            Cropped = New TransformedBitmap(Cropped, New ScaleTransform(-1, 1))
        End If
        Dim Crisp As BitmapSource = ScalePixelArtNearest(Cropped, 16)
        Dim Brush As New ImageBrush(Crisp) With {
            .Stretch = Stretch.Fill,
            .TileMode = TileMode.None
        }
        RenderOptions.SetBitmapScalingMode(Brush, BitmapScalingMode.NearestNeighbor)
        Return New DiffuseMaterial(Brush)
    End Function

    Private Function BuildBoxPart(
        CenterX As Double, CenterY As Double, CenterZ As Double,
        SizeX As Double, SizeY As Double, SizeZ As Double,
        UvX As Integer, UvY As Integer, BoxW As Integer, BoxH As Integer, BoxD As Integer,
        Optional MirrorFaces As Boolean = False,
        Optional IsOuter As Boolean = False,
        Optional SourceTex As BitmapSource = Nothing) As Model3DGroup

        Dim Group As New Model3DGroup()
        Dim Hx As Double = SizeX / 2.0
        Dim Hy As Double = SizeY / 2.0
        Dim Hz As Double = SizeZ / 2.0

        Dim X0 As Double = CenterX - Hx, X1 As Double = CenterX + Hx
        Dim Y0 As Double = CenterY - Hy, Y1 As Double = CenterY + Hy
        Dim Z0 As Double = CenterZ - Hz, Z1 As Double = CenterZ + Hz

        '标准 Minecraft 皮肤展开图 6 个面的像素坐标：
        'Top:    (UvX + D, UvY, W, D)
        'Bottom: (UvX + D + W, UvY, W, D)
        'Right:  (UvX, UvY + D, D, H)        [角色右侧 = -X 或 +X 视角右侧]
        'Front:  (UvX + D, UvY + D, W, H)
        'Left:   (UvX + D + W, UvY + D, D, H)
        'Back:   (UvX + 2*D + W, UvY + D, W, H)

        Dim AddQuad As Action(Of Point3D, Point3D, Point3D, Point3D, Integer, Integer, Integer, Integer) =
        Sub(P00 As Point3D, P10 As Point3D, P11 As Point3D, P01 As Point3D, sx As Integer, sy As Integer, sw As Integer, sh As Integer)
            Dim Mat = SliceMaterial(sx, sy, sw, sh, MirrorFaces, SourceTex)
            If Mat Is Nothing Then Return
            Dim Mesh As New MeshGeometry3D()
            Mesh.Positions.Add(P00) '0 top-left
            Mesh.Positions.Add(P10) '1 top-right
            Mesh.Positions.Add(P11) '2 bottom-right
            Mesh.Positions.Add(P01) '3 bottom-left
            Mesh.TextureCoordinates.Add(New Point(0, 0))
            Mesh.TextureCoordinates.Add(New Point(1, 0))
            Mesh.TextureCoordinates.Add(New Point(1, 1))
            Mesh.TextureCoordinates.Add(New Point(0, 1))
            Mesh.TriangleIndices.Add(0)
            Mesh.TriangleIndices.Add(3)
            Mesh.TriangleIndices.Add(2)
            Mesh.TriangleIndices.Add(0)
            Mesh.TriangleIndices.Add(2)
            Mesh.TriangleIndices.Add(1)
            Dim Geo As New GeometryModel3D(Mesh, Mat)
            If IsOuter Then Geo.BackMaterial = Mat
            Group.Children.Add(Geo)
        End Sub

        'Front (+Z): top-left (X0, Y1, Z1), top-right (X1, Y1, Z1)
        AddQuad(New Point3D(X0, Y1, Z1), New Point3D(X1, Y1, Z1), New Point3D(X1, Y0, Z1), New Point3D(X0, Y0, Z1),
                UvX + BoxD, UvY + BoxD, BoxW, BoxH)
        'Back (-Z): facing -Z so left is X1, right is X0
        AddQuad(New Point3D(X1, Y1, Z0), New Point3D(X0, Y1, Z0), New Point3D(X0, Y0, Z0), New Point3D(X1, Y0, Z0),
                UvX + 2 * BoxD + BoxW, UvY + BoxD, BoxW, BoxH)
        'Right-of-char (-X face) vs Left-of-char (+X face)
        Dim RightU As Integer = If(MirrorFaces, UvX + BoxD + BoxW, UvX)
        Dim LeftU As Integer = If(MirrorFaces, UvX, UvX + BoxD + BoxW)
        'Character's Right side (-X face, outward normal -X)
        AddQuad(New Point3D(X0, Y1, Z0), New Point3D(X0, Y1, Z1), New Point3D(X0, Y0, Z1), New Point3D(X0, Y0, Z0),
                RightU, UvY + BoxD, BoxD, BoxH)
        'Character's Left side (+X face, outward normal +X)
        AddQuad(New Point3D(X1, Y1, Z1), New Point3D(X1, Y1, Z0), New Point3D(X1, Y0, Z0), New Point3D(X1, Y0, Z1),
                LeftU, UvY + BoxD, BoxD, BoxH)
        'Top (+Y face)
        AddQuad(New Point3D(X0, Y1, Z0), New Point3D(X1, Y1, Z0), New Point3D(X1, Y1, Z1), New Point3D(X0, Y1, Z1),
                UvX + BoxD, UvY, BoxW, BoxD)
        'Bottom (-Y face)
        AddQuad(New Point3D(X0, Y0, Z1), New Point3D(X1, Y0, Z1), New Point3D(X1, Y0, Z0), New Point3D(X0, Y0, Z0),
                UvX + BoxD + BoxW, UvY, BoxW, BoxD)

        Return Group
    End Function

    Private Sub RebuildModel()
        If CurrentSkinBitmap Is Nothing Then Return
        Dim Root As New Model3DGroup()
        Dim IsSlim As Boolean = (ComboArmModel.SelectedIndex = 1)
        Dim ShowOuter As Boolean = ChkShowOuterLayer.Checked
        Dim ArmPx As Integer = If(IsSlim, 3, 4)

        '以臀部为 Y=0:
        '腿: Y ∈ [-12, 0], 高度 12 -> 中心 Y = -6px = -0.6
        '躯干: Y ∈ [0, 12], 高度 12 -> 中心 Y = +6px = +0.6
        '头: Y ∈ [12, 20], 高度 8 -> 中心 Y = +16px = +1.6 (旋转轴心 Y = +1.2)
        '手臂: Y ∈ [0, 12], 高度 12 -> 中心 Y = +6px = +0.6 (旋转轴心 Y = +1.1)

        '1. Head (+ Hat outer)
        Dim HeadGroup As New Model3DGroup()
        HeadGroup.Children.Add(BuildBoxPart(0, 16 * ScalePx, 0, 8 * ScalePx, 8 * ScalePx, 8 * ScalePx, 0, 0, 8, 8, 8))
        If ShowOuter Then
            HeadGroup.Children.Add(BuildBoxPart(0, 16 * ScalePx, 0, 9.0 * ScalePx, 9.0 * ScalePx, 9.0 * ScalePx, 32, 0, 8, 8, 8, IsOuter:=True))
        End If
        HeadGroup.Transform = New RotateTransform3D(HeadRot, 0, 12 * ScalePx, 0)
        Root.Children.Add(HeadGroup)

        '2. Body (+ Jacket outer)
        Root.Children.Add(BuildBoxPart(0, 6 * ScalePx, 0, 8 * ScalePx, 12 * ScalePx, 4 * ScalePx, 16, 16, 8, 12, 4))
        If ShowOuter AndAlso Not IsLegacy64x32 Then
            Root.Children.Add(BuildBoxPart(0, 6 * ScalePx, 0, 8.6 * ScalePx, 12.6 * ScalePx, 4.6 * ScalePx, 16, 32, 8, 12, 4, IsOuter:=True))
        End If

        '3. Right Arm (-X side, center X = -(4 + ArmPx/2))
        Dim ArmCenterOffsetX As Double = (4.0 + ArmPx / 2.0) * ScalePx
        Dim RightArmGroup As New Model3DGroup()
        RightArmGroup.Children.Add(BuildBoxPart(-ArmCenterOffsetX, 6 * ScalePx, 0, ArmPx * ScalePx, 12 * ScalePx, 4 * ScalePx, 40, 16, ArmPx, 12, 4))
        If ShowOuter AndAlso Not IsLegacy64x32 Then
            RightArmGroup.Children.Add(BuildBoxPart(-ArmCenterOffsetX, 6 * ScalePx, 0, (ArmPx + 0.6) * ScalePx, 12.6 * ScalePx, 4.6 * ScalePx, 40, 32, ArmPx, 12, 4, IsOuter:=True))
        End If
        RightArmGroup.Transform = New RotateTransform3D(RightArmRot, -ArmCenterOffsetX, 11 * ScalePx, 0)
        Root.Children.Add(RightArmGroup)

        '4. Left Arm (+X side)
        Dim LeftArmGroup As New Model3DGroup()
        If IsLegacy64x32 Then
            LeftArmGroup.Children.Add(BuildBoxPart(ArmCenterOffsetX, 6 * ScalePx, 0, ArmPx * ScalePx, 12 * ScalePx, 4 * ScalePx, 40, 16, ArmPx, 12, 4, MirrorFaces:=True))
        Else
            LeftArmGroup.Children.Add(BuildBoxPart(ArmCenterOffsetX, 6 * ScalePx, 0, ArmPx * ScalePx, 12 * ScalePx, 4 * ScalePx, 32, 48, ArmPx, 12, 4))
            If ShowOuter Then
                LeftArmGroup.Children.Add(BuildBoxPart(ArmCenterOffsetX, 6 * ScalePx, 0, (ArmPx + 0.6) * ScalePx, 12.6 * ScalePx, 4.6 * ScalePx, 48, 48, ArmPx, 12, 4, IsOuter:=True))
            End If
        End If
        LeftArmGroup.Transform = New RotateTransform3D(LeftArmRot, ArmCenterOffsetX, 11 * ScalePx, 0)
        Root.Children.Add(LeftArmGroup)

        '5. Right Leg (-X side, center X = -2px)
        Dim RightLegGroup As New Model3DGroup()
        RightLegGroup.Children.Add(BuildBoxPart(-2 * ScalePx, -6 * ScalePx, 0, 4 * ScalePx, 12 * ScalePx, 4 * ScalePx, 0, 16, 4, 12, 4))
        If ShowOuter AndAlso Not IsLegacy64x32 Then
            RightLegGroup.Children.Add(BuildBoxPart(-2 * ScalePx, -6 * ScalePx, 0, 4.6 * ScalePx, 12.6 * ScalePx, 4.6 * ScalePx, 0, 32, 4, 12, 4, IsOuter:=True))
        End If
        RightLegGroup.Transform = New RotateTransform3D(RightLegRot, -2 * ScalePx, 0, 0)
        Root.Children.Add(RightLegGroup)

        '6. Left Leg (+X side, center X = +2px)
        Dim LeftLegGroup As New Model3DGroup()
        If IsLegacy64x32 Then
            LeftLegGroup.Children.Add(BuildBoxPart(2 * ScalePx, -6 * ScalePx, 0, 4 * ScalePx, 12 * ScalePx, 4 * ScalePx, 0, 16, 4, 12, 4, MirrorFaces:=True))
        Else
            LeftLegGroup.Children.Add(BuildBoxPart(2 * ScalePx, -6 * ScalePx, 0, 4 * ScalePx, 12 * ScalePx, 4 * ScalePx, 16, 48, 4, 12, 4))
            If ShowOuter Then
                LeftLegGroup.Children.Add(BuildBoxPart(2 * ScalePx, -6 * ScalePx, 0, 4.6 * ScalePx, 12.6 * ScalePx, 4.6 * ScalePx, 0, 48, 4, 12, 4, IsOuter:=True))
            End If
        End If
        LeftLegGroup.Transform = New RotateTransform3D(LeftLegRot, 2 * ScalePx, 0, 0)
        Root.Children.Add(LeftLegGroup)

        '7. Back Accessory (Cape / Elytra)
        Dim BackModeItem = TryCast(ComboBackAccessory?.SelectedItem, MyComboBoxItem)
        Dim BackMode = If(BackModeItem?.Tag?.ToString(), "Auto")
        If BackMode = "Auto" Then
            If CurrentCapeBitmap IsNot Nothing Then BackMode = "Cape" Else BackMode = "None"
        End If
        If BackMode = "Cape" Then
            Dim CapeTex = If(CurrentCapeBitmap, BuildDefaultCapeTexture())
            Dim CapeGroup As New Model3DGroup()
            '披风 10x16x1px，挂在后背 Z = -2.6px，顶部 Y = +12px (中心 Y = +4px)
            Dim CapeBox = BuildBoxPart(0, 4 * ScalePx, -2.6 * ScalePx, 10 * ScalePx, 16 * ScalePx, 1 * ScalePx, 0, 0, 10, 16, 1, SourceTex:=CapeTex)
            Dim CapeFlipGroup As New Transform3DGroup()
            '绕披风自身 Z 中心转 180° 使外图案朝向 -Z（背向玩家后方）
            CapeFlipGroup.Children.Add(New RotateTransform3D(New AxisAngleRotation3D(New Vector3D(0, 1, 0), 180), 0, 4 * ScalePx, -2.6 * ScalePx))
            CapeFlipGroup.Children.Add(New RotateTransform3D(CapeRot, 0, 12 * ScalePx, -2.2 * ScalePx))
            CapeGroup.Children.Add(CapeBox)
            CapeGroup.Transform = CapeFlipGroup
            Root.Children.Add(CapeGroup)
        ElseIf BackMode = "Elytra" Then
            Dim ElytraTex = If(CurrentCapeBitmap, BuildDefaultCapeTexture())
            '左翼 (+X) 与 右翼 (-X)，从后背肩部向下自然张开
            For Each Side In {-1, 1}
                Dim WingGroup As New Model3DGroup()
                Dim OffsetX = Side * 5.0 * ScalePx
                Dim WingBox = BuildBoxPart(OffsetX, 2.0 * ScalePx, -2.8 * ScalePx, 10 * ScalePx, 20 * ScalePx, 1.2 * ScalePx, 22, 0, 10, 20, 2, MirrorFaces:=(Side < 0), SourceTex:=ElytraTex)
                Dim WingTx As New Transform3DGroup()
                WingTx.Children.Add(New RotateTransform3D(New AxisAngleRotation3D(New Vector3D(0, 1, 0), 180), OffsetX, 2.0 * ScalePx, -2.8 * ScalePx))
                WingTx.Children.Add(New RotateTransform3D(If(Side > 0, LeftWingRotZ, RightWingRotZ), Side * 1.2 * ScalePx, 11.5 * ScalePx, -2.4 * ScalePx))
                WingTx.Children.Add(New RotateTransform3D(New AxisAngleRotation3D(New Vector3D(1, 0, 0), 14), 0, 11.5 * ScalePx, -2.4 * ScalePx))
                WingGroup.Children.Add(WingBox)
                WingGroup.Transform = WingTx
                Root.Children.Add(WingGroup)
            Next
        End If

        CharacterVisual.Content = Root
    End Sub

    Private Shared Function BuildDefaultCapeTexture() As BitmapSource
        '生成标准 64x32 披风+鞘翅默认纹理（深海蓝金边披风 + 银灰鞘翅纹理）
        Dim Width As Integer = 64
        Dim Height As Integer = 32
        Dim Stride As Integer = Width * 4
        Dim Pixels(Stride * Height - 1) As Byte
        Dim FillRect As Action(Of Integer, Integer, Integer, Integer, Byte, Byte, Byte, Byte) =
        Sub(x0 As Integer, y0 As Integer, w As Integer, h As Integer, r As Byte, g As Byte, b As Byte, a As Byte)
            For y As Integer = y0 To Math.Min(Height - 1, y0 + h - 1)
                For x As Integer = x0 To Math.Min(Width - 1, x0 + w - 1)
                    Dim idx = y * Stride + x * 4
                    Pixels(idx) = b
                    Pixels(idx + 1) = g
                    Pixels(idx + 2) = r
                    Pixels(idx + 3) = a
                Next
            Next
        End Sub
        'Cape area (0,0)-(22,17): deep blue fabric + gold border + bright emblem
        FillRect(0, 0, 22, 17, 28, 56, 110, 255)
        FillRect(1, 1, 10, 1, 225, 180, 60, 255)
        FillRect(1, 16, 10, 1, 225, 180, 60, 255)
        FillRect(1, 1, 1, 16, 225, 180, 60, 255)
        FillRect(10, 1, 1, 16, 225, 180, 60, 255)
        FillRect(4, 5, 4, 5, 245, 208, 90, 255)
        FillRect(5, 6, 2, 3, 28, 56, 110, 255)
        'Elytra area (22,0)-(46,22): slate-silver wing membrane with blue trim
        FillRect(22, 0, 24, 22, 95, 106, 128, 255)
        FillRect(24, 2, 10, 2, 170, 185, 210, 255)
        FillRect(24, 4, 2, 16, 65, 82, 115, 255)
        FillRect(26, 6, 6, 10, 130, 145, 175, 255)
        Dim Bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, Nothing, Pixels, Stride)
        Bmp.Freeze()
        Return Bmp
    End Function

    Private Sub LoadCapeFromBytes(Bytes As Byte(), Optional Silent As Boolean = False, Optional TriggerRebuild As Boolean = True)
        If Bytes Is Nothing OrElse Bytes.Length = 0 Then Return
        Using Ms As New MemoryStream(Bytes)
            Dim Decoder = BitmapDecoder.Create(Ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
            Dim RawFrame As BitmapSource = Decoder.Frames(0)
            Dim Converted As New FormatConvertedBitmap(RawFrame, PixelFormats.Bgra32, Nothing, 0)
            If Converted.PixelWidth <> 64 OrElse Converted.PixelHeight <> 32 Then
                If Converted.PixelWidth = Converted.PixelHeight * 2 AndAlso Converted.PixelWidth >= 64 Then
                    Dim Scale = 64.0 / Converted.PixelWidth
                    Dim Resized = New TransformedBitmap(Converted, New ScaleTransform(Scale, Scale))
                    Converted = New FormatConvertedBitmap(Resized, PixelFormats.Bgra32, Nothing, 0)
                Else
                    If Not Silent Then Throw New Exception($"披风尺寸不支持：{Converted.PixelWidth}x{Converted.PixelHeight}，需为 64x32 或等比 2:1 PNG 披风图。")
                    Return
                End If
            End If
            Converted.Freeze()
            CurrentCapeBitmap = Converted
            If TriggerRebuild Then RebuildModel()
        End Using
    End Sub

    Private Sub BtnLoadLocalCape_Click() Handles BtnLoadLocalCape.Click
        Dim Files = Dialogs.SelectFile("选择 Minecraft 皮肤披风图片 (.png)", False, filter:="PNG 披风文件 (*.png)|*.png")
        If Files Is Nothing OrElse Not Files.Any() Then Return
        Try
            LoadCapeFromBytes(File.ReadAllBytes(Files(0)))
            Hint($"已加载本地披风：{Path.GetFileName(Files(0))}", HintType.Green)
        Catch ex As Exception
            Hint($"加载披风失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub LoadSkinFromBytes(Bytes As Byte(), SourceDesc As String, Optional ForceSlim As Boolean? = Nothing)
        Using Ms As New MemoryStream(Bytes)
            Dim Decoder = BitmapDecoder.Create(Ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
            Dim RawFrame As BitmapSource = Decoder.Frames(0)
            Dim Converted As New FormatConvertedBitmap(RawFrame, PixelFormats.Bgra32, Nothing, 0)
            If Converted.PixelWidth <> 64 OrElse (Converted.PixelHeight <> 64 AndAlso Converted.PixelHeight <> 32) Then
                '支持高清皮肤 (如 128x128) 等比缩放至标准 64x64 以便像素级 UV 裁剪
                If Converted.PixelWidth = Converted.PixelHeight AndAlso Converted.PixelWidth >= 64 Then
                    Dim Scale = 64.0 / Converted.PixelWidth
                    Dim Resized = New TransformedBitmap(Converted, New ScaleTransform(Scale, Scale))
                    Converted = New FormatConvertedBitmap(Resized, PixelFormats.Bgra32, Nothing, 0)
                Else
                    Throw New Exception($"皮肤尺寸不支持：{Converted.PixelWidth}x{Converted.PixelHeight}，请提供 64x64 或 64x32 PNG 皮肤。")
                End If
            End If
            Converted.Freeze()
            CurrentSkinBitmap = Converted
            IsLegacy64x32 = (Converted.PixelHeight = 32)
            If ForceSlim.HasValue Then
                SuppressModelRebuild = True
                Try
                    ComboArmModel.SelectedIndex = If(ForceSlim.Value, 1, 0)
                Finally
                    SuppressModelRebuild = False
                End Try
            End If
            LabSkinInfo.Text = $"当前皮肤：{SourceDesc}（{RawFrame.PixelWidth}x{RawFrame.PixelHeight}" & If(IsLegacy64x32, " 经典旧版模板", " 标准双层模板") & "）"
            RebuildModel()
        End Using
    End Sub

    Private Sub ApplyFetchedSkinOnUi(SkinResult As (Bytes As Byte(), IsSlim As Boolean, CapeBytes As Byte()), SourceDesc As String, SuccessWithCape As String, SuccessNoCape As String, FailPrefix As String)
        RunInUi(
        Sub()
            Try
                If SkinResult.CapeBytes IsNot Nothing Then
                    LoadCapeFromBytes(SkinResult.CapeBytes, Silent:=True, TriggerRebuild:=False)
                Else
                    CurrentCapeBitmap = Nothing
                End If
                LoadSkinFromBytes(SkinResult.Bytes, SourceDesc, SkinResult.IsSlim)
                Hint(If(SkinResult.CapeBytes IsNot Nothing, SuccessWithCape, SuccessNoCape), HintType.Green)
            Catch ex As Exception
                LabSkinInfo.Text = $"{FailPrefix}：{ex.Message}"
                Hint($"{FailPrefix}：{ex.Message}", HintType.Red)
            End Try
        End Sub)
    End Sub

    Private Sub BtnLoadLocalSkin_Click() Handles BtnLoadLocalSkin.Click
        Dim Files = Dialogs.SelectFile("选择 Minecraft 皮肤图片 (.png)", False, filter:="PNG 皮肤文件 (*.png)|*.png")
        If Files Is Nothing OrElse Not Files.Any() Then Return
        Try
            LoadSkinFromBytes(File.ReadAllBytes(Files(0)), Path.GetFileName(Files(0)))
            Hint($"已加载本地皮肤：{Path.GetFileName(Files(0))}", HintType.Green)
        Catch ex As Exception
            Hint($"加载皮肤失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnLoadCurrentSkin_Click() Handles BtnLoadCurrentSkin.Click
        If IsLoadingCurrentSkin Then Return
        IsLoadingCurrentSkin = True
        BtnLoadCurrentSkin.IsEnabled = False
        LabSkinInfo.Text = "正在读取当前启动器账号皮肤..."
        RunInNewThread(
        Sub()
            Try
                '1. 按当前生效登录方式优先检测正版 / 外置皮肤认证缓存
                Dim EffectiveLogin As McLoginType = Settings.Get(Of McLoginType)("LoginType")
                If McInstanceSelected IsNot Nothing Then
                    Dim InstLogin As Integer = Settings.Get(Of Integer)("VersionServerLogin", McInstanceSelected)
                    Select Case InstLogin
                        Case 1 : EffectiveLogin = McLoginType.Ms
                        Case 2 : EffectiveLogin = McLoginType.Legacy
                        Case 3 : EffectiveLogin = McLoginType.Auth
                    End Select
                End If

                Dim MsUuid = Settings.Get(Of String)("CacheMsV2Uuid")
                If EffectiveLogin = McLoginType.Ms AndAlso Not String.IsNullOrWhiteSpace(MsUuid) Then
                    Dim ProfileUrl = $"https://sessionserver.mojang.com/session/minecraft/profile/{MsUuid.Replace("-", "")}"
                    Dim SkinResult = FetchProfileSkin(ProfileUrl)
                    ApplyFetchedSkinOnUi(SkinResult, $"当前正版账号 ({Settings.Get(Of String)("CacheMsV2Name")})", "已加载当前正版账号皮肤与披风！", "已加载当前正版账号皮肤！", "读取当前账号皮肤失败")
                    Return
                End If

                Dim AuthUuid = Settings.Get(Of String)("CacheAuthUuid")
                Dim AuthServer = Settings.Get(Of String)("CacheAuthServerServer")
                If EffectiveLogin = McLoginType.Auth AndAlso Not String.IsNullOrWhiteSpace(AuthUuid) AndAlso Not String.IsNullOrWhiteSpace(AuthServer) Then
                    Dim ProfileUrl = $"{AuthServer.TrimEnd("/"c)}/sessionserver/session/minecraft/profile/{AuthUuid.Replace("-", "")}"
                    Dim SkinResult = FetchProfileSkin(ProfileUrl)
                    ApplyFetchedSkinOnUi(SkinResult, $"当前外置账号 ({Settings.Get(Of String)("CacheAuthName")})", "已加载当前外置账号皮肤与披风！", "已加载当前外置账号皮肤！", "读取当前账号皮肤失败")
                    Return
                End If

                '2. 检测离线皮肤设置（自定义 PNG）
                Dim SkinType As Integer = Settings.Get(Of Integer)("LaunchSkinType")
                Dim CustomPath As String = GetPortableDataPath("CustomSkin.png")
                If SkinType = 4 AndAlso File.Exists(CustomPath) Then
                    Dim Slim As Boolean = Settings.Get(Of Boolean)("LaunchSkinSlim")
                    Dim CustomBytes = File.ReadAllBytes(CustomPath)
                    ApplyFetchedSkinOnUi((CustomBytes, Slim, Nothing), "当前离线自定义皮肤 (CustomSkin.png)", "已加载当前离线自定义皮肤！", "已加载当前离线自定义皮肤！", "读取当前账号皮肤失败")
                    Return
                End If

                RunInUi(Sub()
                            CurrentSkinBitmap = BuildDefaultSteveSkin()
                            IsLegacy64x32 = False
                            SuppressModelRebuild = True
                            Try
                                ComboArmModel.SelectedIndex = If(SkinType = 2, 1, 0)
                            Finally
                                SuppressModelRebuild = False
                            End Try
                            LabSkinInfo.Text = "当前为默认离线模板（未登录正版或未配置自定义皮肤图片）"
                            RebuildModel()
                            Hint("当前使用默认皮肤模板。", Log:=False)
                        End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "读取当前账号皮肤失败")
                RunInUi(Sub()
                            LabSkinInfo.Text = $"读取失败：{ex.Message}"
                            Hint($"读取当前账号皮肤失败：{ex.Message}", HintType.Red)
                        End Sub)
            Finally
                RunInUi(Sub()
                            IsLoadingCurrentSkin = False
                            BtnLoadCurrentSkin.IsEnabled = True
                        End Sub)
            End Try
        End Sub, "Load Current PCL Linking Skin")
    End Sub

    Private Sub BtnLookupMojang_Click() Handles BtnLookupMojang.Click
        Dim PlayerName = TextLookupName.Text.Trim()
        If String.IsNullOrWhiteSpace(PlayerName) Then
            Hint("请输入正版玩家名！", HintType.Red)
            Return
        End If
        BtnLookupMojang.IsEnabled = False
        LabSkinInfo.Text = $"正在从 Mojang 获取玩家「{PlayerName}」的皮肤..."
        RunInNewThread(
        Sub()
            Try
                Dim SkinResult = FetchSkinByUsername(PlayerName)
                ApplyFetchedSkinOnUi(SkinResult, $"正版玩家 {PlayerName}", $"已加载玩家「{PlayerName}」的 3D 皮肤与披风！", $"已加载玩家「{PlayerName}」的 3D 皮肤！", "获取皮肤失败")
            Catch ex As Exception
                RunInUi(Sub()
                            LabSkinInfo.Text = $"获取失败：{ex.Message}"
                            Hint($"获取皮肤失败：{ex.Message}", HintType.Red)
                        End Sub)
            Finally
                RunInUi(Sub() BtnLookupMojang.IsEnabled = True)
            End Try
        End Sub, "Lookup Mojang Skin")
    End Sub

    Private Shared Function FetchSkinByUsername(UserName As String) As (Bytes As Byte(), IsSlim As Boolean, CapeBytes As Byte())
        Using Client As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(15)}
            Using Resp = Client.GetAsync($"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(UserName)}").GetAwaiter().GetResult()
                If Resp.StatusCode = System.Net.HttpStatusCode.NoContent OrElse Resp.StatusCode = System.Net.HttpStatusCode.NotFound Then
                    Throw New Exception("未找到该正版玩家")
                End If
                Resp.EnsureSuccessStatusCode()
                Dim UuidResp = Resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                If String.IsNullOrWhiteSpace(UuidResp) Then Throw New Exception("未找到该正版玩家")
                Dim UuidJson = Newtonsoft.Json.Linq.JObject.Parse(UuidResp)
                Dim Uuid = UuidJson("id")?.ToString()
                If String.IsNullOrEmpty(Uuid) Then Throw New Exception("未找到该正版玩家")
                Return FetchProfileSkinWithClient(Client, $"https://sessionserver.mojang.com/session/minecraft/profile/{Uuid}")
            End Using
        End Using
    End Function

    Private Shared Function FetchProfileSkin(ProfileUrl As String) As (Bytes As Byte(), IsSlim As Boolean, CapeBytes As Byte())
        Using Client As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(15)}
            Return FetchProfileSkinWithClient(Client, ProfileUrl)
        End Using
    End Function

    Private Shared Function FetchProfileSkinWithClient(Client As HttpClient, ProfileUrl As String) As (Bytes As Byte(), IsSlim As Boolean, CapeBytes As Byte())
        Dim ProfileText = Client.GetStringAsync(ProfileUrl).GetAwaiter().GetResult()
        Dim ProfileJson = Newtonsoft.Json.Linq.JObject.Parse(ProfileText)
        Dim Props = TryCast(ProfileJson("properties"), Newtonsoft.Json.Linq.JArray)
        Dim TexProp = Props?.FirstOrDefault(Function(p) p("name")?.ToString() = "textures")
        Dim ValueB64 = TexProp?("value")?.ToString()
        If String.IsNullOrEmpty(ValueB64) Then Throw New Exception("该角色未设置材质信息")
        Dim TexJson = Newtonsoft.Json.Linq.JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(ValueB64)))
        Dim SkinNode = TexJson("textures")?("SKIN")
        Dim SkinUrl = SkinNode?("url")?.ToString()
        If String.IsNullOrEmpty(SkinUrl) Then Throw New Exception("该角色当前使用默认皮肤")
        Dim IsSlim As Boolean = (SkinNode("metadata")?("model")?.ToString() = "slim")
        Dim ImgBytes = Client.GetByteArrayAsync(SkinUrl).GetAwaiter().GetResult()
        Dim CapeBytes As Byte() = Nothing
        Dim CapeUrl = TexJson("textures")?("CAPE")?("url")?.ToString()
        If Not String.IsNullOrWhiteSpace(CapeUrl) Then
            Try
                CapeBytes = Client.GetByteArrayAsync(CapeUrl).GetAwaiter().GetResult()
            Catch
                CapeBytes = Nothing
            End Try
        End If
        Return (ImgBytes, IsSlim, CapeBytes)
    End Function

    Private Sub BtnSnapshotView_Click() Handles BtnSnapshotView.Click
        Dim OutPath = Dialogs.SaveFile("保存 3D 皮肤预览截图", $"skin-3d-{DateTime.Now:yyyyMMdd-HHmmss}.png", filter:={("png", "PNG 图片 (*.png)")})
        If String.IsNullOrEmpty(OutPath) Then Return
        Try
            Dim W As Integer = Math.Max(1, CInt(ViewportHost.ActualWidth * 2))
            Dim H As Integer = Math.Max(1, CInt(ViewportHost.ActualHeight * 2))
            Dim Rtb As New RenderTargetBitmap(W, H, 192, 192, PixelFormats.Pbgra32)
            Dim BgItem = TryCast(ComboPreviewBg.SelectedItem, MyComboBoxItem)
            Dim BgTag = If(BgItem?.Tag?.ToString(), "Checker")
            If BgTag <> "Checker" AndAlso ViewportHost.Background IsNot Nothing Then
                Dim BgVisual As New DrawingVisual()
                Using Dc = BgVisual.RenderOpen()
                    Dc.DrawRectangle(ViewportHost.Background, Nothing, New Rect(0, 0, ViewportHost.ActualWidth, ViewportHost.ActualHeight))
                End Using
                Rtb.Render(BgVisual)
            End If
            Rtb.Render(SkinViewport)
            Dim Enc As New PngBitmapEncoder()
            Enc.Frames.Add(BitmapFrame.Create(Rtb))
            Using Fs As New FileStream(OutPath, FileMode.Create)
                Enc.Save(Fs)
            End Using
            Hint($"已保存 3D 皮肤预览截图到 {Path.GetFileName(OutPath)}", HintType.Green)
        Catch ex As Exception
            Hint($"保存截图失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnExportAvatar_Click() Handles BtnExportAvatar.Click
        If CurrentSkinBitmap Is Nothing Then
            Hint("请先加载皮肤后再导出头像！", HintType.Red)
            Return
        End If
        Dim SizeItem = TryCast(ComboAvatarSize.SelectedItem, MyComboBoxItem)
        Dim TargetPx As Integer = Math.Max(64, CInt(Val(If(SizeItem?.Tag?.ToString(), "64"))))
        Dim OutPath = Dialogs.SaveFile("导出 Minecraft 皮肤头像", $"avatar-{TargetPx}x{TargetPx}-{DateTime.Now:yyyyMMdd-HHmmss}.png", filter:={("png", "PNG 图片 (*.png)")})
        If String.IsNullOrEmpty(OutPath) Then Return
        Try
            Dim Stride8 As Integer = 8 * 4
            Dim BaseBuf(Stride8 * 8 - 1) As Byte
            Dim FaceCrop As New CroppedBitmap(CurrentSkinBitmap, New Int32Rect(8, 8, 8, 8))
            FaceCrop.CopyPixels(BaseBuf, Stride8, 0)

            If ChkShowOuterLayer.Checked AndAlso Not IsLegacy64x32 AndAlso CurrentSkinBitmap.PixelWidth >= 48 AndAlso CurrentSkinBitmap.PixelHeight >= 16 Then
                Dim HatBuf(Stride8 * 8 - 1) As Byte
                Dim HatCrop As New CroppedBitmap(CurrentSkinBitmap, New Int32Rect(40, 8, 8, 8))
                HatCrop.CopyPixels(HatBuf, Stride8, 0)
                For i As Integer = 0 To BaseBuf.Length - 4 Step 4
                    Dim Sa As Integer = HatBuf(i + 3)
                    If Sa = 0 Then Continue For
                    If Sa = 255 Then
                        BaseBuf(i) = HatBuf(i)
                        BaseBuf(i + 1) = HatBuf(i + 1)
                        BaseBuf(i + 2) = HatBuf(i + 2)
                        BaseBuf(i + 3) = 255
                    Else
                        Dim InvA As Integer = 255 - Sa
                        BaseBuf(i) = CByte((HatBuf(i) * Sa + BaseBuf(i) * InvA) \ 255)
                        BaseBuf(i + 1) = CByte((HatBuf(i + 1) * Sa + BaseBuf(i + 1) * InvA) \ 255)
                        BaseBuf(i + 2) = CByte((HatBuf(i + 2) * Sa + BaseBuf(i + 2) * InvA) \ 255)
                        BaseBuf(i + 3) = CByte(Math.Min(255, Sa + (BaseBuf(i + 3) * InvA) \ 255))
                    End If
                Next
            End If

            Dim Head8 = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, Nothing, BaseBuf, Stride8)
            Dim Scaled = ScalePixelArtNearest(Head8, Math.Max(1, TargetPx \ 8))
            Dim Enc As New PngBitmapEncoder()
            Enc.Frames.Add(BitmapFrame.Create(Scaled))
            Using Fs As New FileStream(OutPath, FileMode.Create)
                Enc.Save(Fs)
            End Using
            Hint($"已导出 {TargetPx}×{TargetPx} 皮肤头像：{Path.GetFileName(OutPath)}", HintType.Green)
        Catch ex As Exception
            Hint($"导出头像失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

End Class
