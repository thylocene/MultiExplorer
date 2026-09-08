#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and signs the MultiExplorer MSI installer.

.DESCRIPTION
    1. Publishes MultiExplorer as a self-contained ReadyToRun app. Runtime files
       remain separate so first-launch security scanning does not gate one huge exe.
    2. Builds the WiX 4 installer project (downloads WixToolset.Sdk from NuGet
       automatically on first run — internet access required once).
    3. Signs the MSI with a self-signed code-signing certificate stored in
       Cert:\CurrentUser\My.  The certificate is created the first time and
       reused on subsequent runs.

.PARAMETER Version
    Product version embedded in both the application and MSI. Defaults to the
    version in Directory.Build.props.

.PARAMETER BuildDate
    Build date displayed by the setup wizard (default: today's date, yyyy-MM-dd).

.PARAMETER SkipPublish
    Skip the dotnet publish step.  A staleness check compares the published
    exe against all .cs / .csproj source files and aborts if any are newer.

.PARAMETER SkipSigning
    Produce the MSI without signing it.

.PARAMETER Force
    When used with -SkipPublish, bypasses the staleness check and proceeds
    even if source files are newer than the published exe.

.EXAMPLE
    .\Build-Installer.ps1
    .\Build-Installer.ps1 -Version 1.2.0
    .\Build-Installer.ps1 -SkipPublish -SkipSigning
    .\Build-Installer.ps1 -SkipPublish -Force
#>
param(
    [string]$Version,
    [string]$BuildDate  = (Get-Date -Format "yyyy-MM-dd"),
    [switch]$SkipPublish,
    [switch]$SkipSigning,
    [switch]$Force        # bypass the staleness check when -SkipPublish is set
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root             = $PSScriptRoot
$MainCsproj       = Join-Path $Root "MultiExplorer.csproj"
$BuildProps       = Join-Path $Root "Directory.Build.props"
$InstallerProject = Join-Path $Root "MultiExplorer.Installer\MultiExplorer.Installer.wixproj"
$MsiPath          = Join-Path $Root "MultiExplorer.Installer\bin\Release\en-US\MultiExplorer-Setup.msi"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath $BuildProps
    $Version = [string]$props.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "No Version was supplied and Directory.Build.props does not define one."
    }
}

Write-Host "`n>> Package metadata" -ForegroundColor Cyan
Write-Host "   Version    : $Version"
Write-Host "   Build date : $BuildDate"

# ── 1. Publish the application ────────────────────────────────────────────────
$PublishDir = Join-Path $Root "bin\Release\net8.0-windows\win-x64\publish"
$PublishExe = Join-Path $PublishDir "MultiExplorer.exe"

if (-not $SkipPublish) {
    # Check for a running instance — Windows Installer cannot replace a locked exe.
    $running = Get-Process -Name "MultiExplorer" -ErrorAction SilentlyContinue
    if ($running) {
        throw "MultiExplorer is currently running (PID $($running.Id)). Close it before building the installer."
    }

    Write-Host "`n>> Publishing MultiExplorer..." -ForegroundColor Cyan

    # Start from an empty, validated publish directory so WiX cannot harvest stale
    # files left by an older packaging layout or a diagnostic run.
    $publishFull = [IO.Path]::GetFullPath($PublishDir)
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $Root "bin\Release\net8.0-windows\win-x64"))
    if ([IO.Path]::GetDirectoryName($publishFull) -ne $expectedParent) {
        throw "Refusing to clean unexpected publish directory: $publishFull"
    }
    if (Test-Path -LiteralPath $publishFull) {
        Remove-Item -LiteralPath $publishFull -Recurse -Force
    }

    dotnet publish $MainCsproj -c Release "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

    if (-not (Test-Path $PublishExe)) {
        throw "dotnet publish succeeded but expected exe was not found:`n  $PublishExe"
    }
    Write-Host "   Exe timestamp : $((Get-Item $PublishExe).LastWriteTime)" -ForegroundColor Green
    Write-Host "   Exe size      : $([math]::Round((Get-Item $PublishExe).Length / 1MB, 1)) MB" -ForegroundColor Green
}
else {
    # ── Staleness check ───────────────────────────────────────────────────────
    # Verify the published exe is not older than any source file so the
    # installer never silently embeds a stale executable.

    if (-not (Test-Path $PublishExe)) {
        throw "-SkipPublish was set but no published exe exists at:`n  $PublishExe`nRun without -SkipPublish to build it first."
    }

    $exeTime = (Get-Item $PublishExe).LastWriteTime

    $newerFile = Get-ChildItem $Root -Recurse -Include "*.cs","*.csproj","*.props" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '\\obj\\'                   -and
            $_.FullName -notmatch '\\bin\\'                   -and
            $_.FullName -notmatch '\\MultiExplorer\.Installer\\' -and
            $_.FullName -notmatch '\\MultiExplorer\.Tests\\'  -and
            $_.LastWriteTime -gt $exeTime
        } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($newerFile) {
        $msg = @"

  Staleness check failed — source is newer than the published exe:
    Newest source : $($newerFile.FullName)
                    (modified $($newerFile.LastWriteTime))
    Published exe : $PublishExe
                    (modified $exeTime)

  The installer would embed an out-of-date executable.
  → Remove -SkipPublish to recompile, or add -Force to override.
"@
        if (-not $Force) {
            throw $msg
        }
        Write-Warning $msg.Trim()
        Write-Warning "-Force specified — continuing with potentially stale exe."
    }
    else {
        Write-Host "`n>> Staleness check passed — published exe is up to date." -ForegroundColor Green
    }
}

# ── 2. Build the MSI ──────────────────────────────────────────────────────────
Write-Host "`n>> Building installer (WiX 4)..." -ForegroundColor Cyan
Write-Host "   (First run downloads WixToolset.Sdk from NuGet — may take a moment)"
# --no-incremental forces WiX to repackage the MSI from the freshly-published exe
# rather than reusing a cached MSI from a prior build.
dotnet build $InstallerProject -c Release --no-incremental "-p:Version=$Version" "-p:BuildDate=$BuildDate" -p:SkipAppPublish=true
if ($LASTEXITCODE -ne 0) { throw "WiX build failed (exit $LASTEXITCODE)." }

if (-not (Test-Path $MsiPath)) {
    throw "Expected MSI not found: $MsiPath"
}
Write-Host "   Built: $MsiPath" -ForegroundColor Green

if ($SkipSigning) {
    Write-Host "`nDone (signing skipped)." -ForegroundColor Green
    Write-Host "Installer: $MsiPath"
    exit 0
}

# ── 3. Locate or create a self-signed code-signing certificate ────────────────
Write-Host "`n>> Locating code-signing certificate..." -ForegroundColor Cyan

$certSubject = "CN=MultiExplorer"
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $certSubject -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

if (-not $cert) {
    Write-Host "   Creating self-signed certificate (valid 5 years)..."
    $cert = New-SelfSignedCertificate `
        -Type          CodeSigning `
        -Subject       $certSubject `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -HashAlgorithm SHA256 `
        -NotAfter      (Get-Date).AddYears(5)
    Write-Host "   Created — thumbprint: $($cert.Thumbprint)" -ForegroundColor Green
}
else {
    Write-Host "   Found existing certificate — thumbprint: $($cert.Thumbprint)"
}

# ── 4. Locate signtool.exe ────────────────────────────────────────────────────
Write-Host "`n>> Signing MSI..." -ForegroundColor Cyan

$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" `
                -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -like "*x64*" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

if (-not $signtool) {
    # Fall back to Program Files (arm64/x86 layouts vary on some machines)
    $signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" `
                    -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending |
                Select-Object -First 1
}

if (-not $signtool) {
    Write-Warning @"
signtool.exe not found.
Install the Windows 10/11 SDK (https://developer.microsoft.com/windows/downloads/windows-sdk/)
to enable code signing.  The unsigned MSI is still usable:
  $MsiPath
"@
    exit 0
}

# Sign with SHA-256 and a RFC 3161 timestamp so the signature stays valid after
# the self-signed cert expires.
& $signtool.FullName sign `
    /sha1 $cert.Thumbprint `
    /fd   SHA256 `
    /d    "MultiExplorer" `
    /t    "http://timestamp.sectigo.com" `
    $MsiPath

if ($LASTEXITCODE -ne 0) {
    # Timestamp server may be unreachable; retry without timestamp.
    Write-Warning "Timestamping failed — retrying without timestamp server..."
    & $signtool.FullName sign `
        /sha1 $cert.Thumbprint `
        /fd   SHA256 `
        /d    "MultiExplorer" `
        $MsiPath
    if ($LASTEXITCODE -ne 0) { throw "Signing failed (exit $LASTEXITCODE)." }
}

Write-Host "   Signed successfully." -ForegroundColor Green
Write-Host "`nDone. Installer: $MsiPath" -ForegroundColor Green
