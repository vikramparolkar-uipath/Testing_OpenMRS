<#
  Checks (and optionally installs) everything the five Selenium suites need on a Windows machine that runs
  the UiPath test cases from Studio.

    powershell -ExecutionPolicy Bypass -File Setup\Install-Prerequisites.ps1              # check only
    powershell -ExecutionPolicy Bypass -File Setup\Install-Prerequisites.ps1 -Install     # install what is missing
    powershell -ExecutionPolicy Bypass -File Setup\Install-Prerequisites.ps1 -Prepare     # pre-download suite dependencies

  -Install uses winget (official publisher packages) and downloads Apache Maven from apache.org into
  %USERPROFILE%\tools (checksum verified). Some installers ask for admin rights.
  -Prepare restores each suite's packages once (Maven, NuGet, pip, npm, gems), so the first demo run is quick.
  Restart UiPath Studio afterwards so it sees the new PATH.

  The script also works as a standalone copy (for example on the Desktop). Check and -Install need nothing else;
  -Prepare finds SeleniumSuite next to the repo's Setup folder, in the usual clone locations, or where -SuitePath says:
    powershell -ExecutionPolicy Bypass -File Install-Prerequisites.ps1 -Prepare -SuitePath C:\path\Testing_OpenMRS\SeleniumSuite
#>
param([switch]$Install, [switch]$Prepare, [string]$SuitePath)
$ErrorActionPreference = 'Continue'

function Find-Suite {
    if ($SuitePath) { return $SuitePath }
    $candidates = @(
        (Join-Path (Split-Path $PSScriptRoot -Parent) 'SeleniumSuite'),   # running from the repo's Setup folder
        (Join-Path $PSScriptRoot 'SeleniumSuite'),
        (Join-Path $PSScriptRoot 'Testing_OpenMRS\SeleniumSuite')
    )
    foreach ($base in @("$env:USERPROFILE\Documents\UiPath", "$env:USERPROFILE\Documents", "$env:USERPROFILE\source\repos", $env:USERPROFILE)) {
        $candidates += Join-Path $base 'Testing_OpenMRS\SeleniumSuite'
    }
    $candidates | Where-Object { Test-Path (Join-Path $_ 'run-scenario.ps1') } | Select-Object -First 1
}

function Find-Jdk {
    if ($env:JAVA_HOME -and (Test-Path "$env:JAVA_HOME\bin\javac.exe")) { return $env:JAVA_HOME }
    $jdk = Get-ChildItem "$env:ProgramFiles\Eclipse Adoptium\jdk-*", "$env:ProgramFiles\Microsoft\jdk-*", "$env:ProgramFiles\Java\jdk-*" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name | Select-Object -Last 1
    if ($jdk) { return $jdk.FullName }
}
function Find-Maven {
    $cmd = Get-Command mvn -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $m = Get-ChildItem "$env:USERPROFILE\tools\apache-maven-*\bin\mvn.cmd" -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($m) { return $m.FullName }
}
function Find-Ruby {
    $cmd = Get-Command ruby -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $r = Get-ChildItem 'C:\Ruby3*\bin\ruby.exe' -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($r) { return $r.FullName }
}
function Test-DotnetSdk {
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { return $false }
    return [bool](& $dotnet --list-sdks 2>$null | Where-Object { $_ -match '^(8|9|1\d)\.' })
}

$checks = [ordered]@{
    'JDK 11+ (Java + TestNG)'     = @{ Ok = { [bool](Find-Jdk) };  Winget = 'EclipseAdoptium.Temurin.21.JDK' }
    'Apache Maven'                = @{ Ok = { [bool](Find-Maven) }; Winget = $null }
    '.NET 8+ SDK (C# + NUnit)'    = @{ Ok = { Test-DotnetSdk };    Winget = 'Microsoft.DotNet.SDK.8' }
    'Python 3 (pytest)'           = @{ Ok = { [bool](Get-Command python -ErrorAction SilentlyContinue) -and ((python --version 2>&1) -match 'Python 3') }; Winget = 'Python.Python.3.13' }
    'Node.js (Mocha)'             = @{ Ok = { [bool](Get-Command node -ErrorAction SilentlyContinue) }; Winget = 'OpenJS.NodeJS.LTS' }
    'Ruby 3 (RSpec)'              = @{ Ok = { [bool](Find-Ruby) }; Winget = 'RubyInstallerTeam.Ruby.3.3' }
    'Microsoft Edge'              = @{ Ok = { (Test-Path "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") -or (Test-Path "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") }; Winget = 'Microsoft.Edge' }
}

$missing = @()
foreach ($name in $checks.Keys) {
    $ok = & $checks[$name].Ok
    Write-Host ("{0,-28} {1}" -f $name, $(if ($ok) { 'OK' } else { 'MISSING' })) -ForegroundColor $(if ($ok) { 'Green' } else { 'Yellow' })
    if (-not $ok) { $missing += $name }
}

if ($missing -and -not $Install) {
    Write-Host "`nRun again with -Install to install the missing tools." -ForegroundColor Yellow
}

if ($Install) {
    foreach ($name in $missing) {
        $id = $checks[$name].Winget
        if ($id) {
            Write-Host "`nInstalling $name ($id) ..." -ForegroundColor Cyan
            winget install --id $id -e --silent --accept-package-agreements --accept-source-agreements
        }
        elseif ($name -eq 'Apache Maven') {
            Write-Host "`nInstalling Apache Maven into $env:USERPROFILE\tools ..." -ForegroundColor Cyan
            $index = (Invoke-WebRequest 'https://dlcdn.apache.org/maven/maven-3/' -UseBasicParsing).Content
            $version = ([regex]::Matches($index, '3\.9\.\d+') | ForEach-Object Value | Sort-Object { [version]$_ } | Select-Object -Last 1)
            $zipUrl = "https://dlcdn.apache.org/maven/maven-3/$version/binaries/apache-maven-$version-bin.zip"
            $zip = Join-Path $env:TEMP "apache-maven-$version-bin.zip"
            Invoke-WebRequest $zipUrl -OutFile $zip -UseBasicParsing
            $expected = ((Invoke-WebRequest "$zipUrl.sha512" -UseBasicParsing).Content -split '\s+')[0].Trim().ToLower()
            $actual = (Get-FileHash $zip -Algorithm SHA512).Hash.ToLower()
            if ($expected -ne $actual) { throw "Maven download checksum mismatch" }
            New-Item -ItemType Directory -Force "$env:USERPROFILE\tools" | Out-Null
            Expand-Archive $zip -DestinationPath "$env:USERPROFILE\tools" -Force
            Remove-Item $zip
        }
    }
    $ruby = Find-Ruby
    if ($ruby) {
        $gem = Join-Path (Split-Path $ruby) 'gem.cmd'
        if (Test-Path $gem) { & $gem install bundler --no-document }
    }
    Write-Host "`nDone. Restart UiPath Studio (and any open terminals) so the new PATH is picked up." -ForegroundColor Green
}

if ($Prepare) {
    $suite = Find-Suite
    if (-not $suite -or -not (Test-Path (Join-Path $suite 'run-scenario.ps1'))) {
        Write-Host "`nCan't find the SeleniumSuite folder. Clone Testing_OpenMRS first, then run again with" -ForegroundColor Red
        Write-Host "  -Prepare -SuitePath <clone>\Testing_OpenMRS\SeleniumSuite" -ForegroundColor Red
        exit 1
    }
    Write-Host "`nPre-downloading suite dependencies in $suite ..." -ForegroundColor Cyan
    $jdk = Find-Jdk; if ($jdk) { $env:JAVA_HOME = $jdk; $env:Path = "$jdk\bin;$env:Path" }
    $mvn = Find-Maven; $ruby = Find-Ruby; if ($ruby) { $env:Path = "$(Split-Path $ruby);$env:Path" }
    Push-Location "$suite\s1-login-java-testng"; if ($mvn) { & $mvn -q -B test-compile }; Pop-Location
    Push-Location "$suite\s2-register-csharp-nunit"; & (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') build -nologo -v q; Pop-Location
    Push-Location "$suite\s3-find-python-pytest"
    if (-not (Test-Path '.venv\Scripts\python.exe')) { python -m venv .venv }
    .\.venv\Scripts\python -m pip install -q -r requirements.txt; Pop-Location
    Push-Location "$suite\s4-vitals-js-mocha"; npm install --no-audit --no-fund; Pop-Location
    Push-Location "$suite\s5-rbac-ruby-rspec"; bundle install; Pop-Location
    Write-Host "Suites ready." -ForegroundColor Green
}
