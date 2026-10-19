$ErrorActionPreference = 'Stop'
try {
    $config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw | ConvertFrom-Json
    Write-Host '请先通过云厂商控制台核对 SSH 主机指纹，本脚本不会自动接受指纹。'
    $common = @('-i',$config.identityFile,'-o','ConnectTimeout=8','-o','ConnectionAttempts=1')
    $destination = "$($config.user)@$($config.host)"
    & ssh.exe @common -o StrictHostKeyChecking=ask $destination 'python3 --version; tmux -V; test -x ~/.local/bin/claude && echo claude-present; test -x ~/.local/bin/cswap && echo cswap-present'
    if($LASTEXITCODE -ne 0) {throw '远程依赖检查未通过，请在服务器配置 Python、tmux、Claude Code 和兼容 cswap。'}
    & ssh.exe @common -o BatchMode=yes -o StrictHostKeyChecking=yes $destination 'true'
    if($LASTEXITCODE -ne 0) {throw '后台密钥认证未通过，加密私钥请先载入 ssh-agent。'}
    Write-Host 'SSH 后台认证通过。请同时核对上方各远程依赖结果。没有安装或启动 Claude。'
} catch {Write-Host $_.Exception.Message -ForegroundColor Red; exit 1}