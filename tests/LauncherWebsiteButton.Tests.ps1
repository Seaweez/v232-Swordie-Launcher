$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$xaml = Get-Content -LiteralPath (Join-Path $repoRoot 'MainWindow.xaml') -Raw -Encoding UTF8
$code = Get-Content -LiteralPath (Join-Path $repoRoot 'MainWindow.xaml.cs') -Raw -Encoding UTF8

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}

Assert-True ($xaml -match 'x:Name="WebsiteButton"') 'Launcher must expose a WebsiteButton.'
Assert-True ($xaml -match 'Text="🌐"') 'Website button must use a globe icon.'
Assert-True ($xaml -match 'Text="Website"') 'Website button must be labelled Website.'
Assert-True ($code -match 'WebsiteButton_Click') 'Website click handler must exist.'
Assert-True ($code -match 'FileName\s*=\s*"https://mstory-x\.com"') 'Website button must open the website homepage.'
Assert-True ($xaml -notmatch 'DiscordButton') 'Launcher XAML must not retain the Discord button name.'
Assert-True ($code -notmatch 'discord\.gg|discord\.com/invite') 'Launcher code must not open Discord directly.'

Write-Output 'Launcher website button checks passed.'
