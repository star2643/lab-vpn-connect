[CmdletBinding()]
param([string]$Version = '1.1.1')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
& (Join-Path $PSScriptRoot 'build.ps1')
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$stage = Join-Path $artifacts ('release-' + $Version)
if (Test-Path -LiteralPath $stage) { throw 'Release staging directory already exists; use a fresh checkout or a new version.' }
[void][IO.Directory]::CreateDirectory($stage)
Copy-Item -LiteralPath (Join-Path $artifacts 'lab-vpn-connect.exe') -Destination $stage
foreach ($name in @('Install.cmd','install.ps1','uninstall.ps1','README.md','LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $stage
}
$zip = Join-Path $artifacts ('lab-vpn-connect-' + $Version + '-windows.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$hashLines = foreach ($path in @((Join-Path $artifacts 'lab-vpn-connect.exe'), $zip)) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($path)
}
[IO.File]::WriteAllLines((Join-Path $artifacts 'SHA256SUMS.txt'), $hashLines, (New-Object Text.UTF8Encoding($false)))
Write-Host ('Release package: ' + $zip)
