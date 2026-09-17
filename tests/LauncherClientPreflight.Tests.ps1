$ErrorActionPreference = 'Stop'

$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\Services\LoginService.cs')
$requiredRuntime = @(
    'BlackCipher\BlackCall64.aes',
    'BlackCipher\BlackCipher64.aes',
    'BlackCipher\BlackXchg.aes',
    'BlackCipher\config.bc',
    'BlackCipher\CrashReporter_64.dll'
)

foreach ($relativePath in $requiredRuntime) {
    if ($source -notmatch [regex]::Escape($relativePath)) {
        throw "Launcher preflight does not require $relativePath"
    }
}

Write-Host 'PASS: launcher preflight requires the complete NGS runtime before starting the game.'
