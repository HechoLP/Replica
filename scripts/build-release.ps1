[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $Version = '0.1.0-alpha.1',

    [Parameter()]
    [string] $DotNetPath,

    [Parameter()]
    [string] $IsccPath,

    [Parameter()]
    [string] $SigningCertificateThumbprint,

    [Parameter()]
    [string] $PublisherCertificateSha256,

    [Parameter()]
    [string] $SignToolPath,

    [Parameter()]
    [ValidatePattern('^https://')]
    [string] $TimestampUrl = 'https://timestamp.digicert.com',

    [Parameter()]
    [switch] $RunInstallTests,

    [Parameter()]
    [switch] $UsePreparedPublish,

    [Parameter()]
    [switch] $SkipVerification
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath"
    }
}

function Remove-ReleaseDirectory {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $ArtifactsRoot
    )

    $resolvedArtifacts = [IO.Path]::GetFullPath($ArtifactsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolvedTarget = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (!$resolvedTarget.StartsWith("$resolvedArtifacts$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside the artifacts root: $resolvedTarget"
    }

    if (Test-Path -LiteralPath $resolvedTarget) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}

function Get-NormalizedSha256 {
    param(
        [Parameter(Mandatory)]
        [string] $Value
    )

    $normalized = $Value.Replace(':', '').Replace(' ', '').ToUpperInvariant()
    if ($normalized -notmatch '^[0-9A-F]{64}$') {
        throw 'Publisher certificate SHA-256 must contain exactly 64 hexadecimal characters.'
    }
    return $normalized
}

function Get-CertificateSha256 {
    param(
        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.X509Certificate2] $Certificate
    )

    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($Certificate.RawData))
}

function Find-SignTool {
    param(
        [Parameter()]
        [string] $RequestedPath
    )

    if (![string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)
        if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "signtool.exe was not found at $resolved"
        }
        return $resolved
    }

    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Filter 'signtool.exe' -File -Recurse |
            Where-Object { $_.DirectoryName.EndsWith('\x64', [StringComparison]::OrdinalIgnoreCase) } |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    throw 'signtool.exe was not found. Install the Windows SDK or pass -SignToolPath.'
}

if ($Version -notmatch '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "Version must be a supported semantic version, for example 1.2.3 or 1.2.3-beta.1."
}

$numericParts = @([int] $Matches.major, [int] $Matches.minor, [int] $Matches.patch)
if ($numericParts.Where({ $_ -gt 65535 }).Count -ne 0) {
    throw 'Version components must be no greater than 65535 for Windows file metadata.'
}

$fileVersion = "$($numericParts[0]).$($numericParts[1]).$($numericParts[2]).0"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'Replica.sln'
$appProject = Join-Path $repositoryRoot 'src\Replica.App\Replica.App.csproj'
$installerScript = Join-Path $repositoryRoot 'installer\Replica.iss'
$installerTestScript = Join-Path $repositoryRoot 'scripts\test-installer.ps1'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$publishDirectory = Join-Path $artifactsRoot 'publish\win-x64'
$releaseDirectory = Join-Path $artifactsRoot 'release'
$testResultsDirectory = Join-Path $artifactsRoot 'test-results'
$normalizedPublisherHashes = @()
if (![string]::IsNullOrWhiteSpace($PublisherCertificateSha256)) {
    $normalizedPublisherHashes = @($PublisherCertificateSha256.Split(
            @(',', ';'),
            [StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { Get-NormalizedSha256 $_.Trim() } |
        Select-Object -Unique)
}

$normalizedSigningThumbprint = if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    ''
}
else {
    $SigningCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
}
$signingCertificate = $null
$signingCertificateSha256 = $null
if (![string]::IsNullOrWhiteSpace($normalizedSigningThumbprint)) {
    if ($normalizedSigningThumbprint -notmatch '^[0-9A-F]{40,128}$') {
        throw 'Signing certificate thumbprint is invalid.'
    }

    $certificatePath = "Cert:\CurrentUser\My\$normalizedSigningThumbprint"
    if (!(Test-Path -LiteralPath $certificatePath)) {
        throw 'The requested signing certificate is not installed for the current user.'
    }

    $signingCertificate = Get-Item -LiteralPath $certificatePath
    if (!$signingCertificate.HasPrivateKey) {
        throw 'The requested signing certificate has no accessible private key.'
    }

    $signingCertificateSha256 = Get-CertificateSha256 $signingCertificate
    if ($normalizedPublisherHashes.Count -eq 0) {
        $normalizedPublisherHashes = @($signingCertificateSha256)
    }
    elseif ($normalizedPublisherHashes -notcontains $signingCertificateSha256) {
        throw 'The update publisher policy does not contain the selected signing certificate.'
    }

    $SignToolPath = Find-SignTool $SignToolPath
}

$publisherProperty = $normalizedPublisherHashes -join ','

if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $repositoryDotNet = Join-Path $repositoryRoot '.dotnet\dotnet.exe'
    $DotNetPath = if (Test-Path -LiteralPath $repositoryDotNet -PathType Leaf) {
        $repositoryDotNet
    }
    else {
        'dotnet'
    }
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $isccCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $isccCandidates.Where({ Test-Path -LiteralPath $_ }) | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($IsccPath) -or !(Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 6 compiler was not found. Install JRSoftware.InnoSetup or pass -IsccPath.'
}

Push-Location $repositoryRoot
try {
    if (!$UsePreparedPublish) {
        Remove-ReleaseDirectory -Path $publishDirectory -ArtifactsRoot $artifactsRoot
    }
    Remove-ReleaseDirectory -Path $releaseDirectory -ArtifactsRoot $artifactsRoot
    Remove-ReleaseDirectory -Path $testResultsDirectory -ArtifactsRoot $artifactsRoot
    New-Item -ItemType Directory -Path $publishDirectory, $releaseDirectory, $testResultsDirectory -Force | Out-Null

    if (!$SkipVerification) {
        $buildArguments = @('build', $solutionPath, '-c', 'Release', '--no-restore')
        if (![string]::IsNullOrWhiteSpace($publisherProperty)) {
            $buildArguments += "-p:ReplicaPublisherCertificateSha256=$publisherProperty"
        }
        Invoke-CheckedCommand $DotNetPath @('clean', $solutionPath, '-c', 'Release', '--nologo')
        Invoke-CheckedCommand $DotNetPath @('restore', $solutionPath)
        Invoke-CheckedCommand $DotNetPath @('format', $solutionPath, '--verify-no-changes', '--no-restore')
        Invoke-CheckedCommand $DotNetPath $buildArguments
        Invoke-CheckedCommand $DotNetPath @(
            'test', $solutionPath, '-c', 'Release', '--no-build',
            '--logger', 'trx;LogFilePrefix=replica', '--results-directory', $testResultsDirectory)
    }
    if (!$UsePreparedPublish) {
        $publishArguments = @(
            'publish', $appProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '--no-restore', '-o', $publishDirectory,
            "-p:Version=$Version", "-p:FileVersion=$fileVersion", "-p:AssemblyVersion=$fileVersion",
            "-p:InformationalVersion=$Version", '-p:PublishSingleFile=true', '-p:PublishTrimmed=false',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:IncludeSourceRevisionInInformationalVersion=false')
        if (![string]::IsNullOrWhiteSpace($publisherProperty)) {
            $publishArguments += "-p:ReplicaPublisherCertificateSha256=$publisherProperty"
        }
        Invoke-CheckedCommand $DotNetPath $publishArguments
    }

    $publishedExecutable = Join-Path $publishDirectory 'Replica.exe'
    if (!(Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
        throw 'Publish validation failed: Replica.exe is missing.'
    }

    $publishedExecutables = @(Get-ChildItem -LiteralPath $publishDirectory -Filter '*.exe' -File)
    $publishedLibraries = @(Get-ChildItem -LiteralPath $publishDirectory -Filter '*.dll' -File)
    if ($publishedExecutables.Count -ne 1 -or $publishedExecutables[0].Name -ne 'Replica.exe' -or $publishedLibraries.Count -ne 0) {
        throw 'Publish validation failed: the self-contained output must expose only Replica.exe.'
    }

    if ($UsePreparedPublish) {
        $preparedManifestPath = Join-Path $publishDirectory 'prepared-release.json'
        if (!(Test-Path -LiteralPath $preparedManifestPath -PathType Leaf)) {
            throw 'The credential-free Windows publish manifest is missing.'
        }
        $preparedManifest = Get-Content -LiteralPath $preparedManifestPath -Raw | ConvertFrom-Json
        $preparedHash = (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash
        if ($preparedManifest.version -ne $Version -or
            $preparedManifest.publisherCertificateSha256 -ne $publisherProperty -or
            $preparedManifest.replicaExeSha256 -ne $preparedHash) {
            throw 'The prepared Windows application does not match its reviewed digest manifest.'
        }
    }

    if ($null -ne $signingCertificate) {
        Invoke-CheckedCommand $SignToolPath @(
            'sign', '/sha1', $normalizedSigningThumbprint,
            '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256',
            $publishedExecutable)
        $applicationSignature = Get-AuthenticodeSignature -LiteralPath $publishedExecutable
        if ($applicationSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
            $null -eq $applicationSignature.SignerCertificate -or
            (Get-CertificateSha256 $applicationSignature.SignerCertificate) -ne $signingCertificateSha256) {
            throw 'Replica.exe Authenticode verification failed after signing.'
        }
    }

    $isccArguments = @(
        "/DReplicaVersion=$Version",
        "/DReplicaFileVersion=$fileVersion",
        "/DPublishDirectory=$publishDirectory",
        "/DOutputDirectory=$releaseDirectory",
        $installerScript)
    if ($null -ne $signingCertificate) {
        $isccArguments = @(
            '/DReplicaSigningEnabled=1',
            "/Sreplica=`"$SignToolPath`" sign /sha1 $normalizedSigningThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 `$f"
        ) + $isccArguments
    }
    Invoke-CheckedCommand $IsccPath $isccArguments

    $installerName = "ReplicaSetup-$Version.exe"
    $installerPath = Join-Path $releaseDirectory $installerName
    if (!(Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "Installer validation failed: $installerName is missing."
    }

    $installerSignature = Get-AuthenticodeSignature -LiteralPath $installerPath
    if ($null -ne $signingCertificate) {
        if ($installerSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
            $null -eq $installerSignature.SignerCertificate -or
            (Get-CertificateSha256 $installerSignature.SignerCertificate) -ne $signingCertificateSha256) {
            throw 'Installer Authenticode verification failed after signing.'
        }
    }
    elseif ($installerSignature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
        throw "Unsigned build produced an unexpected Authenticode state: $($installerSignature.Status)"
    }

    $hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = "$installerPath.sha256"
    [IO.File]::WriteAllText(
        $checksumPath,
        "$hash  $installerName`n",
        [Text.UTF8Encoding]::new($false))

    $testArguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installerTestScript,
        '-InstallerPath', $installerPath,
        '-Version', $Version,
        '-ChecksumPath', $checksumPath)
    if ($RunInstallTests) {
        $testArguments += '-Install'
    }
    if ($null -ne $signingCertificate) {
        $testArguments += @('-ExpectedSignerCertificateSha256', $signingCertificateSha256)
    }
    Invoke-CheckedCommand 'powershell.exe' $testArguments

    $releaseFiles = @(Get-ChildItem -LiteralPath $releaseDirectory -File)
    if ($releaseFiles.Count -ne 2) {
        throw 'Release validation failed: artifacts/release must contain only the installer and SHA-256 file.'
    }

    Write-Host "Release package created: $installerPath"
    Write-Host "SHA-256: $hash"
    if ($null -ne $signingCertificate) {
        Write-Host "Authenticode publisher certificate SHA-256: $signingCertificateSha256"
    }
    if (!$RunInstallTests) {
        Write-Warning 'Install/upgrade/uninstall smoke tests were not run. Use -RunInstallTests on a clean Windows test account or VM.'
    }
}
finally {
    Pop-Location
}
