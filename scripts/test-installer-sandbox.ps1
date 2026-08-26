[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $Version = '0.2.0-alpha.2',

    [Parameter()]
    [ValidateRange(2, 30)]
    [int] $TimeoutMinutes = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$resultDirectory = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'sandbox-installer-test'))
$expectedPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (!$resultDirectory.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Sandbox result directory must remain beneath artifacts.'
}

$sandboxCommand = Get-Command WindowsSandbox.exe -ErrorAction SilentlyContinue
if ($null -eq $sandboxCommand) {
    throw 'Windows Sandbox is not available on this computer.'
}
$sandboxExecutable = $sandboxCommand.Source

$installerName = "ReplicaSetup-$Version.exe"
$installerPath = Join-Path $artifactsRoot "release\$installerName"
$checksumPath = "$installerPath.sha256"
if (!(Test-Path -LiteralPath $installerPath -PathType Leaf) -or
    !(Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    throw "Build the $Version installer and checksum before starting the Sandbox test."
}

if (Test-Path -LiteralPath $resultDirectory) {
    Remove-Item -LiteralPath $resultDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null

$runnerPath = Join-Path $resultDirectory 'run-installer-smoke.ps1'
$statusPath = Join-Path $resultDirectory 'status.txt'
$logPath = Join-Path $resultDirectory 'installer-smoke.log'
$configurationPath = Join-Path $resultDirectory 'ReplicaInstallerSmoke.wsb'
$sandboxInstaller = "C:\ReplicaSource\artifacts\release\$installerName"
$sandboxChecksum = "$sandboxInstaller.sha256"

$runner = @"
`$ErrorActionPreference = 'Continue'
`$testArguments = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\ReplicaSource\scripts\test-installer.ps1',
    '-InstallerPath', '$sandboxInstaller',
    '-Version', '$Version',
    '-ChecksumPath', '$sandboxChecksum',
    '-Install')
`$output = & powershell.exe @testArguments 2>&1
`$exitCode = `$LASTEXITCODE
`$output | Out-File -LiteralPath 'C:\ReplicaResults\installer-smoke.log' -Encoding utf8
if (`$exitCode -eq 0) {
    'PASS' | Set-Content -LiteralPath 'C:\ReplicaResults\status.txt' -Encoding ascii
}
else {
    "FAIL:`$exitCode" | Set-Content -LiteralPath 'C:\ReplicaResults\status.txt' -Encoding ascii
}
Start-Sleep -Seconds 2
Stop-Computer -Force
"@
[IO.File]::WriteAllText($runnerPath, $runner, [Text.UTF8Encoding]::new($false))

$escapedRepository = [Security.SecurityElement]::Escape($repositoryRoot)
$escapedResults = [Security.SecurityElement]::Escape($resultDirectory)
$configuration = @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$escapedRepository</HostFolder>
      <SandboxFolder>C:\ReplicaSource</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$escapedResults</HostFolder>
      <SandboxFolder>C:\ReplicaResults</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MemoryInMB>4096</MemoryInMB>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\ReplicaResults\run-installer-smoke.ps1"</Command>
  </LogonCommand>
</Configuration>
"@
[IO.File]::WriteAllText(
    $configurationPath,
    $configuration,
    [Text.UTF8Encoding]::new($false))

$sandboxStart = @{
    FilePath = $sandboxExecutable
    ArgumentList = ('"{0}"' -f $configurationPath)
    PassThru = $true
    WindowStyle = 'Hidden'
}
$sandboxProcess = Start-Process @sandboxStart
$deadline = [DateTimeOffset]::UtcNow.AddMinutes($TimeoutMinutes)
try {
    while (!(Test-Path -LiteralPath $statusPath -PathType Leaf)) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Windows Sandbox installer verification exceeded $TimeoutMinutes minutes."
        }
        Start-Sleep -Seconds 2
    }

    $status = (Get-Content -Raw -Encoding ascii -LiteralPath $statusPath).Trim()
    if ($status -ne 'PASS') {
        throw "Windows Sandbox installer verification failed with status $status. See $logPath."
    }

    Write-Host 'Windows Sandbox install, launch, association, upgrade, uninstall, and retention checks passed.'
    Write-Host "Sandbox log: $logPath"
}
finally {
    if (!$sandboxProcess.HasExited) {
        if (!$sandboxProcess.WaitForExit(30000)) {
            Stop-Process -Id $sandboxProcess.Id -Force
        }
    }
}
