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

    $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github\workflows\release.yml') -Raw
    $windowsBuild = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts\build-release.ps1') -Raw
    $windowsTest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts\test-installer.ps1') -Raw
    $macBuild = Get-Content -LiteralPath (Join-Path $repositoryRoot 'scripts\build-macos-release.ps1') -Raw
    $installer = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\Replica.iss') -Raw
    $releaseContracts = @{
        'release workflow Windows stable signing gate' = @($workflow, 'A Stable release requires the protected Windows Authenticode signing identity.')
        'release workflow Apple stable signing gate' = @($workflow, 'A Stable release requires Apple Developer ID signing and notarization credentials.', 'REPLICA_APPLE_DEVELOPER_ID_CERTIFICATE_SHA256', 'public certificate pin')
        'release authorization uses protected default-branch dispatch and immutable downstream commit' = @($workflow, 'workflow_dispatch:', 'github.event.repository.default_branch', '-RequiredAncestorRef', 'git check-ref-format --branch', 'merge-base --is-ancestor', 'commit_sha: ${{ steps.version.outputs.commit_sha }}', 'ref: ${{ needs.build-windows.outputs.commit_sha }}', 'environment: release-signing', 'environment: release-publishing')
        'release signing credentials are isolated on fresh runners' = @($workflow, 'Sign and package Windows on an isolated runner', 'Sign and notarize macOS (${{ matrix.architecture }}) on an isolated runner', 'Prepare self-contained Windows application without signing credentials', 'Prepare self-contained macOS application without signing credentials', 'UsePreparedPublish = $true', 'prepared-release.json', 'replicaExeSha256', 'replicaExecutableSha256')
        'release workflow secret cleanup proves absence' = @($workflow, 'REPLICA_SIGNING_CERTIFICATE_THUMBPRINT', 'REPLICA_SIGNING_PFX_PATH', 'REPLICA_APPLE_KEYCHAIN_PATH', 'prove absence', 'remained after cleanup')
        'Inno compiler artifact is authenticated before signing' = @($workflow, 'REPLICA_INNO_SETUP_TOOL_TREE_SHA256', 'REPLICA_INNO_SETUP_PUBLISHER_CERTIFICATE_SHA256', 'get-directory-tree-sha256.ps1', 'Inno Setup tool tree does not match', 'before exposing signing capability')
        'Windows publisher certificate binding' = @($windowsBuild, 'ReplicaPublisherCertificateSha256', 'Get-CertificateSha256', 'UsePreparedPublish', 'prepared-release.json', 'reviewed digest manifest')
        'Windows installer signer verification' = @($windowsTest, 'ExpectedSignerCertificateSha256')
        'Inno Setup executable and uninstaller signing' = @($installer, 'SignTool=replica', 'SignedUninstaller=yes')
        'macOS Developer ID hardened-runtime signing' = @($macBuild, 'codesign --force --deep --options runtime --timestamp')
        'macOS notarization and stapling' = @($macBuild, 'notarytool', 'stapler validate', 'NotaryKeychainPath', 'UsePreparedPublish', 'prepared-release.json', 'reviewed digest manifest')
    }
    foreach ($contract in $releaseContracts.GetEnumerator()) {
        $content = $contract.Value[0]
        foreach ($marker in $contract.Value[1..($contract.Value.Count - 1)]) {
            if ($content.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) {
                throw "Release contract '$($contract.Key)' is missing '$marker'."
            }
        }
    }

    Write-Host 'Release version, trust, prerelease, and workflow syntax checks passed.'
}
finally {
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
