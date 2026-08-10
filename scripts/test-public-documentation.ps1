[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$excludedDirectories = @('.git', '.dotnet', 'artifacts', 'TestResults', 'bin', 'obj')
$requiredCommunityFiles = @(
    'README.md',
    'CONTRIBUTING.md',
    'SECURITY.md',
    'CODE_OF_CONDUCT.md',
    'CHANGELOG.md',
    '.github/PULL_REQUEST_TEMPLATE.md',
    '.github/ISSUE_TEMPLATE/bug_report.yml',
    '.github/ISSUE_TEMPLATE/feature_request.yml',
    '.github/ISSUE_TEMPLATE/plugin_request.yml'
)

foreach ($relativePath in $requiredCommunityFiles) {
    $path = Join-Path $repositoryRoot $relativePath
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required public repository document is missing: $relativePath"
    }
}

$markdownFiles = @(Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File -Filter '*.md' |
    Where-Object {
        $relative = $_.FullName.Substring($repositoryRoot.Length).TrimStart(
            [IO.Path]::DirectorySeparatorChar)
        $segments = $relative.Split([IO.Path]::DirectorySeparatorChar)
        !($segments | Where-Object { $excludedDirectories -contains $_ })
    })
$markdownLinkPattern = [regex]'(?<!!)\[[^\]]+\]\((?<target>[^)]+)\)'
$brokenLinks = [Collections.Generic.List[string]]::new()

foreach ($markdownFile in $markdownFiles) {
    $content = Get-Content -Raw -Encoding utf8 -LiteralPath $markdownFile.FullName
    foreach ($match in $markdownLinkPattern.Matches($content)) {
        $target = $match.Groups['target'].Value.Trim()
        if ($target.StartsWith('<') -and $target.EndsWith('>')) {
            $target = $target.Substring(1, $target.Length - 2)
        }

        if ($target -match '^(https?://|mailto:|#)') {
            continue
        }

        $pathPart = ($target -split '#', 2)[0]
        $decodedPath = [Uri]::UnescapeDataString($pathPart)
        $resolvedPath = [IO.Path]::GetFullPath((Join-Path $markdownFile.DirectoryName $decodedPath))
        if (!(Test-Path -LiteralPath $resolvedPath)) {
            $relativeFile = $markdownFile.FullName.Substring($repositoryRoot.Length).TrimStart(
                [IO.Path]::DirectorySeparatorChar)
            $brokenLinks.Add("$relativeFile -> $target")
        }
    }
}

if ($brokenLinks.Count -gt 0) {
    throw "Broken local Markdown links:`n$($brokenLinks -join "`n")"
}

$readme = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $repositoryRoot 'README.md')
$requiredReadmeText = @(
    'Clone your Windows setup, not your files.',
    'Replica is a Windows environment migration and recovery tool that restores only what is missing or different.',
    'https://github.com/HechoLP/Replica',
    'https://github.com/HechoLP/Replica/releases',
    'ReplicaSetup.exe',
    'Source code (zip)',
    'Stable',
    'Alpha',
    'Beta',
    'Safe',
    'Recommended',
    'Exact',
    'No license has been selected'
)

foreach ($requiredText in $requiredReadmeText) {
    if ($readme.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
        throw "README is missing required public-release text: $requiredText"
    }
}

$badgeWorkflowMatch = [regex]::Match(
    $readme,
    'actions/workflows/(?<workflow>[^/\s)]+)\.yml/badge\.svg')
if (!$badgeWorkflowMatch.Success) {
    throw 'README must contain a workflow badge tied to an existing workflow file.'
}

$badgeWorkflow = "$($badgeWorkflowMatch.Groups['workflow'].Value).yml"
$badgeWorkflowPath = Join-Path $repositoryRoot ".github/workflows/$badgeWorkflow"
if (!(Test-Path -LiteralPath $badgeWorkflowPath -PathType Leaf)) {
    throw "README badge references a missing workflow: $badgeWorkflow"
}

$expectedWorkflowNames = @{
    'ci.yml' = 'CI'
    'codeql.yml' = 'CodeQL'
    'release.yml' = 'Release'
}
foreach ($entry in $expectedWorkflowNames.GetEnumerator()) {
    $workflowPath = Join-Path $repositoryRoot ".github/workflows/$($entry.Key)"
    $workflowContent = Get-Content -Raw -Encoding utf8 -LiteralPath $workflowPath
    if ($workflowContent -notmatch "(?m)^name:\s+$([regex]::Escape($entry.Value))\s*$") {
        throw "Workflow $($entry.Key) does not use the documented name $($entry.Value)."
    }
}

$issueForms = @{
    '.github/ISSUE_TEMPLATE/bug_report.yml' = 'Bug Report'
    '.github/ISSUE_TEMPLATE/feature_request.yml' = 'Feature Request'
    '.github/ISSUE_TEMPLATE/plugin_request.yml' = 'Plugin Request'
}
foreach ($entry in $issueForms.GetEnumerator()) {
    $formPath = Join-Path $repositoryRoot $entry.Key
    $formContent = Get-Content -Raw -Encoding utf8 -LiteralPath $formPath
    if ($formContent -notmatch "(?m)^name:\s+$([regex]::Escape($entry.Value))\s*$" -or
        $formContent -notmatch '(?m)^body:\s*$') {
        throw "Issue Form $($entry.Key) is missing its expected name or body."
    }

    $ids = @([regex]::Matches($formContent, '(?m)^\s+id:\s+(?<id>[a-z0-9_]+)\s*$') |
        ForEach-Object { $_.Groups['id'].Value })
    if ($ids.Count -ne (@($ids | Sort-Object -Unique)).Count) {
        throw "Issue Form $($entry.Key) contains duplicate field identifiers."
    }
}

$sensitivePatterns = @(
    '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----',
    'gh[pousr]_[A-Za-z0-9]{20,}',
    'AKIA[0-9A-Z]{16}',
    '(?i)(password|token|secret)\s*[:=]\s*["''][^"'']{8,}["'']'
)
$publicDocumentPaths = @($requiredCommunityFiles | ForEach-Object { Join-Path $repositoryRoot $_ })
foreach ($pattern in $sensitivePatterns) {
    foreach ($path in $publicDocumentPaths) {
        $content = Get-Content -Raw -Encoding utf8 -LiteralPath $path
        if ([regex]::IsMatch($content, $pattern)) {
            $relativePath = $path.Substring($repositoryRoot.Length).TrimStart(
                [IO.Path]::DirectorySeparatorChar)
            throw "Potential sensitive value in public documentation: $relativePath"
        }
    }
}

Write-Host "Public documentation verification passed for $($markdownFiles.Count) Markdown files."
