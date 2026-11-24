<#
  Runs one Selenium scenario with its own toolchain and normalises the output, so a UiPath workflow
  only needs one activity to call any of the five:

    powershell -ExecutionPolicy Bypass -File run-scenario.ps1 -Scenario S3

  Inputs  (environment variables, set by the caller - the password never goes on the command line):
    OMRS_BASE_URL, OMRS_USERNAME, OMRS_PASSWORD, OMRS_LOCATION, OMRS_PATIENT_QUERY, OMRS_BROWSER, OMRS_HEADLESS
  Outputs (in the scenario folder):
    results\junit.xml        JUnit-style results (every framework)
    results\screenshots\*.png one screenshot per test
    results\output.json      values to hand on (S2 new patient, S3 found patient)
  Exit code: 0 = all tests passed, non-zero = at least one failure.
#>
param(
    [Parameter(Mandatory)][ValidateSet('S1', 'S2', 'S3', 'S4', 'S5')][string]$Scenario
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$folders = @{
    S1 = 's1-login-java-testng'; S2 = 's2-register-csharp-nunit'; S3 = 's3-find-python-pytest'
    S4 = 's4-vitals-js-mocha';   S5 = 's5-rbac-ruby-rspec'
}
$dir = Join-Path $root $folders[$Scenario]
Set-Location $dir

if (-not $env:OMRS_PASSWORD) { throw 'Environment variable OMRS_PASSWORD is required' }

# Find toolchains that a fresh install may not have put on PATH yet
function Add-ToPath($path) { if ($path -and (Test-Path $path)) { $env:Path = "$path;$env:Path" } }
# Portable toolchains from Framework\Bootstrap-SeleniumSuite.ps1 (fresh robot machines, e.g. a cloud VM pool)
$tools = $env:SELENIUM_TOOLS
if ($tools -and (Test-Path $tools)) {
    foreach ($bin in 'dotnet', 'ruby\bin', 'node', 'python\Scripts', 'python', 'maven\bin', 'jdk\bin') { Add-ToPath (Join-Path $tools $bin) }
    if (Test-Path "$tools\jdk\bin\javac.exe") { $env:JAVA_HOME = "$tools\jdk" }
}
if (-not (Get-Command mvn -ErrorAction SilentlyContinue)) {
    Add-ToPath (Get-ChildItem "$env:USERPROFILE\tools\apache-maven-*\bin" -Directory -ErrorAction SilentlyContinue | Select-Object -Last 1).FullName
}
if (-not $env:JAVA_HOME -or -not (Test-Path "$env:JAVA_HOME\bin\javac.exe")) {
    $jdk = Get-ChildItem "$env:ProgramFiles\Eclipse Adoptium\jdk-*" -Directory -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($jdk) { $env:JAVA_HOME = $jdk.FullName; Add-ToPath "$($jdk.FullName)\bin" }
}
if (-not (Get-Command ruby -ErrorAction SilentlyContinue)) {
    Add-ToPath (Get-ChildItem 'C:\Ruby3*\bin' -Directory -ErrorAction SilentlyContinue | Select-Object -Last 1).FullName
}

# Fresh results for every run
Remove-Item -Recurse -Force (Join-Path $dir 'results') -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $dir 'results') | Out-Null

# Test tools write progress and warnings to stderr; judge them by exit code only
$ErrorActionPreference = 'Continue'

switch ($Scenario) {
    'S1' {   # Java + TestNG through Maven
        mvn -q -B test
        $code = $LASTEXITCODE
        $xml = Join-Path $dir 'target\surefire-reports\TEST-TestSuite.xml'
        if (Test-Path $xml) { Copy-Item $xml (Join-Path $dir 'results\junit.xml') }
    }
    'S2' {   # C# + NUnit through dotnet test
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
        # A UiPath robot runs on its own bundled .NET runtime and passes that environment down, which hides the
        # machine's SDK ("No .NET SDKs were found"). Drop the inherited settings and call the SDK's dotnet.exe directly.
        Get-ChildItem Env: | Where-Object { $_.Name -match '^(DOTNET_ROOT|DOTNET_HOST_PATH|DOTNET_MSBUILD_SDK_RESOLVER|MSBuild|MSBUILD)' } |
            ForEach-Object { Remove-Item "Env:$($_.Name)" }
        $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        if ($tools -and (Test-Path "$tools\dotnet\dotnet.exe")) { $dotnet = "$tools\dotnet\dotnet.exe" }
        elseif (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
        & $dotnet test --logger 'junit;LogFilePath=results/junit.xml' --logger 'console;verbosity=normal'
        $code = $LASTEXITCODE
    }
    'S3' {   # Python + pytest (virtual environment created on first run)
        if (-not (Test-Path '.venv\Scripts\python.exe')) { python -m venv .venv }
        .\.venv\Scripts\python -c "import pytest, selenium" *> $null
        if ($LASTEXITCODE -ne 0) { .\.venv\Scripts\python -m pip install -q -r requirements.txt }
        .\.venv\Scripts\python -m pytest -v
        $code = $LASTEXITCODE
    }
    'S4' {   # JavaScript + Mocha
        if (-not (Test-Path 'node_modules')) { npm install --no-audit --no-fund }
        npm test
        $code = $LASTEXITCODE
    }
    'S5' {   # Ruby + RSpec
        bundle check *> $null
        if ($LASTEXITCODE -ne 0) { bundle install }
        bundle exec rspec
        $code = $LASTEXITCODE
    }
}
Write-Host "Scenario $Scenario finished with exit code $code. Results: $dir\results"
exit $code
