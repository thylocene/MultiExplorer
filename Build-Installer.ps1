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

.PARAMETER SkipMsiValidation
    Skip WiX ICE validation and administrative-install payload validation. Use only
    when the build environment cannot access Windows Installer validation APIs;
    normal release builds should validate.

.PARAMETER Force
    When used with -SkipPublish, bypasses the staleness check and proceeds
    even if source files are newer than the published exe.

.EXAMPLE
    .\Build-Installer.ps1
    .\Build-Installer.ps1 -Version 1.4.7
    .\Build-Installer.ps1 -SkipPublish -SkipSigning
    .\Build-Installer.ps1 -SkipPublish -Force
#>
param(
    [string]$Version,
    [string]$BuildDate  = (Get-Date -Format "yyyy-MM-dd"),
    [switch]$SkipPublish,
    [switch]$SkipSigning,
    [switch]$SkipMsiValidation,
    [switch]$Force        # bypass the staleness check when -SkipPublish is set
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$Root             = $PSScriptRoot
$MainCsproj       = Join-Path $Root "MultiExplorer.csproj"
$BuildProps       = Join-Path $Root "Directory.Build.props"
$InstallerProject = Join-Path $Root "MultiExplorer.Installer\MultiExplorer.Installer.wixproj"
$IdentityBuildScript = Join-Path $Root "MultiExplorer.Identity\Build-IdentityPackage.ps1"
$MsiPath          = Join-Path $Root "MultiExplorer.Installer\bin\Release\en-US\MultiExplorer-Setup.msi"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath $BuildProps
    $Version = [string]$props.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "No Version was supplied and Directory.Build.props does not define one."
    }
}

$parsedVersion = $null
if (-not [Version]::TryParse($Version, [ref]$parsedVersion)) {
    throw "Version '$Version' is not a valid numeric product version."
}

# Windows Installer normally retains an existing versioned file when a rebuilt
# package supplies the same file version. Stamp release binaries with a monotonic
# build identity while leaving ProductVersion (and the About dialog) unchanged.
# Each field remains inside the Windows four-part file-version limit (0..65535).
$buildTimestampUtc = [DateTime]::UtcNow
$fileVersionEpoch = [DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
$fileVersionDay = [int][Math]::Floor(($buildTimestampUtc.Date - $fileVersionEpoch).TotalDays)
$fileVersionTick = [int][Math]::Floor($buildTimestampUtc.TimeOfDay.TotalSeconds / 2)
if ($fileVersionDay -gt 65535) {
    throw "The generated binary file-version day exceeds the Windows limit."
}
$BinaryFileVersion = "$fileVersionDay.$fileVersionTick.$($parsedVersion.Major).$($parsedVersion.Minor)"
# AppX stages packages by their four-part identity version. Reusing Version.0 for
# rebuilt same-version installers leaves old manifests and visual assets cached.
# Keep the displayed product version unchanged while giving the sparse identity a
# monotonically increasing internal version.
$IdentityPackageVersion = "$($parsedVersion.Major).$($parsedVersion.Minor).$fileVersionDay.$fileVersionTick"

$VersionedMsiPath = Join-Path (Split-Path -Parent $MsiPath) "MultiExplorer-Setup-$Version.msi"

function Publish-VersionedInstaller {
    Copy-Item -LiteralPath $MsiPath -Destination $VersionedMsiPath -Force
    Write-Host "   Versioned copy: $VersionedMsiPath" -ForegroundColor Green
}

Write-Host "`n>> Package metadata" -ForegroundColor Cyan
Write-Host "   Version    : $Version"
Write-Host "   Build date : $BuildDate"
Write-Host "   File version: $BinaryFileVersion"
Write-Host "   Identity version: $IdentityPackageVersion"

# ── 1. Publish the application ────────────────────────────────────────────────
$PublishDir = Join-Path $Root "bin\Release\net8.0-windows10.0.18362.0\win-x64\publish"
$PublishExe = Join-Path $PublishDir "MultiExplorer.exe"

if (-not $SkipPublish) {
    Write-Host "`n>> Publishing MultiExplorer..." -ForegroundColor Cyan

    # Start from an empty, validated publish directory so WiX cannot harvest stale
    # files left by an older packaging layout or a diagnostic run.
    $publishFull = [IO.Path]::GetFullPath($PublishDir)
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $Root "bin\Release\net8.0-windows10.0.18362.0\win-x64"))
    if ([IO.Path]::GetDirectoryName($publishFull) -ne $expectedParent) {
        throw "Refusing to clean unexpected publish directory: $publishFull"
    }
    if (Test-Path -LiteralPath $publishFull) {
        Remove-Item -LiteralPath $publishFull -Recurse -Force
    }

    dotnet publish $MainCsproj -c Release "-p:Version=$Version" "-p:FileVersion=$BinaryFileVersion"
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

    $newerFile = Get-ChildItem $Root -Recurse -Include "*.cs","*.csproj","*.props","*.html" -ErrorAction SilentlyContinue |
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

# Build the small sparse package that gives the installed Win32 application an
# identity. Windows requires that identity for Pin to Start secondary tiles.
$IdentityPackagePath = Join-Path $PublishDir "MultiExplorer.Identity.msix"
Write-Host "`n>> Building Start-menu identity package..." -ForegroundColor Cyan
& $IdentityBuildScript -Version $IdentityPackageVersion -OutputPath $IdentityPackagePath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $IdentityPackagePath)) {
    throw "The MultiExplorer identity package could not be built."
}

# ── 2. Build the MSI ──────────────────────────────────────────────────────────
Write-Host "`n>> Building installer (WiX 4)..." -ForegroundColor Cyan
Write-Host "   (First run downloads WixToolset.Sdk from NuGet — may take a moment)"
# --no-incremental forces WiX to repackage the MSI from the freshly-published exe
# rather than reusing a cached MSI from a prior build.
$installerBuildArgs = @(
    "build",
    $InstallerProject,
    "-c", "Release",
    "--no-incremental",
    "-p:Version=$Version",
    "-p:IdentityPackageVersion=$IdentityPackageVersion",
    "-p:BuildDate=$BuildDate",
    "-p:SkipAppPublish=true"
)
if ($SkipMsiValidation) {
    $installerBuildArgs += "-p:SuppressValidation=true"
    Write-Warning "Skipping WiX ICE validation because -SkipMsiValidation was specified."
}
& dotnet @installerBuildArgs
if ($LASTEXITCODE -ne 0) { throw "WiX build failed (exit $LASTEXITCODE)." }

if (-not (Test-Path $MsiPath)) {
    throw "Expected MSI not found: $MsiPath"
}
Write-Host "   Built: $MsiPath" -ForegroundColor Green

if ($SkipMsiValidation) {
    Write-Warning "Skipping administrative-install payload validation because -SkipMsiValidation was specified."
}
else {
    # Validate what WiX actually put in the MSI. Checking timestamps or the publish
    # directory alone cannot detect an incrementally reused cabinet containing an
    # older executable.
    Write-Host "`n>> Validating installer payload..." -ForegroundColor Cyan
    $validationDir = Join-Path $Root "MultiExplorer.Installer\obj\PayloadValidation"
    $validationFull = [IO.Path]::GetFullPath($validationDir)
    $expectedValidationParent = [IO.Path]::GetFullPath((Join-Path $Root "MultiExplorer.Installer\obj"))
    if ([IO.Path]::GetDirectoryName($validationFull) -ne $expectedValidationParent) {
        throw "Refusing to clean unexpected validation directory: $validationFull"
    }
    if (Test-Path -LiteralPath $validationFull) {
        Remove-Item -LiteralPath $validationFull -Recurse -Force
    }
    New-Item -ItemType Directory -Path $validationFull | Out-Null

    $msiexec = Join-Path $env:SystemRoot "System32\msiexec.exe"
    $validationProcess = Start-Process -FilePath $msiexec -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
        "/a", "`"$MsiPath`"", "/qn", "TARGETDIR=`"$validationFull`""
    )
    if ($validationProcess.ExitCode -ne 0) {
        throw "MSI payload extraction failed (msiexec exit $($validationProcess.ExitCode))."
    }

    $packagedExe = Get-ChildItem -LiteralPath $validationFull -Recurse -Filter "MultiExplorer.exe" -File |
        Select-Object -First 1
    if (-not $packagedExe) {
        throw "The built MSI does not contain MultiExplorer.exe."
    }

    $packagedRoot = $packagedExe.Directory.FullName
    $publishedFiles = Get-ChildItem -LiteralPath $PublishDir -Recurse -File
    foreach ($publishedFile in $publishedFiles) {
        $relativePath = $publishedFile.FullName.Substring($PublishDir.Length).TrimStart('\')
        $packagedPath = Join-Path $packagedRoot $relativePath
        if (-not (Test-Path -LiteralPath $packagedPath -PathType Leaf)) {
            throw "Installer payload validation failed: missing published file '$relativePath'."
        }

        $publishedHash = (Get-FileHash -LiteralPath $publishedFile.FullName -Algorithm SHA256).Hash
        $packagedHash = (Get-FileHash -LiteralPath $packagedPath -Algorithm SHA256).Hash
        if ($publishedHash -ne $packagedHash) {
            throw @"
Installer payload validation failed for '$relativePath':
  Published SHA-256: $publishedHash
  Packaged SHA-256 : $packagedHash
"@
        }
    }
    Write-Host "   All $($publishedFiles.Count) embedded application files match the fresh publish." -ForegroundColor Green
    Remove-Item -LiteralPath $validationFull -Recurse -Force
}

if ($SkipSigning) {
    Publish-VersionedInstaller
    Write-Host "`nDone (signing skipped)." -ForegroundColor Green
    Write-Host "Installer: $VersionedMsiPath"
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
    Publish-VersionedInstaller
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
Publish-VersionedInstaller
Write-Host "`nDone. Installer: $VersionedMsiPath" -ForegroundColor Green
