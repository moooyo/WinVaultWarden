# WinVaultWarden 项目骨架设计

> 状态:已与用户对齐,待复审
> 日期:2026-06-21
> 范围:从零搭建解决方案骨架。Crypto 项目做真实实现,其余项目为占位骨架。

## 1. 目标与范围

为 WinVaultWarden(Windows 原生 Bitwarden 兼容客户端)建立可编译运行的解决方案骨架。本次**不**实现真实业务功能(登录联网、sync、组织、2FA、WebSocket 等),但**例外**:`Crypto` 项目实现白皮书 PBKDF2 主路的真实加解密逻辑,因为它是纯函数、不依赖 UI/网络,最适合独立编写与验证。

### 本次交付(In Scope)

- `WinVaultWarden.sln` + 4 个 csproj(`App` / `Core` / `Crypto` / `Api`),依赖关系连好,能编译。
- `Core`:基础领域模型 POCO + 服务接口 + 抽象接口。
- `Crypto`:**真实实现** PBKDF2 全链(详见第 4 节),全部用微软官方 `System.Security.Cryptography`。
- `Api`:`ApiClient` 占位 + 少量核心 DTO(prelogin / 登录 / sync)。
- `App`:WinUI 3 + MSIX 打包,DI 装配 + LoginPage/VaultPage(XAML 占位)+ ViewModel + 导航。

### 明确不做(Out of Scope,YAGNI)

- 真实 HTTP 请求与登录联网、sync 解析、2FA、组织/集合、Send、紧急访问、WebSocket 实时同步。
- Argon2id 派生分支的实现(接口预留,抛 `NotImplementedException`)。
- 测试项目(Crypto 设计成纯函数式,预留测试边界,后续补白皮书测试向量)。
- 凭据持久化、自动锁定、系统通知等系统集成。

## 2. 前置条件(重要)

**本机当前无 .NET SDK,也无 Visual Studio**(已通过 `where dotnet` / 常见安装路径 / vswhere 确认均无)。因此:

- 本设计落地时,我**手写全部工程文件与源码**,但**无法在本机执行 `dotnet build` 验证编译**。
- 用户需先安装工具链(见下)。首次构建若因 TFM 版本号、Windows SDK 细节等报错,届时据报错调整。

**安装清单**:

1. **.NET 10 SDK**(LTS,2025-11)——`dotnet --list-sdks` 应能看到 `10.0.x`。
2. **Visual Studio 2022/2026**,勾选 **“Windows 应用开发(.NET)”** 工作负载(含 WinUI 3 / Windows App SDK 模板与 MSIX 打包工具)。
3. **Windows App SDK 2.2.0 Runtime**(若用打包模式,VS 工作负载通常已带;独立运行时按需从官方下载页安装)。

> 打包(MSIX)模式说明:VS 里 F5 可正常断点调试、热重载;但**命令行 `dotnet run` 不支持启动打包应用**,需经 VS 或 MSIX 部署。这是选打包模式的已知约束。

## 3. 解决方案分层

四个项目,依赖方向**单向向下**,无环:

```
WinVaultWarden.sln
│
├─ App        [WinUI3 可执行, net10.0-windows10.0.26100.0, MSIX 打包]
│    ├─ Views/            XAML 页面(LoginPage / VaultPage,占位)
│    ├─ ViewModels/       CommunityToolkit.Mvvm
│    ├─ App.xaml(.cs)     程序入口 + DI 容器装配
│    └─ 依赖 → Core, Crypto, Api
│
├─ Core       [类库 net10.0, 平台无关]
│    ├─ Models/           领域模型(User/Cipher/Folder/Send… POCO)
│    ├─ Services/         IAuthService / ISyncService / IVaultService 接口(+占位)
│    ├─ Abstractions/     ICryptoService / IApiClient 接口
│    └─ 不依赖 App/UI/Crypto/Api
│
├─ Crypto     [类库 net10.0]  ← 本次真实实现
│    ├─ ICryptoService 实现(真实 PBKDF2 链)
│    ├─ SymmetricCryptoKey / EncString / KdfConfig 类型
│    └─ 依赖 → Core(实现其 ICryptoService 接口)
│
└─ Api        [类库 net10.0]
     ├─ ApiClient(占位, 基于 HttpClient)
     ├─ Dtos/             请求/响应 DTO(大小写严格匹配 API.md)
     └─ 依赖 → Core
```

**设计原则**:

- `Core` 是中枢,定义接口与模型;`Crypto`、`Api` 各自实现 `Core` 的接口,**互不依赖**。
- `App` 只依赖抽象,启动时通过 DI 注入具体实现。
- 项目名 = 程序集名 = 根命名空间,三者一致用短名(`namespace Core` / `namespace Crypto` 等)。单一应用、不发布 NuGet,短名取舍可接受。

## 4. Crypto 设计(严格遵循 Bitwarden 安全白皮书)

权威来源:Bitwarden 官方安全白皮书 + Vaultwarden 源码核实 + Bitwarden 官方 KDF 文档。白皮书故意省略的实现常量已从后两者补齐。

### 4.1 密钥派生链(登录时本地计算)

```
1. MasterKey = KDF(主密码, salt, 参数 = prelogin 返回)  → 32 字节(256-bit)
     ├ PBKDF2-HMAC-SHA256, 默认 600,000 次, salt = 原始邮箱小写字符串        【本次实现】
     └ Argon2id, 默认 iter=3 / mem=64MiB / parallel=4,
        salt = SHA-256(邮箱小写)的 32 字节摘要(注意:与 PBKDF2 不同!)     【接口预留, 抛 NotImplemented】
     ※ 两条路径 salt 处理不同:PBKDF2 用原始邮箱字节,Argon2id 用邮箱的 SHA-256 哈希。

2. MasterPasswordHash(发送给服务端的 `password` 字段)
     = PBKDF2-HMAC-SHA256(password = MasterKey, salt = 主密码字节, iterations = 1), 再 base64
     ※ 绝非明文主密码

3. 由 MasterKey 拉伸出两把 32 字节子钥(各做一次独立 HKDF-Expand,SHA-256):
     StretchedEncKey = HKDF-Expand(prk = MasterKey, info = "enc", L = 32)
     StretchedMacKey = HKDF-Expand(prk = MasterKey, info = "mac", L = 32)
     ※ 是 Expand-only(非完整 HKDF,不做 Extract);info 是确切字节串 "enc" / "mac"。
     ※ 合称 StretchedMasterKey(64 字节 = EncKey 32 ‖ MacKey 32)。

4. UserKey = 用 (StretchedEncKey, StretchedMacKey) 解密 protected symmetric key
     （= 登录响应 `Key`/`akey`,通常是 encType 2 的 EncString）
     解密所得 UserKey 一般为 64 字节(EncKey 32 ‖ MacKey 32);
     若源为 encType 0,则为 32 字节(仅 EncKey,无 MacKey)。长度由该 EncString 的 encType 决定。

5. RSA 私钥 = 用 UserKey 解密登录响应 `PrivateKey`(EncString),得到 DER 编码私钥
     用于组织共享密钥交换(RSA-OAEP,见 4.3 的 encType 3/4)。
```

### 4.2 加解密原语

- **Vault 数据**:AES-256-CBC + HMAC-SHA256(PKCS7 填充)。
- **MAC 验证(解密时)**:在解密前先验 MAC——`HMAC-SHA256(MacKey, IV ‖ ciphertext)`,即 MAC 覆盖 **IV 在前、密文在后** 的拼接。计算值与 EncString 携带的 MAC 段做**恒定时间比较**(`CryptographicOperations.FixedTimeEquals`),通过才解密。
- **特例**:encType 0(AesCbc256_B64)无 MAC 段,跳过 MAC 验证直接解密(仅历史遗留数据可能用到)。
- 服务端永不解密(已从 Vaultwarden `crypto.rs` 确认,服务端只存密文)。

### 4.3 EncString 类型(`<encType>.<payload>`)

| encType | 含义 | 结构 | 本次 |
| --- | --- | --- | --- |
| 0 | AesCbc256_B64 | `iv\|ct`(无 MAC) | 解析+解密 |
| 1 | AesCbc128_HmacSha256_B64 | `iv\|ct\|mac` | 解析+解密 |
| **2** | **AesCbc256_HmacSha256_B64** | `iv\|ct\|mac`(当前主流) | **核心实现** |
| 3 | Rsa2048_OaepSha256_B64 | RSA 密文 | 解密(组织共享) |
| 4 | Rsa2048_OaepSha1_B64 | RSA 密文 | 解密 |

### 4.4 类型与接口

- `KdfConfig`:`KdfType`(Pbkdf2=0 / Argon2id=1)、`Iterations`、`Memory?`、`Parallelism?`。
- `SymmetricCryptoKey`:封装 EncKey(32) + 可选 MacKey(32)。接受 32 字节(仅 Enc)或 64 字节(Enc‖Mac)输入,据长度判定是否带 MAC。
- `EncString`:解析 `<encType>.iv|ct|mac` 字符串(encType 0 与 RSA 类型无 `mac` 段),持有 encType 与各分段字节。
- `ICryptoService`(纯函数式,无副作用):
  - `byte[] DeriveMasterKey(string password, string email, KdfConfig kdf)` — 内部按 KdfType 分派;PBKDF2 用原始邮箱为 salt,Argon2id 用 SHA-256(邮箱) 为 salt
  - `string ComputeMasterPasswordHash(byte[] masterKey, string password)`
  - `SymmetricCryptoKey StretchMasterKey(byte[] masterKey)` — 两次 HKDF-Expand(info="enc"/"mac")得 64 字节
  - `SymmetricCryptoKey DecryptUserKey(SymmetricCryptoKey stretchedKey, EncString protectedUserKey)`
  - `byte[] Decrypt(EncString data, SymmetricCryptoKey key)` — 先验 MAC(覆盖 IV‖ct)再解密
  - `EncString Encrypt(byte[] plaintext, SymmetricCryptoKey key)`
  - `byte[] DecryptRsa(EncString data, byte[] privateKeyDer)`

### 4.5 安全红线(写入代码注释与文档)

主密码 / MasterKey / UserKey 仅存于内存,**绝不落盘明文、绝不写日志**。骨架阶段不做持久化,正合规。

## 5. App 装配与占位策略

### 5.1 DI 容器装配(`App.xaml.cs` 启动时)

```
ServiceCollection 注册:
  ICryptoService  → CryptoService          【真实实现】
  IApiClient      → ApiClient              【占位】
  IAuthService    → AuthService            【占位】
  ISyncService    → SyncService            【占位】
  IVaultService   → VaultService           【占位】
  LoginViewModel / VaultViewModel
→ 构建 ServiceProvider,View 从容器取 ViewModel
```

### 5.2 占位策略

- 占位服务方法体:`throw new NotImplementedException("TODO: ...")` 或返回空集合/默认值,确保启动不崩。
- `LoginPage`:服务器地址 / 邮箱 / 主密码 三输入框 + 登录按钮(点击暂不联网或提示未实现)。
- `VaultPage`:空列表占位。
- 目的:跑起来看到窗口、页面骨架与导航通路,验证分层 + DI + MVVM,而非真实业务。

### 5.3 App csproj 关键配置

- `TargetFramework`:`net10.0-windows10.0.26100.0`(随安装的 Windows SDK 调整)。
- `TargetPlatformMinVersion`:`10.0.17763.0`(典型最小值)。
- `<UseWinUI>true</UseWinUI>`。
- 打包模式(MSIX):`<EnableMsixTooling>true</EnableMsixTooling>`,含 `Package.appxmanifest`;**不**设 `WindowsPackageType=None`。
- `Platforms`:`x64;arm64`;`RuntimeIdentifiers`:`win-x64;win-arm64`。
- NuGet:`Microsoft.WindowsAppSDK` 2.2.0、`CommunityToolkit.Mvvm`、`Microsoft.Extensions.DependencyInjection`。

## 6. 技术栈版本(动工前复核)

- **.NET 10**(LTS)。
- **Windows App SDK 2.2.0**(稳定版,2026-06-09)。
- **WinUI 3**(随 Windows App SDK)。
- **CommunityToolkit.Mvvm**(源生成器,最新稳定版)。
- **Microsoft.Extensions.DependencyInjection**(最新稳定版)。
- Crypto 零第三方依赖(仅微软官方 `System.Security.Cryptography`)。

## 7. 风险与已知约束

1. **无法本机编译验证**:手写工程文件,首次构建可能因 TFM/SDK 细节报错,需据报错迭代。
2. **MSIX + 无 VS**:命令行无法启动打包应用,验证依赖用户在 VS 中 F5。
3. **Crypto 正确性**:本次实现暂无测试覆盖;接口为纯函数式,后续应第一时间补白皮书测试向量(已知答案验证)。
4. **版本漂移**:Windows App SDK / .NET 版本号随时间过时,动工前用 `dotnet --list-sdks` 与 NuGet 复核。

## 8. 验收标准

- 解决方案在装好工具链的机器上能 `dotnet build`(或 VS 构建)通过。
- VS F5 启动,显示主窗口与 LoginPage,可导航到 VaultPage(占位)。
- `Crypto` 项目的 `ICryptoService` PBKDF2 链代码完整可读、逻辑符合白皮书(即便暂无自动化测试)。
- 其余项目占位实现不导致启动崩溃。
