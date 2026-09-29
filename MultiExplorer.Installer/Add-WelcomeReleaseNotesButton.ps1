#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedMsiPath = [IO.Path]::GetFullPath($MsiPath)
if (-not (Test-Path -LiteralPath $resolvedMsiPath -PathType Leaf)) {
    throw "MSI to patch was not found: $resolvedMsiPath"
}

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember(
    "OpenDatabase",
    "InvokeMethod",
    $null,
    $installer,
    @($resolvedMsiPath, 1))

function Invoke-MsiQuery {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Query
    )

    $view = $database.GetType().InvokeMember(
        "OpenView",
        "InvokeMethod",
        $null,
        $database,
        @($Query))
    $null = $view.GetType().InvokeMember(
        "Execute",
        "InvokeMethod",
        $null,
        $view,
        $null)
}

function Test-MsiRow {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Query
    )

    $view = $database.GetType().InvokeMember(
        "OpenView",
        "InvokeMethod",
        $null,
        $database,
        @($Query))
    $null = $view.GetType().InvokeMember(
        "Execute",
        "InvokeMethod",
        $null,
        $view,
        $null)
    $record = $view.GetType().InvokeMember(
        "Fetch",
        "InvokeMethod",
        $null,
        $view,
        $null)
    return $null -ne $record
}

function Invoke-MsiParameterizedQuery {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Query,

        [Parameter(Mandatory = $true)]
        [object[]]$Fields,

        [int[]]$IntegerFields = @()
    )

    $view = $database.GetType().InvokeMember(
        "OpenView",
        "InvokeMethod",
        $null,
        $database,
        @($Query))
    $record = $installer.GetType().InvokeMember(
        "CreateRecord",
        "InvokeMethod",
        $null,
        $installer,
        $Fields.Count)

    for ($index = 1; $index -le $Fields.Count; $index++) {
        if ($IntegerFields -contains $index) {
            $null = $record.GetType().InvokeMember(
                "IntegerData",
                "SetProperty",
                $null,
                $record,
                @($index, [int]$Fields[$index - 1]))
        }
        else {
            $null = $record.GetType().InvokeMember(
                "StringData",
                "SetProperty",
                $null,
                $record,
                @($index, [string]$Fields[$index - 1]))
        }
    }

    $null = $view.GetType().InvokeMember(
        "Execute",
        "InvokeMethod",
        $null,
        $view,
        @($record))
}

if (-not (Test-MsiRow -Query "SELECT ``Dialog`` FROM ``Dialog`` WHERE ``Dialog`` = 'WelcomeDlg'")) {
    throw "The linked MSI does not contain the stock WelcomeDlg."
}
if (-not (Test-MsiRow -Query "SELECT ``Dialog`` FROM ``Dialog`` WHERE ``Dialog`` = 'ReleaseNotesDlg'")) {
    throw "The linked MSI does not contain ReleaseNotesDlg."
}

# Keep repeated incremental builds deterministic.
Invoke-MsiQuery -Query "DELETE FROM ``ControlEvent`` WHERE ``Dialog_`` = 'WelcomeDlg' AND ``Control_`` = 'ReleaseNotes'"
Invoke-MsiQuery -Query "DELETE FROM ``Control`` WHERE ``Dialog_`` = 'WelcomeDlg' AND ``Control`` = 'ReleaseNotes'"

# Preserve one complete control-navigation cycle. Windows Installer rejects a
# dialog with error 2810 when two controls point to the same next control.
# Stock: Bitmap -> Back -> Next -> Cancel -> Bitmap.
# Patched: Bitmap -> Release notes -> Back -> Next -> Cancel -> Bitmap.
Invoke-MsiQuery -Query "UPDATE ``Control`` SET ``Control_Next`` = 'Bitmap' WHERE ``Dialog_`` = 'WelcomeDlg' AND ``Control`` = 'Cancel'"
Invoke-MsiQuery -Query "UPDATE ``Control`` SET ``Control_Next`` = 'ReleaseNotes' WHERE ``Dialog_`` = 'WelcomeDlg' AND ``Control`` = 'Bitmap'"
Invoke-MsiParameterizedQuery `
    -Query "INSERT INTO ``Control`` (``Dialog_``, ``Control``, ``Type``, ``X``, ``Y``, ``Width``, ``Height``, ``Attributes``, ``Property``, ``Text``, ``Control_Next``, ``Help``) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)" `
    -Fields @('WelcomeDlg', 'ReleaseNotes', 'PushButton', 135, 158, 80, 17, 3, '', 'Release notes...', 'Back', '') `
    -IntegerFields @(4, 5, 6, 7, 8)
Invoke-MsiParameterizedQuery `
    -Query "INSERT INTO ``ControlEvent`` (``Dialog_``, ``Control_``, ``Event``, ``Argument``, ``Condition``, ``Ordering``) VALUES (?, ?, ?, ?, ?, ?)" `
    -Fields @('WelcomeDlg', 'ReleaseNotes', 'SpawnDialog', 'ReleaseNotesDlg', '1', 1) `
    -IntegerFields @(6)

$database.GetType().InvokeMember(
    "Commit",
    "InvokeMethod",
    $null,
    $database,
    $null)

if (-not (Test-MsiRow -Query "SELECT ``Control`` FROM ``Control`` WHERE ``Dialog_`` = 'WelcomeDlg' AND ``Control`` = 'ReleaseNotes'")) {
    throw "The Release notes button was not written to WelcomeDlg."
}
if (-not (Test-MsiRow -Query "SELECT ``Event`` FROM ``ControlEvent`` WHERE ``Dialog_`` = 'WelcomeDlg' AND ``Control_`` = 'ReleaseNotes' AND ``Event`` = 'SpawnDialog' AND ``Argument`` = 'ReleaseNotesDlg'")) {
    throw "The Release notes button event was not written to the MSI."
}
if (-not (Test-MsiRow -Query "SELECT ``Control`` FROM ``Control`` WHERE ``Dialog_`` = 'WelcomeDlg' AND ``Control`` = 'Bitmap' AND ``Control_Next`` = 'ReleaseNotes'")) {
    throw "WelcomeDlg does not link Bitmap to the Release notes button."
}
if (-not (Test-MsiRow -Query "SELECT ``Control`` FROM ``Control`` WHERE ``Dialog_`` = 'WelcomeDlg' AND ``Control`` = 'ReleaseNotes' AND ``Control_Next`` = 'Back'")) {
    throw "WelcomeDlg does not link the Release notes button to Back."
}
if (-not (Test-MsiRow -Query "SELECT ``Control`` FROM ``Control`` WHERE ``Dialog_`` = 'WelcomeDlg' AND ``Control`` = 'Cancel' AND ``Control_Next`` = 'Bitmap'")) {
    throw "WelcomeDlg does not retain the Cancel-to-Bitmap tab-order link."
}

Write-Host "Added the Release notes button to WelcomeDlg: $resolvedMsiPath"
