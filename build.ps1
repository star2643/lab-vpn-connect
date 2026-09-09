[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
$outDir = Join-Path $PSScriptRoot 'artifacts'
[void][IO.Directory]::CreateDirectory($outDir)
$source = Join-Path $PSScriptRoot 'src\Program.cs'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /warnaserror+ ('/out:' + (Join-Path $outDir 'lab-vpn-connect.exe')) $source
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /warnaserror+ /main:LabVpnConnect.Tests ('/out:' + (Join-Path $outDir 'tests.exe')) $source (Join-Path $PSScriptRoot 'tests\Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& (Join-Path $outDir 'tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
& (Join-Path $PSScriptRoot 'tests\StdIO.Tests.ps1')
Write-Host ('Built: ' + (Join-Path $outDir 'lab-vpn-connect.exe'))
