<#
  Prepares a fresh robot machine (for example a Test Cloud VM pool machine) to run the Selenium suites.
  Called by Framework\RunSeleniumScenario.cs when it can't find a prepared suite. Safe to run again: each step
  is skipped when its result is already there.

    1. Downloads the Selenium suites from GitHub into -Root (no git needed).
    2. Installs portable toolchains into -Tools, for the current user only: no admin rights, no winget.
       JDK 21 (Temurin) + Maven, .NET 8 SDK, Python 3.12, Node.js LTS, Ruby 3.3 + bundler.
    3. Checks that Microsoft Edge is installed (the suites drive Edge).
  Writes <Tools>\ready.txt when everything is in place. run-scenario.ps1 picks the tools up through the
  SELENIUM_TOOLS environment variable.
#>
param(
    [string]$Root  = "$env:USERPROFILE\Documents\UiPath\Testing_OpenMRS",
    [string]$Tools = "$env:LOCALAPPDATA\UiPathSeleniumTools",
    [string]$RepoZip = 'https://github.com/vikramparolkar-uipath/Testing_OpenMRS/archive/refs/heads/main.zip'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'          # Invoke-WebRequest is very slow with the progress bar on
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
function Log($m) { Write-Host "[bootstrap $(Get-Date -Format HH:mm:ss)] $m" }
function Get-File($url, $out) { Log "download $url"; Invoke-WebRequest $url -OutFile $out -UseBasicParsing }

New-Item -ItemType Directory -Force $Tools | Out-Null
$dl = Join-Path $Tools '_downloads'; New-Item -ItemType Directory -Force $dl | Out-Null

# Expand a zip whose content sits in one top-level folder, and put that folder at $dest
function Install-Zip($url, $file, $dest) {
    if (Test-Path $dest) { return }
    $zip = Join-Path $dl $file; Get-File $url $zip
    $x = Join-Path $dl ("x_" + (Split-Path $dest -Leaf)); Remove-Item -Recurse -Force $x -ErrorAction SilentlyContinue
    Expand-Archive $zip $x
    $inner = @(Get-ChildItem $x -Directory)
    if ($inner.Count -eq 1) { Move-Item $inner[0].FullName $dest } else { Move-Item $x $dest }
    Remove-Item -Recurse -Force $x, $zip -ErrorAction SilentlyContinue
    Log "installed $(Split-Path $dest -Leaf)"
}

# 1. The Selenium suites
if (-not (Test-Path (Join-Path $Root 'SeleniumSuite\run-scenario.ps1'))) {
    $zip = Join-Path $dl 'repo.zip'; Get-File $RepoZip $zip
    $x = Join-Path $dl 'repo'; Remove-Item -Recurse -Force $x -ErrorAction SilentlyContinue
    Expand-Archive $zip $x
    $src = (Get-ChildItem $x -Directory | Select-Object -First 1).FullName
    New-Item -ItemType Directory -Force $Root | Out-Null
    Copy-Item (Join-Path $src '*') $Root -Recurse -Force
    Remove-Item -Recurse -Force $x, $zip -ErrorAction SilentlyContinue
    Set-Content (Join-Path $Root '.bootstrapped') "Downloaded from $RepoZip on $(Get-Date -Format s)"
    Log "Selenium suites in $Root"
} else { Log "Selenium suites already in $Root" }

# 2. Portable toolchains
Install-Zip 'https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jdk/hotspot/normal/eclipse' 'jdk21.zip' (Join-Path $Tools 'jdk')
Install-Zip 'https://archive.apache.org/dist/maven/maven-3/3.9.9/binaries/apache-maven-3.9.9-bin.zip' 'maven.zip' (Join-Path $Tools 'maven')

$node = Join-Path $Tools 'node'
if (-not (Test-Path $node)) {
    $ver = 'v22.12.0'
    try { $ver = (Invoke-RestMethod 'https://nodejs.org/dist/index.json' -UseBasicParsing | Where-Object { $_.lts } | Select-Object -First 1).version } catch { }
    Install-Zip "https://nodejs.org/dist/$ver/node-$ver-win-x64.zip" 'node.zip' $node
}

$dotnet = Join-Path $Tools 'dotnet'
if (-not (Test-Path (Join-Path $dotnet 'dotnet.exe'))) {
    $ps1 = Join-Path $dl 'dotnet-install.ps1'; Get-File 'https://dot.net/v1/dotnet-install.ps1' $ps1
    & powershell -NoProfile -ExecutionPolicy Bypass -File $ps1 -Channel 8.0 -InstallDir $dotnet -NoPath | Out-Null
    if (-not (Test-Path (Join-Path $dotnet 'dotnet.exe'))) { throw '.NET 8 SDK install failed' }
    Log 'installed dotnet'
}

$python = Join-Path $Tools 'python'
if (-not (Test-Path (Join-Path $python 'python.exe'))) {
    $exe = Join-Path $dl 'python.exe'; Get-File 'https://www.python.org/ftp/python/3.12.8/python-3.12.8-amd64.exe' $exe
    $p = Start-Process $exe -Wait -PassThru -ArgumentList '/quiet', 'InstallAllUsers=0', "TargetDir=$python", 'Include_launcher=0',
        'PrependPath=0', 'Include_test=0', 'Shortcuts=0', 'AssociateFiles=0'
    if (-not (Test-Path (Join-Path $python 'python.exe'))) { throw "Python install failed (exit $($p.ExitCode))" }
    Log 'installed python'
}

$ruby = Join-Path $Tools 'ruby'
if (-not (Test-Path (Join-Path $ruby 'bin\ruby.exe'))) {
    $url = 'https://github.com/oneclick/rubyinstaller2/releases/download/RubyInstaller-3.3.14-1/rubyinstaller-3.3.14-1-x64.exe'
    try {
        $rel = Invoke-RestMethod 'https://api.github.com/repos/oneclick/rubyinstaller2/releases?per_page=20' -UseBasicParsing
        $hit = $rel | ForEach-Object { $_.assets } | Where-Object { $_.name -match '^rubyinstaller-3\.3\.[\d-]+-x64\.exe$' } | Select-Object -First 1
        if ($hit) { $url = $hit.browser_download_url }
    } catch { }
    $exe = Join-Path $dl 'ruby.exe'; Get-File $url $exe
    $p = Start-Process $exe -Wait -PassThru -ArgumentList '/verysilent', '/currentuser', "/dir=$ruby", '/mergetasks=!assocfiles,!modpath,!ridkinstall'
    if (-not (Test-Path (Join-Path $ruby 'bin\ruby.exe'))) { throw "Ruby install failed (exit $($p.ExitCode))" }
    Log 'installed ruby'
}
if (-not (Test-Path (Join-Path $ruby 'bin\bundle.bat')) -and -not (Test-Path (Join-Path $ruby 'bin\bundle'))) {
    & (Join-Path $ruby 'bin\gem.cmd') install bundler --no-document | Out-Null
}

# 3. Browser
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge is not installed on this machine. The Selenium suites drive Edge; ask the machine owner to install it.' }
Log "Edge: $edge"

Remove-Item -Recurse -Force $dl -ErrorAction SilentlyContinue
Set-Content (Join-Path $Tools 'ready.txt') "Prepared $(Get-Date -Format s) for $Root"
Log "ready: tools in $Tools"
