#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$ExternalLocation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$identityRoot = $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $identityRoot ".."))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "obj\IdentityPackage"))
$expectedStagingParent = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "obj"))
if ([IO.Path]::GetDirectoryName($stagingRoot) -ne $expectedStagingParent) {
    throw "Refusing to clean an unexpected identity staging directory: $stagingRoot"
}

$parsedVersion = $null
if (-not [Version]::TryParse($Version, [ref]$parsedVersion)) {
    throw "Identity package version '$Version' is invalid."
}
$packageVersion = "{0}.{1}.{2}.{3}" -f $parsedVersion.Major,
    $parsedVersion.Minor,
    [Math]::Max(0, $parsedVersion.Build),
    [Math]::Max(0, $parsedVersion.Revision)

if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $identityRoot "Assets") `
    -Destination $stagingRoot -Recurse

# Packaged processes use Square44x44Logo target-size resources for the taskbar
# instead of the executable's icon. Supply unplated variants so Windows renders
# MultiExplorer's transparent artwork rather than placing it on a neutral box.
# The application ICO already contains carefully rendered images at common Shell
# sizes; missing DPI sizes are produced from the next-largest embedded image.
Add-Type -AssemblyName System.Drawing
$applicationIconPath = Join-Path $repositoryRoot "MultiExplorer-Installer.ico"
$applicationIconBytes = [IO.File]::ReadAllBytes($applicationIconPath)
$iconEntryCount = [BitConverter]::ToUInt16($applicationIconBytes, 4)
$iconEntries = for ($index = 0; $index -lt $iconEntryCount; $index++) {
    $entryOffset = 6 + (16 * $index)
    $entrySize = if ($applicationIconBytes[$entryOffset] -eq 0) {
        256
    }
    else {
        [int]$applicationIconBytes[$entryOffset]
    }

    [pscustomobject]@{
        Size = $entrySize
        Length = [int][BitConverter]::ToUInt32(
            $applicationIconBytes, $entryOffset + 8)
        Offset = [int][BitConverter]::ToUInt32(
            $applicationIconBytes, $entryOffset + 12)
    }
}

$taskbarIconSizes = @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)
foreach ($size in $taskbarIconSizes) {
    $sourceEntry = $iconEntries |
        Where-Object Size -eq $size |
        Select-Object -First 1
    if (-not $sourceEntry) {
        $sourceEntry = $iconEntries |
            Where-Object Size -gt $size |
            Sort-Object Size |
            Select-Object -First 1
    }
    if (-not $sourceEntry) {
        $sourceEntry = $iconEntries |
            Sort-Object Size -Descending |
            Select-Object -First 1
    }

    $sourceBytes = [byte[]]::new($sourceEntry.Length)
    [Array]::Copy($applicationIconBytes, $sourceEntry.Offset,
        $sourceBytes, 0, $sourceEntry.Length)
    $targetNames = @(
        "Square44x44Logo.targetsize-$size.png"
        "Square44x44Logo.targetsize-$($size)_altform-unplated.png"
        "Square44x44Logo.targetsize-$($size)_altform-lightunplated.png"
    )

    if ($sourceEntry.Size -eq $size) {
        foreach ($targetName in $targetNames) {
            [IO.File]::WriteAllBytes(
                (Join-Path $stagingRoot "Assets\$targetName"), $sourceBytes)
        }
        continue
    }

    $sourceStream = [IO.MemoryStream]::new($sourceBytes, $false)
    $sourceImage = [Drawing.Image]::FromStream($sourceStream)
    $targetImage = [Drawing.Bitmap]::new(
        $size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($targetImage)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingMode =
            [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality =
            [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode =
            [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode =
            [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode =
            [Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.DrawImage($sourceImage,
            [Drawing.Rectangle]::new(0, 0, $size, $size),
            0, 0, $sourceImage.Width, $sourceImage.Height,
            [Drawing.GraphicsUnit]::Pixel)

        foreach ($targetName in $targetNames) {
            $targetImage.Save(
                (Join-Path $stagingRoot "Assets\$targetName"),
                [Drawing.Imaging.ImageFormat]::Png)
        }
    }
    finally {
        $graphics.Dispose()
        $targetImage.Dispose()
        $sourceImage.Dispose()
        $sourceStream.Dispose()
    }
}

$manifestTemplate = Get-Content -LiteralPath `
    (Join-Path $identityRoot "AppxManifest.xml") -Raw

$manifestDocument = [Xml.XmlDocument]::new()
$manifestDocument.PreserveWhitespace = $true
$manifestDocument.LoadXml($manifestTemplate)
$identityNode = $manifestDocument.SelectSingleNode(
    "/*[local-name()='Package']/*[local-name()='Identity']")
if (-not $identityNode) {
    throw "The identity package manifest does not contain an Identity element."
}
$identityNode.SetAttribute("Version", $packageVersion)

$manifestPath = Join-Path $stagingRoot "AppxManifest.xml"
$xmlWriterSettings = [Xml.XmlWriterSettings]::new()
$xmlWriterSettings.Encoding = [Text.UTF8Encoding]::new($false)
$xmlWriterSettings.Indent = $false
$xmlWriter = [Xml.XmlWriter]::Create($manifestPath, $xmlWriterSettings)
try {
    $manifestDocument.Save($xmlWriter)
}
finally {
    $xmlWriter.Dispose()
}

function Find-WindowsSdkTool {
    param([Parameter(Mandatory = $true)][string]$Name)

    $tool = Get-ChildItem `
        "C:\Program Files (x86)\Windows Kits\10\bin" `
            -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like "*\x64" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $tool) {
        $tool = Get-ChildItem `
            "C:\Program Files (x86)\Windows Kits\10\bin" `
                -Recurse -Filter $Name -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
    }
    if (-not $tool) {
        throw "$Name was not found. Install the Windows 10 or 11 SDK."
    }

    return $tool
}

$makePri = Find-WindowsSdkTool -Name "makepri.exe"
$resourcesPath = Join-Path $stagingRoot "resources.pri"
& $makePri.FullName new `
    /pr $stagingRoot `
    /cf (Join-Path $identityRoot "priconfig.xml") `
    /mn $manifestPath `
    /of $resourcesPath `
    /o
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $resourcesPath)) {
    throw "makepri.exe failed to index the identity package resources."
}

$makeAppx = Find-WindowsSdkTool -Name "makeappx.exe"
$outputFull = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFull)) | Out-Null
# A sparse package intentionally keeps MultiExplorer.exe outside the MSIX. /nv
# prevents MakeAppx from rejecting that external executable during packaging;
# Windows validates AllowExternalContent when the package is registered.
& $makeAppx.FullName pack /d $stagingRoot /p $outputFull /o /nv
if ($LASTEXITCODE -ne 0) {
    throw "makeappx.exe failed with exit code $LASTEXITCODE."
}

$externalContentRoot = if ([string]::IsNullOrWhiteSpace($ExternalLocation)) {
    [IO.Path]::GetDirectoryName($outputFull)
}
else {
    [IO.Path]::GetFullPath($ExternalLocation)
}
[IO.Directory]::CreateDirectory($externalContentRoot) | Out-Null

# A sparse package's manifest logos are resolved relative to the registered
# external location, not from the files embedded in the identity MSIX. Deploy
# both the assets and their qualifier index beside the application payload so
# the taskbar can load the unplated icon variants at every display scale.
Copy-Item -LiteralPath (Join-Path $stagingRoot "Assets") `
    -Destination $externalContentRoot -Recurse -Force
Copy-Item -LiteralPath $resourcesPath `
    -Destination (Join-Path $externalContentRoot "resources.pri") -Force

Write-Host "Built MultiExplorer identity package: $outputFull"
Write-Host "Deployed identity visual assets: $externalContentRoot"
