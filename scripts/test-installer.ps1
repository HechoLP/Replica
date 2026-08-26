[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $InstallerPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $ChecksumPath,

    [Parameter()]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $ExpectedSignerCertificateSha256,

    [Parameter()]
    [switch] $Install
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList
    )

    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Process failed with exit code $($process.ExitCode): $FilePath"
    }
}

$resolvedInstaller = [IO.Path]::GetFullPath($InstallerPath)
$expectedChecksum = (Get-Content -LiteralPath $ChecksumPath -Raw).Trim().Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0]
$actualChecksum = (Get-FileHash -LiteralPath $resolvedInstaller -Algorithm SHA256).Hash
if (!$actualChecksum.Equals($expectedChecksum, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer SHA-256 verification failed.'
}

$installerInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedInstaller)
if ($installerInfo.ProductName.Trim() -ne 'Replica' -or $installerInfo.CompanyName.Trim() -ne 'HechoLP') {
    throw 'Installer Windows metadata validation failed.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $resolvedInstaller
if (![string]::IsNullOrWhiteSpace($ExpectedSignerCertificateSha256)) {
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        throw "Expected a valid Authenticode signature, but found $($signature.Status)."
    }
    $actualSignerSha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($signature.SignerCertificate.RawData))
    if (!$actualSignerSha256.Equals($ExpectedSignerCertificateSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installer signer certificate did not match the pinned publisher policy.'
    }
    Write-Host "Authenticode: signed by $($signature.SignerCertificate.Subject)"
}
elseif ($signature.Status -eq [Management.Automation.SignatureStatus]::NotSigned) {
    Write-Host 'Authenticode: UNSIGNED (expected until a production certificate is configured)'
}
else {
    throw "Installer Authenticode state is unsafe: $($signature.Status)"
}

if (!$Install) {
    Write-Host 'Installer package, metadata, and SHA-256 checks passed.'
    return
}

$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Replica'
$applicationPath = Join-Path $installDirectory 'Replica.exe'
$uninstallerPath = Join-Path $installDirectory 'unins000.exe'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'Replica'
$testDataDirectory = Join-Path $dataDirectory 'InstallerTests'
$preservationMarker = Join-Path $testDataDirectory 'preserve.marker'
$associationKey = 'HKCU:\Software\Classes\Replica.Snapshot\shell\open\command'
$installedByTest = $false

if (Test-Path -LiteralPath $applicationPath) {
    throw "Install smoke test requires a clean target. Replica is already installed at $applicationPath"
}
if (Test-Path -LiteralPath $testDataDirectory) {
    throw "Install smoke test refuses to reuse its data marker directory: $testDataDirectory"
}

try {
    Invoke-CheckedProcess $resolvedInstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-')
    $installedByTest = $true

    if (!(Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
        throw 'Install test failed: Replica.exe was not installed.'
    }

    $installedExecutables = @(Get-ChildItem -LiteralPath $installDirectory -Filter '*.exe' -File |
        Where-Object { $_.Name -notlike 'unins*.exe' })
    $installedLibraries = @(Get-ChildItem -LiteralPath $installDirectory -Filter '*.dll' -File)
    if ($installedExecutables.Count -ne 1 -or $installedExecutables[0].Name -ne 'Replica.exe' -or $installedLibraries.Count -ne 0) {
        throw 'Install test failed: Replica must be the only user-facing installed executable.'
    }

    $applicationInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($applicationPath)
    if ($applicationInfo.ProductName.Trim() -ne 'Replica' -or $applicationInfo.ProductVersion.Trim() -ne $Version) {
        throw "Installed version metadata does not match $Version."
    }

    $associationCommand = (Get-ItemProperty -LiteralPath $associationKey -Name '(default)').'(default)'
    if ($associationCommand -notlike "*Replica.exe*--open-snapshot*%1*") {
        throw 'Install test failed: .replica file association is invalid.'
    }

    New-Item -ItemType Directory -Path $testDataDirectory -Force | Out-Null
    Set-Content -LiteralPath $preservationMarker -Value 'Replica installer retention test' -Encoding UTF8

    $applicationProcess = Start-Process -FilePath $applicationPath -PassThru
    Start-Sleep -Seconds 3
    if ($applicationProcess.HasExited) {
        throw "Launch test failed with exit code $($applicationProcess.ExitCode)."
    }
    Stop-Process -Id $applicationProcess.Id -Force
    $applicationProcess.WaitForExit()

    Invoke-CheckedProcess $resolvedInstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-')
    if (!(Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
        throw 'Upgrade test failed: Replica.exe is missing after reinstall.'
    }

    Invoke-CheckedProcess $uninstallerPath @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    $installedByTest = $false
    if (Test-Path -LiteralPath $applicationPath) {
        throw 'Uninstall test failed: Replica.exe remains installed.'
    }
    if (!(Test-Path -LiteralPath $preservationMarker -PathType Leaf)) {
        throw 'Uninstall test failed: Replica user data was not preserved.'
    }

    Remove-Item -LiteralPath $preservationMarker -Force
    Remove-Item -LiteralPath $testDataDirectory -Force
    Write-Host 'Install, launch, association, upgrade, uninstall, version, and user-data retention checks passed.'
}
finally {
    if ($installedByTest -and (Test-Path -LiteralPath $uninstallerPath -PathType Leaf)) {
        Invoke-CheckedProcess $uninstallerPath @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    }
}
