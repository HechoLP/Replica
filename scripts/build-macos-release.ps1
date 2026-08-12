[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(alpha|beta|rc)\.(0|[1-9]\d*))?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidateSet('arm64', 'x64')]
    [string] $Architecture,

    [Parameter()]
    [string] $OutputDirectory = 'artifacts/macos-release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (!$IsMacOS) {
    throw 'macOS release packages must be built on macOS.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$resolvedOutput = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
if (!$resolvedOutput.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) {
    throw 'The macOS output directory must stay beneath the repository artifacts directory.'
}

$runtimeIdentifier = "osx-$Architecture"
$workRoot = Join-Path $artifactRoot "macos-$Architecture"
$publishDirectory = Join-Path $workRoot 'publish'
$bundleRoot = Join-Path $workRoot 'bundle'
$appBundle = Join-Path $bundleRoot 'Replica.app'
$contents = Join-Path $appBundle 'Contents'
$macOsDirectory = Join-Path $contents 'MacOS'
$resourcesDirectory = Join-Path $contents 'Resources'

foreach ($directory in @($workRoot, $resolvedOutput)) {
    $resolvedDirectory = [IO.Path]::GetFullPath($directory)
    if (!$resolvedDirectory.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) {
        throw 'Refusing to clean a directory outside artifacts.'
    }
    if (Test-Path -LiteralPath $resolvedDirectory) {
        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedDirectory | Out-Null
}

& dotnet publish (Join-Path $repositoryRoot 'src/Replica.Mac/Replica.Mac.csproj') `
    -c Release `
    -r $runtimeIdentifier `
    --self-contained true `
    -o $publishDirectory `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for $runtimeIdentifier."
}

$publishedExecutable = Join-Path $publishDirectory 'Replica'
if (!(Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw 'The published macOS executable is missing.'
}

New-Item -ItemType Directory -Path $macOsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $resourcesDirectory -Force | Out-Null
Copy-Item -LiteralPath $publishedExecutable -Destination (Join-Path $macOsDirectory 'Replica')

$versionParts = $Version.Split('-', 2)[0].Split('.')
$bundleVersion = "$($versionParts[0]).$($versionParts[1]).$($versionParts[2])"
$plistTemplate = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer/macos/Info.plist') -Raw
$plist = $plistTemplate.Replace('__SHORT_VERSION__', $Version.Split('-', 2)[0]).Replace('__BUNDLE_VERSION__', $bundleVersion)
[IO.File]::WriteAllText(
    (Join-Path $contents 'Info.plist'),
    $plist,
    [Text.UTF8Encoding]::new($false))

& chmod +x (Join-Path $macOsDirectory 'Replica')
if ($LASTEXITCODE -ne 0) {
    throw 'The Replica executable could not be marked executable.'
}

# This is an ad-hoc integrity signature, not an Apple Developer ID signature or notarization.
& codesign --force --deep --sign '-' $appBundle
if ($LASTEXITCODE -ne 0) {
    throw 'Ad-hoc signing of Replica.app failed.'
}
& codesign --verify --deep --strict $appBundle
if ($LASTEXITCODE -ne 0) {
    throw 'Replica.app failed code-signature verification.'
}

& ln -s /Applications (Join-Path $bundleRoot 'Applications')
if ($LASTEXITCODE -ne 0) {
    throw 'The Applications link could not be created.'
}

$dmgName = "Replica-macOS-$Architecture.dmg"
$dmgPath = Join-Path $resolvedOutput $dmgName
& hdiutil create -volname 'Replica' -srcfolder $bundleRoot -ov -format UDZO $dmgPath
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $dmgPath -PathType Leaf)) {
    throw 'DMG creation failed.'
}

$hash = (Get-FileHash -LiteralPath $dmgPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    "$dmgPath.sha256",
    "$hash  $dmgName`n",
    [Text.UTF8Encoding]::new($false))

Write-Host "Created $dmgName ($hash). The app is ad-hoc signed and not notarized."
