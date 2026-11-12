# Windows-managed MSIX automatic updates

Stable signed releases publish two update feeds:

- [x64 App Installer feed](https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-x64.appinstaller).
- [arm64 App Installer feed](https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-arm64.appinstaller).

These are direct file downloads. Download and open the `.appinstaller` file with Windows App Installer, review the package publisher, and approve installation. A standalone `.msix` installation does not automatically enroll in a feed. An existing installation can use Settings to prepare and open the correct feed; the vault locks before handing control to Windows. If Windows only offers to launch an already installed version, verify the association in Settings and install the next higher signed package through the feed if necessary. The app never reports that opening a prompt alone completed enrollment.

The release publisher certificate must already be trusted by Windows. Self-signed MSIX releases retain the existing explicit certificate-trust requirement described in the installation instructions. The application does not install a certificate or bypass Windows trust. Keeping the same package identity and publisher across releases is necessary for in-place updates. Changing publisher identity is a separate installation and migration decision.

## Release pipeline

The production-signing job signs and verifies both architecture-specific MSIX files first. `tools/ReleaseFeedGenerator` then reads the actual `AppxManifest.xml` from each signed MSIX without extracting package contents. It checks name, publisher, architecture and four-part version against the repository manifest and validated release metadata. A missing signature envelope, wrong identity, wrong architecture, malformed XML or version mismatch prevents feed generation. Signature-envelope presence is structural validation; SignTool and Windows perform the cryptographic signature verification.

Both packages must pass before any feed is staged. Stable feeds are emitted as UTF-8 without a BOM into the signed release output, included in release checksums and uploaded with the signed assets. Prereleases never publish the stable feed filenames. No workflow or release publication is performed by running the unit tests.

The feed's own URI remains the fixed `releases/latest/download` address above. Its main-package URI points to the exact versioned signed MSIX asset from the current stable release. Each feed has an independent monotonically ordered version `1.<package-major>.<package-minor>.<package-build>`, while `MainPackage.Version` exactly matches `<package-major>.<package-minor>.<package-build>.0`. This supports the project's `0.x` package versions without using zero as the App Installer feed's major version.

The 2018 schema is used for the supported Windows baseline. Update settings enable launch checks with a 24-hour interval and the native automatic background task. Windows schedules background checks independently of the app; timing depends on Windows policy, connectivity and App Installer availability. No downgrade or forced in-use application shutdown option is enabled. Updates can be deferred while the app is in use.

## Client enrollment

Settings reads the actual package identity and `GetAppInstallerInfo` association. It does not store a Boolean claiming that enrollment succeeded. Before opening an installer, the client validates all of the following:

- Fixed official HTTPS feed and bounded redirect chain through GitHub's release-asset transport.
- Feed size, XML schema and DTD prohibition.
- Exact installed package name, publisher and architecture.
- A nondecreasing package version, stable release tag, exact versioned package URL and expected automatic-update settings.
- The saved file's digest and content immediately before opening it.

After explicit confirmation, the app prepares only the metadata file, locks through the common security lifecycle, and uses `Launcher.LaunchFileAsync` to open Windows App Installer. It avoids the `ms-appinstaller:` URI scheme, which Microsoft disabled by default on consumer devices. Windows verifies the MSIX package signature and asks the user to approve installation. Cancelling, untrusted certificates, unsupported policy or a missing feed remain failures or pending enrollment, never success.

`LaunchFileAsync` reports application activation, not that the target has consumed the file. A successful handoff therefore retains its non-sensitive validated metadata file. Startup cleanup and later enrollment attempts consider only this service's GUID-named metadata files older than seven days, with bounded enumeration; failed handoffs delete their file immediately. Cleanup never treats a successful launch as completed installation. See the [Launcher contract](https://learn.microsoft.com/en-us/uwp/api/windows.system.launcher.launchfileasync).

The separate optional GitHub release-check preference controls in-app notifications. It does not disable a Windows-managed App Installer association. Windows controls native servicing after enrollment. Portable installations do not use this MSIX channel and retain manual updates until a signed complete-bundle and recovery protocol is implemented.

## Verification

Automated remote tests cover feed production, actual-manifest identity checks, URL and version validation, source-to-client round trips, prerelease exclusions and CLI failure behavior. Windows-targeted compilation checks the enrollment API surface. Actual signing, installation, association persistence, automatic update application and locked-state behavior during a real deployment still require authorized Windows verification with the production certificate. A future release must publish the generated feed assets before enrollment can succeed.

The implementation follows Microsoft's [App Installer overview](https://learn.microsoft.com/en-us/windows/msix/app-installer/app-installer-file-overview), [update settings](https://learn.microsoft.com/en-us/windows/msix/app-installer/update-settings), [package identity rules](https://learn.microsoft.com/en-us/uwp/schemas/appinstallerschema/element-main-package), and [association query](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.package.getappinstallerinfo). GitHub hosting and the disabled protocol are described in Microsoft's [web installation guidance](https://learn.microsoft.com/en-us/windows/msix/app-installer/installing-windows10-apps-web).
