# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 沟通约定

**始终使用中文回复用户。** 代码、标识符、提交信息可用英文，但所有面向用户的解释、总结、提问一律用中文。

## 项目目的

WinVaultWarden 是一个 **Windows 原生的 Bitwarden 兼容客户端**。核心目标：

1. 使用 **最新版 .NET + Windows App SDK + WinUI 3** 构建纯 Windows 原生客户端（**只考虑 Windows 平台**，不做跨平台）。
2. 实现的 API 必须与 **Bitwarden 服务端协议兼容**，使其能直接对接 Vaultwarden / Bitwarden 服务端。
3. **API 的权威定义参考 Vaultwarden 源码**，位于 `D:\Code\vaultwarden`（Rust 项目）。当需要确认某个端点的路径、请求体、响应体或字段命名时，**以该项目的实际代码为准**，而不是凭记忆或外部文档。

当前仓库为空白起点（仅有 README、LICENSE、.gitignore），尚未建立 .NET 解决方案。首次搭建时需要从零创建项目骨架。

## 技术栈（截至 2026-06，请在动工前复核最新版本）

- **.NET 10**（LTS，2025-11 发布）——“最新版 .NET”当前指向它。目标框架形如 `net10.0-windows10.0.26100.0`，随安装的 Windows SDK 调整。
- **Windows App SDK 2.2.0**（稳定版，2026-06-09）——通过 `Microsoft.WindowsAppSDK` NuGet 包引入。
- **WinUI 3** 作为 UI 框架（随 Windows App SDK 一同提供）。
- 推荐打包方式：MSIX；架构优先 x64 / arm64。

> 这些版本号会过时。新会话动工前用 `dotnet --list-sdks` 确认本机 SDK，并核对 Windows App SDK NuGet 的最新稳定版。

## 常用命令

项目骨架建立后，以下为日常命令（解决方案文件出现前部分命令不可用）：

```bash
# 还原 / 构建 / 运行
dotnet restore
dotnet build -c Debug
dotnet run --project src/WinVaultWarden     # 或在 Visual Studio 中 F5

# 发布（MSIX / 自包含，按需指定 RID）
dotnet publish -c Release -r win-x64

# 测试
dotnet test                                  # 全部测试
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"   # 单个测试
dotnet test --filter "Category=Crypto"       # 按分类
```

WinUI 3 + Windows App SDK 项目通常需要 Visual Studio 2022/2026（含“Windows 应用开发”工作负载）才能完整设计与调试 XAML；命令行 `dotnet build` 可用于 CI 与快速校验。

## 架构：对接 Vaultwarden API

客户端的网络层本质上是 Vaultwarden 服务端的镜像。**实现任何端点前，先读对应的 Rust 源文件确认契约。** 路由前缀在 `D:\Code\vaultwarden\src\main.rs` 中挂载：

| 前缀 | 用途 | 源文件 |
| --- | --- | --- |
| `/identity` | 登录、令牌、注册、prelogin、SSO | `src/api/identity.rs` |
| `/api` | 核心业务（账户、密码库条目、文件夹、组织等） | `src/api/core/*.rs` |
| `/icons` | 网站图标代理 | `src/api/icons.rs` |
| `/notifications` | WebSocket 实时推送 | `src/api/notifications.rs` |
| `/events` | 事件日志 | `src/api/core/events.rs` |
| `/admin` | 管理后台（客户端一般不需要） | `src/api/admin.rs` |

### 关键源文件映射

- **认证流程** — `src/api/identity.rs`：`POST /identity/connect/token` 是登录核心，`grant_type` 支持 `password`（用户名+主密码哈希）、`refresh_token`（刷新）、`client_credentials`（API Key）、`authorization_code`（SSO）。响应含 `access_token` / `refresh_token` / `Key`（用户加密密钥）/ `PrivateKey` 等。`ConnectData` 结构体定义了所有表单字段（注意同时接受 snake_case 与 camelCase 两种命名）。
- **prelogin** — `POST /identity/accounts/prelogin`（也存在于 `src/api/core/accounts.rs`）：客户端登录前先调用它获取 KDF 参数（类型/迭代次数/内存/并行度），据此从主密码派生密钥。
- **账户与设备** — `src/api/core/accounts.rs`：profile、改密、KDF 调整、设备管理（`/devices/...`）、auth-requests（被动登录授权）。
- **密码库条目（Cipher）** — `src/api/core/ciphers.rs`：最大最复杂的文件。`GET /api/sync` 是首屏全量拉取入口；CRUD 在 `/ciphers/...`；附件走 multipart 上传。
- **文件夹** — `src/api/core/folders.rs`；**组织/集合/成员/策略** — `src/api/core/organizations.rs`；**Send** — `src/api/core/sends.rs`；**紧急访问** — `src/api/core/emergency_access.rs`。
- **数据模型与 JSON 形状** — `src/db/models/*.rs`：每个模型的 `to_json()` 方法定义了返回给客户端的确切字段名与结构（例如 `user.rs`、`cipher.rs`）。**字段大小写很关键**——Bitwarden 客户端对 PascalCase / camelCase 敏感，代码中多处注释标注了必须保留的特定大小写。

### 加密模型（客户端必须正确实现，否则无法解密数据）

参考 `src/crypto.rs` 与 `src/db/models/user.rs`：

- **KDF**：默认 `PBKDF2-HMAC-SHA256`（`UserKdfType::Pbkdf2 = 0`），也支持 Argon2id。主密码经 KDF + 邮箱(salt) 派生出 **主密钥**。
- 服务端存储的 `password_hash` 是“主密钥再哈希”后的值；登录时客户端发送的是这个二次哈希（`MasterPasswordHash`），而非主密码本身。
- 用户的对称密钥 `akey`（JSON 字段 `Key`）和非对称密钥对 `private_key`/`public_key` 均以加密形式存储和下发，需用主密钥在**客户端本地**解密。
- 条目字段（用户名、密码、备注等）是 Bitwarden `EncString` 格式的密文，客户端负责加解密；服务端只存储不可读的密文。

> 安全要点：主密码与主密钥**绝不**离开客户端内存边界，更不可落盘明文或写入日志。

## 测试环境（实测 Vaultwarden 服务端）

用于把客户端代码跑在真实 Bitwarden 兼容服务端上做端到端验证。**该测试账户与服务端均为一次性测试用途，凭据可明文记录于此。**

### 服务器与登录

- **SSH 登录**：`ssh test-env`（已在 dotssh 配置 `D:\Code\dotssh\config.d/hosts` 定义 → `root@10.0.1.20:22`，认证走 Bitwarden SSH Agent 命名管道）。
  - ⚠️ **必须用 Windows 原生 ssh.exe**（`$env:WINDIR\System32\OpenSSH\ssh.exe`，经 PowerShell 调用）。Git Bash 自带的 ssh 会因 `~/.ssh/config` 开头的 UTF-8 BOM 报 `Bad configuration option: \357\273\277include` 而失败，且其无法访问 Windows 命名管道里的 agent 私钥。
  - 远程命令避免在 PowerShell 单引号里带 `()`（bash 会当语法）；多行命令用 PowerShell here-string `@' ... '@` 传给 ssh.exe。
- **服务器**：test-env = Debian 13 (trixie) x86_64，对外 IP `10.0.1.20`，当前网络可直连。
- **Vaultwarden**：docker 容器 `vaultwarden`（镜像 `vaultwarden/server:latest`，服务端 version `2025.12.0`），数据卷 `/opt/vaultwarden/data`，端口映射 `8080:80`。
  - 启动参数：`DOMAIN=http://10.0.1.20:8080`、`SIGNUPS_ALLOWED=true`、`WEBSOCKET_ENABLED=true`、`ADMIN_TOKEN=testadmintoken123`。
  - **客户端服务端 URL**：`http://10.0.1.20:8080`（客户端接受 http，无需 HTTPS）。
  - 健康检查：`curl http://10.0.1.20:8080/alive`；管理后台：`http://10.0.1.20:8080/admin`（token `testadmintoken123`）。
- **测试账户**：邮箱 `test@winvaultwarden.local` / 主密码 `Test-Master-Password-1!`（KDF = PBKDF2-SHA256，600000 次）。

### 运行端到端冒烟测试

`tests/LiveSmoke`（控制台 Exe，**不**随 `dotnet test` 跑，需手动指向 live 服务端）覆盖：config → 注册（幂等）→ 登录 → 同步 → 建文件夹/Login 条目 → 往返解密校验 → 更新 → 软删/恢复 → 硬删清理。全程走我们自己的 `Crypto`/`Api`/`Vault` 代码。

```bash
# 默认即指向上面的测试服务端与账户
dotnet run --project tests/LiveSmoke -- http://10.0.1.20:8080
# 或自定义: dotnet run --project tests/LiveSmoke -- <serverUrl> <email> <password>
```

> 客户端本身未实现自助注册（注册仅 Web 端）；冒烟测试用我方 Crypto 按 Vaultwarden `RegisterData` 契约（`src/api/core/accounts.rs`）自行构造注册体来准备账户，账户已存在时自动复用。

## 给未来实例的工作提示

- 新增/修改任何 API 调用前，**先 grep Vaultwarden 源码**确认真实契约：路由用 `#[get/post/put/delete(...)]` 宏标注，请求体看 `data` 参数的结构体（`#[derive(Deserialize)]` + `#[serde(rename_all = "camelCase")]`），响应体看对应 model 的 `to_json()`。
- 遇到字段大小写或命名疑问时，Vaultwarden 代码中的内联注释（常引用 bitwarden/clients 仓库链接）是最可靠的依据。
- 这是绿地项目：涉及多步骤的功能搭建，先用 brainstorming 技能对齐方案再动手。
