# Startup and release-check preferences

Startup is disabled by default and changes only after an explicit settings action.

- MSIX uses the `WinVaultWardenStartup` `windows.startupTask` manifest declaration and the Windows `StartupTask` API. A user or policy denial is returned as an actual disabled state. The application does not override the Startup page or Task Manager.
- Portable installations register one quoted executable command in the current user's `Software\Microsoft\Windows\CurrentVersion\Run` key. The command must fit the documented 260-character limit. Another installation's conflicting value is preserved. No machine-wide key, task, service, or undocumented `StartupApproved` value is written.
- A portable Run entry is reported as registered, rather than a guarantee that Windows will launch it. Windows startup policy remains authoritative. Moving a portable installation may require removing its previous startup entry before registering the new location.

The implementation follows Microsoft's [StartupTask contract](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask) and [Run-key contract](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys).

Release checks are anonymous requests to the fixed official GitHub repository. The client rejects redirects, oversized metadata, unexpected release-page or asset URLs, draft/prerelease responses, and incompatible architecture/distribution assets. Failures and missing releases never mean that the installed version is current. Portable version comparison reads the actual compiled informational or assembly version; MSIX reads the real package version.

Automatic release checking is opt-in and limited to one attempt per day. The attempt time is recorded even if the network is unavailable. The UI can open the validated official release page after a new stable version is found. This notification preference is separate from Windows-managed MSIX automatic installation.

MSIX users can explicitly open the official architecture-specific App Installer feed to enroll in Windows-managed automatic updates. The app downloads and validates the bounded feed against the installed package identity, version and architecture. Before launching the local `.appinstaller` file, the security lifecycle locks the vault, clears sensitive views and copied secrets, and stops its background integrations. Windows App Installer then owns user approval, MSIX signature verification, installation and future automatic servicing. The app reports an existing association only when `Package.GetAppInstallerInfo` actually returns the official source; merely opening the installer is not installation success. The release workflow generates both feeds only after production signing and verification. See [MSIX automatic updates](msix-automatic-updates.md).

Portable updates remain manual. Release metadata and an API-provided SHA-256 digest are not signer identity for the entire portable bundle. Automatic replacement additionally requires a reviewed signed bundle manifest, trusted signer pinning and a recoverable multi-file replacement transaction; the application does not execute or replace an unverified bundle.

The GitHub request follows the official [latest-release API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release).

Pure policy, command construction, release parsing, feed generation and transport behavior can be tested remotely on Linux. Real StartupTask consent, HKCU registration, App Installer enrollment, signed deployment and Windows update-policy interactions require an authorized Windows verification environment.
