[CmdletBinding()]
param(
    [Parameter()]
    [string] $ActionlintPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$validator = Join-Path $PSScriptRoot 'validate-release-version.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "Replica.ReleaseTests.$([Guid]::NewGuid().ToString('N'))"

function Test-VersionCase {
    param(
        [Parameter(Mandatory)]
        [string] $Version,

        [Parameter(Mandatory)]
        [bool] $ExpectedPrerelease
    )

    $caseRoot = Join-Path $temporaryRoot ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $caseRoot | Out-Null
    $parts = $Version.Split('-', 2)
    $suffixElement = if ($parts.Count -eq 2) { "<VersionSuffix>$($parts[1])</VersionSuffix>" } else { '' }
    $properties = "<Project><PropertyGroup><VersionPrefix>$($parts[0])</VersionPrefix>$suffixElement</PropertyGroup></Project>"
    [IO.File]::WriteAllText(
        (Join-Path $caseRoot 'Directory.Build.props'),
        $properties,
        [Text.UTF8Encoding]::new($false))
    $outputPath = Join-Path $caseRoot 'output.txt'

    & $validator -Tag "v$Version" -RepositoryRoot $caseRoot -GitHubOutputPath $outputPath | Out-Null
    $outputs = Get-Content -LiteralPath $outputPath
    if ($outputs -notcontains "version=$Version" -or
        $outputs -notcontains "prerelease=$($ExpectedPrerelease.ToString().ToLowerInvariant())") {
        throw "Version validation output was incorrect for $Version"
    }
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    Test-VersionCase -Version '1.2.3' -ExpectedPrerelease $false
    Test-VersionCase -Version '1.2.3-alpha.1' -ExpectedPrerelease $true
    Test-VersionCase -Version '1.2.3-beta.2' -ExpectedPrerelease $true
    Test-VersionCase -Version '1.2.3-rc.3' -ExpectedPrerelease $true

    $mismatchRoot = Join-Path $temporaryRoot 'mismatch'
    New-Item -ItemType Directory -Path $mismatchRoot | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $mismatchRoot 'Directory.Build.props'),
        '<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix></PropertyGroup></Project>',
        [Text.UTF8Encoding]::new($false))
    $mismatchAccepted = $false
    try {
        & $validator -Tag 'v1.2.4' -RepositoryRoot $mismatchRoot | Out-Null
        $mismatchAccepted = $true
    }
    catch {
    }
    if ($mismatchAccepted) {
        throw 'A tag that did not match the project version was accepted.'
    }

    $invalidAccepted = $false
    try {
        & $validator -Tag 'release-1.2.3' -RepositoryRoot $temporaryRoot | Out-Null
        $invalidAccepted = $true
    }
    catch {
    }
    if ($invalidAccepted) {
        throw 'Invalid release tag was accepted.'
    }

    if (![string]::IsNullOrWhiteSpace($ActionlintPath)) {
        $workflowFiles = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot '.github\workflows') -Filter '*.yml' -File |
            Select-Object -ExpandProperty FullName)
        & $ActionlintPath @workflowFiles
        if ($LASTEXITCODE -ne 0) {
            throw 'actionlint reported invalid GitHub Actions workflow syntax.'
        }
    }

    Write-Host 'Release version, prerelease, and workflow syntax checks passed.'
}
finally {
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
