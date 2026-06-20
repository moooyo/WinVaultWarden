# Vaultwarden / Bitwarden 兼容 API 完整契约

> **用途**:WinVaultWarden(Windows 原生 Bitwarden 兼容客户端)实现网络层的权威参考。
> **来源**:逐项核对 `D:\Code\vaultwarden` 源码(Rust),每条契约附 `文件:行号`。**非凭记忆或外部文档**。
> **大小写红线**:所有字段名严格照抄源码——Bitwarden 客户端对 PascalCase/camelCase 敏感,同一概念在不同端点可能大小写不同,**不可擅自统一**。
> 登录主线的深入细节另见 [vaultwarden-api-contracts.md](vaultwarden-api-contracts.md)。

---

## 目录

- [0. 全局通用约定](#0-全局通用约定)
- [1. Identity 身份认证](#1-identity-身份认证)(`/identity`)
- [2. Accounts 账户与设备](#2-accounts-账户与设备)(`/api`)
- [3. Ciphers 密码库条目与 Sync](#3-ciphers-密码库条目与-sync)(`/api`)
- [4. Folders 文件夹](#4-folders-文件夹)(`/api`)
- [5. Sends 分享](#5-sends-分享)(`/api`)
- [6. Organizations 组织](#6-organizations-组织)(`/api`)
- [7. Two-Factor 双因素认证](#7-two-factor-双因素认证)(`/api`)
- [8. Emergency Access 紧急访问](#8-emergency-access-紧急访问)(`/api`)
- [9. Notifications 实时通知(WebSocket)](#9-notifications-实时通知websocket)(`/notifications`)
- [10. Events / Icons / Config 其他](#10-events--icons--config-其他)

---

## 0. 全局通用约定

### 0.1 路由挂载前缀

源:`src/main.rs:585-591`。所有端点路径 = 前缀 + 各路由声明里的相对路径。

| 前缀 | 路由集 | 源文件 | 说明 |
| --- | --- | --- | --- |
| `/` | web_routes | `src/api/web.rs` | Web vault 静态资源 |
| `/api` | core_routes | `src/api/core/*.rs` | 核心业务 API(账户/条目/组织…) |
| `/admin` | admin_routes | `src/api/admin.rs` | 管理后台(客户端一般不用) |
| `/events` | core_events_routes | `src/api/core/events.rs` | 事件日志 |
| `/identity` | identity_routes | `src/api/identity.rs` | 登录、令牌、注册 |
| `/icons` | icons_routes | `src/api/icons.rs` | 网站图标代理 |
| `/notifications` | notifications_routes | `src/api/notifications.rs` | WebSocket 实时推送 |

> 本文每节标题注明所属前缀。节内端点路径均为**相对路径**,需自行拼接前缀。

### 0.2 认证机制

源:`src/auth.rs`。请求通过 Rocket 的 request guard 鉴权,客户端需按 guard 类型携带请求头:

- **`Headers`**(最常用):需 `Authorization: Bearer <access_token>`。access_token 是登录返回的 JWT。
- **`LoginHeaders`**:登录类端点,额外解析设备信息。
- **`OrgHeaders`** / **`ManagerHeaders`** / **`AdminHeaders`** / **`OwnerHeaders`**:组织端点,在 Bearer 基础上校验调用者在该组织的成员角色(见 [6. Organizations](#6-organizations-组织) 的 MembershipType)。
- **匿名端点**:prelogin、connect/token、register、send 匿名访问、icons 等不需要 Bearer。

通用请求头(客户端应始终携带):
- `Authorization: Bearer <jwt>`(已登录时)
- `Content-Type: application/json`(多数写操作)或 `application/x-www-form-urlencoded`(仅 `connect/token`)或 `multipart/form-data`(文件上传)
- `Bitwarden-Client-Name` / `Bitwarden-Client-Version`(客户端标识,服务端据此做版本兼容判断,如 SSH key、send-email-login 行为)
- `Device-Type`(整数,见 [DeviceType 枚举](#26-枚举-devicetype))

### 0.3 错误响应契约

源:`src/error.rs`、`src/api/core/mod.rs:295`。

Vaultwarden 错误统一返回 JSON。典型 400/500 业务错误形如:

```json
{
  "message": "错误描述",
  "validationErrors": { "": ["错误描述"] },
  "errorModel": { "message": "错误描述", "object": "error" },
  "exceptionMessage": null,
  "exceptionStackTrace": null,
  "innerExceptionMessage": null,
  "object": "error"
}
```

- 客户端解析错误优先读 `message`。
- **2FA required**、**invalid_grant** 等 OAuth 错误走 `connect/token` 专用结构(`error` / `error_description`,见 [1.3](#13-2fa-错误响应)),与上面通用结构不同。
- 404(catcher,`mod.rs:295`):`{ "error": { "code": 404, "reason": "Not Found", "description": "..." } }`。

HTTP 状态码:成功多为 `200`(含 JSON)或 `204 No Content`;鉴权失败 `401`;业务错误 `400`;限流 `429`。

### 0.4 列表响应包装

源:广泛使用,如 `ciphers.rs:219-221`、`accounts.rs:1370`。

多数"列表"端点返回统一包装(注意是 camelCase / 全小写 key):

```json
{
  "data": [ /* 元素数组 */ ],
  "continuationToken": null,
  "object": "list"
}
```

> 注意:`GET /sync` 是例外,直接返回各分类数组(见 [3.1](#31-get-syncsync))。部分简单端点直接返回裸数组。

### 0.5 日期格式

源:`src/util.rs` 的 `format_date`。统一输出 ISO 8601 UTC,形如 `2024-01-15T10:30:00.000000Z`(微秒精度)。客户端发送 `last_known_revision_date` 等也用 ISO 8601。

### 0.6 加密模型(客户端必须正确实现)

源:`src/crypto.rs`、`src/db/models/user.rs:160-215`。详见 [vaultwarden-api-contracts.md 第5节](vaultwarden-api-contracts.md)。要点:

- **服务端从不解密密码库数据**。`crypto.rs` 只有 PBKDF2/HMAC/随机数,无 EncString 解析。条目字段加解密 100% 是客户端职责。
- **KDF**:`Pbkdf2=0`(默认,`PBKDF2-HMAC-SHA256`,默认 600000 次)/ `Argon2id=1`。salt = 邮箱小写。
- **MasterKey** = KDF(主密码, salt=邮箱, 参数=prelogin 返回)。
- **MasterPasswordHash**(发给服务端的 `password`)= PBKDF2-SHA256(MasterKey, salt=主密码, 1次) 再 base64。**绝非明文**。
- **UserKey**:`Key`/`akey` 字段,用 MasterKey 包裹的对称密钥,客户端本地解开。
- **RSA 私钥**:`PrivateKey` 用 UserKey 加密,用于组织间共享。
- **EncString 格式**:`<encType>.<iv>|<ct>|<mac>`,常见 `encType=2`(AesCbc256_HmacSha256_B64)。
- 红线:主密码 / MasterKey / UserKey **绝不落盘明文、绝不写日志、绝不离开客户端进程**。

### 0.7 通用枚举速查

| 枚举 | 值 | 源 |
| --- | --- | --- |
| **CipherType** | Login=1, SecureNote=2, Card=3, Identity=4, SshKey=5 | `ciphers.rs:263-268` |
| **RepromptType** | None=0, Password=1 | `cipher.rs:64-67` |
| **SendType** | Text=0, File=1 | `send.rs:52-54` |
| **UserKdfType** | Pbkdf2=0, Argon2id=1 | `user.rs:89-91` |
| **TwoFactorType** | Authenticator=0, Email=1, Duo=2, YubiKey=3, U2f=4, Remember=5, OrganizationDuo=6, Webauthn=7, RecoveryCode=8 | `two_factor.rs:28-37` |
| **MembershipType**(组织角色) | Owner=0, Admin=1, User=2, Manager=3 | `organization.rs:96-100` |
| **MembershipStatus** | Revoked=-1, Invited=0, Accepted=1, Confirmed=2 | `organization.rs:75-79` |
| **EmergencyAccessType** | View=0, Takeover=1 | `emergency_access.rs:118-120` |
| **EmergencyAccessStatus** | Invited=0, Accepted=1, Confirmed=2, RecoveryInitiated=3, RecoveryApproved=4 | `emergency_access.rs:133-138` |
| **OrgPolicyType** | TwoFactorAuthentication=0, MasterPassword=1, PasswordGenerator=2, SingleOrg=3, PersonalOwnership=5, DisableSend=6, SendOptions=7, ResetPassword=8, RemoveUnlockWithPin=14, RestrictedItemTypes=15, UriMatchDefaults=16 | `org_policy.rs:31-53` |
| **DeviceType** | 见 [2.6](#26-枚举-devicetype) | `device.rs:276+` |

---

## 1. Identity 身份认证

前缀 `/identity`。源:`src/api/identity.rs`。完整登录主线另见 [vaultwarden-api-contracts.md](vaultwarden-api-contracts.md)。

### 1.1 POST /connect/token

获取访问令牌。**Content-Type: `application/x-www-form-urlencoded`**(唯一用表单的端点)。源:`identity.rs:59-136`。字段同时接受 snake_case 与 camelCase(`ConnectData`,`identity.rs:1090-1147`)。

按 `grant_type` 分四种:

**(a) `password`** —— 主密码登录(`identity.rs:348-468`):

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `grant_type` | ✓ | `password` |
| `username` | ✓ | 邮箱 |
| `password` | ✓ | **MasterPasswordHash**(见 [0.6](#06-加密模型客户端必须正确实现)),非明文 |
| `scope` | ✓ | 固定 `api offline_access`(`auth.rs:1157`) |
| `client_id` | ✓ | `web`/`desktop`/`browser`/`mobile`/`cli` |
| `device_identifier` | ✓ | 客户端生成的稳定 GUID |
| `device_name` | ✓ | 如 `windows` |
| `device_type` | ✓ | 整数,见 [2.6](#26-枚举-devicetype)(Windows 桌面=6) |
| `two_factor_provider` | | 2FA 类型(见 [0.7](#07-通用枚举速查)) |
| `two_factor_token` | | 2FA 验证码 |
| `two_factor_remember` | | `1` 返回记住令牌 |
| `auth_request` | | 被动登录授权 ID(用它时 `password` 传 access_code) |

**(b) `refresh_token`** —— 刷新(`identity.rs:138-176`):字段 `grant_type=refresh_token` + `refresh_token`。响应:`{access_token, refresh_token, expires_in, token_type:"Bearer", scope}`(全小写)。

**(c) `client_credentials`** —— API Key 登录(`identity.rs:570+`):`grant_type=client_credentials` + `client_id` + `client_secret` + `scope=api` + 设备字段。用于个人 API Key 或组织 API Key。

**(d) `authorization_code`** —— SSO(需服务端启用):`grant_type=authorization_code` + `code` + `code_verifier`(PKCE)+ 设备字段。

**成功响应**(`authenticated_response`,`identity.rs:470-568`):
```json
{
  "access_token": "<JWT>", "expires_in": 3600, "token_type": "Bearer",
  "refresh_token": "<token>", "scope": "api offline_access",
  "Key": "<akey 加密用户密钥>", "PrivateKey": "<RSA 私钥 EncString>",
  "Kdf": 0, "KdfIterations": 600000, "KdfMemory": null, "KdfParallelism": null,
  "ResetMasterPassword": false, "ForcePasswordReset": false,
  "MasterPasswordPolicy": { "Object": "masterPasswordPolicy" },
  "AccountKeys": {
    "publicKeyEncryptionKeyPair": {
      "wrappedPrivateKey": "<EncString>", "publicKey": "<base64>",
      "Object": "publicKeyEncryptionKeyPair" },
    "Object": "privateKeys" },
  "UserDecryptionOptions": {
    "HasMasterPassword": true,
    "MasterPasswordUnlock": {
      "Kdf": { "KdfType": 0, "Iterations": 600000, "Memory": null, "Parallelism": null },
      "MasterKeyEncryptedUserKey": "<akey>", "MasterKeyWrappedUserKey": "<akey>",
      "Salt": "user@example.com" },
    "Object": "userDecryptionOptions" },
  "TwoFactorToken": "<仅 two_factor_remember=1 时>"
}
```
> `Key` 仅在 `user.akey` 非空时出现(`identity.rs:558-560`)。注意此响应是 **PascalCase**,而 `/sync` 里同名结构是 camelCase。

### 1.2 prelogin / register

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| POST | `/accounts/prelogin` | 取 KDF 参数。请求 `{email}`,响应 `{kdf, kdfIterations, kdfMemory, kdfParallelism}`(全小写) | `identity.rs:1012`,`1234-1248` |
| POST | `/accounts/prelogin/password` | 同上 | `identity.rs:1017` |
| POST | `/accounts/register` | 直接注册。请求体 = RegisterData(见下) | `identity.rs:1022` |
| POST | `/accounts/register/send-verification-email` | 请求 `{email, name?}`。邮件开启时发验证邮件返回 204;否则直接返回 token 字符串 | `identity.rs:1042-1081` |
| POST | `/accounts/register/finish` | 邮箱验证后完成注册,RegisterData + `email_verification_token` | `identity.rs:1083` |

**RegisterData**(`accounts.rs:96-119`,camelCase + 别名):`email`、`masterPasswordHash`、`masterPasswordHint?`、`name?`、`key`(别名 `userSymmetricKey`)、`keys`(别名 `userAsymmetricKeys`,= `{encryptedPrivateKey, publicKey}`)、`kdf`(flatten:`kdf`/`kdfIterations`/`kdfMemory`/`kdfParallelism`)、`organizationUserId?`、`emailVerificationToken?`、`token?`(org 邀请,别名 `orgInviteToken`)、`acceptEmergencyAccessId?`、`acceptEmergencyAccessInviteToken?`。

### 1.3 2FA 错误响应

当启用 2FA 且未提供有效 `two_factor_token`,`connect/token` 返回 **HTTP 400** +(`json_err_twofactor`,`identity.rs:899-914`):
```json
{
  "error": "invalid_grant",
  "error_description": "Two factor required.",
  "TwoFactorProviders": ["0"],
  "TwoFactorProviders2": { "0": null },
  "MasterPasswordPolicy": { "Object": "masterPasswordPolicy" }
}
```
`TwoFactorProviders2` 内各类型附带数据(`identity.rs:916-1006`):Email→`{Email:"脱敏邮箱"}`、WebAuthn→assertion challenge、Duo→`{AuthUrl}` 或 `{Host,Signature}`、YubiKey→`{Nfc:bool}`、Authenticator→无附加。拿到验证码后**重发完整 password grant**并带 `two_factor_provider`+`two_factor_token`。

### 1.4 SSO(可选,服务端启用时)

| 方法 | 路径 | 源 |
| --- | --- | --- |
| GET | `/sso/prevalidate` | `identity.rs:1155` |
| GET | `/connect/oidc-signin?<code>&<state>` | `identity.rs:1169` |
| GET | `/connect/authorize?<data..>` | `identity.rs:1271` |

---

## 2. Accounts 账户与设备

前缀 `/api`。源:`src/api/core/accounts.rs`。所有端点需 `Authorization: Bearer`(除 prelogin)。

### 2.1 Profile 个人资料

| 方法 | 路径 | 请求 / 响应 | 源 |
| --- | --- | --- | --- |
| GET | `/accounts/profile` | 响应 = User profile(见 [2.5](#25-user-profile-结构)) | `accounts.rs:411` |
| PUT/POST | `/accounts/profile` | 请求 `{name}`(ProfileData,`culture` 被忽略);返回更新后 profile | `accounts.rs:423,428` |
| PUT | `/accounts/avatar` | 请求 `{avatarColor?}`;返回 profile | `accounts.rs:451` |
| GET | `/users/<user_id>/public-key` | 返回 `{userId, publicKey, object:"userKey"}` | `accounts.rs:471` |

### 2.2 密钥与密码

| 方法 | 路径 | 请求要点 | 源 |
| --- | --- | --- | --- |
| POST | `/accounts/keys` | `{encryptedPrivateKey, publicKey}`;返回 `{privateKey, publicKey, object:"keys"}` | `accounts.rs:486` |
| POST | `/accounts/password` | `{masterPasswordHash, newMasterPasswordHash, masterPasswordHint?, key}`(改密,key=新 UserKey 包裹) | `accounts.rs:513` |
| POST | `/accounts/kdf` | `{masterPasswordHash, newMasterPasswordHash, key, kdf, kdfIterations, kdfMemory?, kdfParallelism?}`;PBKDF2 迭代须 ≥100000 | `accounts.rs:615,553` |
| POST | `/accounts/key-management/rotate-user-account-keys` | 密钥轮换(带新 KDF + 全量重加密数据) | `accounts.rs:797` |
| POST | `/accounts/security-stamp` | 重置 security stamp(踢掉所有会话) | `accounts.rs:919` |
| POST | `/accounts/set-password` | 首次设密(SSO/邀请场景):`{kdf..., key, keys?, masterPasswordHash, masterPasswordHint?, orgIdentifier?}` | `accounts.rs:348` |
| POST | `/accounts/verify-password` | `{masterPasswordHash}` 校验密码 | `accounts.rs:1270` |
| POST | `/accounts/password-hint` | 匿名:`{email}` 找回密码提示 | `accounts.rs:1182` |

### 2.3 邮箱与账户删除

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| POST | `/accounts/email-token` | 请求换邮箱验证码 | `accounts.rs:943` |
| POST | `/accounts/email` | 确认换邮箱 | `accounts.rs:1005` |
| POST | `/accounts/verify-email` | 请求发验证邮件 | `accounts.rs:1057` |
| POST | `/accounts/verify-email-token` | 校验验证邮件 token | `accounts.rs:1079` |
| POST | `/accounts/delete-recover` | 匿名:请求账户删除恢复邮件 | `accounts.rs:1109` |
| POST | `/accounts/delete-recover-token` | 校验删除 token | `accounts.rs:1136` |
| POST / DELETE | `/accounts/delete` / `/accounts` | 删除账户(`{masterPasswordHash}`) | `accounts.rs:1154,1159` |

### 2.4 其他账户端点

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/accounts/revision-date` | 返回账户最后修改时间戳(毫秒数,用于判断是否需 sync) | `accounts.rs:1170` |
| POST | `/accounts/api-key` | `{masterPasswordHash}` 取个人 API Key | `accounts.rs:1302` |
| POST | `/accounts/rotate-api-key` | 轮换 API Key | `accounts.rs:1307` |
| GET | `/tasks` | 返回安全任务列表(`{data:[], object:"list"}`) | `accounts.rs:1449` |

### 2.5 设备

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/devices` | 当前用户所有设备,列表包装。Device::to_json:`{id, name, type, identifier, creationDate, isTrusted, object:"device"}` | `accounts.rs:1364` |
| GET | `/devices/knowndevice` | 头 `X-Device-Identifier`+`X-Request-Email`,返回 bool 是否已知设备 | `accounts.rs:1312` |
| GET | `/devices/identifier/<device_id>` | 单设备 | `accounts.rs:1376` |
| POST/PUT | `/devices/identifier/<device_id>/token` | 注册 push token | `accounts.rs:1390,1395` |
| PUT/POST | `/devices/identifier/<device_id>/clear-token` | 清除 push token | `accounts.rs:1422,1444` |

### 2.6 Auth Requests(被动/免密登录)

新设备发起登录请求,已登录设备审批。源:`accounts.rs:1469-1672`。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/auth-requests` | 发起:`{deviceIdentifier, accessCode, publicKey, type, email...}`。响应见下 |
| GET | `/auth-requests/<id>` | 轮询审批状态 |
| PUT | `/auth-requests/<id>` | 审批方批准/拒绝:`{requestApproved, key, masterPasswordHash, deviceIdentifier}` |
| GET | `/auth-requests/<id>/response?<code>` | 发起方用 accessCode 取审批结果(含加密的 key) |
| GET | `/auth-requests` / `/auth-requests/pending` | 列表 |

响应结构(`accounts.rs:1505+`):
```json
{
  "id": "<uuid>", "publicKey": "...", "requestDeviceType": "Android",
  "requestIpAddress": "1.2.3.4", "key": null, "masterPasswordHash": null,
  "creationDate": "...", "responseDate": null, "requestApproved": false,
  "origin": "https://vault.example.com", "object": "auth-request"
}
```

### 2.7 枚举 DeviceType

源:`device.rs:337-361`。`connect/token` 与设备端点用:

| 值 | 类型 | 值 | 类型 |
| --- | --- | --- | --- |
| 0 | Android | 12 | EdgeBrowser |
| 1 | iOS | 13 | IEBrowser |
| 2 | ChromeExtension | 14 | UnknownBrowser |
| 3 | FirefoxExtension | 15 | AndroidAmazon |
| 4 | OperaExtension | 16 | UWP |
| 5 | EdgeExtension | 17 | SafariBrowser |
| **6** | **WindowsDesktop** ← 本项目用 | 18 | VivaldiBrowser |
| 7 | MacOsDesktop | 19 | VivaldiExtension |
| 8 | LinuxDesktop | 20 | SafariExtension |
| 9 | ChromeBrowser | 21 | SDK |
| 10 | FirefoxBrowser | 22 | Server |
| 11 | OperaBrowser | 23/24 | Windows/MacOS CLI |

### 2.8 User Profile 结构

源:`user.rs` 的 `to_json`。`GET /accounts/profile` 与 `/sync` 的 `profile` 字段:
```json
{
  "id": "<uuid>", "name": "...", "email": "...", "emailVerified": true,
  "premium": true, "premiumFromOrganization": false, "culture": "en-US",
  "twoFactorEnabled": false, "key": "<akey>", "privateKey": "<EncString>",
  "securityStamp": "...", "organizations": [ ... ], "providers": [],
  "providerOrganizations": [], "forcePasswordReset": false,
  "avatarColor": null, "usesKeyConnector": false, "creationDate": "...",
  "object": "profile"
}
```

---

## 3. Ciphers 密码库条目与 Sync

前缀 `/api`。源:`src/api/core/ciphers.rs`(最大最核心)。需 `Authorization: Bearer`。

### 3.1 GET /sync(?<data..>)

首屏全量拉取。源:`ciphers.rs:121-204`。查询参数 `excludeDomains=<bool>`。

**响应**(直接返回,非列表包装):
```json
{
  "profile": { ...User profile,见 2.8... },
  "folders": [ ...Folder,见 4... ],
  "collections": [ ...Collection... ],
  "policies": [ ...OrgPolicy... ],
  "ciphers": [ ...cipherDetails,见 3.3... ],
  "domains": null,
  "sends": [ ...Send,见 5... ],
  "userDecryption": { "masterPasswordUnlock": { ...camelCase,见下... } },
  "object": "sync"
}
```
- `userDecryption.masterPasswordUnlock`(camelCase!与登录响应的 PascalCase 版本同源不同壳,`ciphers.rs:170-189`):`{kdf:{kdfType,iterations,memory,parallelism}, masterKeyEncryptedUserKey, masterKeyWrappedUserKey, salt}`。
- SSH key 条目(`type=5`)仅对客户端版本 ≥2024.12.0 返回(`ciphers.rs:128-137`)。

### 3.2 CipherData 请求结构

创建/更新条目时发送。源:`ciphers.rs:251-301`(camelCase):

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | string? | 仅批量分享时带 |
| `type` | int | CipherType:Login=1/SecureNote=2/Card=3/Identity=4/SshKey=5 |
| `name` | string | EncString 密文 |
| `notes` | string? | EncString |
| `folderId` | string? | |
| `organizationId` | string? | 别名 `organizationID` |
| `key` | string? | 条目级密钥(EncString) |
| `fields` | array? | 自定义字段 |
| `login` / `secureNote` / `card` / `identity` / `sshKey` | object? | **按 type 仅填一个** |
| `favorite` | bool? | |
| `reprompt` | int? | RepromptType:0/1 |
| `passwordHistory` | array? | |
| `attachments2` | map? | `{id: {fileName, key}}`,密钥轮换用 |
| `lastKnownRevisionDate` | string? | 乐观锁:与服务端不一致则拒绝更新防丢数据 |
| `archivedDate` | string? | |

各 type 的 data(`login`/`card` 等)是嵌套对象,字段均为 EncString,客户端自行构造(参考 Bitwarden 规范:login 含 `username`/`password`/`totp`/`uris[]`,card 含 `cardholderName`/`number`/`expMonth`/`expYear`/`code`/`brand`,等)。

### 3.3 cipherDetails 响应结构

源:`cipher.rs:336-368`(`Cipher::to_json`):
```json
{
  "object": "cipherDetails", "id": "<uuid>", "type": 1,
  "creationDate": "...", "revisionDate": "...", "deletedDate": null,
  "reprompt": 0, "organizationId": null, "key": null,
  "attachments": [ ...见 3.5... ], "organizationUseTotp": true,
  "collectionIds": [], "name": "<EncString>", "notes": null,
  "fields": null, "data": { ...type 专属数据展开... },
  "passwordHistory": null,
  "login": null, "secureNote": null, "card": null, "identity": null, "sshKey": null,
  "folderId": null, "favorite": false, "archivedDate": null
}
```
> `data` 字段冗余包含 type 专属数据 + `name`/`notes`/`fields`/`passwordHistory`(向后兼容,`cipher.rs:310-317`)。匹配 type 的那个对象(如 `login`)被填充,其余为 null。`folderId`/`favorite`/`archivedDate` 仅用户 sync 时出现,组织 sync 时省略。

### 3.4 条目读取 / 增删改

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/ciphers` | 用户全部条目,列表包装 | `ciphers.rs:206` |
| GET | `/ciphers/<id>` / `/details` / `/admin` | 单条目(不同详情级别) | `ciphers.rs:225,244,238` |
| POST | `/ciphers` | 创建(个人) | `ciphers.rs:361` |
| POST | `/ciphers/create` | 创建(带 collectionIds,组织条目) | `ciphers.rs:327` |
| POST | `/ciphers/admin` | 创建(组织管理员) | `ciphers.rs:319` |
| PUT/POST | `/ciphers/<id>` | 全量更新 | `ciphers.rs:680,669` |
| PUT/POST | `/ciphers/<id>/partial` | 部分更新:`{folderId?, favorite}` | `ciphers.rs:708,719` |
| PUT/POST | `/ciphers/<id>/collections` / `/collections_v2` | 改所属集合 | `ciphers.rs:784,757` |
| PUT/POST | `/ciphers/<id>/collections-admin` | 管理员改集合 | `ciphers.rs:864,875` |
| POST/PUT | `/ciphers/<id>/share` / `/ciphers/share` | 分享到组织(单/批量) | `ciphers.rs:953,966,986` |
| POST | `/ciphers/import` | 批量导入:`{ciphers:[CipherData], folders:[FolderData], folderRelationships:[{key,value}]}`(key=cipher索引, value=folder索引) | `ciphers.rs:595,580` |
| POST/PUT | `/ciphers/move` | 移动到文件夹:`{folderId?, ids:[...]}` | `ciphers.rs:1593,1647` |

### 3.5 软删除 / 回收站 / 归档

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| POST/PUT | `/ciphers/<id>/delete` | **软删除**(进回收站) | `ciphers.rs:1453,1465` |
| DELETE | `/ciphers/<id>` | **硬删除** | `ciphers.rs:1477` |
| DELETE/POST/PUT | `/ciphers` / `/ciphers/delete` | 批量删除:`{ids:[...]}` | `ciphers.rs:1489,1500,1511` |
| PUT | `/ciphers/<id>/restore` / `/ciphers/restore` | 从回收站恢复(单/批量) | `ciphers.rs:1555,1575` |
| POST | `/ciphers/purge` (?<organization..>) | 清空回收站:`{masterPasswordHash}` | `ciphers.rs:1666,1706` |
| PUT | `/ciphers/<id>/archive` / `/ciphers/archive` | 归档(单/批量) | `ciphers.rs:1732,1737` |
| PUT | `/ciphers/<id>/unarchive` / `/ciphers/unarchive` | 取消归档 | `ciphers.rs:1747,1752` |

> 多数 admin 变体(`/delete-admin`、`/restore-admin` 等)逻辑相同,仅鉴权要求组织管理员角色,此处省略。

### 3.6 附件

源:`ciphers.rs:1085-1442`。上传分两步(v2 协议):

1. **POST `/ciphers/<id>/attachment/v2`** — 请求 `{fileName, fileSize, key}`(均 EncString/加密元数据)。响应:
   ```json
   {
     "object": "attachment-fileUpload", "attachmentId": "<id>",
     "url": "/ciphers/<id>/attachment/<aid>",
     "fileUploadType": 0,
     "cipherResponse": { ...cipherDetails... }
   }
   ```
2. **POST `/ciphers/<id>/attachment/<aid>`**(`multipart/form-data`)— 上传加密后的文件体。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/ciphers/<id>/attachment/<aid>` | 取附件下载信息(含 url) |
| POST | `/ciphers/<id>/attachment`(multipart) | 旧版直接上传 |
| POST | `/ciphers/<id>/attachment/<aid>/share`(multipart) | 分享时重新上传 |
| DELETE / POST `.../delete` | `/ciphers/<id>/attachment/<aid>` | 删除附件 |

**Attachment::to_json**(`attachment.rs:68-78`):`{id, url, fileName, size(字符串), sizeName, key, object:"attachment"}`。

---

## 4. Folders 文件夹

前缀 `/api`。源:`src/api/core/folders.rs`(仅 108 行)。需 Bearer。

| 方法 | 路径 | 请求 | 源 |
| --- | --- | --- | --- |
| GET | `/folders` | 列表包装,元素 = Folder::to_json | `folders.rs:18` |
| GET | `/folders/<id>` | 单个 | `folders.rs:30` |
| POST | `/folders` | `{name}`(EncString) | `folders.rs:47` |
| PUT/POST | `/folders/<id>` | `{name}` | `folders.rs:70,59` |
| POST | `/folders/<id>/delete` / DELETE `/folders/<id>` | 删除 | `folders.rs:92,97` |

**Folder::to_json**(`folder.rs`):`{id, revisionDate, name, object:"folder"}`。

---

## 5. Sends 分享

前缀 `/api`(匿名访问端点也在 `/api` 下)。源:`src/api/core/sends.rs`。

### 5.1 端点

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/sends` | 我的全部 Send,列表包装 | `sends.rs:168` |
| GET | `/sends/<id>` | 单个 | `sends.rs:180` |
| POST | `/sends` | 创建文本 Send,body = SendData | `sends.rs:189` |
| POST | `/sends/file`(multipart) | 创建文件 Send(旧版,直接传文件) | `sends.rs:230` |
| POST | `/sends/file/v2` | 创建文件 Send(v2,先建元数据) | `sends.rs:302` |
| POST | `/sends/<id>/file/<fid>`(multipart) | v2 第二步上传文件体 | `sends.rs:374` |
| PUT | `/sends/<id>` | 更新 | `sends.rs:593` |
| DELETE | `/sends/<id>` | 删除 | `sends.rs:667` |
| PUT | `/sends/<id>/remove-password` | 移除访问密码 | `sends.rs:686` |
| **POST** | **`/sends/access/<access_id>`** | **匿名**访问(收件人,`{password?}`) | `sends.rs:450` |
| POST | `/sends/<id>/access/file/<fid>` | 匿名取文件下载 url | `sends.rs:509` |
| GET | `/sends/<id>/<fid>?<t>` | 文件下载(带临时 token) | `sends.rs:583` |

### 5.2 Send::to_json

源:`send.rs:140-171`:
```json
{
  "id": "<uuid>", "accessId": "<base64url>", "type": 0,
  "name": "<EncString>", "notes": null,
  "text": { ...仅 type=0... }, "file": null,
  "key": "<akey>", "maxAccessCount": null, "accessCount": 0,
  "password": null, "authType": 0,
  "disabled": false, "hideEmail": false,
  "revisionDate": "...", "expirationDate": null, "deletionDate": "...",
  "object": "send"
}
```
- `type`:SendType,Text=0 / File=1。`text` 与 `file` 按 type 二选一填充,另一个为 null。
- `text` data:`{text:<EncString>, hidden:bool}`;`file` data:`{id, fileName:<EncString>, size:"字符串", sizeName}`(mobile 要求 size 为字符串)。
- `authType`:0=无密码,1=有密码(`SendAuthType`)。

---

## 6. Organizations 组织

前缀 `/api`。源:`src/api/core/organizations.rs`(3168 行,70+ 端点)。鉴权用 `OrgHeaders`/`ManagerHeaders`/`AdminHeaders`/`OwnerHeaders`,校验调用者在组织内的 [MembershipType](#07-通用枚举速查) 角色。

> 端点极多,以下按类别给出**全部路径** + 关键请求/响应结构。同类重复的 admin/批量变体仅列路径。

### 6.1 组织 CRUD

| 方法 | 路径 | 请求/说明 | 源 |
| --- | --- | --- | --- |
| POST | `/organizations` | 创建:`{name, billingEmail, collectionName, key, keys?:{encryptedPrivateKey,publicKey}}` | `organizations.rs:193` |
| GET | `/organizations/<org_id>` | 单组织 = Organization::to_json | `organizations.rs:284` |
| PUT/POST | `/organizations/<org_id>` | 更新 | `organizations.rs:296,306` |
| DELETE/POST | `/organizations/<org_id>` / `/delete` | 删除(`{masterPasswordHash}`) | `organizations.rs:227,247` |
| POST | `/organizations/<org_id>/leave` | 退出组织 | `organizations.rs:257` |
| GET | `/organizations/<org_id>/public-key` | 组织公钥 | `organizations.rs:2886` |
| GET | `/organizations/<org_id>/keys` | 组织密钥对 | `organizations.rs:2903` |
| POST | `/organizations/<org_id>/api-key` / `/rotate-api-key` | 组织 API Key | `organizations.rs:3150,3160` |
| GET | `/organizations/<org_id>/export` | 导出组织数据 | `organizations.rs:3101` |

**Organization::to_json**(`organization.rs`)关键字段:`{id, name, billingEmail, planType:6, useGroups, useEvents, usePolicies, useApi, useResetPassword, hasPublicAndPrivateKeys, maxStorageGb, usersGetPremium:true, object:"organization", ...}`(大量 `useXxx`/`maxXxx` feature 标志,多为硬编码)。
**Membership::to_json**(用户视角,profile.organizations[] 元素):含组织信息 + `{id:org_uuid, status, type(角色), permissions:{...}, resetPasswordEnrolled, ...}`。

### 6.2 Collections 集合

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/collections` | 跨组织我可见的所有集合 | `organizations.rs:343` |
| GET | `/organizations/<org_id>/collections` | 组织全部集合 | `organizations.rs:386` |
| GET | `/organizations/<org_id>/collections/details` | 带详情(用户/组) | `organizations.rs:403` |
| POST | `/organizations/<org_id>/collections` | 创建:`{name, groups:[], users:[], externalId?}` | `organizations.rs:495` |
| PUT/POST | `/organizations/<org_id>/collections/<col_id>` | 更新 | `organizations.rs:627,638` |
| DELETE/POST | `/organizations/<org_id>/collections/<col_id>` / `/delete` | 删除 | `organizations.rs:730,740` |
| DELETE | `/organizations/<org_id>/collections` | 批量删除 | `organizations.rs:756` |
| GET | `/organizations/<org_id>/collections/<col_id>/details` | 单集合详情 | `organizations.rs:778` |
| GET | `/organizations/<org_id>/collections/<col_id>/users` | 集合成员 | `organizations.rs:845` |
| POST | `/organizations/<org_id>/collections/bulk-access` | 批量授权 | `organizations.rs:563` |

**Collection::to_json**(`collection.rs:68`):`{externalId, id, organizationId, name, object:"collection"}`。details 版额外含 `readOnly`/`hidePasswords`/`manage` 权限位。

**CollectionData**(分配集合权限时):`{id, readOnly, hidePasswords, manage}`(`organizations.rs`)。

### 6.3 Members 成员管理

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/organizations/<org_id>/users?<data..>` | 成员列表 | `organizations.rs:938` |
| GET | `/organizations/<org_id>/users/mini-details` | 精简列表 | `organizations.rs:1463` |
| GET | `/organizations/<org_id>/users/<member_id>` | 单成员 | `organizations.rs:1480` |
| POST | `/organizations/<org_id>/users/invite` | 邀请:`{emails:[], groups:[], type, collections?:[CollectionData], permissions:{}}` | `organizations.rs:1029` |
| POST | `/organizations/<org_id>/users/reinvite` / `/<member_id>/reinvite` | 重新邀请 | `organizations.rs:1173,1208` |
| POST | `/organizations/<org_id>/users/<member_id>/accept` | 接受邀请 | `organizations.rs:1271` |
| POST | `/organizations/<org_id>/users/confirm` / `/<member_id>/confirm` | 确认成员(交换组织密钥):`{key}` | `organizations.rs:1339,1382` |
| PUT/POST | `/organizations/<org_id>/users/<member_id>` | 改角色/权限/集合 | `organizations.rs:1511,1522` |
| DELETE | `/organizations/<org_id>/users/<member_id>` / `/users` | 移除成员(单/批量) | `organizations.rs:1666,1630` |
| PUT | `/organizations/<org_id>/users/<member_id>/revoke` / `/revoke` | 吊销访问 | `organizations.rs:2231,2241` |
| PUT | `/organizations/<org_id>/users/<member_id>/restore` / `/restore` | 恢复访问 | `organizations.rs:2336,2346` |
| POST | `/organizations/<org_id>/users/public-keys` | 批量取成员公钥 | `organizations.rs:1721` |
| PUT | `/organizations/<org_id>/users/<member_id>/reset-password` | 管理员重置成员密码 | `organizations.rs:2908` |
| GET | `/organizations/<org_id>/users/<member_id>/reset-password-details` | 重置详情 | `organizations.rs:2970` |
| PUT | `/organizations/<org_id>/users/<user_id>/reset-password-enrollment` | 加入/退出密码重置 | `organizations.rs:3043` |

### 6.4 Groups 组

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/organizations/<org_id>/groups` / `/details` | 组列表 | `organizations.rs:2458,2463` |
| POST | `/organizations/<org_id>/groups` / `/<group_id>` | 创建/更新组 | `organizations.rs:2537,2526` |
| PUT | `/organizations/<org_id>/groups/<group_id>` | 更新 | `organizations.rs:2570` |
| GET | `/organizations/<org_id>/groups/<group_id>` / `/details` | 单组 | `organizations.rs:2741,2652` |
| DELETE/POST | `/organizations/<org_id>/groups/<group_id>` / `/delete` | 删除 | `organizations.rs:2683,2673` |
| DELETE | `/organizations/<org_id>/groups` | 批量删除 | `organizations.rs:2719` |
| GET | `/organizations/<org_id>/groups/<group_id>/users` | 组成员 | `organizations.rs:2757` |
| PUT | `/organizations/<org_id>/groups/<group_id>/users` | 设置组成员 | `organizations.rs:2784` |
| POST | `/organizations/<org_id>/groups/<group_id>/delete-user/<member_id>` | 移除组成员 | `organizations.rs:2831` |

**Group::to_json**(`group.rs`):`{id, organizationId, name, accessAll, externalId, object:"group", ...}`。

### 6.5 Policies 策略

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/organizations/<org_id>/policies` | 全部策略,列表包装 | `organizations.rs:1933` |
| GET | `/organizations/<org_id>/policies/token?<token>` | 邀请 token 查策略(匿名) | `organizations.rs:1948` |
| GET | `/organizations/<org_id>/policies/master-password` | 主密码策略 | `organizations.rs:1984` |
| GET | `/organizations/<org_id>/policies/<pol_type>` | 单类型策略 | `organizations.rs:1999` |
| PUT | `/organizations/<org_id>/policies/<pol_type>` | 设置策略:`{enabled, data}` | `organizations.rs:2023` |

`<pol_type>` 取 [OrgPolicyType](#07-通用枚举速查) 整数值。**OrgPolicy::to_json**(`org_policy.rs`):`{id, organizationId, type, data, enabled, object:"policy"}`。

### 6.6 其他组织端点

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/organizations/<id>/auto-enroll-status` | 自动加入状态 | `organizations.rs:359` |
| POST | `/ciphers/import-organization?<query..>` | 组织批量导入条目 | `organizations.rs:1782` |
| POST | `/ciphers/bulk-collections` | 批量改条目集合 | `organizations.rs:1879` |
| GET | `/ciphers/organization-details?<data..>` | 组织条目详情 | `organizations.rs:879` |
| GET | `/plans` | 套餐列表(兼容用,硬编码) | `organizations.rs:2164` |
| GET | `/organizations/<id>/billing/...` | 账单元数据(多为占位) | `organizations.rs:2192+` |

> 公开端点(前缀 `/api/public`,源 `public.rs:53`):`POST /public/organization/import` —— SCIM/目录同步用,用组织 API Key 鉴权。

---

## 7. Two-Factor 双因素认证

前缀 `/api`。源:`src/api/core/two_factor/`。除 `send-email-login` 外均需 Bearer。各"管理"端点需先用 `{masterPasswordHash}` 或 OTP 验证(`PasswordOrOtpData`)。

### 7.1 通用

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/two-factor` | 已启用的 2FA 列表(列表包装,元素 `{enabled, type, object:"twoFactorProvider"}`) | `mod.rs:90` |
| POST | `/two-factor/get-recover` | `{masterPasswordHash/otp}` 取恢复码,返回 `{code, object:"twoFactorRecover"}` | `mod.rs:108` |
| POST | `/two-factor/recover` | 用恢复码禁用所有 2FA(匿名,`{email, masterPasswordHash, recoveryCode}`) | `mod.rs` |
| POST/PUT | `/two-factor/disable` | 禁用某 provider:`{type, masterPasswordHash/otp}` | `mod.rs:137,169` |
| GET | `/two-factor/get-device-verification-settings` | 设备验证设置 | `mod.rs:295` |

### 7.2 各 Provider(get 取配置 / put 启用 / delete 禁用)

| Provider | 端点 | 源 |
| --- | --- | --- |
| **Authenticator(TOTP)** | `POST /two-factor/get-authenticator`、`POST/PUT /two-factor/authenticator`、`DELETE` | `authenticator.rs:21,56,96` |
| **Email** | `POST /two-factor/get-email`、`POST /two-factor/send-email`、`PUT /two-factor/email`、`POST /two-factor/send-email-login`(登录中发码,**可匿名**,`{email, masterPasswordHash?, authRequestId?, deviceIdentifier?}`) | `email.rs:126,159,203,39` |
| **WebAuthn** | `POST /two-factor/get-webauthn`、`/get-webauthn-challenge`、`POST/PUT /two-factor/webauthn`、`DELETE` | `webauthn.rs:110,131,255,318` |
| **Duo** | `POST /two-factor/get-duo`、`POST/PUT /two-factor/duo` | `duo.rs:92,158,196` |
| **YubiKey** | `POST /two-factor/get-yubikey`、`POST/PUT /two-factor/yubikey` | `yubikey.rs:85,118,178` |

### 7.3 Protected Actions(敏感操作 OTP)

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| POST | `/accounts/request-otp` | 请求发送保护性操作 OTP(邮件) | `protected_actions.rs:64` |
| POST | `/accounts/verify-otp` | 校验 OTP:`{otp}` | `protected_actions.rs:106` |

**TwoFactorType 完整枚举** 见 [0.7](#07-通用枚举速查)。

---

## 8. Emergency Access 紧急访问

前缀 `/api`。源:`src/api/core/emergency_access.rs`。需 Bearer。需服务端开启 `emergency_access_allowed`。

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/emergency-access/trusted` | 我信任的紧急联系人(我是 grantor) | `emergency_access.rs:48` |
| GET | `/emergency-access/granted` | 授予我的(我是 grantee) | `emergency_access.rs:69` |
| GET | `/emergency-access/<id>` | 单条详情 | `emergency_access.rs:88` |
| PUT/POST | `/emergency-access/<id>` | 更新(waitTimeDays/type) | `emergency_access.rs:115,125` |
| DELETE/POST | `/emergency-access/<id>` / `/delete` | 删除 | `emergency_access.rs:162,185` |
| POST | `/emergency-access/invite` | 邀请:`{email, type, waitTimeDays}` | `emergency_access.rs:202` |
| POST | `/emergency-access/<id>/reinvite` | 重新邀请 | `emergency_access.rs:281` |
| POST | `/emergency-access/<id>/accept` | 接受:`{token}` | `emergency_access.rs:331` |
| POST | `/emergency-access/<id>/confirm` | 确认:`{key}`(grantor 用 grantee 公钥包裹 UserKey) | `emergency_access.rs:392` |
| POST | `/emergency-access/<id>/initiate` | grantee 发起紧急访问 | `emergency_access.rs:445` |
| POST | `/emergency-access/<id>/approve` | grantor 批准 | `emergency_access.rs:483` |
| POST | `/emergency-access/<id>/reject` | grantor 拒绝 | `emergency_access.rs:518` |
| POST | `/emergency-access/<id>/view` | grantee 查看库(View 类型) | `emergency_access.rs:555` |
| POST | `/emergency-access/<id>/takeover` | grantee 接管(Takeover 类型) | `emergency_access.rs:593` |
| POST | `/emergency-access/<id>/password` | 接管后重置密码:`{key, newMasterPasswordHash}` | `emergency_access.rs:631` |
| GET | `/emergency-access/<id>/policies` | 取目标账户策略 | `emergency_access.rs:677` |

**EmergencyAccess::to_json**(`emergency_access.rs:63`):`{id, status, type, waitTimeDays, object:"emergencyAccess"}`;details 版附 grantor/grantee 信息。
枚举:[EmergencyAccessType / EmergencyAccessStatus](#07-通用枚举速查)。

---

## 9. Notifications 实时通知(WebSocket)

前缀 `/notifications`。源:`src/api/notifications.rs`。协议:**SignalR over WebSocket + MessagePack**。

### 9.1 连接与握手

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/hub?<data..>` | 主 WebSocket,需 access_token(查询参数 `access_token`) | `notifications.rs:106` |
| GET | `/anonymous-hub?<token..>` | 匿名 hub(auth-request 用) | `notifications.rs:190` |

握手流程(SignalR,`notifications.rs:152-160,309-319`):
1. 客户端连上后先发握手帧:`{"protocol":"messagepack","version":1}` + **记录分隔符 `0x1e`**(`RECORD_SEPARATOR`)。
2. 服务端回 `INITIAL_RESPONSE` = `0x7b 0x7d 0x1e`(即 `{}` + RS)表示握手成功。
3. 之后消息体用 **MessagePack** 编码,每帧以 `0x1e` 结尾。
4. 心跳:服务端定时发 WebSocket `Ping`,客户端回 `Pong`(`notifications.rs` ping/pong 分支)。

> SignalR 协商端点 `/notifications/hub/negotiate` 由 web vault 层处理,不是独立 Rust 路由。客户端通常直接走 WebSocket 升级。

### 9.2 服务端推送消息(UpdateType)

服务端主动推送变更,客户端据此局部刷新而非全量 sync。源:`notifications.rs:622-653`。

| 值 | UpdateType | 值 | UpdateType |
| --- | --- | --- | --- |
| 0 | SyncCipherUpdate | 10 | SyncSettings |
| 1 | SyncCipherCreate | 11 | LogOut |
| 2 | SyncLoginDelete | 12 | SyncSendCreate |
| 3 | SyncFolderDelete | 13 | SyncSendUpdate |
| 4 | SyncCiphers | 14 | SyncSendDelete |
| 5 | SyncVault | 15 | AuthRequest |
| 6 | SyncOrgKeys | 16 | AuthRequestResponse |
| 7 | SyncFolderCreate | 100 | None |
| 8 | SyncFolderUpdate | | |

> 推送(push.rs):移动端 push 通知注册,Vaultwarden 转发到 Bitwarden push relay。桌面客户端主要依赖上面的 WebSocket,push 注册可不实现。

---

## 10. Events / Icons / Config 其他

### 10.1 Events 事件日志

前缀 `/events`(查询)与 `/api`(collect)。源:`src/api/core/events.rs`。需组织事件功能开启。

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/events/organizations/<org_id>/events?<data..>` | 组织事件(查询 `start`/`end`/`continuationToken`) | `events.rs:33` |
| GET | `/events/ciphers/<cipher_id>/events?<data..>` | 单条目事件 | `events.rs:65` |
| GET | `/events/organizations/<org_id>/users/<member_id>/events?<data..>` | 成员事件 | `events.rs:91` |
| POST | `/api/collect` | 客户端上报事件 | `events.rs:164` |

**Event::to_json**(`event.rs`):`{type, userId, organizationId, cipherId, collectionId, groupId, organizationUserId, actingUserId, date, deviceType, ipAddress, object:"event"}`。EventType 有 78+ 个值(`event.rs`,如 1000=User_LoggedIn、1100=Cipher_Created…),需要时查源码枚举。

### 10.2 Icons 图标

前缀 `/icons`。源:`src/api/icons.rs`。匿名。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/icons/<host>/icon.png` | 取网站 favicon(服务端代理/重定向到 icon service,带缓存) |

### 10.3 Config 与杂项

前缀 `/api`。源:`src/api/core/mod.rs`。

| 方法 | 路径 | 说明 | 源 |
| --- | --- | --- | --- |
| GET | `/config` | **重要**:服务器配置。返回 `{version:"2025.12.0", gitHash, server:{name:"Vaultwarden",url}, environment:{vault,api,identity,notifications}, featureStates:{...}, object:"config"}` | `mod.rs:210` |
| GET | `/settings/domains` | 等价域名设置 | `mod.rs:75` |
| POST/PUT | `/settings/domains` | 更新等价域名 | `mod.rs:112,138` |
| GET | `/hibp/breach?<username>` | HaveIBeenPwned 泄露查询 | `mod.rs:148` |
| GET | `/alive` | 健康检查,返回当前时间 | `mod.rs:183` |
| GET | `/now` | 服务器时间 | `mod.rs:188` |
| GET | `/version` | 版本 | `mod.rs:193` |
| GET | `/webauthn` | WebAuthn 配置 | `mod.rs:198` |

> 客户端启动应先打 `GET /api/config` 探测服务器版本与 feature flags,据此决定行为兼容性。

---

*本文档覆盖 Vaultwarden 全部对客户端开放的 API 端点(200+),按业务域组织。Admin 后台(`/admin`)端点客户端通常不需要,未详列。如发现与源码不符,以 `D:\Code\vaultwarden` 当前代码为准。*

---


