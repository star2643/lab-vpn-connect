[CmdletBinding()]
param(
    [string]$HostName,
    [string]$UserName,
    [string]$VpnName,
    [string]$Alias = 'work',
    [int]$Port = 10137,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\lab-vpn-connect'),
    [string]$ConfigPath = (Join-Path $env:USERPROFILE '.ssh\config')
)
$ErrorActionPreference = 'Stop'
if (-not $HostName) { $HostName = Read-Host 'Lab public IPv4 address' }
if (-not $UserName) { $UserName = Read-Host 'Your SSH account on the target computer (not a password)' }
if ($Alias -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'Invalid SSH alias.' }
if ($UserName -notmatch '^[A-Za-z0-9_][A-Za-z0-9._-]*$') { throw 'Invalid SSH account name.' }
if ($HostName -notmatch '^([0-9]{1,3}\.){3}[0-9]{1,3}$') { throw 'Use a numeric IPv4 address.' }
$parsedHost = $null
if (-not [Net.IPAddress]::TryParse($HostName, [ref]$parsedHost)) { throw 'Invalid IPv4 address.' }
if ($Port -lt 1 -or $Port -gt 65535) { throw 'Port must be 1-65535.' }
if ($PSBoundParameters.ContainsKey('VpnName') -and [string]::IsNullOrWhiteSpace($VpnName)) { throw 'VPN name must not be empty if specified.' }
if ($VpnName -match '["%\r\n&|<>^]') { throw 'VPN name contains unsupported shell characters.' }
$interfaceOption = if ($VpnName) { ' --interface "' + $VpnName + '"' } else { '' }
$sourceExe = Join-Path $PSScriptRoot 'lab-vpn-connect.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) { $sourceExe = Join-Path $PSScriptRoot 'artifacts\lab-vpn-connect.exe' }
if (-not (Test-Path -LiteralPath $sourceExe)) { throw 'Executable missing. Extract the entire release ZIP, or run build.ps1 first.' }
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
$targetExe = Join-Path $InstallDir 'lab-vpn-connect.exe'
$sshExePath = $targetExe.Replace('\', '/')
if ($sshExePath -match '["%\r\n&|<>^]') { throw 'Installation path contains unsupported shell characters.' }
$oldConfig = if (Test-Path -LiteralPath $ConfigPath) { [IO.File]::ReadAllText($ConfigPath) } else { '' }
$begin = '# BEGIN lab-vpn-connect ' + $Alias
$end = '# END lab-vpn-connect ' + $Alias
$pattern = '(?ms)^' + [regex]::Escape($begin) + '\r?\n.*?^' + [regex]::Escape($end) + '\r?\n?'
$beginCount = [regex]::Matches($oldConfig, '(?m)^' + [regex]::Escape($begin) + '\r?$').Count
$blocks = [regex]::Matches($oldConfig, $pattern)
if ($beginCount -ne $blocks.Count -or $beginCount -gt 1) { throw 'Malformed existing managed block; config was not changed.' }
$remaining = [regex]::Replace($oldConfig, $pattern, '')
$block = @"
$begin
Host $Alias
    HostName $HostName
    Port $Port
    User $UserName
    ProxyCommand "$sshExePath" --host %h --port %p$interfaceOption
Host *
$end
"@
# Put specific values first: OpenSSH uses the first obtained value for each option.
$newConfig = $block.TrimEnd() + "`r`n" + $remaining
[void][IO.Directory]::CreateDirectory($InstallDir)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ConfigPath))
if ([IO.Path]::GetFullPath($sourceExe) -ne [IO.Path]::GetFullPath($targetExe)) {
    Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force
}
if ($oldConfig -ne $newConfig) {
    if (Test-Path -LiteralPath $ConfigPath) {
        $backupPath = $ConfigPath + '.before-lab-vpn-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
        Copy-Item -LiteralPath $ConfigPath -Destination $backupPath
        Write-Host ('SSH config backup: ' + $backupPath)
    }
    [IO.File]::WriteAllText($ConfigPath, $newConfig, (New-Object Text.UTF8Encoding($false)))
}
Write-Host ('Installed: ' + $targetExe)
Write-Host ('SSH config: ' + $ConfigPath)
if ($VpnName) { Write-Host ('Connect VPN "' + $VpnName + '", then run: ssh ' + $Alias) }
else { Write-Host ('Connect the VPN with server IP ' + $HostName + ', then run: ssh ' + $Alias + '. Its name does not matter.') }
Write-Host 'You may delete the extracted installer folder. Keep the installed executable and SSH config.'
