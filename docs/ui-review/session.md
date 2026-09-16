# UI review session

## Scope and evidence

The 2026-09-15 review documents the current WinVaultWarden implementation at commit `8cbbdbd218d029baf6acf3d385e3fdbeb95850cd`. The user explicitly authorized local verification, starting the Windows UI, provisioning the backend on `test-env`, and populating synthetic data.

All nine navigable pages have native screenshot evidence: Login, Register, Vault, Send, Security report, Import/export, Settings, Devices, and Emergency access. The source inventory also contains dialogs, logical views, conditional states, shared controls, native-system boundaries, and development-only harnesses. Its 178 entries are not 178 independent application pages.

The atlas includes 53 native captures, of which two use the built-in demo. Remaining captures are the actual application with the dedicated review account, or authentication forms before sign-in. The 178 source-derived diagrams describe logical controls and navigation; they are not exact rendered screenshots and do not certify runtime behavior. Full integration tests, destructive actions, and every conditional branch were not exercised.

## Build and environment

| Item | Observed value |
| --- | --- |
| Local SDK | .NET SDK 10.0.301 |
| Application target | net10.0-windows10.0.26100.0, x64 |
| Runtime in About | .NET 10.0.9 |
| Windows in About | Windows 10.0.28000.0 |
| Application version | 0.1.0 |
| Windows App SDK used | 2.2.0, as pinned in the project |
| Backend | Vaultwarden 1.37.2 on test-env |
| Local connection | http://127.0.0.1:18083 via SSH loopback tunnel |
| Capture mechanism | Windows Computer Use window capture and UI Automation |

The NuGet stable-version index was checked before work. It listed 2.4.0 as the latest Windows App SDK stable package at that time. The audit uses the repository's pinned 2.2.0 build; no dependency upgrade is part of the review. Source: [NuGet package version index](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk/index.json).

The local build completed with zero warnings and zero errors:

```powershell
dotnet build src/App/App.csproj -c Debug -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:SelfContained=true -p:BuildBrowserNativeHost=false --nologo
```

The native-host target still ran as part of the project build. The captured executable was `src/App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/App.exe`. No production application files were modified by this task.

## Reproduction

1. Follow [backend and fixture instructions](../../tools/UiReviewSeed/README.md) to start the isolated service and SSH tunnel.
2. Seed synthetic data only when needed. The seed helper encrypts writes using the production client services and verifies decryption on readback.
3. Start the built executable. Choose Self-hosted, use `http://127.0.0.1:18083`, and sign in as `review@winvaultwarden.example`.
4. The master password is stored only as DPAPI CurrentUser ciphertext under `.secrets/ui-review/review.password.dpapi`. Consume it directly in memory for test automation; do not print or persist its plaintext.
5. Navigate using the entry points in [page-inventory.md](page-inventory.md). Use [capture-coverage.json](capture-coverage.json) to see exactly which surfaces were visible in each image.
6. Rebuild the offline atlas after changing its source inventory or capture manifest:

```powershell
node scripts/ui-review/build-atlas.mjs
```

The dedicated backend and tunnel were left available for continued review. The native client was restarted after the last observed exit and left at its locked account screen. Opening the offline atlas does not contact the backend.

## Remaining runtime boundaries

- Vaultwarden 1.37.2 accepts only cipher types 1 through 5. Bank account, driver's license, and passport layouts therefore have source diagrams but no successful live fixture capture.
- Trusted-device status is fixed false by this backend version. Enterprise SSO, provider-hosted Duo flows, Windows Hello enrollment, and security-key ceremonies require their own environment or hardware.
- The application exited once while expanding repeated-password results and once while entering Trash. The login editor also rendered blank. These paths retain source diagrams and explicit failure evidence; they are not reported as passing UI checks.
- Other observed presentation issues and their limits are recorded in [runtime-findings.md](runtime-findings.md).
- Account-deletion dialogs were inspected without deleting the account. Password/KDF and invitation forms were cancelled. No operational credentials or personal vault records were used.
