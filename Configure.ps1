$ErrorActionPreference = 'Stop'
try {
    Write-Host '配置你自己的服务器。私钥只引用路径，不复制文件。SSH 使用默认端口 22。'
    $server = Read-Host '服务器 IP 或域名'
    $remoteUser = Read-Host 'SSH 用户名'
    $key = (Read-Host '本机 SSH 私钥完整路径').Trim('"')
    $root = (Read-Host '服务器已有项目绝对路径（例如 /home/yourname/projects）').TrimEnd('/')
    foreach($value in @($server,$remoteUser)) { if($value -notmatch '^[\w.-]+$') { throw '服务器或用户名格式无效' } }
    if($key.Contains('"') -or !(Test-Path -LiteralPath $key -PathType Leaf)) { throw '私钥路径无效' }
    if($root -notmatch '^/[A-Za-z0-9_./-]+$') { throw '项目路径格式无效，请使用已存在的 Linux 绝对路径' }
    $encoding = New-Object Text.UTF8Encoding($false)
    $config = @{host=$server;user=$remoteUser;identityFile=$key;session='claude-shared';refreshSeconds=8}
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'config.json'),($config | ConvertTo-Json),$encoding)
    $projects = @{projects=@(@{Name='我的项目';Path=$root});selected=$root}
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'projects.json'),($projects | ConvertTo-Json -Depth 5),$encoding)
    Write-Host '配置已保存。请运行 验证SSH连接.cmd，成功后再启动监控。'
} catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }