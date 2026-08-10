[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Tag,

    [Parameter()]
    [string] $RepositoryRoot,

    [Parameter()]
    [string] $GitHubOutputPath,

    [Parameter()]
    [switch] $RequireGitTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Join-Path $PSScriptRoot '..'
}

$tagPattern = '^v(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<channel>alpha|beta|rc)\.(?<sequence>0|[1-9]\d*))?$'
if ($Tag -notmatch $tagPattern) {
    throw 'Release tag must use v<major>.<minor>.<patch> with an optional alpha, beta, or rc numeric suffix.'
}
$major = [int] $Matches.major
$minor = [int] $Matches.minor
$patch = [int] $Matches.patch
$channel = if ($Matches.ContainsKey('channel')) { [string] $Matches.channel } else { '' }

$resolvedRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$propsPath = Join-Path $resolvedRoot 'Directory.Build.props'
if (!(Test-Path -LiteralPath $propsPath -PathType Leaf)) {
    throw "Directory.Build.props was not found beneath $resolvedRoot"
}

[xml] $buildProperties = Get-Content -LiteralPath $propsPath -Raw
$versionPrefixNode = $buildProperties.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
$versionSuffixNode = $buildProperties.SelectSingleNode('/Project/PropertyGroup/VersionSuffix')
$versionPrefix = if ($null -eq $versionPrefixNode) { '' } else { [string] $versionPrefixNode.InnerText }
$versionSuffix = if ($null -eq $versionSuffixNode) { '' } else { [string] $versionSuffixNode.InnerText }
if ([string]::IsNullOrWhiteSpace($versionPrefix)) {
    throw 'Directory.Build.props must define VersionPrefix.'
}

$projectVersion = if ([string]::IsNullOrWhiteSpace($versionSuffix)) {
    $versionPrefix
}
else {
    "$versionPrefix-$versionSuffix"
}
$version = $Tag.Substring(1)
if (!$version.Equals($projectVersion, [StringComparison]::Ordinal)) {
    throw "Release tag version '$version' does not match project version '$projectVersion'."
}

$numericParts = @($major, $minor, $patch)
if ($numericParts.Where({ $_ -gt 65535 }).Count -ne 0) {
    throw 'Version components must be no greater than 65535 for Windows file metadata.'
}

if ($RequireGitTag) {
    & git -C $resolvedRoot show-ref --verify --quiet "refs/tags/$Tag"
    if ($LASTEXITCODE -ne 0) {
        throw "The release tag '$Tag' does not exist in the checked-out repository."
    }

    $tagObjectType = (& git -C $resolvedRoot cat-file -t "refs/tags/$Tag").Trim()
    if ($LASTEXITCODE -ne 0 -or $tagObjectType -ne 'tag') {
        throw "The release tag '$Tag' must be an annotated tag."
    }

    $tagCommit = (& git -C $resolvedRoot rev-list -n 1 $Tag).Trim()
    $headCommit = (& git -C $resolvedRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or !$tagCommit.Equals($headCommit, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The checked-out commit does not match release tag '$Tag'."
    }
}

$fileVersion = "$($numericParts[0]).$($numericParts[1]).$($numericParts[2]).0"
$isPrerelease = ![string]::IsNullOrWhiteSpace($channel)
$outputs = [ordered]@{
    tag = $Tag
    version = $version
    file_version = $fileVersion
    prerelease = $isPrerelease.ToString().ToLowerInvariant()
}

if (![string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    foreach ($output in $outputs.GetEnumerator()) {
        [IO.File]::AppendAllText(
            $GitHubOutputPath,
            "$($output.Key)=$($output.Value)`n",
            [Text.UTF8Encoding]::new($false))
    }
}

[pscustomobject] $outputs
