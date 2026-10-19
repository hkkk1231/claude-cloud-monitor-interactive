$ErrorActionPreference='Stop'
python -m unittest quota_probe_test conversations_test input_test
if($LASTEXITCODE -ne 0){throw 'Python tests failed'}
$p=Start-Process -FilePath (Join-Path $PSScriptRoot 'bin\ClaudeMonitor.exe') -ArgumentList '--self-test' -Wait -PassThru -WindowStyle Hidden
if($p.ExitCode -ne 0){throw 'Desktop self-test failed: --self-test'}
$p=Start-Process -FilePath (Join-Path $PSScriptRoot 'bin\ClaudeMonitor.exe') -ArgumentList '--quota-self-test' -Wait -PassThru -WindowStyle Hidden
if($p.ExitCode -ne 0){throw 'Desktop self-test failed: --quota-self-test'}
$p=Start-Process -FilePath (Join-Path $PSScriptRoot 'bin\ClaudeMonitor.exe') -ArgumentList '--notify-self-test' -Wait -PassThru -WindowStyle Hidden
if($p.ExitCode -ne 0){throw 'Desktop self-test failed: --notify-self-test'}
