# WinVaultWarden 骨架实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 从零搭建 WinVaultWarden 解决方案骨架:4 个项目(App/Core/Crypto/Api)+ Crypto 单元测试,其中 Crypto 真实实现 Bitwarden 白皮书 PBKDF2 加解密链,其余为可编译占位。

**Architecture:** Core 定义模型与接口;Crypto、Api 各自实现 Core 的接口且互不依赖;App(WinUI 3 + MSIX)通过 DI 装配。依赖方向单向向下,无环。

**Tech Stack:** .NET 10、Windows App SDK 2.2.0、WinUI 3、CommunityToolkit.Mvvm、Microsoft.Extensions.DependencyInjection、xUnit、System.Security.Cryptography(零第三方加密依赖)。

## Global Constraints

- **TFM**:类库与测试用 `net10.0`;App 用 `net10.0-windows10.0.26100.0`(随安装的 Windows SDK 调整)。
- **项目名 = 程序集名 = 根命名空间**,全用短名:`Core` / `Crypto` / `Api` / `App` / `Crypto.Tests`。
- **加密只用微软官方 `System.Security.Cryptography`**;Argon2id 分支抛 `NotImplementedException`,本次不引入第三方包。
- **字段大小写严格匹配 API.md**(Bitwarden 客户端大小写敏感)。
- **安全红线**:主密码 / MasterKey / UserKey 仅存于内存,绝不落盘明文、绝不写日志。
- **本机无 .NET SDK**:实现者手写全部文件;`dotnet build`/`dotnet test` 的"运行"步骤在装好工具链后由执行机执行,期望值已写入测试。
- **加密权威来源**:Bitwarden 安全白皮书 + Vaultwarden 源码 + 独立实现 BitwardenDecrypt(已交叉核实)。

---

## 文件结构

```
WinVaultWarden.sln
src/
  Core/
    Core.csproj
    Enums/CipherType.cs, KdfType.cs, EncryptionType.cs
    Models/User.cs, Cipher.cs, Folder.cs, Send.cs
    Abstractions/ICryptoService.cs, IApiClient.cs
    Services/IAuthService.cs, ISyncService.cs, IVaultService.cs
  Crypto/
    Crypto.csproj
    KdfConfig.cs
    EncString.cs
    SymmetricCryptoKey.cs
    CryptoService.cs
  Api/
    Api.csproj
    ApiClient.cs
    Dtos/PreloginDtos.cs, TokenDtos.cs, SyncDtos.cs
  App/
    App.csproj
    App.xaml, App.xaml.cs
    Package.appxmanifest
    Services/ServiceConfiguration.cs
    Views/LoginPage.xaml(.cs), VaultPage.xaml(.cs)
    ViewModels/LoginViewModel.cs, VaultViewModel.cs
tests/
  Crypto.Tests/
    Crypto.Tests.csproj
    EncStringTests.cs
    KdfTests.cs
    StretchAndUserKeyTests.cs
    EncryptDecryptTests.cs
```

依赖:`App → Core, Crypto, Api`;`Crypto → Core`;`Api → Core`;`Crypto.Tests → Crypto, Core`。

---

## Task 1: 解决方案 + Core 项目(模型/枚举/接口)

**Files:**
- Create: `WinVaultWarden.sln`
- Create: `src/Core/Core.csproj`
- Create: `src/Core/Enums/CipherType.cs`, `KdfType.cs`, `EncryptionType.cs`
- Create: `src/Core/Models/User.cs`, `Cipher.cs`, `Folder.cs`, `Send.cs`
- Create: `src/Core/Abstractions/ICryptoService.cs`, `IApiClient.cs`
- Create: `src/Core/Services/IAuthService.cs`, `ISyncService.cs`, `IVaultService.cs`

**Interfaces:**
- Consumes: 无(首个任务)
- Produces:
  - `enum Core.Enums.KdfType { Pbkdf2 = 0, Argon2id = 1 }`
  - `enum Core.Enums.EncryptionType { AesCbc256_B64 = 0, AesCbc128_HmacSha256_B64 = 1, AesCbc256_HmacSha256_B64 = 2, Rsa2048_OaepSha256_B64 = 3, Rsa2048_OaepSha1_B64 = 4 }`
  - `enum Core.Enums.CipherType { Login = 1, SecureNote = 2, Card = 3, Identity = 4, SshKey = 5 }`
  - `interface Core.Abstractions.ICryptoService`(签名见 Task 3-5,本任务先定义完整接口)
  - `interface Core.Abstractions.IApiClient`
  - 模型 POCO:`Core.Models.User/Cipher/Folder/Send`

- [ ] **Step 1: 创建解决方案文件**

`WinVaultWarden.sln`(最小手写 sln;GUID 用固定值便于复现):

```
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.0.0
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Core", "src\Core\Core.csproj", "{11111111-1111-1111-1111-111111111111}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Crypto", "src\Crypto\Crypto.csproj", "{22222222-2222-2222-2222-222222222222}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Api", "src\Api\Api.csproj", "{33333333-3333-3333-3333-333333333333}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{44444444-4444-4444-4444-444444444444}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Crypto.Tests", "tests\Crypto.Tests\Crypto.Tests.csproj", "{55555555-5555-5555-5555-555555555555}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|Any CPU
		{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{33333333-3333-3333-3333-333333333333}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{33333333-3333-3333-3333-333333333333}.Release|Any CPU.Build.0 = Release|Any CPU
		{44444444-4444-4444-4444-444444444444}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{44444444-4444-4444-4444-444444444444}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{44444444-4444-4444-4444-444444444444}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{44444444-4444-4444-4444-444444444444}.Release|Any CPU.Build.0 = Release|Any CPU
		{55555555-5555-5555-5555-555555555555}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{55555555-5555-5555-5555-555555555555}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{55555555-5555-5555-5555-555555555555}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{55555555-5555-5555-5555-555555555555}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
EndGlobal
```

- [ ] **Step 2: 创建 Core.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Core</RootNamespace>
    <AssemblyName>Core</AssemblyName>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: 创建枚举**

`src/Core/Enums/KdfType.cs`:
```csharp
namespace Core.Enums;

public enum KdfType
{
    Pbkdf2 = 0,
    Argon2id = 1,
}
```

`src/Core/Enums/EncryptionType.cs`:
```csharp
namespace Core.Enums;

// Bitwarden EncString encType。结构见 EncString 解析。
public enum EncryptionType
{
    AesCbc256_B64 = 0,              // iv|ct,无 MAC
    AesCbc128_HmacSha256_B64 = 1,  // iv|ct|mac
    AesCbc256_HmacSha256_B64 = 2,  // iv|ct|mac(当前主流)
    Rsa2048_OaepSha256_B64 = 3,    // RSA 密文
    Rsa2048_OaepSha1_B64 = 4,      // RSA 密文
}
```

`src/Core/Enums/CipherType.cs`:
```csharp
namespace Core.Enums;

public enum CipherType
{
    Login = 1,
    SecureNote = 2,
    Card = 3,
    Identity = 4,
    SshKey = 5,
}
```

- [ ] **Step 4: 创建模型 POCO**

`src/Core/Models/User.cs`:
```csharp
namespace Core.Models;

// 登录后用户档案的最小骨架。字段对应 /sync 的 profile。
public sealed class User
{
    public string Id { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Name { get; init; }
    // akey:用 MasterKey 包裹的 UserKey(EncString 文本)。
    public string Key { get; init; } = string.Empty;
    // 用 UserKey 加密的 RSA 私钥(EncString 文本)。
    public string? PrivateKey { get; init; }
}
```

`src/Core/Models/Cipher.cs`:
```csharp
using Core.Enums;

namespace Core.Models;

// 密码库条目骨架。name/notes 等为 EncString 密文,解密是客户端职责。
public sealed class Cipher
{
    public string Id { get; init; } = string.Empty;
    public CipherType Type { get; init; }
    public string? OrganizationId { get; init; }
    public string? FolderId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public bool Favorite { get; init; }
    public DateTimeOffset RevisionDate { get; init; }
}
```

`src/Core/Models/Folder.cs`:
```csharp
namespace Core.Models;

public sealed class Folder
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset RevisionDate { get; init; }
}
```

`src/Core/Models/Send.cs`:
```csharp
namespace Core.Models;

public sealed class Send
{
    public string Id { get; init; } = string.Empty;
    public int Type { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset? DeletionDate { get; init; }
}
```

- [ ] **Step 5: 创建抽象接口**

`src/Core/Abstractions/ICryptoService.cs`(完整签名,实现在 Crypto 项目):
```csharp
using Core.Enums;

namespace Core.Abstractions;

// 纯函数式加密服务。所有方法无副作用,不持有可变状态。
// KdfConfig / EncString / SymmetricCryptoKey 由 Crypto 项目定义并在此引用为 object?
// —— 为保持 Core 零依赖,接口用 Crypto 项目的类型时改由 Crypto 自身的扩展承载。
// 故此接口仅声明不依赖 Crypto 类型的方法;含 Crypto 类型的方法定义在 Crypto 项目内。
public interface ICryptoService
{
    // 由主密码 + 邮箱 + KDF 参数派生 32 字节 MasterKey。
    byte[] DeriveMasterKey(string password, string email, KdfType kdfType, int iterations, int? memoryMiB, int? parallelism);

    // 计算发送给服务端的 MasterPasswordHash(base64)。
    string ComputeMasterPasswordHash(byte[] masterKey, string password);
}
```

> 说明:`SymmetricCryptoKey`/`EncString`/`KdfConfig` 是 Crypto 项目的类型。为遵守"Core 不依赖 Crypto",涉及这些类型的解密方法(`StretchMasterKey`/`DecryptUserKey`/`Decrypt`/`Encrypt`/`DecryptRsa`)定义在 Crypto 项目的 `CryptoService` 上,App 通过 Crypto 项目直接调用。Core 的 `ICryptoService` 只暴露字节进字节出的两个 KDF 方法,供需要抽象的上层使用。

`src/Core/Abstractions/IApiClient.cs`:
```csharp
namespace Core.Abstractions;

// 网络层抽象。骨架阶段为占位,方法签名最小化。
public interface IApiClient
{
    // 设置服务端基址(如 https://vault.example.com)。
    void SetBaseAddress(string baseUrl);
}
```

- [ ] **Step 6: 创建服务接口(占位)**

`src/Core/Services/IAuthService.cs`:
```csharp
namespace Core.Services;

public interface IAuthService
{
    // 登录主线占位:prelogin → 派生 → connect/token。
    Task LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default);
}
```

`src/Core/Services/ISyncService.cs`:
```csharp
using Core.Models;

namespace Core.Services;

public interface ISyncService
{
    Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default);
}
```

`src/Core/Services/IVaultService.cs`:
```csharp
using Core.Models;

namespace Core.Services;

public interface IVaultService
{
    IReadOnlyList<Cipher> GetCiphers();
}
```

- [ ] **Step 7: 提交**

```bash
git add WinVaultWarden.sln src/Core
git commit -m "feat: add solution and Core project skeleton (models, enums, interfaces)"
```

---

## Task 2: Crypto 基础类型(KdfConfig / EncString / SymmetricCryptoKey)+ 测试

**Files:**
- Create: `src/Crypto/Crypto.csproj`
- Create: `src/Crypto/KdfConfig.cs`, `EncString.cs`, `SymmetricCryptoKey.cs`
- Create: `tests/Crypto.Tests/Crypto.Tests.csproj`
- Create: `tests/Crypto.Tests/EncStringTests.cs`

**Interfaces:**
- Consumes: `Core.Enums.EncryptionType`、`Core.Enums.KdfType`
- Produces:
  - `Crypto.KdfConfig { KdfType KdfType; int Iterations; int? MemoryMiB; int? Parallelism }`
  - `Crypto.EncString`:属性 `EncryptionType Type`、`byte[] Iv`、`byte[] Ct`、`byte[]? Mac`;静态 `EncString Parse(string s)`;`override string ToString()`
  - `Crypto.SymmetricCryptoKey`:构造 `SymmetricCryptoKey(byte[] keyBytes)`;属性 `byte[] EncKey`(32)、`byte[]? MacKey`(32 或 null)、`byte[] FullKey`

- [ ] **Step 1: 创建 Crypto.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Crypto</RootNamespace>
    <AssemblyName>Crypto</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core\Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 创建 Crypto.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RootNamespace>Crypto.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Crypto\Crypto.csproj" />
    <ProjectReference Include="..\..\src\Core\Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: 写失败测试 —— EncString 解析**

`tests/Crypto.Tests/EncStringTests.cs`:
```csharp
using Core.Enums;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class EncStringTests
{
    [Fact]
    public void Parse_Type2_SplitsIvCtMac()
    {
        // 构造 type 2:iv(16B)|ct(16B)|mac(32B),均 base64
        var iv = Convert.ToBase64String(new byte[16]);
        var ct = Convert.ToBase64String(new byte[16]);
        var mac = Convert.ToBase64String(new byte[32]);
        var s = $"2.{iv}|{ct}|{mac}";

        var enc = EncString.Parse(s);

        Assert.Equal(EncryptionType.AesCbc256_HmacSha256_B64, enc.Type);
        Assert.Equal(16, enc.Iv.Length);
        Assert.Equal(16, enc.Ct.Length);
        Assert.NotNull(enc.Mac);
        Assert.Equal(32, enc.Mac!.Length);
    }

    [Fact]
    public void Parse_Type0_HasNoMac()
    {
        var iv = Convert.ToBase64String(new byte[16]);
        var ct = Convert.ToBase64String(new byte[16]);
        var s = $"0.{iv}|{ct}";

        var enc = EncString.Parse(s);

        Assert.Equal(EncryptionType.AesCbc256_B64, enc.Type);
        Assert.Null(enc.Mac);
    }

    [Fact]
    public void ToString_RoundTripsType2()
    {
        var iv = Convert.ToBase64String(new byte[16]);
        var ct = Convert.ToBase64String(new byte[16]);
        var mac = Convert.ToBase64String(new byte[32]);
        var s = $"2.{iv}|{ct}|{mac}";

        Assert.Equal(s, EncString.Parse(s).ToString());
    }
}
```

- [ ] **Step 4: 运行测试,确认失败**

Run: `dotnet test tests/Crypto.Tests --filter EncStringTests`
Expected: FAIL —— `EncString` 类型不存在 / 编译错误。

- [ ] **Step 5: 实现 KdfConfig**

`src/Crypto/KdfConfig.cs`:
```csharp
using Core.Enums;

namespace Crypto;

// KDF 参数,来自 prelogin 响应。
public sealed record KdfConfig(KdfType KdfType, int Iterations, int? MemoryMiB, int? Parallelism)
{
    public static KdfConfig Pbkdf2Default => new(KdfType.Pbkdf2, 600_000, null, null);
    public static KdfConfig Argon2idDefault => new(KdfType.Argon2id, 3, 64, 4);
}
```

- [ ] **Step 6: 实现 EncString**

`src/Crypto/EncString.cs`:
```csharp
using Core.Enums;

namespace Crypto;

// 解析 Bitwarden EncString:"<encType>.<base64 段...>"
// type 0:iv|ct;type 1/2:iv|ct|mac;type 3/4(RSA):data(单段)。
public sealed class EncString
{
    public EncryptionType Type { get; }
    public byte[] Iv { get; }
    public byte[] Ct { get; }
    public byte[]? Mac { get; }

    public EncString(EncryptionType type, byte[] iv, byte[] ct, byte[]? mac)
    {
        Type = type;
        Iv = iv;
        Ct = ct;
        Mac = mac;
    }

    public static EncString Parse(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new FormatException("EncString 为空");

        var dot = value.IndexOf('.');
        if (dot < 0)
            throw new FormatException("EncString 缺少 encType 前缀");

        var type = (EncryptionType)int.Parse(value[..dot]);
        var payload = value[(dot + 1)..];
        var parts = payload.Split('|');

        return type switch
        {
            EncryptionType.AesCbc256_B64 =>
                new EncString(type, Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), null),
            EncryptionType.AesCbc128_HmacSha256_B64 or EncryptionType.AesCbc256_HmacSha256_B64 =>
                new EncString(type, Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), Convert.FromBase64String(parts[2])),
            EncryptionType.Rsa2048_OaepSha256_B64 or EncryptionType.Rsa2048_OaepSha1_B64 =>
                new EncString(type, Array.Empty<byte>(), Convert.FromBase64String(parts[0]), null),
            _ => throw new FormatException($"未知 encType: {(int)type}"),
        };
    }

    public override string ToString()
    {
        var t = (int)Type;
        return Type switch
        {
            EncryptionType.AesCbc256_B64 =>
                $"{t}.{Convert.ToBase64String(Iv)}|{Convert.ToBase64String(Ct)}",
            EncryptionType.AesCbc128_HmacSha256_B64 or EncryptionType.AesCbc256_HmacSha256_B64 =>
                $"{t}.{Convert.ToBase64String(Iv)}|{Convert.ToBase64String(Ct)}|{Convert.ToBase64String(Mac!)}",
            EncryptionType.Rsa2048_OaepSha256_B64 or EncryptionType.Rsa2048_OaepSha1_B64 =>
                $"{t}.{Convert.ToBase64String(Ct)}",
            _ => throw new InvalidOperationException(),
        };
    }
}
```

- [ ] **Step 7: 实现 SymmetricCryptoKey**

`src/Crypto/SymmetricCryptoKey.cs`:
```csharp
namespace Crypto;

// 对称密钥封装:32 字节(仅 EncKey)或 64 字节(EncKey 32 ‖ MacKey 32)。
public sealed class SymmetricCryptoKey
{
    public byte[] FullKey { get; }
    public byte[] EncKey { get; }
    public byte[]? MacKey { get; }

    public SymmetricCryptoKey(byte[] keyBytes)
    {
        FullKey = keyBytes;
        switch (keyBytes.Length)
        {
            case 32:
                EncKey = keyBytes;
                MacKey = null;
                break;
            case 64:
                EncKey = keyBytes[..32];
                MacKey = keyBytes[32..];
                break;
            default:
                throw new ArgumentException($"密钥长度须为 32 或 64,实际 {keyBytes.Length}");
        }
    }

    public SymmetricCryptoKey(byte[] encKey, byte[] macKey)
    {
        EncKey = encKey;
        MacKey = macKey;
        FullKey = [.. encKey, .. macKey];
    }
}
```

- [ ] **Step 8: 运行测试,确认通过**

Run: `dotnet test tests/Crypto.Tests --filter EncStringTests`
Expected: PASS(3 个测试)。

- [ ] **Step 9: 提交**

```bash
git add src/Crypto tests/Crypto.Tests
git commit -m "feat: add Crypto base types (KdfConfig, EncString, SymmetricCryptoKey) with tests"
```

---

## Task 3: KDF 派生(DeriveMasterKey / ComputeMasterPasswordHash)+ 测试

**Files:**
- Create: `src/Crypto/CryptoService.cs`
- Create: `tests/Crypto.Tests/KdfTests.cs`

**Interfaces:**
- Consumes: `Core.Abstractions.ICryptoService`、`Core.Enums.KdfType`、`Crypto.KdfConfig`
- Produces: `Crypto.CryptoService : ICryptoService`,实现 `DeriveMasterKey`、`ComputeMasterPasswordHash`

- [ ] **Step 1: 写自校验测试 —— PBKDF2 与内置实现一致**

> 不依赖外部"魔数":用 .NET 内置 `Rfc2898DeriveBytes` 独立算一遍,断言我们的封装结果一致。装好工具链即恒真。

`tests/Crypto.Tests/KdfTests.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class KdfTests
{
    private const string Email = "nobody@example.com";
    private const string Password = "p4ssw0rd";
    private const int Iterations = 600_000;

    [Fact]
    public void DeriveMasterKey_Pbkdf2_MatchesReferenceImpl()
    {
        var svc = new CryptoService();

        var actual = svc.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, Iterations, null, null);

        // 参考:PBKDF2-SHA256(pw=主密码, salt=原始邮箱, iter, 32B)
        var expected = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(Password),
            Encoding.UTF8.GetBytes(Email),
            Iterations,
            HashAlgorithmName.SHA256,
            32);

        Assert.Equal(expected, actual);
        Assert.Equal(32, actual.Length);
    }

    [Fact]
    public void ComputeMasterPasswordHash_MatchesReferenceImpl()
    {
        var svc = new CryptoService();
        var masterKey = svc.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, Iterations, null, null);

        var actual = svc.ComputeMasterPasswordHash(masterKey, Password);

        // 参考:PBKDF2-SHA256(pw=MasterKey, salt=主密码, iter=1, 32B) → base64
        var expectedBytes = Rfc2898DeriveBytes.Pbkdf2(
            masterKey,
            Encoding.UTF8.GetBytes(Password),
            1,
            HashAlgorithmName.SHA256,
            32);
        var expected = Convert.ToBase64String(expectedBytes);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DeriveMasterKey_Argon2id_Throws()
    {
        var svc = new CryptoService();
        Assert.Throws<NotImplementedException>(() =>
            svc.DeriveMasterKey(Password, Email, KdfType.Argon2id, 3, 64, 4));
    }
}
```

- [ ] **Step 2: 运行测试,确认失败**

Run: `dotnet test tests/Crypto.Tests --filter KdfTests`
Expected: FAIL —— `CryptoService` 不存在。

- [ ] **Step 3: 实现 CryptoService 的 KDF 部分**

`src/Crypto/CryptoService.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using Core.Abstractions;
using Core.Enums;

namespace Crypto;

// Bitwarden 加密链实现。严格遵循安全白皮书 + 独立实现核实。
public sealed class CryptoService : ICryptoService
{
    public byte[] DeriveMasterKey(string password, string email, KdfType kdfType, int iterations, int? memoryMiB, int? parallelism)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        var normalizedEmail = email.Trim().ToLowerInvariant();

        return kdfType switch
        {
            // PBKDF2:salt = 原始邮箱字节
            KdfType.Pbkdf2 => Rfc2898DeriveBytes.Pbkdf2(
                pw,
                Encoding.UTF8.GetBytes(normalizedEmail),
                iterations,
                HashAlgorithmName.SHA256,
                32),

            // Argon2id:salt = SHA-256(邮箱);需第三方包,本次不实现
            KdfType.Argon2id => throw new NotImplementedException(
                "Argon2id 派生待实现:salt = SHA-256(邮箱),参数 iter/mem/parallel 来自 prelogin。引入 Konscious.Security.Cryptography.Argon2 后补全。"),

            _ => throw new ArgumentOutOfRangeException(nameof(kdfType)),
        };
    }

    public string ComputeMasterPasswordHash(byte[] masterKey, string password)
    {
        // PBKDF2-SHA256(pw = MasterKey, salt = 主密码, iter = 1) → base64
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            masterKey,
            Encoding.UTF8.GetBytes(password),
            1,
            HashAlgorithmName.SHA256,
            32);
        return Convert.ToBase64String(hash);
    }
}
```

- [ ] **Step 4: 运行测试,确认通过**

Run: `dotnet test tests/Crypto.Tests --filter KdfTests`
Expected: PASS(3 个测试)。

- [ ] **Step 5: 提交**

```bash
git add src/Crypto/CryptoService.cs tests/Crypto.Tests/KdfTests.cs
git commit -m "feat: implement PBKDF2 master key derivation and password hash"
```

---

## Task 4: HKDF 拉伸 + 解密 UserKey + 测试

**Files:**
- Modify: `src/Crypto/CryptoService.cs`(增加 `StretchMasterKey`、`DecryptUserKey`)
- Create: `tests/Crypto.Tests/StretchAndUserKeyTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `CryptoService`、Task 2 的 `SymmetricCryptoKey`/`EncString`
- Produces:
  - `SymmetricCryptoKey CryptoService.StretchMasterKey(byte[] masterKey)` — 64 字节(enc‖mac)
  - `SymmetricCryptoKey CryptoService.DecryptUserKey(SymmetricCryptoKey stretchedKey, EncString protectedUserKey)`
  - `byte[] CryptoService.Decrypt(EncString data, SymmetricCryptoKey key)`(本任务先实现,供 DecryptUserKey 复用;Task 5 补 Encrypt/Rsa 与边界测试)

- [ ] **Step 1: 写自校验测试 —— HKDF 拉伸与内置一致**

`tests/Crypto.Tests/StretchAndUserKeyTests.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class StretchAndUserKeyTests
{
    [Fact]
    public void StretchMasterKey_MatchesHkdfExpandReference()
    {
        var svc = new CryptoService();
        var masterKey = svc.DeriveMasterKey("p4ssw0rd", "nobody@example.com", KdfType.Pbkdf2, 600_000, null, null);

        var stretched = svc.StretchMasterKey(masterKey);

        // 参考:两次独立 HKDF-Expand(SHA256),info="enc"/"mac",各 32 字节
        var enc = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, Encoding.UTF8.GetBytes("enc"));
        var mac = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, Encoding.UTF8.GetBytes("mac"));

        Assert.Equal(enc, stretched.EncKey);
        Assert.Equal(mac, stretched.MacKey);
        Assert.Equal(64, stretched.FullKey.Length);
    }

    [Fact]
    public void DecryptUserKey_RoundTripsThroughEncrypt()
    {
        var svc = new CryptoService();
        var masterKey = svc.DeriveMasterKey("p4ssw0rd", "nobody@example.com", KdfType.Pbkdf2, 600_000, null, null);
        var stretched = svc.StretchMasterKey(masterKey);

        // 模拟服务端:用 stretched key 加密一把随机 64 字节 UserKey
        var userKeyPlain = RandomNumberGenerator.GetBytes(64);
        var protectedUserKey = svc.Encrypt(userKeyPlain, stretched);

        var decrypted = svc.DecryptUserKey(stretched, protectedUserKey);

        Assert.Equal(userKeyPlain, decrypted.FullKey);
        Assert.Equal(32, decrypted.EncKey.Length);
        Assert.Equal(32, decrypted.MacKey!.Length);
    }
}
```

- [ ] **Step 2: 运行测试,确认失败**

Run: `dotnet test tests/Crypto.Tests --filter StretchAndUserKeyTests`
Expected: FAIL —— `StretchMasterKey`/`DecryptUserKey`/`Encrypt` 未定义。

- [ ] **Step 3: 在 CryptoService 增加拉伸、解密、Decrypt/Encrypt**

在 `src/Crypto/CryptoService.cs` 的类体内追加(`Encrypt` 完整实现在此,Task 5 仅补 Rsa 与测试):
```csharp
    // 由 MasterKey 拉伸出 EncKey/MacKey(各一次独立 HKDF-Expand,SHA256)。
    public SymmetricCryptoKey StretchMasterKey(byte[] masterKey)
    {
        var enc = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, "enc"u8.ToArray());
        var mac = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, "mac"u8.ToArray());
        return new SymmetricCryptoKey(enc, mac);
    }

    // 用拉伸子钥解开 protected user key,得到真正的 UserKey。
    public SymmetricCryptoKey DecryptUserKey(SymmetricCryptoKey stretchedKey, EncString protectedUserKey)
    {
        var plain = Decrypt(protectedUserKey, stretchedKey);
        return new SymmetricCryptoKey(plain);
    }

    // AES-256-CBC 解密,先验 HMAC(覆盖 IV‖ct)。type 0 无 MAC 跳过验证。
    public byte[] Decrypt(EncString data, SymmetricCryptoKey key)
    {
        if (data.Mac is not null)
        {
            if (key.MacKey is null)
                throw new CryptographicException("密文带 MAC 但密钥无 MacKey");

            var computed = ComputeMac(key.MacKey, data.Iv, data.Ct);
            if (!CryptographicOperations.FixedTimeEquals(computed, data.Mac))
                throw new CryptographicException("MAC 校验失败");
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key.EncKey;
        aes.IV = data.Iv;
        return aes.DecryptCbc(data.Ct, data.Iv);
    }

    // AES-256-CBC 加密,生成随机 IV,计算 MAC(覆盖 IV‖ct)。
    public EncString Encrypt(byte[] plaintext, SymmetricCryptoKey key)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key.EncKey;
        aes.IV = iv;
        var ct = aes.EncryptCbc(plaintext, iv);

        byte[]? mac = key.MacKey is null ? null : ComputeMac(key.MacKey, iv, ct);
        var type = mac is null ? EncryptionType.AesCbc256_B64 : EncryptionType.AesCbc256_HmacSha256_B64;
        return new EncString(type, iv, ct, mac);
    }

    private static byte[] ComputeMac(byte[] macKey, byte[] iv, byte[] ct)
    {
        using var hmac = new HMACSHA256(macKey);
        hmac.TransformBlock(iv, 0, iv.Length, null, 0);
        hmac.TransformFinalBlock(ct, 0, ct.Length);
        return hmac.Hash!;
    }
```

> 注意:文件顶部 `using` 已含 `System.Security.Cryptography`(Task 3 已加)。需补 `using Crypto;` 不必要(同命名空间);需确保 `using Core.Enums;` 已在。

- [ ] **Step 4: 运行测试,确认通过**

Run: `dotnet test tests/Crypto.Tests --filter StretchAndUserKeyTests`
Expected: PASS(2 个测试)。

- [ ] **Step 5: 提交**

```bash
git add src/Crypto/CryptoService.cs tests/Crypto.Tests/StretchAndUserKeyTests.cs
git commit -m "feat: implement HKDF stretch, UserKey decryption, AES-CBC+HMAC primitives"
```

---

## Task 5: EncString 加解密边界 + RSA 解密 + 测试

**Files:**
- Modify: `src/Crypto/CryptoService.cs`(增加 `DecryptRsa`)
- Create: `tests/Crypto.Tests/EncryptDecryptTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `Decrypt`/`Encrypt`、`SymmetricCryptoKey`、`EncString`
- Produces: `byte[] CryptoService.DecryptRsa(EncString data, byte[] privateKeyDer)`

- [ ] **Step 1: 写测试 —— 往返、MAC 篡改检测、RSA 往返**

`tests/Crypto.Tests/EncryptDecryptTests.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class EncryptDecryptTests
{
    private static SymmetricCryptoKey NewKey() => new(RandomNumberGenerator.GetBytes(64));

    [Fact]
    public void EncryptThenDecrypt_RoundTripsPlaintext()
    {
        var svc = new CryptoService();
        var key = NewKey();
        var plaintext = Encoding.UTF8.GetBytes("hello vault");

        var enc = svc.Encrypt(plaintext, key);
        var dec = svc.Decrypt(enc, key);

        Assert.Equal(plaintext, dec);
        Assert.Equal(EncryptionType.AesCbc256_HmacSha256_B64, enc.Type);
    }

    [Fact]
    public void Decrypt_TamperedMac_Throws()
    {
        var svc = new CryptoService();
        var key = NewKey();
        var enc = svc.Encrypt(Encoding.UTF8.GetBytes("secret"), key);

        // 篡改 MAC 一字节
        enc.Mac![0] ^= 0xFF;

        Assert.Throws<CryptographicException>(() => svc.Decrypt(enc, key));
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var svc = new CryptoService();
        var enc = svc.Encrypt(Encoding.UTF8.GetBytes("secret"), NewKey());

        Assert.Throws<CryptographicException>(() => svc.Decrypt(enc, NewKey()));
    }

    [Fact]
    public void DecryptRsa_RoundTripsThroughRsaOaepSha1()
    {
        var svc = new CryptoService();
        using var rsa = RSA.Create(2048);
        var plaintext = Encoding.UTF8.GetBytes("user key material");

        // 模拟服务端:用公钥 RSA-OAEP-SHA1 加密(type 4)
        var ctBytes = rsa.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA1);
        var enc = new EncString(EncryptionType.Rsa2048_OaepSha1_B64, Array.Empty<byte>(), ctBytes, null);
        var privateKeyDer = rsa.ExportPkcs8PrivateKey();

        var dec = svc.DecryptRsa(enc, privateKeyDer);

        Assert.Equal(plaintext, dec);
    }
}
```

- [ ] **Step 2: 运行测试,确认失败**

Run: `dotnet test tests/Crypto.Tests --filter EncryptDecryptTests`
Expected: FAIL —— `DecryptRsa` 未定义。

- [ ] **Step 3: 实现 DecryptRsa**

在 `src/Crypto/CryptoService.cs` 类体内追加:
```csharp
    // RSA 解密(OAEP)。type 3 用 SHA256,type 4 用 SHA1。privateKeyDer 为 PKCS8 DER。
    public byte[] DecryptRsa(EncString data, byte[] privateKeyDer)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(privateKeyDer, out _);

        var padding = data.Type switch
        {
            EncryptionType.Rsa2048_OaepSha256_B64 => RSAEncryptionPadding.OaepSHA256,
            EncryptionType.Rsa2048_OaepSha1_B64 => RSAEncryptionPadding.OaepSHA1,
            _ => throw new CryptographicException($"非 RSA encType: {(int)data.Type}"),
        };
        return rsa.Decrypt(data.Ct, padding);
    }
```

- [ ] **Step 4: 运行测试,确认通过**

Run: `dotnet test tests/Crypto.Tests`
Expected: PASS(全部:EncString 3 + Kdf 3 + Stretch 2 + EncryptDecrypt 4 = 12)。

- [ ] **Step 5: 提交**

```bash
git add src/Crypto/CryptoService.cs tests/Crypto.Tests/EncryptDecryptTests.cs
git commit -m "feat: add RSA-OAEP decryption and encrypt/decrypt boundary tests"
```

---

## Task 6: Api 项目(ApiClient 占位 + DTO)

**Files:**
- Create: `src/Api/Api.csproj`
- Create: `src/Api/ApiClient.cs`
- Create: `src/Api/Dtos/PreloginDtos.cs`, `TokenDtos.cs`, `SyncDtos.cs`

**Interfaces:**
- Consumes: `Core.Abstractions.IApiClient`
- Produces: `Api.ApiClient : IApiClient`(占位);DTO 记录类型(字段大小写匹配 API.md)

- [ ] **Step 1: 创建 Api.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Api</RootNamespace>
    <AssemblyName>Api</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core\Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 创建 DTO(字段大小写严格匹配 API.md)**

`src/Api/Dtos/PreloginDtos.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Api.Dtos;

// POST /identity/accounts/prelogin
public sealed record PreloginRequest([property: JsonPropertyName("email")] string Email);

// 响应:全小写 kdf 字段
public sealed record PreloginResponse(
    [property: JsonPropertyName("kdf")] int Kdf,
    [property: JsonPropertyName("kdfIterations")] int KdfIterations,
    [property: JsonPropertyName("kdfMemory")] int? KdfMemory,
    [property: JsonPropertyName("kdfParallelism")] int? KdfParallelism);
```

`src/Api/Dtos/TokenDtos.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Api.Dtos;

// POST /identity/connect/token 成功响应(部分关键字段,PascalCase)
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("Key")] string? Key,
    [property: JsonPropertyName("PrivateKey")] string? PrivateKey,
    [property: JsonPropertyName("Kdf")] int Kdf,
    [property: JsonPropertyName("KdfIterations")] int KdfIterations);
```

`src/Api/Dtos/SyncDtos.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Api.Dtos;

// GET /api/sync 顶层骨架(仅占位字段)
public sealed record SyncResponse(
    [property: JsonPropertyName("object")] string Object);
```

- [ ] **Step 3: 创建 ApiClient 占位**

`src/Api/ApiClient.cs`:
```csharp
using Core.Abstractions;

namespace Api;

// 网络层占位。骨架阶段仅持有 HttpClient 与基址,不发真实请求。
public sealed class ApiClient : IApiClient
{
    private readonly HttpClient _http;
    private string _baseUrl = string.Empty;

    public ApiClient(HttpClient http) => _http = http;

    public void SetBaseAddress(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http.BaseAddress = new Uri(_baseUrl);
    }
}
```

- [ ] **Step 4: 提交**

```bash
git add src/Api
git commit -m "feat: add Api project with placeholder client and core DTOs"
```

> 本任务无自动化测试(占位层,无逻辑);验证靠 Task 7 整体 `dotnet build`。

---

## Task 7: App 项目(WinUI 3 + MSIX + DI + 页面 + ViewModel)

**Files:**
- Create: `src/App/App.csproj`, `App.xaml`, `App.xaml.cs`, `Package.appxmanifest`
- Create: `src/App/Services/ServiceConfiguration.cs`
- Create: `src/App/ViewModels/LoginViewModel.cs`, `VaultViewModel.cs`
- Create: `src/App/Views/LoginPage.xaml(.cs)`, `VaultPage.xaml(.cs)`
- Create: `src/App/Services/PlaceholderServices.cs`(AuthService/SyncService/VaultService 占位)
- Create: `src/App/MainWindow.xaml(.cs)`

**Interfaces:**
- Consumes: `Core.Services.*`、`Core.Abstractions.*`、`Crypto.CryptoService`、`Api.ApiClient`
- Produces: 可启动的 WinUI 3 应用,DI 装配完成,LoginPage→VaultPage 导航

- [ ] **Step 1: 创建 App.csproj(MSIX 打包)**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RootNamespace>App</RootNamespace>
    <AssemblyName>App</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWinUI>true</UseWinUI>
    <EnableMsixTooling>true</EnableMsixTooling>
    <Platforms>x64;arm64</Platforms>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.7.250606001" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.1742" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core\Core.csproj" />
    <ProjectReference Include="..\Crypto\Crypto.csproj" />
    <ProjectReference Include="..\Api\Api.csproj" />
  </ItemGroup>
</Project>
```

> 注意:`Microsoft.WindowsAppSDK` 版本号在动工时复核最新稳定版(设计文档定的是 2.2.0 系列;NuGet 包版本号格式可能不同,以 NuGet 实际可还原版本为准)。首次 `dotnet restore` 报版本不存在时据 NuGet 调整。

- [ ] **Step 2: 创建占位服务**

`src/App/Services/PlaceholderServices.cs`:
```csharp
using Core.Models;
using Core.Services;

namespace App.Services;

public sealed class AuthService : IAuthService
{
    public Task LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default)
        => throw new NotImplementedException("TODO: prelogin → 派生 → connect/token");
}

public sealed class SyncService : ISyncService
{
    public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Cipher>>(Array.Empty<Cipher>());
}

public sealed class VaultService : IVaultService
{
    public IReadOnlyList<Cipher> GetCiphers() => Array.Empty<Cipher>();
}
```

- [ ] **Step 3: 创建 DI 装配**

`src/App/Services/ServiceConfiguration.cs`:
```csharp
using Api;
using Core.Abstractions;
using Core.Services;
using Crypto;
using Microsoft.Extensions.DependencyInjection;
using App.ViewModels;

namespace App.Services;

public static class ServiceConfiguration
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<CryptoService>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IApiClient, ApiClient>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<ISyncService, SyncService>();
        services.AddSingleton<IVaultService, VaultService>();

        services.AddTransient<LoginViewModel>();
        services.AddTransient<VaultViewModel>();

        return services.BuildServiceProvider();
    }
}
```

- [ ] **Step 4: 创建 ViewModel**

`src/App/ViewModels/LoginViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Services;

namespace App.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IAuthService _auth;

    [ObservableProperty] private string _serverUrl = "https://vault.bitwarden.com";
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _masterPassword = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    public LoginViewModel(IAuthService auth) => _auth = auth;

    [RelayCommand]
    private async Task LoginAsync()
    {
        try
        {
            await _auth.LoginAsync(ServerUrl, Email, MasterPassword);
        }
        catch (NotImplementedException)
        {
            Status = "登录尚未实现(骨架阶段)";
        }
    }
}
```

`src/App/ViewModels/VaultViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Models;
using Core.Services;

namespace App.ViewModels;

public partial class VaultViewModel : ObservableObject
{
    public ObservableCollection<Cipher> Ciphers { get; } = new();

    public VaultViewModel(IVaultService vault)
    {
        foreach (var c in vault.GetCiphers())
            Ciphers.Add(c);
    }
}
```

- [ ] **Step 5: 创建 App.xaml / App.xaml.cs**

`src/App/App.xaml`:
```xml
<Application
    x:Class="App.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

`src/App/App.xaml.cs`:
```csharp
using Microsoft.UI.Xaml;

namespace App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = Services.Build();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
```

> 修正:`Services = Services.Build()` 命名冲突。改为 `Services = App.Services.ServiceConfiguration.Build();`。完整正确版本:

```csharp
using Microsoft.UI.Xaml;
using App.Services;

namespace App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = ServiceConfiguration.Build();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
```

- [ ] **Step 6: 创建 MainWindow(承载 Frame 导航)**

`src/App/MainWindow.xaml`:
```xml
<Window
    x:Class="App.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Frame x:Name="RootFrame" />
</Window>
```

`src/App/MainWindow.xaml.cs`:
```csharp
using App.Views;
using Microsoft.UI.Xaml;

namespace App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RootFrame.Navigate(typeof(LoginPage));
    }
}
```

- [ ] **Step 7: 创建 LoginPage**

`src/App/Views/LoginPage.xaml`:
```xml
<Page
    x:Class="App.Views.LoginPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <StackPanel Width="320" HorizontalAlignment="Center" VerticalAlignment="Center" Spacing="12">
        <TextBlock Text="WinVaultWarden 登录" FontSize="20" />
        <TextBox Header="服务器地址" Text="{x:Bind ViewModel.ServerUrl, Mode=TwoWay}" />
        <TextBox Header="邮箱" Text="{x:Bind ViewModel.Email, Mode=TwoWay}" />
        <PasswordBox Header="主密码" Password="{x:Bind ViewModel.MasterPassword, Mode=TwoWay}" />
        <Button Content="登录" Command="{x:Bind ViewModel.LoginCommand}" />
        <Button Content="进入密码库(占位)" Click="OnGoVault" />
        <TextBlock Text="{x:Bind ViewModel.Status, Mode=OneWay}" Foreground="Red" />
    </StackPanel>
</Page>
```

`src/App/Views/LoginPage.xaml.cs`:
```csharp
using App.ViewModels;
using App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class LoginPage : Page
{
    public LoginViewModel ViewModel { get; }

    public LoginPage()
    {
        ViewModel = App.Services.GetRequiredService<LoginViewModel>();
        InitializeComponent();
    }

    private void OnGoVault(object sender, RoutedEventArgs e)
        => Frame.Navigate(typeof(VaultPage));
}
```

- [ ] **Step 8: 创建 VaultPage**

`src/App/Views/VaultPage.xaml`:
```xml
<Page
    x:Class="App.Views.VaultPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Padding="24">
        <ListView ItemsSource="{x:Bind ViewModel.Ciphers, Mode=OneWay}" />
        <TextBlock Text="密码库为空(骨架占位)" HorizontalAlignment="Center" VerticalAlignment="Center" />
    </Grid>
</Page>
```

`src/App/Views/VaultPage.xaml.cs`:
```csharp
using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class VaultPage : Page
{
    public VaultViewModel ViewModel { get; }

    public VaultPage()
    {
        ViewModel = App.Services.GetRequiredService<VaultViewModel>();
        InitializeComponent();
    }
}
```

- [ ] **Step 9: 创建 Package.appxmanifest 与 app.manifest**

`src/App/app.manifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="WinVaultWarden.App" />
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

`src/App/Package.appxmanifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
  <Identity Name="WinVaultWarden" Publisher="CN=WinVaultWarden" Version="1.0.0.0" />
  <Properties>
    <DisplayName>WinVaultWarden</DisplayName>
    <PublisherDisplayName>WinVaultWarden</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources>
    <Resource Language="x-generate" />
  </Resources>
  <Applications>
    <Application Id="App" Executable="$targetnametoken$.exe" EntryPoint="$targetentrypoint$">
      <uap:VisualElements DisplayName="WinVaultWarden" Description="Bitwarden 兼容客户端"
        BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
        <uap:SplashScreen Image="Assets\SplashScreen.png" />
      </uap:VisualElements>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient" />
  </Capabilities>
</Package>
```

> Assets 图标:VS 新建 WinUI3 模板会自动生成 `Assets/` 下的占位图标。手写场景下,首次在 VS 打开后用模板默认图标补齐,或从 VS 模板复制。本步骤不嵌入二进制图标。

- [ ] **Step 10: 构建验证(装好工具链后)**

Run: `dotnet build WinVaultWarden.slnx -c Debug`
Expected: 全部 5 个项目编译成功(0 error)。若 WindowsAppSDK 版本号报错,据 NuGet 实际可还原版本调整 csproj。

Run(VS 中):F5 启动 App
Expected: 显示主窗口与 LoginPage;点"进入密码库(占位)"导航到 VaultPage 显示空列表;点"登录"显示"登录尚未实现(骨架阶段)"。

- [ ] **Step 11: 提交**

```bash
git add src/App
git commit -m "feat: add WinUI3 App with DI, login/vault pages, navigation"
```

---

## 自检结果(Self-Review)

**1. Spec 覆盖**:
- 4 项目分层 → Task 1/2/6/7 ✅
- Crypto PBKDF2 全链(派生/Hash/HKDF/UserKey/AES+HMAC/RSA)→ Task 3/4/5 ✅
- Argon2id 抛 NotImplemented → Task 3 Step 3 ✅
- EncString encType 0/1/2/3/4 → Task 2 Step 6 ✅
- MAC 覆盖 IV‖ct、恒定时间比较 → Task 4 Step 3(`ComputeMac` + `FixedTimeEquals`)✅
- 两条 KDF salt 差异(PBKDF2 原始邮箱 / Argon2id SHA256)→ Task 3 注释标注 ✅
- MVVM/DI/MSIX/页面导航 → Task 7 ✅
- xUnit 测试向量 → Task 2-5 ✅
- DTO 大小写匹配 API.md → Task 6 ✅
- **范围变更**:spec 原列"测试项目"为 Out of Scope,经用户批准升级为 In Scope(Crypto.Tests)。已在 Global Constraints 与本计划体现。

**2. 占位符扫描**:无 TBD/TODO 残留作为计划缺口(代码中的 `NotImplementedException("TODO: ...")` 是设计内容)。✅

**3. 类型一致性**:
- `CryptoService` 方法签名在 Task 3-5 间一致(`DeriveMasterKey`/`ComputeMasterPasswordHash`/`StretchMasterKey`/`DecryptUserKey`/`Decrypt`/`Encrypt`/`DecryptRsa`)✅
- `SymmetricCryptoKey` 双构造器(byte[] / encKey+macKey)在 Task 2 定义,Task 4 使用 ✅
- `EncString` 属性(Type/Iv/Ct/Mac)在 Task 2 定义,Task 4/5 使用 ✅
- 已修正 App.xaml.cs 中 `Services` 命名冲突(Step 5 给出正确版本)✅

**已知风险(执行时注意)**:
- WindowsAppSDK / 各 NuGet 版本号需在 `dotnet restore` 时据实际可还原版本核对(计划值为占位,可能漂移)。
- 本机无工具链,所有"Run"步骤在执行机验证。
- `ICryptoService`(Core)只暴露两个 KDF 方法;解密方法在 `CryptoService` 具体类上。App 通过 DI 同时注册了接口与具体类(Task 7 Step 3),需用具体类型调用解密——这是有意设计(Core 零依赖 Crypto)。
