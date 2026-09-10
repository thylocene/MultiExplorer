#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$publishRoot = [IO.Path]::GetFullPath($PublishDir).TrimEnd('\')
if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw "Published application directory does not exist: $publishRoot"
}

$directoryIds = @{
    "" = "INSTALLFOLDER"
}

function Get-StableId([string]$prefix, [string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant())
        $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "")
        return "${prefix}_$($hash.Substring(0, 20))"
    }
    finally { $sha.Dispose() }
}

function Get-StableGuid([string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes("MultiExplorer/runtime/" + $value.ToLowerInvariant())
        $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "")
        return "{0}-{1}-{2}-{3}-{4}" -f $hash.Substring(0, 8),
            $hash.Substring(8, 4), $hash.Substring(12, 4),
            $hash.Substring(16, 4), $hash.Substring(20, 12)
    }
    finally { $sha.Dispose() }
}

function Escape-Xml([string]$value) {
    return [Security.SecurityElement]::Escape($value)
}

$files = Get-ChildItem -LiteralPath $publishRoot -Recurse -File |
    Where-Object { $_.FullName -ne (Join-Path $publishRoot "MultiExplorer.exe") } |
    Sort-Object FullName

if (-not $files) { throw "No runtime files were found in $publishRoot" }

$groups = $files | Group-Object {
    $relative = $_.FullName.Substring($publishRoot.Length + 1)
    $relativeDirectory = [IO.Path]::GetDirectoryName($relative)
    if ($null -eq $relativeDirectory) { "" } else { $relativeDirectory }
}

$unknown = @($groups | Where-Object { -not $directoryIds.ContainsKey($_.Name) })
if ($unknown.Count -gt 0) {
    throw "Publish output contains unhandled subdirectories: $($unknown.Name -join ', ')"
}

$sb = [Text.StringBuilder]::new()
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="PublishedRuntimeFiles">')

foreach ($group in $groups | Sort-Object Name) {
    $directory = [string]$group.Name
    $componentId = Get-StableId "PublishedComponent" $directory
    $componentGuid = Get-StableGuid $directory
    $registryName = Get-StableId "Runtime" $directory
    [void]$sb.AppendLine("      <Component Id=`"$componentId`" Directory=`"$($directoryIds[$directory])`" Guid=`"$componentGuid`">")

    foreach ($file in $group.Group) {
        $relative = $file.FullName.Substring($publishRoot.Length + 1)
        $source = '$' + '(var.PublishDir)' + $relative
        $fileId = Get-StableId "PublishedFile" $relative
        [void]$sb.AppendLine("        <File Id=`"$fileId`" Source=`"$(Escape-Xml $source)`" />")
    }

    if ($directory -ne "") {
        $removeId = Get-StableId "RemovePublishedDir" $directory
        [void]$sb.AppendLine("        <RemoveFolder Id=`"$removeId`" Directory=`"$($directoryIds[$directory])`" On=`"uninstall`" />")
    }

    [void]$sb.AppendLine("        <RegistryValue Root=`"HKCU`" Key=`"Software\MultiExplorer\RuntimeComponents`" Name=`"$registryName`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" />")
    [void]$sb.AppendLine('      </Component>')
}

[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

$outputFull = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFull)
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
[IO.File]::WriteAllText($outputFull, $sb.ToString(), [Text.UTF8Encoding]::new($false))
Write-Host "Generated WiX manifest for $($files.Count) published runtime files: $outputFull"
