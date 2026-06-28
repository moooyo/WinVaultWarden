# 安装 WinVaultWarden

提供两种获取方式,任选其一:

- **方式 A — 便携版 zip(最简,免安装、免证书)**:解压即用,适合大多数人。
- **方式 B — MSIX 安装包**:正规安装/卸载、开始菜单集成,首次需一次性信任发布证书。

> 不确定 CPU 架构:设置 → 系统 → 关于 →「系统类型」。64 位 Intel/AMD 选 `x64`,ARM(如 Surface Pro X、骁龙本)选 `arm64`。

---

## 方式 A:便携版 zip(推荐给嫌麻烦的用户)

1. 下载 `WinVaultWarden-<版本>-<架构>-portable.zip`。
2. 解压到任意文件夹(如 `D:\WinVaultWarden`)。
3. 双击运行里面的 `App.exe`。

无需安装、无需信任任何证书,删除文件夹即"卸载"。首次运行 Windows SmartScreen 可能提示"未知发布者",点「更多信息 → 仍要运行」即可。自包含,无需预装 .NET 或 Windows App SDK 运行时。

---

## 方式 B:MSIX 安装包

MSIX 以自签名签发,**首次安装需一次性信任发布证书**,之后即可正常安装/更新。

### 1. 下载

- 对应架构的包:`WinVaultWarden-<版本>-x64.msix` 或 `WinVaultWarden-<版本>-arm64.msix`
- 信任证书:`WinVaultWarden-<版本>.cer`

### 2. 信任发布证书(仅首次)

**以管理员身份**打开 PowerShell,运行:

```powershell
Import-Certificate -FilePath .\WinVaultWarden-<版本>.cer `
  -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

这会把证书装入「本地计算机 → 受信任人」存储,使系统接受用它签名的 MSIX。

### 3. 安装

双击 `.msix` 走应用安装器,或在 PowerShell:

```powershell
Add-AppxPackage .\WinVaultWarden-<版本>-x64.msix
```

### 4. 更新

新版本沿用同一证书签名,证书信任过一次即可;直接 `Add-AppxPackage` 新包覆盖安装。

### 5. 卸载与清理

```powershell
Get-AppxPackage *WinVaultWarden* | Remove-AppxPackage
# 如需移除信任证书:
Get-ChildItem Cert:\LocalMachine\TrustedPeople |
  Where-Object Subject -eq 'CN=WinVaultWarden' | Remove-Item
```

> 安全说明:自签名证书仅代表"由本项目签发",不构成第三方背书。仅在信任来源时安装。
