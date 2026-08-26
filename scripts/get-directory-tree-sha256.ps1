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

[string[]] $entries = Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File |
    ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($resolvedRoot, $_.FullName).Replace('\', '/')
        "$relative=$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    }
[Array]::Sort($entries, [StringComparer]::Ordinal)
$bytes = [Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))
[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
