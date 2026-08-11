[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\zvt2sumup.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath([Drawing.RectangleF]$Rectangle, [float]$Radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($Rectangle.Left, $Rectangle.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.Left, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng([int]$Size) {
    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.ScaleTransform($Size / 64.0, $Size / 64.0)

        $background = New-RoundedRectanglePath ([Drawing.RectangleF]::new(0, 0, 64, 64)) 17
        $dark = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(5, 8, 13))
        try { $graphics.FillPath($dark, $background) } finally { $dark.Dispose(); $background.Dispose() }

        $start = [Drawing.PointF]::new(8, 6); $end = [Drawing.PointF]::new(56, 58)
        $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new($start, $end, [Drawing.Color]::FromArgb(51, 163, 255), [Drawing.Color]::FromArgb(22, 94, 232))
        $blue = [Drawing.Pen]::new($gradient, 5)
        try {
            $blue.LineJoin = [Drawing.Drawing2D.LineJoin]::Round; $blue.StartCap = [Drawing.Drawing2D.LineCap]::Round; $blue.EndCap = [Drawing.Drawing2D.LineCap]::Round
            [Drawing.PointF[]]$bridge = @([Drawing.PointF]::new(9, 43), [Drawing.PointF]::new(20, 27), [Drawing.PointF]::new(44, 27), [Drawing.PointF]::new(55, 43))
            $graphics.DrawLines($blue, $bridge)
        } finally { $blue.Dispose(); $gradient.Dispose() }

        $light = [Drawing.Pen]::new([Drawing.Color]::FromArgb(135, 199, 255), 3)
        try {
            $light.StartCap = [Drawing.Drawing2D.LineCap]::Round; $light.EndCap = [Drawing.Drawing2D.LineCap]::Round
            $graphics.DrawLine($light, 9, 43, 55, 43); $graphics.DrawLine($light, 15, 35, 15, 47); $graphics.DrawLine($light, 49, 35, 49, 47)
        } finally { $light.Dispose() }
        $green = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(54, 211, 153))
        try { $graphics.FillEllipse($green, 51, 39, 7, 7) } finally { $green.Dispose() }

        $memory = [IO.MemoryStream]::new()
        try { $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png); return ,$memory.ToArray() } finally { $memory.Dispose() }
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

[int[]]$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) { $images.Add([byte[]](New-IconPng $size)) }
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullOutputPath)) | Out-Null
$stream = [IO.FileStream]::new($fullOutputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { [byte]0 } else { [byte]$sizes[$index] }
        $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$images[$index].Length); $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
} finally { $writer.Dispose(); $stream.Dispose() }

Write-Output $fullOutputPath
