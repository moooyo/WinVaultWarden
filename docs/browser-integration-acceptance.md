# Chrome/Edge 浏览器集成人工验收清单

本清单覆盖普通 CI（`dotnet test` / `npm test` / MSBuild / 源码扫描）**无法可靠自动化**的
11 项验收，**仅适用于 portable 版** WinVaultWarden。MSIX 不携带浏览器集成，另见下方独立
的 MSIX 负验收。

参见 `docs\passkeys-browser-integration.md` 了解协议、安装与安全模型的权威说明。

> 自动化门禁（单元测试、Node 测试、App x64/arm64 构建、`scripts\verify-browser-native-host-package.ps1`）
> 只能验证清单构造、manifest 内容、二进制放置位置和签名固定逻辑的**代码正确性**；它们不能证明浏览器
> 在真实用户交互下的实际行为——这正是本清单存在的原因。**用户拒绝及安全错误不得原生回退**：除
> `client_not_handled` 外的任何错误码都必须以稳定 `DOMException` 终止请求，绝不能让页面静默回退到
> 浏览器原生 WebAuthn 实现（这会让用户误以为 WinVaultWarden 拒绝等同于“换个认证器再试一次”）。

## 记录方式

对每一项：

- 分别在 Chrome 列与 Edge 列勾选 `- [ ]` → `- [x]`。
- 记录字段只写 **浏览器版本号**（`chrome://version` / `edge://version`）、App 版本、
  CPU 架构（x64/arm64）和观察到的现象；**不要再写安装形态列**。
- 任何一项失败都必须记录复现步骤，不得以“预期会这样”跳过。

---

### 1. 固定扩展 ID 与 native manifest `allowed_origins` 一致性

分别在 Chrome 与 Edge 加载 `browser-extension` 未打包扩展，确认地址栏/`chrome://extensions`
（`edge://extensions`）显示的扩展 ID 与 `browser-extension\extension-identities.json` 中固定的
`ehakaifefnodlknfnekcnljhljcaoclb`（Chrome）/`icobpiocjfkfebioicnpmccolpofhpik`（Edge）完全一致；
并确认安装后生成的 `com.winvaultwarden.browser.json` 中 `allowed_origins` 恰为
`chrome-extension://<对应固定 ID>/`。

- [ ] Chrome：版本 ____，App 版本 ____，架构 ____，结果 ____
- [ ] Edge：版本 ____，App 版本 ____，架构 ____，结果 ____

### 2. App 根目录启动 Host；升级后 manifest path 刷新

portable 版都能让浏览器从 `AppContext.BaseDirectory` 根目录启动 `BrowserNativeHost.exe`。升级（覆盖安装
新版本 portable）后，重新运行 `install-native-host.ps1`，旧版本目录路径不再被任何浏览器 manifest 引用。

- [ ] Chrome：版本 ____，App 版本（升级前→后）____，架构 ____，结果 ____
- [ ] Edge：版本 ____，App 版本（升级前→后）____，架构 ____，结果 ____

### 3. 真实 Authenticode signer-pinning 双向校验

portable 安装后，App↔Host 的双向 **Authenticode** signer SPKI 校验通过（`WinVerifyTrust` 返回 `S_OK`
或恰好 `CERT_E_UNTRUSTEDROOT`）。分别替换 `App.exe` 或 `BrowserNativeHost.exe` 为未签名/其他签发者签名的
同名文件后，验证握手 fail-closed（拒绝连接，不回任何数据）。

- [ ] Chrome：版本 ____，App 版本 ____，架构 ____，结果 ____
- [ ] Edge：版本 ____，App 版本 ____，架构 ____，结果 ____

### 4. MV3 service worker suspend/resume 后持久端口保持/重连

保持标签页打开，等待浏览器把扩展的 MV3 service worker 挂起（或用 `chrome://extensions` 手动
“Service worker → 检查视图 → terminate”触发），确认 **service worker suspend/resume** 后：
与 native host 的持久 Native Messaging 端口要么保持存活，要么被重新建立；挂起期间的旧请求被取消（收到稳定
`AbortError`/`operation_aborted`），恢复后发起的新请求可以正常完成。

- [ ] Chrome：版本 ____，App 版本 ____，架构 ____，结果 ____
- [ ] Edge：版本 ____，App 版本 ____，架构 ____，结果 ____

### 5. 真实 MAIN-world 原型/`toJSON()` 与跨域 iframe Permissions Policy

用 `test-pages\passkey-get.html`、`passkey-create.html` 确认返回的凭据对象 `instanceof PublicKeyCredential`，
`response instanceof AuthenticatorAssertionResponse`/`AuthenticatorAttestationResponse`，且
`credential.toJSON()` 可用。再用 `test-pages\iframe-top.html` + `iframe-child.html`
（`python -m http.server 8787/8788`）验证：声明 `allow="publickey-credentials-get"` 的跨域 iframe
可进入 App 完成请求，未声明的 iframe 收到稳定 `SecurityError` 且不触发任何 App UI。

- [ ] Chrome：版本 ____，App 版本 ____，架构 ____，结果 ____
- [ ] Edge：版本 ____，App 版本 ____，架构 ____，结果 ____

### 6. `conditional`/`silent` 不激活 App，直接走浏览器原生账户选择

在 `passkey-get.html` 中把 mediation 设为 `conditional` 或 `silent` 后发起请求，确认 WinVaultWarden
不出现任何对话框、不被激活到前台、Host 不收到该请求；浏览器进入自己的原生（自动填充/静默）账户选择流程。

- [ ] Chrome：版本 ____，App 版本 ____，架构 ____，结果 ____
- [ ] Edge：版本 ____，App 版本 ____，架构 ____，结果 ____

### 7. 多凭据默认不选；frame/top/RP 的 Unicode/punycode 与跨域警告

对拥有多个匹配 discoverable 凭据的 RP 发起 get 请求，确认 WinUI 审批对话框列出全部候选且**默认不预选任何一个**，
必须用户显式点选。再用 IDN（如 `xn--` 编码域名）与跨域 iframe 场景确认对话框同时显示 Unicode 与 punycode
形式，跨域时显示明确警告；网站自报名称（RP `name`）只作为次要提示行，不能替代 origin/RP 身份展示。

- [ ] Chrome：版本 ____，App 版本 ____，架构 ____，结果 ____
- [ ] Edge：版本 ____，App 版本 ____，架构 ____，结果 ____

### 8. Windows Hello / 主密码 UV 与 Reprompt 真实手势

对 `userVerification: "required"` 的请求确认必须完成 **Windows Hello** 手势（PIN/生物识别）或主密码验证才能继续；
`preferred`/`discouraged` 分别确认 UI 提示与是否强制验证的差异；对标记了 Reprompt 的登录条目对应的 passkey，
确认使用前必须重新验证主密码，与揭示密码等其他敏感操作共用同一门禁。

- [ ] Chrome：版本 ____，App 版本 ____，架构 ____，结果 ____
- [ ] Edge：版本 ____，App 版本 ____，架构 ____，结果 ____

### 9. 对话框期间中断：Abort/导航/关闭标签/Lock/Logout/切账户/终止 Host

用 `abort-deadline.html` 在审批对话框打开时分别：点击页面 Abort 按钮、导航离开、关闭标签页、在 App 中执行 Lock、
Logout、切换账户，以及在任务管理器中终止 `BrowserNativeHost.exe`。每种情况下确认 App 审批对话框立即关闭，
不保存/不签名，且页面/扩展不会在之后收到任何迟到的响应。

- [ ] Chrome：版本 ____，App 版本 ____，架构 ____，结果 ____
- [ ] Edge：版本 ____，App 版本 ____，架构 ____，结果 ____

### 10. 用户拒绝/非法 RP/非安全来源/不允许的 iframe 只产生一次稳定 DOMException

分别触发：用户在审批对话框点击拒绝；请求一个非法/跨子域 RP id；从 `http://` 非 loopback 页面发起请求；
从未声明 Permissions Policy 的跨域 iframe 发起请求。每种情况必须只出现**一次**稳定的 `DOMException`
（`NotAllowedError` / `SecurityError` 等），不得出现第二次浏览器原生提示，也不得在页面控制台/结果区看到本地文件路径、
调用栈或内部异常文本。

- [ ] Chrome：版本 ____，App 版本 ____，架构 ____，结果 ____
- [ ] Edge：版本 ____，App 版本 ____，架构 ____，结果 ____

### 11. 卸载互不影响、可重复执行

分别对 Chrome、Edge 运行 `BrowserIntegration\uninstall-native-host.ps1 -Browser Chrome`（或 `Edge`），确认只删除
对应浏览器的 `HKCU\Software\...\NativeMessagingHosts\com.winvaultwarden.browser` 注册表项与其 manifest 文件，
不触碰另一浏览器的注册表/manifest；重复执行同一卸载命令两次都成功退出（幂等）。

- [ ] Chrome：版本 ____，App 版本 ____，架构 ____，结果 ____
- [ ] Edge：版本 ____，App 版本 ____，架构 ____，结果 ____

---

## MSIX 负验收（独立于 Chrome/Edge）

以下三项用于确认 **MSIX 彻底不提供浏览器集成**，而不是把它当作另一套可验收的安装形态：

- [ ] 无 Host：MSIX 安装后不存在 `BrowserNativeHost.exe`，`AppContext.BaseDirectory` 下也不应出现该文件。
- [ ] 设置禁用：`Settings → Browser integration` 在 packaged 状态下被禁用并强制保持关闭。
- [ ] 安装脚本 loud 拒绝：在 MSIX 路径运行 `install-native-host.ps1` 必须报错退出，且不写 registry/manifest。

## 范围负验收（Scope negative checks）

- [ ] 两份 manifest 均不存在 `action`、`default_popup`、`options_page`、`options_ui`。
- [ ] 扩展不检测密码字段，不写用户名/密码，不实现密码自动填充。
