[CmdletBinding()]
param(
    [string]$Alias = 'work',
    [string]$ConfigPath = (Join-Path $env:USERPROFILE '.ssh\config'),
    [switch]$RemoveExecutable
)
$ErrorActionPreference = 'Stop'
if ($Alias -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'Invalid SSH alias.' }
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
if (Test-Path -LiteralPath $ConfigPath) {
    $oldConfig = [IO.File]::ReadAllText($ConfigPath)
    $begin = '# BEGIN lab-vpn-connect ' + $Alias
    $end = '# END lab-vpn-connect ' + $Alias
    $pattern = '(?ms)^' + [regex]::Escape($begin) + '\r?\n.*?^' + [regex]::Escape($end) + '\r?\n?'
    $count = [regex]::Matches($oldConfig, '(?m)^' + [regex]::Escape($begin) + '\r?$').Count
    if ($count -gt 1 -or $count -ne [regex]::Matches($oldConfig, $pattern).Count) { throw 'Malformed managed block; nothing changed.' }
    $newConfig = [regex]::Replace($oldConfig, $pattern, '')
    if ($oldConfig -ne $newConfig) {
        Copy-Item -LiteralPath $ConfigPath -Destination ($ConfigPath + '.before-lab-vpn-uninstall-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
        [IO.File]::WriteAllText($ConfigPath, $newConfig, (New-Object Text.UTF8Encoding($false)))
    }
    if ($RemoveExecutable -and $newConfig -match 'lab-vpn-connect\.exe') { throw 'Another SSH entry still references the executable; kept it installed.' }
}
if ($RemoveExecutable) {
    $installedExe = Join-Path $env:LOCALAPPDATA 'Programs\lab-vpn-connect\lab-vpn-connect.exe'
    if (Test-Path -LiteralPath $installedExe) { Remove-Item -LiteralPath $installedExe }
}
Write-Host ('Removed managed SSH entry: ' + $Alias)
Write-Host 'Original SSH entries and backups were retained.'
