$ErrorActionPreference = 'Stop'

$launcherRoot = Split-Path -Parent $PSScriptRoot
$loginServicePath = Join-Path $launcherRoot 'Services\LoginService.cs'
$source = Get-Content -LiteralPath $loginServicePath -Raw

$forbidden = @(
    'CREATE_SUSPENDED',
    'CreateRemoteThread',
    'WriteProcessMemory',
    'VirtualAllocEx',
    'OpenProcess',
    'ResumeThread'
)

$found = @($forbidden | Where-Object { $source -match [regex]::Escape($_) })
if ($found.Count -gt 0) {
    throw "Launcher still contains remote-injection primitives: $($found -join ', ')"
}

if ($source -notmatch 'new\s+ProcessStartInfo') {
    throw 'Launcher must start MapleStory through a normal ProcessStartInfo path.'
}

if ($source -notmatch 'Process\.Start\s*\(') {
    throw 'Launcher must start MapleStory through Process.Start.'
}

if ($source -notmatch 'UseShellExecute\s*=\s*true') {
    throw 'Launcher must let Windows ShellExecute elevate the requireAdministrator game.'
}

if ($source -notmatch 'Verb\s*=\s*"runas"') {
    throw 'Launcher must request the standard Windows UAC flow for MapleStory.'
}

if ($source -match 'startInfo\.EnvironmentVariables') {
    throw 'An elevated ShellExecute launch cannot mutate ProcessStartInfo.EnvironmentVariables.'
}

Write-Host 'PASS: launcher uses a normal process start and contains no remote-injection primitives.'
