$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = Join-Path $project 'artifacts\tests.exe'
$start.Arguments = '--stdio-echo'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
$output = New-Object IO.MemoryStream
$download = $process.StandardOutput.BaseStream.CopyToAsync($output)
$errors = $process.StandardError.ReadToEndAsync()
$payload = New-Object byte[] (1024 * 1024)
(New-Object Random(17)).NextBytes($payload)
$process.StandardInput.BaseStream.Write($payload, 0, $payload.Length)
$process.StandardInput.Close()
if (-not $process.WaitForExit(10000)) { $process.Kill(); throw 'Binary stdio echo stalled.' }
[void]$download.GetAwaiter().GetResult()
if ($process.ExitCode -ne 0) { throw ('Binary stdio failed: ' + $errors.Result) }
$actual = $output.ToArray()
if ($actual.Length -ne $payload.Length) { throw 'Binary stdio changed the data length.' }
$sha = [Security.Cryptography.SHA256]::Create()
if ([Convert]::ToBase64String($sha.ComputeHash($payload)) -ne [Convert]::ToBase64String($sha.ComputeHash($actual))) {
    throw 'Binary stdio changed the payload.'
}
$sha.Dispose()
$process.Dispose()
$output.Dispose()
Write-Host 'PASS real redirected standard input/output: 1 MiB of binary data'
