$ErrorActionPreference = 'Stop'

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$SourcePath = Join-Path $ProjectRoot 'CPA-SingleFile.cs'
$AssetPath = Join-Path $ProjectRoot 'assets\cpa.png'
$ObjDir = Join-Path $ProjectRoot 'obj'
$ColorIcon = Join-Path $ObjDir 'CPA-color.ico'
$GrayIcon = Join-Path $ObjDir 'CPA-gray.ico'
$OutputExe = Join-Path $ProjectRoot 'CPA.exe'
$Compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) {
    $Compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}

if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) {
    throw 'Cannot find the .NET Framework C# compiler csc.exe.'
}

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw 'Cannot find CPA-SingleFile.cs.'
}

if (-not (Test-Path -LiteralPath $AssetPath -PathType Leaf)) {
    throw 'Cannot find assets\cpa.png.'
}

[System.IO.Directory]::CreateDirectory($ObjDir) | Out-Null

Add-Type -AssemblyName System.Drawing

function New-IcoFromPng {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Output,
        [Parameter(Mandatory = $true)][bool]$Gray
    )

    $sizes = @(16, 32, 48, 256)
    $sourceImage = [System.Drawing.Image]::FromFile($Source)
    $entries = @()

    try {
        foreach ($size in $sizes) {
            $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($sourceImage, 0, 0, $size, $size)
            }
            finally {
                $graphics.Dispose()
            }

            if ($Gray) {
                for ($y = 0; $y -lt $bitmap.Height; $y++) {
                    for ($x = 0; $x -lt $bitmap.Width; $x++) {
                        $c = $bitmap.GetPixel($x, $y)
                        $v = [int](($c.R * 0.299) + ($c.G * 0.587) + ($c.B * 0.114))
                        $bitmap.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($c.A, $v, $v, $v))
                    }
                }
            }

            $stream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                $entries += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
            }
            finally {
                $stream.Dispose()
                $bitmap.Dispose()
            }
        }
    }
    finally {
        $sourceImage.Dispose()
    }

    $fileStream = [System.IO.File]::Create($Output)
    $writer = [System.IO.BinaryWriter]::new($fileStream)

    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$entries.Count)

        $offset = 6 + (16 * $entries.Count)

        foreach ($entry in $entries) {
            $iconSize = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
            $writer.Write([byte]$iconSize)
            $writer.Write([byte]$iconSize)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$entry.Bytes.Length)
            $writer.Write([UInt32]$offset)
            $offset += $entry.Bytes.Length
        }

        foreach ($entry in $entries) {
            $writer.Write($entry.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $fileStream.Dispose()
    }
}

try {
    New-IcoFromPng -Source $AssetPath -Output $ColorIcon -Gray $false
    New-IcoFromPng -Source $AssetPath -Output $GrayIcon -Gray $true

    & $Compiler `
        /nologo `
        /codepage:65001 `
        /target:winexe `
        /platform:anycpu `
        /reference:System.Windows.Forms.dll `
        /reference:System.Drawing.dll `
        /reference:System.Web.Extensions.dll `
        /reference:System.IO.Compression.dll `
        /reference:System.IO.Compression.FileSystem.dll `
        /win32icon:$ColorIcon `
        /resource:$ColorIcon,CpaColorIcon `
        /resource:$GrayIcon,CpaGrayIcon `
        /out:$OutputExe `
        $SourcePath

    if ($LASTEXITCODE -ne 0) {
        throw "csc.exe failed with exit code $LASTEXITCODE."
    }

    Write-Host "Built: $OutputExe"
}
finally {
    [System.IO.File]::Delete($ColorIcon)
    [System.IO.File]::Delete($GrayIcon)
}
