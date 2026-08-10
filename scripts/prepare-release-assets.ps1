[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(alpha|beta|rc)\.(0|[1-9]\d*))?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $ReleaseDirectory,

    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedReleaseDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $resolvedOutputDirectory) {
    if ((Get-Item -LiteralPath $resolvedOutputDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Release Asset output directory cannot be a reparse point.'
    }
    if (@(Get-ChildItem -LiteralPath $resolvedOutputDirectory -Force).Count -ne 0) {
        throw 'Release Asset output directory must be empty.'
    }
}
else {
    New-Item -ItemType Directory -Path $resolvedOutputDirectory | Out-Null
}

$versionedName = "ReplicaSetup-$Version.exe"
$versionedInstaller = Join-Path $resolvedReleaseDirectory $versionedName
$versionedChecksum = "$versionedInstaller.sha256"
if (!(Test-Path -LiteralPath $versionedInstaller -PathType Leaf) -or
    !(Test-Path -LiteralPath $versionedChecksum -PathType Leaf)) {
    throw 'Versioned installer or checksum is missing.'
}

$checksumLine = (Get-Content -LiteralPath $versionedChecksum -Raw).Trim()
if ($checksumLine -notmatch "^(?<hash>[0-9a-fA-F]{64})  $([Regex]::Escape($versionedName))$") {
    throw 'Versioned installer checksum file has an invalid format or filename.'
}

$actualHash = (Get-FileHash -LiteralPath $versionedInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
if (!$actualHash.Equals($Matches.hash, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Versioned installer SHA-256 verification failed.'
}

$canonicalInstaller = Join-Path $resolvedOutputDirectory 'ReplicaSetup.exe'
$canonicalChecksum = Join-Path $resolvedOutputDirectory 'ReplicaSetup.exe.sha256'
$artifactInstaller = Join-Path $resolvedOutputDirectory $versionedName
$artifactChecksum = "$artifactInstaller.sha256"
Copy-Item -LiteralPath $versionedInstaller -Destination $canonicalInstaller
Copy-Item -LiteralPath $versionedInstaller -Destination $artifactInstaller
Copy-Item -LiteralPath $versionedChecksum -Destination $artifactChecksum
[IO.File]::WriteAllText(
    $canonicalChecksum,
    "$actualHash  ReplicaSetup.exe`n",
    [Text.UTF8Encoding]::new($false))

if ((Get-FileHash -LiteralPath $canonicalInstaller -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $artifactInstaller -Algorithm SHA256).Hash) {
    throw 'Canonical and versioned installers do not contain identical bytes.'
}

Write-Host "Prepared canonical and versioned release Assets with SHA-256 $actualHash"
