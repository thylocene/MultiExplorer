#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Install", "Remove")]
    [string]$Action,

    [string]$PackagePath,
    [string]$ExternalLocation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Action -eq "Remove") {
    Get-AppxPackage -Name "MultiExplorer.Identity" | Remove-AppxPackage
    exit 0
}

$minimumIdentityBuild = 19041
$osVersion = [Environment]::OSVersion.Version
if ($osVersion.Major -lt 10 -or
    ($osVersion.Major -eq 10 -and $osVersion.Build -lt $minimumIdentityBuild)) {
    Write-Warning (
        "MultiExplorer's optional package identity requires Windows build " +
        "$minimumIdentityBuild or later. The application will be installed " +
        "without enhanced Pin to Start integration.")
    exit 0
}

if ([string]::IsNullOrWhiteSpace($PackagePath) -or
    -not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "The MultiExplorer identity package was not found: $PackagePath"
}
if ([string]::IsNullOrWhiteSpace($ExternalLocation) -or
    -not (Test-Path -LiteralPath $ExternalLocation -PathType Container)) {
    throw "The MultiExplorer installation directory was not found: $ExternalLocation"
}

Add-AppxPackage -Path ([IO.Path]::GetFullPath($PackagePath)) `
    -ExternalLocation ([IO.Path]::GetFullPath($ExternalLocation)) `
    -AllowUnsigned `
    -ForceUpdateFromAnyVersion
