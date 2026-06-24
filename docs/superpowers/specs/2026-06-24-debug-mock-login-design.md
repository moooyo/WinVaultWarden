# Debug Mock Login Design

## 背景

当前 App 登录页已经接入真实 Vaultwarden 认证和同步流程。开发 UI 时如果没有可用服务器或测试账号，会反复卡在登录前，因此需要一个只面向本地开发的 mock login 入口。

## 已选方案

在 Debug 构建中显示“使用演示保险库”按钮。点击后不请求服务器、不写入 DPAPI、不保存 refresh token，只在当前进程内填充一个演示 `VaultSession`，然后进入主界面。

Release 构建不显示这个入口，也不注册相关 UI 操作，避免普通包误用演示数据。

## 行为

- 登录页在 Debug 下显示次要按钮“使用演示保险库”。
- 点击后通过 ViewModel 调用 mock login 命令。
- mock login 会把会话状态设置为 `Unlocked`，填充账户、文件夹、设备和包含登录、银行卡、身份、笔记、SSH 密钥、回收站条目的演示数据。
- 成功后沿用现有登录成功回调进入主界面。
- mock login 不触发 `IAuthService.LoginAsync`、`UnlockAsync` 或 `SubmitTwoFactorAsync`。
- mock login 不调用 `ITokenStore.Save`。

## 架构

新增 App 层接口 `IDemoVaultSessionService`，由 Debug-only 的 `DemoVaultSessionService` 操作 `VaultSession`。`LoginViewModel` 依赖可选的 demo 服务；当服务存在时暴露 `CanUseDemoVault=true` 并提供 `UseDemoVaultCommand`。

真实认证仍由 `IAuthService` 负责。演示数据只构造 Core domain model，不经过 API DTO 或加解密层。

## 测试

- ViewModel 测试验证 demo 命令调用 demo 服务、触发成功回调、清空状态。
- 服务测试验证 demo 会话包含账户、条目、文件夹、设备，并且没有 access/refresh token。
- XAML 测试验证 Debug 构建下登录页包含“使用演示保险库”按钮。

## 明确不做

- 不为 Release 提供演示入口。
- 不把演示登录伪装成真实 Vaultwarden 登录。
- 不写入本地持久会话。
