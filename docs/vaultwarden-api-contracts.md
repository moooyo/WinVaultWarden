# Vaultwarden API 契约参考(登录主线 + 加密模型)

> 本文档由逐行精读 `D:\Code\vaultwarden` 源码确认,非凭记忆。每条契约附源文件:行号。
> 字段大小写按源码原样照抄——Bitwarden 客户端对大小写敏感,**不可擅自统一**。
> 路由前缀在 `src/main.rs:585-591` 挂载。

## 0. 关键架构约束

- **服务端从不解密密码库数据。** `src/crypto.rs` 只有 PBKDF2 派生、HMAC、随机数,**没有 EncString 解析**。条目的用户名/密码/备注等都是 EncString 密文,加解密 100% 是客户端责任。
- 登录响应同一份数据用**两种大小写**返回(顶层 PascalCase + 嵌套 camelCase),为兼容不同客户端版本,见下文 `MasterPasswordUnlock`。
- 表单字段名同时接受 snake_case 与 camelCase(`ConnectData` 用 `#[field(name = uncased(...))]` 双重标注)。

## 1. Prelogin —— 获取 KDF 参数

`POST /identity/accounts/prelogin`(亦存在于 `/api/accounts/prelogin`)
源:`src/api/identity.rs:1234-1248`、`src/api/core/accounts.rs:1229`

**请求**(camelCase):
```json
{ "email": "user@example.com" }
```

**响应**(camelCase,注意全小写 `kdf`):
```json
{
  "kdf": 0,
  "kdfIterations": 600000,
  "kdfMemory": null,
  "kdfParallelism": null
}
```

- 用户不存在时返回默认值:`kdf=0`(PBKDF2),`kdfIterations=600000`,内存/并行度为 `null`(`src/db/models/user.rs:109-110`)。
- **KDF 枚举**(`src/db/models/user.rs:89-91`):`Pbkdf2 = 0`,`Argon2id = 1`。
- Argon2id 时 `kdfMemory`(MiB)与 `kdfParallelism` 才非空。

## 2. 登录取令牌 —— password grant

`POST /identity/connect/token`,**Content-Type: application/x-www-form-urlencoded**
源:`src/api/identity.rs:59-136`(分发)、`348-468`(password_login)

**请求表单字段**(`ConnectData`,`src/api/identity.rs:1090-1147`):

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `grant_type` | ✓ | `password` |
| `username` | ✓ | 邮箱 |
| `password` | ✓ | **MasterPasswordHash**(见第 5 节),不是明文主密码 |
| `scope` | ✓ | 固定 `api offline_access`(`src/auth.rs:1157`) |
| `client_id` | ✓ | `web`/`desktop`/`browser`/`mobile`/`cli` |
| `device_identifier` | ✓ | 客户端生成的稳定 GUID |
| `device_name` | ✓ | 如 `windows` |
| `device_type` | ✓ | 整数枚举,Windows 桌面= **6**(`src/db/models/device.rs:290`) |
| `two_factor_provider` | | 2FA 类型(见第 3 节) |
| `two_factor_token` | | 2FA 验证码 |
| `two_factor_remember` | | `1` 则返回记住令牌 |
| `auth_request` | | 被动登录(免密)授权 ID |

**device_type 常用值**(`src/db/models/device.rs:276-308`):Android=0, iOS=1, ChromeExtension=2, **WindowsDesktop=6**, MacOsDesktop=7, LinuxDesktop=8, ChromeBrowser=9, FirefoxBrowser=10。

**成功响应**(`authenticated_response`,`src/api/identity.rs:470-568`):
```json
{
  "access_token": "<JWT>",
  "expires_in": 3600,
  "token_type": "Bearer",
  "refresh_token": "<token>",
  "scope": "api offline_access",
  "Key": "<akey 加密用户密钥>",
  "PrivateKey": "<RSA 私钥, EncString>",
  "Kdf": 0,
  "KdfIterations": 600000,
  "KdfMemory": null,
  "KdfParallelism": null,
  "ResetMasterPassword": false,
  "ForcePasswordReset": false,
  "MasterPasswordPolicy": { "Object": "masterPasswordPolicy" },
  "AccountKeys": {
    "publicKeyEncryptionKeyPair": {
      "wrappedPrivateKey": "<EncString>",
      "publicKey": "<base64>",
      "Object": "publicKeyEncryptionKeyPair"
    },
    "Object": "privateKeys"
  },
  "UserDecryptionOptions": {
    "HasMasterPassword": true,
    "MasterPasswordUnlock": {
      "Kdf": { "KdfType": 0, "Iterations": 600000, "Memory": null, "Parallelism": null },
      "MasterKeyEncryptedUserKey": "<akey>",
      "MasterKeyWrappedUserKey": "<akey>",
      "Salt": "user@example.com"
    },
    "Object": "userDecryptionOptions"
  },
  "TwoFactorToken": "<仅 two_factor_remember=1 时存在>"
}
```

- `Key`(=`user.akey`)仅在非空时出现(`identity.rs:558-560`)。它是用主密钥包裹的用户对称密钥。
- 刷新:`grant_type=refresh_token` + `refresh_token`,响应见 `identity.rs:161-173`(小写 `access_token`/`refresh_token`/`scope`)。
- API Key 登录:`grant_type=client_credentials` + `client_id`/`client_secret`,scope=`api`。

## 3. 双因素认证(2FA)

当账户启用 2FA 且未提供有效 `two_factor_token` 时,`connect/token` 返回 **HTTP 400** + 如下 JSON(`json_err_twofactor`,`src/api/identity.rs:899-914`):

```json
{
  "error": "invalid_grant",
  "error_description": "Two factor required.",
  "TwoFactorProviders": ["0"],
  "TwoFactorProviders2": { "0": null },
  "MasterPasswordPolicy": { "Object": "masterPasswordPolicy" }
}
```

**TwoFactorType 枚举**(`src/db/models/two_factor.rs:28-37`):
`Authenticator=0`, `Email=1`, `Duo=2`, `YubiKey=3`, `U2f=4`, `Remember=5`, `OrganizationDuo=6`, `Webauthn=7`, `RecoveryCode=8`。

`TwoFactorProviders2` 内各 provider 附带的数据(`identity.rs:916-1006`):
- **Email(1)**:`{ "Email": "ob***@example.com" }`(脱敏邮箱);若 email 是唯一方式则服务端立即发码。新版客户端(≥2025.5.0)改为主动调 `/api/two-factor/send-email-login`。
- **WebAuthn(7)**:`{ ...assertion challenge... }`。
- **Duo(2)**:`{ "AuthUrl": ... }`(OIDC)或 `{ "Host", "Signature" }`(iframe)。
- **YubiKey(3)**:`{ "Nfc": bool }`。
- **Authenticator(0)**:无附加数据,客户端直接输入 TOTP。

客户端拿到验证码后,**重发完整 password grant** 并带上 `two_factor_provider` + `two_factor_token`。RecoveryCode(8)/Remember(5) 是特殊类型,走单独校验分支(`identity.rs:837-875`)。

## 4. 首次同步 —— GET /api/sync

`GET /api/sync?excludeDomains=<bool>`,需 `Authorization: Bearer <access_token>`
源:`src/api/core/ciphers.rs:121-204`

**响应**(全 camelCase / 小写 key):
```json
{
  "profile": { ... User::to_json ... },
  "folders": [ ... ],
  "collections": [ ... ],
  "policies": [ ... ],
  "ciphers": [ ... ],
  "domains": null,
  "sends": [ ... ],
  "userDecryption": { "masterPasswordUnlock": { ... } },
  "object": "sync"
}
```

- `profile`(`User::to_json`,`src/db/models/user.rs`):含 `id`/`email`/`name`/`key`(=akey)/`privateKey`/`securityStamp`/`organizations`/`object:"profile"` 等。
- `ciphers[]`(`Cipher::to_json`,`src/db/models/cipher.rs:337+`):`object:"cipherDetails"`、`id`、`type`(整数)、`name`/`notes`(EncString)、`key`、`organizationId`、`favorite`、`reprompt`、`login`/`card`/`identity`/`secureNote`/`sshKey`(按 type 二选一)、`creationDate`/`revisionDate`。
- SSH key 类型(`type=5`)仅对客户端版本 ≥2024.12.0 返回(`ciphers.rs:128-137`)。
- **注意大小写不一致**:登录响应的 `MasterPasswordUnlock` 是 PascalCase,sync 里的 `masterPasswordUnlock` 是 camelCase(源码 `ciphers.rs:170-171` 明确注释了这点)。

## 5. 加密模型(客户端必须正确实现)

源:`src/crypto.rs:1-24`、`src/db/models/user.rs:160-215`

派生链(Bitwarden 标准,服务端只参与最后一步校验):

1. **MasterKey** = `KDF(主密码, salt=邮箱小写, 参数=prelogin 返回值)`。PBKDF2 时为 `PBKDF2-HMAC-SHA256`,迭代次数取 `kdfIterations`。
2. **MasterPasswordHash**(即发给服务端的 `password` 字段)= `PBKDF2-SHA256(password=MasterKey, salt=主密码, iterations=1)`,再 base64。**这才是 `connect/token` 里传的值,绝非明文。**
3. 服务端二次哈希存储:`password_hash = PBKDF2-SHA256(MasterPasswordHash, salt=user.salt(64字节随机), iterations=user.password_iterations)`(`user.rs:200-201`),校验用 `verify_password_hash`(`crypto.rs:21-24`)。客户端无需关心这一步。
4. **UserKey(对称)**:服务端下发的 `Key`/`akey` 是用 MasterKey(经 stretch)包裹的 EncString。客户端本地用 MasterKey 解开得到真正的 UserKey。
5. **RSA 私钥**:`PrivateKey`/`privateKey` 是用 UserKey 加密的 EncString,解开后用于组织间共享(收件箱密钥交换)。
6. **条目字段**:每个 EncString 用 UserKey(或条目自带的 `key`)解密。

EncString 格式(Bitwarden 规范,客户端自行实现):`<encType>.<iv>|<ct>|<mac>`,常见 `encType=2` 表示 AesCbc256_HmacSha256_B64。

> 安全红线:主密码、MasterKey、UserKey **绝不落盘明文、绝不写日志、绝不离开客户端进程内存**。

## 6. 实现登录主线的调用顺序

```
1. POST /identity/accounts/prelogin   → 拿 KDF 参数
2. 本地派生 MasterKey + MasterPasswordHash
3. POST /identity/connect/token (password grant)
   ├─ 200 → 拿 access_token + Key/PrivateKey,本地解出 UserKey
   └─ 400 invalid_grant "Two factor required" → 走 2FA,带 token 重发
4. GET /api/sync → 拉全量库,逐条用 UserKey 解密 EncString
5. 令牌过期 → grant_type=refresh_token 刷新
```
