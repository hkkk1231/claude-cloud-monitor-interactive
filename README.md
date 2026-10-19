# Claude 云端监控 · 交互版（旧版交互开源项目）

这是单页只读监控改版前的交互版本，基于保留的交互版源码重新编译。支持对话列表、项目筛选、实时终端、中文输入、长文本粘贴、切换/新建/恢复对话、重命名/本地归档以及账号额度窗口。

## 使用

1. 完整解压到可写目录；Windows 10 1809 或更新的 64 位系统，安装 .NET Framework 4.8、Microsoft Edge WebView2 Runtime 和 Windows OpenSSH 客户端。
2. 双击「修改连接.cmd」，填写自己的服务器、SSH 用户、私钥路径和已有项目路径。本版使用 SSH 端口 22。不要提供 Claude token 或邮箱密码。
3. 双击「验证SSH连接.cmd」，核对自己服务器的主机指纹并验证密钥认证。加密私钥需先通过 ssh-agent 解锁。
4. 双击「启动监控.cmd」。首次运行只提示配置；完成配置、验证后再次启动。

服务器需要 Linux、Python 3.8+、tmux、已通过官方 OAuth 登录的 Claude Code（~/.local/bin/claude）及兼容的 claude-swap/cswap（~/.local/bin/cswap，支持 run --share-history 和 list --json）。不会为你安装或登录这些依赖。实际工作运行在服务器，本机无需安装 Claude CLI。首次使用前在自己的服务器执行 `tmux has-session -t claude-shared 2>/dev/null || tmux new-session -d -s claude-shared`，准备共享终端；项目目录需位于当前 Linux 用户的 home 内且已经存在。随后可通过「新建对话」建立交互会话。已有历史对话按服务器实际记录发现。

终端中 Enter 提交、Ctrl+V 粘贴、F5 重连；长文本编辑区的按钮只粘贴，需要在终端按 Enter。关闭客户端只断开 SSH，不结束远程任务。新建/恢复对话会启动服务器上的交互式 Claude，请自行确认目标工程及模型。软件不会自动替你选择 max 或配置额度暂停、换号、渲染桥接等外部流程。

飞书通知默认禁用，包内无机器人或收件人配置。需要时将 feishu-notifications.example.json 复制为 bin/feishu-notifications.json，再填写自己的 lark-cli profile 和收件人。无兼容 cswap 时账号额度不可用，新建/恢复入口也需要先补齐依赖。

## 脱敏与文件

未携带原服务器 IP、个人邮箱、Windows 用户路径、SSH 密钥、Claude 凭据、浏览器资料、项目清单、对话历史、通知状态、截图及日志。固定远程用户路径改为服务器当前用户的 home；source 中 /home/remoteuser 仅为默认占位/回归样例，正常使用以配置的项目清单为准。通知、互斥和 API 名称使用独立分发版命名，避免误连现用私有监控。

source 保存 C# 源码，根目录 Python 和终端资源是运行依赖。第三方许可分别保存在 vendor 下。

使用后 config.json、projects.json、conversation-settings.json、通知状态和运行诊断可能包含你自己的环境或内容；再次对外转发应使用原始压缩包，勿直接压缩已使用目录。本包与官方 Anthropic 产品无隶属关系。

## 从源码构建

在 Windows PowerShell 中运行 `.\build.ps1`，然后运行 `.\test.ps1`。生成文件位于 `bin/`；首次使用按上方步骤配置自己的连接。构建仅从 NuGet 下载固定版本 WebView2 SDK（1.0.4258.31），并核验 SHA-256；无需 Node.js 或本机 Claude CLI。GitHub Actions 同样执行构建和离线自测。`.deps/`、`bin/` 及使用者配置均不提交。测试不会连接服务器或发送 Claude 消息。

## 项目与许可

本项目为独立社区工具，与 Anthropic 无隶属关系。项目源码采用 MIT；第三方组件适用各自许可，见 THIRD_PARTY_NOTICES.md。

配套项目：[旧版交互](https://github.com/hkkk1231/claude-cloud-monitor-interactive) · [只读监控](https://github.com/hkkk1231/claude-cloud-monitor-readonly)。
