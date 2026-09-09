$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $env:TEMP ('lab-vpn-installer-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$config = Join-Path $testRoot 'config'
$original = "# Existing user config`r`nHost other`r`n    HostName 203.0.113.20`r`n    User another`r`n"
[IO.File]::WriteAllText($config, $original)
$parameters = @{ HostName='203.0.113.10'; UserName='testuser'; VpnName='Lab VPN'; Alias='work'; Port=10137; InstallDir=(Join-Path $testRoot 'Path With Spaces'); ConfigPath=$config }
& (Join-Path $project 'install.ps1') @parameters
$first = [IO.File]::ReadAllText($config)
& (Join-Path $project 'install.ps1') @parameters
if ([IO.File]::ReadAllText($config) -cne $first) { throw 'Installer is not idempotent.' }
if (-not $first.EndsWith($original)) { throw 'Original user configuration changed.' }
$effective = & ssh.exe -G -T -F $config work 2>&1
if ($LASTEXITCODE -ne 0 -or -not ($effective -match '^hostname 203\.0\.113\.10$')) { throw 'SSH could not parse the installed configuration.' }
if ($first -notmatch '--interface "Lab VPN"') { throw 'Explicit VPN override was lost.' }
$parameters.Remove('VpnName')
& (Join-Path $project 'install.ps1') @parameters
$automatic = [IO.File]::ReadAllText($config)
if ($automatic -match '--interface' -or $automatic -notmatch '--host %h --port %p') { throw 'Default installation must use automatic endpoint discovery.' }
if (-not $automatic.EndsWith($original)) { throw 'Upgrade changed the original user configuration.' }
& (Join-Path $project 'install.ps1') @parameters
if ([IO.File]::ReadAllText($config) -cne $automatic) { throw 'Automatic-mode installation is not idempotent.' }
& (Join-Path $project 'uninstall.ps1') -Alias work -ConfigPath $config
if ([IO.File]::ReadAllText($config) -cne $original) { throw 'Uninstall did not restore the original config.' }
Write-Host 'PASS installer: spaced paths, explicit/automatic modes, upgrade, repeat install, SSH parsing, preservation, uninstall'
Write-Host ('Test artifacts: ' + $testRoot)
