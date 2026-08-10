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
    [switch] $RunInstallTests,

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
    Remove-ReleaseDirectory -Path $publishDirectory -ArtifactsRoot $artifactsRoot
    Remove-ReleaseDirectory -Path $releaseDirectory -ArtifactsRoot $artifactsRoot
    Remove-ReleaseDirectory -Path $testResultsDirectory -ArtifactsRoot $artifactsRoot
    New-Item -ItemType Directory -Path $publishDirectory, $releaseDirectory, $testResultsDirectory -Force | Out-Null

    if (!$SkipVerification) {
        Invoke-CheckedCommand $DotNetPath @('clean', $solutionPath, '-c', 'Release', '--nologo')
        Invoke-CheckedCommand $DotNetPath @('restore', $solutionPath)
        Invoke-CheckedCommand $DotNetPath @('format', $solutionPath, '--verify-no-changes', '--no-restore')
        Invoke-CheckedCommand $DotNetPath @('build', $solutionPath, '-c', 'Release', '--no-restore')
        Invoke-CheckedCommand $DotNetPath @(
            'test', $solutionPath, '-c', 'Release', '--no-build',
            '--logger', 'trx;LogFilePrefix=replica', '--results-directory', $testResultsDirectory)
    }
    Invoke-CheckedCommand $DotNetPath @(
        'publish', $appProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '--no-restore', '-o', $publishDirectory,
        "-p:Version=$Version", "-p:FileVersion=$fileVersion", "-p:AssemblyVersion=$fileVersion",
        "-p:InformationalVersion=$Version", '-p:PublishSingleFile=true', '-p:PublishTrimmed=false',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:IncludeSourceRevisionInInformationalVersion=false')

    $publishedExecutable = Join-Path $publishDirectory 'Replica.exe'
    if (!(Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
        throw 'Publish validation failed: Replica.exe is missing.'
    }

    $publishedExecutables = @(Get-ChildItem -LiteralPath $publishDirectory -Filter '*.exe' -File)
    $publishedLibraries = @(Get-ChildItem -LiteralPath $publishDirectory -Filter '*.dll' -File)
    if ($publishedExecutables.Count -ne 1 -or $publishedExecutables[0].Name -ne 'Replica.exe' -or $publishedLibraries.Count -ne 0) {
        throw 'Publish validation failed: the self-contained output must expose only Replica.exe.'
    }

    Invoke-CheckedCommand $IsccPath @(
        "/DReplicaVersion=$Version",
        "/DReplicaFileVersion=$fileVersion",
        "/DPublishDirectory=$publishDirectory",
        "/DOutputDirectory=$releaseDirectory",
        $installerScript)

    $installerName = "ReplicaSetup-$Version.exe"
    $installerPath = Join-Path $releaseDirectory $installerName
    if (!(Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "Installer validation failed: $installerName is missing."
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
    Invoke-CheckedCommand 'powershell.exe' $testArguments

    $releaseFiles = @(Get-ChildItem -LiteralPath $releaseDirectory -File)
    if ($releaseFiles.Count -ne 2) {
        throw 'Release validation failed: artifacts/release must contain only the installer and SHA-256 file.'
    }

    Write-Host "Release package created: $installerPath"
    Write-Host "SHA-256: $hash"
    if (!$RunInstallTests) {
        Write-Warning 'Install/upgrade/uninstall smoke tests were not run. Use -RunInstallTests on a clean Windows test account or VM.'
    }
}
finally {
    Pop-Location
}
