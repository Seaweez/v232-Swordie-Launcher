$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '../Services/ClientLanguageService.cs')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('clover-language-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $service = [v232.Launcher.WPF.Services.ClientLanguageService]
    $language = [v232.Launcher.WPF.Services.ClientLanguage]
    if ($service::Load($testRoot) -ne $language::EN) { throw 'Missing preference must default to EN' }
    $service::PrepareLaunch($testRoot)
    if (-not (Test-Path (Join-Path $testRoot 'clover-diagnostics.ini'))) { throw 'Launch must materialize the default' }
    $service::Save($testRoot, $language::TH)
    if ($service::Load($testRoot) -ne $language::TH) { throw 'TH persistence failed' }
    $service::Save($testRoot, $language::EN)
    if ($service::Load($testRoot) -ne $language::EN) { throw 'EN persistence failed' }
    $rejected = $false
    try { $service::Save($testRoot, [v232.Launcher.WPF.Services.ClientLanguage]99) } catch { $rejected = $true }
    if (-not $rejected) { throw 'Unknown language must be rejected' }
    'PASS: EN default, launch materialization, TH/EN persistence, invalid mode rejection'
} finally {
    if ((Split-Path $testRoot -Leaf) -notmatch '^clover-language-test-[a-f0-9]{32}$') { throw 'Unexpected test directory' }
    Remove-Item -LiteralPath (Join-Path $testRoot 'clover-diagnostics.ini') -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testRoot
}
