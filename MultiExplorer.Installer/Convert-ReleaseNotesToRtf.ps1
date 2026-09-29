#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedInputPath = [IO.Path]::GetFullPath($InputPath)
$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $resolvedInputPath -PathType Leaf)) {
    throw "Release-notes source was not found: $resolvedInputPath"
}

$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutputPath)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw "Release-notes output path has no parent directory: $resolvedOutputPath"
}
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$plainText = [IO.File]::ReadAllText($resolvedInputPath)

# The source file wraps long bullet text onto indented physical lines so it is
# pleasant to edit in a text editor. Rich Edit treats each physical line as a
# paragraph, which produces large gaps. Join those continuation lines and let
# the installer control perform its own width-aware wrapping. Also collapse
# repeated blank lines to one deliberate paragraph break.
$logicalLines = [Collections.Generic.List[string]]::new()
foreach ($sourceLine in [Text.RegularExpressions.Regex]::Split(
    $plainText,
    "\r?\n")) {
    if (($sourceLine -match '^\s{2,}\S') -and
        ($logicalLines.Count -gt 0) -and
        (-not [string]::IsNullOrWhiteSpace(
            $logicalLines[$logicalLines.Count - 1]))) {
        $lastLineIndex = $logicalLines.Count - 1
        $logicalLines[$lastLineIndex] =
            $logicalLines[$lastLineIndex].TrimEnd() +
            " " +
            $sourceLine.Trim()
        continue
    }

    if ([string]::IsNullOrWhiteSpace($sourceLine)) {
        if (($logicalLines.Count -gt 0) -and
            (-not [string]::IsNullOrWhiteSpace(
                $logicalLines[$logicalLines.Count - 1]))) {
            $logicalLines.Add("")
        }
        continue
    }

    $logicalLines.Add($sourceLine.TrimEnd())
}
while (($logicalLines.Count -gt 0) -and
    [string]::IsNullOrWhiteSpace(
        $logicalLines[$logicalLines.Count - 1])) {
    $logicalLines.RemoveAt($logicalLines.Count - 1)
}

$normalizedText = [string]::Join("`n", $logicalLines)
$rtfBody = [Text.StringBuilder]::new($normalizedText.Length + 512)
:CharacterLoop foreach ($character in $normalizedText.ToCharArray()) {
    switch ([int]$character) {
        9 {
            $null = $rtfBody.Append('\tab ')
            continue CharacterLoop
        }
        10 {
            $null = $rtfBody.Append('\par ')
            continue CharacterLoop
        }
        13 {
            continue CharacterLoop
        }
        92 {
            $null = $rtfBody.Append('\\')
            continue CharacterLoop
        }
        123 {
            $null = $rtfBody.Append('\{')
            continue CharacterLoop
        }
        125 {
            $null = $rtfBody.Append('\}')
            continue CharacterLoop
        }
    }

    $codePoint = [int]$character
    if ($codePoint -ge 32 -and $codePoint -le 126) {
        $null = $rtfBody.Append($character)
        continue CharacterLoop
    }

    $signedCodePoint = if ($codePoint -gt 32767) {
        $codePoint - 65536
    }
    else {
        $codePoint
    }
    $null = $rtfBody.Append("\u${signedCodePoint}?")
}

$rtf = "{\rtf1\ansi\ansicpg1252\deff0" `
    + "{\fonttbl{\f0\fswiss Segoe UI;}}" `
    + "\viewkind4\uc1\pard\f0\fs18\sa0\sb0\sl240\slmult1 " `
    + $rtfBody `
    + "}"
[IO.File]::WriteAllText(
    $resolvedOutputPath,
    $rtf,
    [Text.Encoding]::ASCII)

Write-Host "Generated installer release notes: $resolvedOutputPath"
