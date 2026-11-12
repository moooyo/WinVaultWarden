# Stable MSIX update feeds

This tool creates `WinVaultWarden-x64.appinstaller` and `WinVaultWarden-arm64.appinstaller` from the exact MSIX files staged by the production signing job. Run it only after SignTool has signed and verified both packages. The ZIP signature entry check in the portable builder detects a missing signature envelope; it is not cryptographic signature verification.

```powershell
dotnet run --project tools/ReleaseFeedGenerator/ReleaseFeedGenerator.csproj -c Release -- `
  --artifacts output --repository-manifest src/App/Package.appxmanifest `
  --release-tag v0.1.0 --release-version 0.1.0 `
  --core-version 0.1.0 --manifest-version 0.1.0.0 --is-prerelease false
```

The repository manifest supplies the expected package name and publisher. Both signed package manifests must match that identity, their expected architecture, the normalized release version and exact asset filename. Both architectures are validated before either feed is written. XML external entities and oversized or duplicate package manifests are rejected.

The stable feed addresses are:

- `https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-x64.appinstaller`
- `https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-arm64.appinstaller`

Each feed references its release's versioned signed MSIX URL. Its independent four-part feed version is `1.<package-major>.<package-minor>.<package-build>`. Package revisions must remain zero in this release scheme. Published prereleases, including a GitHub prerelease event whose tag has no prerelease suffix, never generate stable feeds. Build metadata is permitted and URI encoded.

The 2018 App Installer schema enables launch-time update checks every 24 hours and the Windows background update task. `ForceUpdateFromAnyVersion` is intentionally absent, so the feed does not authorize package downgrades. Windows still verifies package identity, signatures and trust during installation. A user must enroll an MSIX installation through App Installer for Windows to manage these updates; downloading an MSIX directly does not claim that enrollment.

The release job generates feeds after signing and before checksums and artifact upload. It does not publish by itself. The existing release publication step uploads the staged feed files with the signed release assets.

Official references: [AppInstaller schema](https://learn.microsoft.com/en-us/uwp/schemas/appinstallerschema/element-appinstaller), [update settings](https://learn.microsoft.com/en-us/uwp/schemas/appinstallerschema/element-update-settings), [OnLaunch](https://learn.microsoft.com/en-us/uwp/schemas/appinstallerschema/element-onlaunch), and [Windows update and repair settings](https://learn.microsoft.com/en-us/windows/msix/app-installer/update-settings).
