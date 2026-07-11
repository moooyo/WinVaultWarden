# WinVaultWarden

## 下载与安装

预编译版本发布在 [Releases](https://github.com/moooyo/WinVaultWarden/releases),提供两种形式(x64 / arm64):

- **便携版 zip**:解压即用,免安装、免证书。
- **MSIX 安装包**:正规安装,首次需信任随包附带的自签名证书。

详见 [docs/INSTALL.md](docs/INSTALL.md)。

> 浏览器 passkey 桥接采用**签名者绑定(signer-pinning)**:App 与原生主机二进制由同一签名者签名,进程间只在签名者公钥匹配时互认。**无需**把签名证书装入信任存储,证书也**不**随 zip 分发;绑定只校验二者签名者是否一致,不要求用户信任该签名者。详见 [docs/passkeys-browser-integration.md](docs/passkeys-browser-integration.md)。