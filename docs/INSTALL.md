# 安装 WinVaultWarden

WinVaultWarden 以自签名的 MSIX 分发。首次安装需**一次性信任发布证书**,之后即可正常安装/更新。

## 1. 下载

从本次 Release 下载:

- 你的 CPU 架构对应的包:
  - 64 位 Intel/AMD:`WinVaultWarden-<版本>-x64.msix`
  - ARM(如 Surface Pro X、骁龙本):`WinVaultWarden-<版本>-arm64.msix`
- 信任证书:`WinVaultWarden-<版本>.cer`

> 不确定架构:设置 → 系统 → 关于 → "系统类型"。

## 2. 信任发布证书(仅首次)

**以管理员身份**打开 PowerShell,运行:

```powershell
Import-Certificate -FilePath .\WinVaultWarden-<版本>.cer `
  -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

这会把证书装入「本地计算机 → 受信任人」存储,使系统接受用它签名的 MSIX。

## 3. 安装

双击 `.msix` 走应用安装器,或在 PowerShell:

```powershell
Add-AppxPackage .\WinVaultWarden-<版本>-x64.msix
```

## 4. 更新

新版本沿用同一证书签名,证书信任过一次即可;直接 `Add-AppxPackage` 新包覆盖安装。

## 5. 卸载与清理

```powershell
Get-AppxPackage *WinVaultWarden* | Remove-AppxPackage
# 如需移除信任证书:
Get-ChildItem Cert:\LocalMachine\TrustedPeople |
  Where-Object Subject -eq 'CN=WinVaultWarden' | Remove-Item
```

> 安全说明:自签名证书仅代表"由本项目签发",不构成第三方背书。仅在信任来源时安装。
