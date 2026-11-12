# Synthetic Windows platform tests

This project links the actual Windows passkey platform API, provider COM host and provider interface. It exercises native code through real unmanaged vtable callbacks and Windows CNG while keeping all objects and cryptographic keys owned by the test process.

Run only in a Windows verification environment authorized for the current task:

```powershell
dotnet test tests/WindowsPlatform.Tests/WindowsPlatform.Tests.csproj -c Debug
```

Tests are skipped on non-Windows systems. Decoder cases additionally skip when the OS does not export the WebAuthn plugin ABI.

The suite covers:

- Read-only System32 export discovery and provider availability/state queries.
- Documented 64-bit structure sizes and offsets.
- In-memory COM QueryInterface, CreateInstance, reference counting, lock status and invalid argument handling.
- Cancellation of exactly the owned transaction, without affecting another request.
- Forged request rejection before any approval or vault write.
- Ephemeral RSA-PSS/SHA-256 and ECDSA-P256/P1363 Windows CNG verification, including altered or malformed input.
- The real Windows CTAP decoder's get/create layouts, optional empty names and malformed-buffer rejection.

The tests never call `StartAsync`, `CoRegisterClassObject`, provider registration/unregistration APIs, Windows Hello, a real vault, a relying party, or the real application UI. An `AboutInfo` test stub throws if a registration path is accidentally requested. No certificate, persisted key or user provider setting is created.

If the provider is not enabled, the forged operation is rejected by the real disabled-state gate. That result must not be reported as validation against a real platform operation-signing key. Full Windows activation and a platform-issued signed operation still require separate explicit acceptance.
