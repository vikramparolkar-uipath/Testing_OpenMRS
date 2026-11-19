<#
  Creates or updates the four OpenMRS persona credential assets in an Orchestrator folder with the uip CLI.
  You type each password; it is not echoed, printed or saved. Leave a password blank to skip that persona.

    uip login                     # sign in to the SAME tenant your Studio / Assistant robot is connected to
    powershell -ExecutionPolicy Bypass -File Setup\Set-OpenMRSCredentials.ps1 -FolderPath OpenMRS

  No uip CLI? Create the same four Credential assets by hand in Orchestrator (Assets > Add asset > Credential):
  OpenMRS Admin / admin, OpenMRS Doctor / doctor, OpenMRS Nurse / nurse, OpenMRS Clerk / clerk.
#>
param([string]$FolderPath = 'OpenMRS')
$ErrorActionPreference = 'Stop'
if (-not (Get-Command uip -ErrorAction SilentlyContinue)) {
    throw "uip CLI not found. Create the assets in Orchestrator by hand (see the comment at the top of this script)."
}

$personas = @(
    @{ Name = 'OpenMRS Admin';  User = 'admin';  Desc = 'OpenMRS System Administrator persona' },
    @{ Name = 'OpenMRS Doctor'; User = 'doctor'; Desc = 'OpenMRS Doctor persona' },
    @{ Name = 'OpenMRS Nurse';  User = 'nurse';  Desc = 'OpenMRS Nurse persona' },
    @{ Name = 'OpenMRS Clerk';  User = 'clerk';  Desc = 'OpenMRS Registration Clerk persona' }
)

$existing = (uip or assets list --folder-path $FolderPath --type Credential --output json | ConvertFrom-Json).Data

foreach ($p in $personas) {
    $sec = Read-Host -AsSecureString "Password for $($p.Name) (user name '$($p.User)'), blank to skip"
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
    if ([string]::IsNullOrEmpty($plain)) { Write-Host "  skipped $($p.Name)"; continue }
    $value = "$($p.User):$plain"
    $match = $existing | Where-Object { $_.Name -eq $p.Name } | Select-Object -First 1
    if ($match) {
        uip or assets update $match.Key $value --folder-path $FolderPath --type Credential -d $p.Desc --output plain | Out-Null
        Write-Host "  updated $($p.Name)"
    }
    else {
        uip or assets create $p.Name $value --folder-path $FolderPath --type Credential -d $p.Desc --output plain | Out-Null
        Write-Host "  created $($p.Name)"
    }
    $plain = $null; $value = $null
}
uip or assets list --folder-path $FolderPath --output table
