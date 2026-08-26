[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $RootPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedRoot = (Resolve-Path -LiteralPath $RootPath).Path
if (!(Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
    throw 'The tree-digest root must be an existing directory.'
}

if (Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force |
    Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
    throw 'The tree-digest root contains a reparse point.'
}

$rootPrefix = $resolvedRoot
$directorySeparator = [IO.Path]::DirectorySeparatorChar
if (!$rootPrefix.EndsWith($directorySeparator.ToString(), [StringComparison]::Ordinal)) {
    $rootPrefix += $directorySeparator
}
$pathComparison = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    [StringComparison]::OrdinalIgnoreCase
}
else {
    [StringComparison]::Ordinal
}

[string[]] $entries = Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File |
    ForEach-Object {
        $fullName = [IO.Path]::GetFullPath($_.FullName)
        if (!$fullName.StartsWith($rootPrefix, $pathComparison)) {
            throw 'The tree-digest input escaped the resolved root.'
        }
        $relative = $fullName.Substring($rootPrefix.Length).Replace($directorySeparator, [char] '/')
        "$relative=$((Get-FileHash -LiteralPath $fullName -Algorithm SHA256).Hash)"
    }
[Array]::Sort($entries, [StringComparer]::Ordinal)
$bytes = [Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    -join ($sha256.ComputeHash($bytes) | ForEach-Object { $_.ToString('X2') })
}
finally {
    $sha256.Dispose()
}
