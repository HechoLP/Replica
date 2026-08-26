[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(alpha|beta|rc)\.(0|[1-9]\d*))?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidateSet('arm64', 'x64')]
    [string] $Architecture,

    [Parameter()]
    [string] $OutputDirectory = 'artifacts/macos-release',

    [Parameter()]
    [string] $SigningIdentity,

    [Parameter()]
    [string] $NotaryKeychainProfile,

    [Parameter()]
    [string] $NotaryKeychainPath,

    [Parameter()]
    [switch] $UsePreparedPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (!$IsMacOS) {
    throw 'macOS release packages must be built on macOS.'
}
if (![string]::IsNullOrWhiteSpace($NotaryKeychainProfile) -and
    [string]::IsNullOrWhiteSpace($SigningIdentity)) {
    throw 'Notarization requires an Apple Developer ID Application signing identity.'
}
if (![string]::IsNullOrWhiteSpace($NotaryKeychainPath) -and
    [string]::IsNullOrWhiteSpace($NotaryKeychainProfile)) {
    throw 'A notarization keychain path requires a notarization profile.'
}
if (![string]::IsNullOrWhiteSpace($NotaryKeychainPath)) {
    $NotaryKeychainPath = [IO.Path]::GetFullPath($NotaryKeychainPath)
    if (!(Test-Path -LiteralPath $NotaryKeychainPath -PathType Leaf)) {
        throw "The notarization keychain does not exist: $NotaryKeychainPath"
    }
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
$expectedExecutableArchitecture = if ($Architecture -eq 'arm64') { 'arm64' } else { 'x86_64' }

function Assert-BundleContract {
    param(
        [Parameter(Mandatory)]
        [string] $BundlePath
    )

    $bundleContents = Join-Path $BundlePath 'Contents'
    $bundleExecutable = Join-Path $bundleContents 'MacOS/Replica'
    $bundlePlist = Join-Path $bundleContents 'Info.plist'
    if (!(Test-Path -LiteralPath $bundleExecutable -PathType Leaf) -or
        !(Test-Path -LiteralPath $bundlePlist -PathType Leaf)) {
        throw 'The application bundle contract is incomplete.'
    }

    & /bin/test -x $bundleExecutable
    if ($LASTEXITCODE -ne 0) {
        throw 'The bundled Replica executable is not executable.'
    }
    & /usr/bin/lipo $bundleExecutable -verify_arch $expectedExecutableArchitecture
    if ($LASTEXITCODE -ne 0) {
        throw "The bundled executable does not contain the required $expectedExecutableArchitecture architecture."
    }
    $architectures = (& /usr/bin/lipo $bundleExecutable -archs).Trim()
    if ($LASTEXITCODE -ne 0 -or $architectures -ne $expectedExecutableArchitecture) {
        throw "The bundled executable architecture is '$architectures', expected exactly '$expectedExecutableArchitecture'."
    }

    & /usr/bin/plutil -lint $bundlePlist | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Info.plist validation failed.'
    }
    $expectedShortVersion = $Version.Split('-', 2)[0]
    $expectedValues = @{
        'CFBundleIdentifier' = 'com.hecholp.replica'
        'CFBundleExecutable' = 'Replica'
        'CFBundlePackageType' = 'APPL'
        'CFBundleShortVersionString' = $expectedShortVersion
        'CFBundleVersion' = $bundleVersion
        'LSMinimumSystemVersion' = '13.0'
    }
    foreach ($key in $expectedValues.Keys) {
        $actual = (& /usr/libexec/PlistBuddy -c "Print :$key" $bundlePlist).Trim()
        if ($LASTEXITCODE -ne 0 -or $actual -ne $expectedValues[$key]) {
            throw "Info.plist value '$key' is '$actual', expected '$($expectedValues[$key])'."
        }
    }

    & /usr/bin/codesign --verify --deep --strict $BundlePath
    if ($LASTEXITCODE -ne 0) {
        throw 'Replica.app failed code-signature verification.'
    }
}

foreach ($directory in @($resolvedOutput)) {
    $resolvedDirectory = [IO.Path]::GetFullPath($directory)
    if (!$resolvedDirectory.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) {
        throw 'Refusing to clean a directory outside artifacts.'
    }
    if (Test-Path -LiteralPath $resolvedDirectory) {
        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedDirectory | Out-Null
}

if (!$UsePreparedPublish) {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $workRoot | Out-Null
}
elseif (!(Test-Path -LiteralPath (Join-Path $publishDirectory 'Replica') -PathType Leaf)) {
    throw 'The prepared macOS publish output is missing Replica.'
}

if (Test-Path -LiteralPath $bundleRoot) {
    Remove-Item -LiteralPath $bundleRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $bundleRoot | Out-Null

if (!$UsePreparedPublish) {
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
}

$publishedExecutable = Join-Path $publishDirectory 'Replica'
if (!(Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw 'The published macOS executable is missing.'
}
if ($UsePreparedPublish) {
    $preparedManifestPath = Join-Path $publishDirectory 'prepared-release.json'
    if (!(Test-Path -LiteralPath $preparedManifestPath -PathType Leaf)) {
        throw 'The credential-free macOS publish manifest is missing.'
    }
    $preparedManifest = Get-Content -LiteralPath $preparedManifestPath -Raw | ConvertFrom-Json
    $preparedHash = (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash
    if ($preparedManifest.version -ne $Version -or
        $preparedManifest.architecture -ne $Architecture -or
        $preparedManifest.replicaExecutableSha256 -ne $preparedHash) {
        throw 'The prepared macOS application does not match its reviewed digest manifest.'
    }
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

$isDeveloperIdSigned = ![string]::IsNullOrWhiteSpace($SigningIdentity)
if ($isDeveloperIdSigned) {
    & codesign --force --deep --options runtime --timestamp --sign $SigningIdentity $appBundle
    if ($LASTEXITCODE -ne 0) {
        throw 'Developer ID signing of Replica.app failed.'
    }
}
else {
    # Pull-request and prerelease builds may use an ad-hoc integrity signature.
    & codesign --force --deep --sign '-' $appBundle
    if ($LASTEXITCODE -ne 0) {
        throw 'Ad-hoc signing of Replica.app failed.'
    }
}
& codesign --verify --deep --strict $appBundle
if ($LASTEXITCODE -ne 0) {
    throw 'Replica.app failed code-signature verification.'
}
if ($isDeveloperIdSigned) {
    $signatureDetails = (& codesign --display --verbose=4 $appBundle 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or
        $signatureDetails -notmatch 'Authority=Developer ID Application:') {
        throw 'Replica.app does not carry the expected Developer ID Application authority.'
    }
}
Assert-BundleContract -BundlePath $appBundle

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

& hdiutil verify $dmgPath | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw 'DMG verification failed.'
}

if ($isDeveloperIdSigned) {
    & codesign --force --timestamp --sign $SigningIdentity $dmgPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Developer ID signing of the DMG failed.'
    }
    & codesign --verify --strict $dmgPath
    if ($LASTEXITCODE -ne 0) {
        throw 'The signed DMG failed code-signature verification.'
    }
}

$isNotarized = ![string]::IsNullOrWhiteSpace($NotaryKeychainProfile)
if ($isNotarized) {
    $notaryArguments = @(
        'notarytool', 'submit', $dmgPath,
        '--keychain-profile', $NotaryKeychainProfile,
        '--wait',
        '--output-format', 'json')
    if (![string]::IsNullOrWhiteSpace($NotaryKeychainPath)) {
        $notaryArguments += @('--keychain', $NotaryKeychainPath)
    }
    $notaryJson = (& xcrun @notaryArguments) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw 'Apple notarization submission failed.'
    }
    $notaryResult = $notaryJson | ConvertFrom-Json
    if ($notaryResult.status -ne 'Accepted') {
        throw "Apple notarization was not accepted. Status: $($notaryResult.status)"
    }
    & xcrun stapler staple $dmgPath
    if ($LASTEXITCODE -ne 0) {
        throw 'The notarization ticket could not be stapled to the DMG.'
    }
    & xcrun stapler validate $dmgPath
    if ($LASTEXITCODE -ne 0) {
        throw 'The stapled notarization ticket failed validation.'
    }
}

$mountPath = Join-Path $workRoot 'mounted-dmg'
New-Item -ItemType Directory -Path $mountPath | Out-Null
$mounted = $false
try {
    & hdiutil attach -readonly -nobrowse -mountpoint $mountPath $dmgPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'The DMG could not be mounted read-only for verification.'
    }
    $mounted = $true
    Assert-BundleContract -BundlePath (Join-Path $mountPath 'Replica.app')
    $applicationsLink = Get-Item -LiteralPath (Join-Path $mountPath 'Applications') -Force
    $linkTargets = @($applicationsLink.Target)
    if ($applicationsLink.LinkType -ne 'SymbolicLink' -or
        $linkTargets.Count -ne 1 -or
        $linkTargets[0] -ne '/Applications') {
        throw 'The DMG Applications link is invalid.'
    }
}
finally {
    if ($mounted) {
        & hdiutil detach $mountPath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw 'The verified DMG could not be detached.'
        }
    }
}

$hash = (Get-FileHash -LiteralPath $dmgPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    "$dmgPath.sha256",
    "$hash  $dmgName`n",
    [Text.UTF8Encoding]::new($false))

$workRootPrefix = [IO.Path]::GetFullPath($workRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (!$resolvedOutput.StartsWith($workRootPrefix, [StringComparison]::Ordinal) -and
    (Test-Path -LiteralPath $workRoot)) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}

$trustDescription = if ($isNotarized) {
    'Developer ID signed and notarized'
}
elseif ($isDeveloperIdSigned) {
    'Developer ID signed but not notarized'
}
else {
    'ad-hoc signed and not notarized'
}
Write-Host "Created $dmgName ($hash). The package is $trustDescription."
