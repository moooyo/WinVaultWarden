# Debug Mock Login Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Debug-only mock login path that opens a local demo vault without calling Vaultwarden or writing persisted session data.

**Architecture:** Add an App-layer `IDemoVaultSessionService` and Debug-only implementation that populates `VaultSession` with Core model demo data. `LoginViewModel` exposes `CanUseDemoVault` and `UseDemoVaultCommand` when the service is registered; the login page binds a secondary button to that command under `#if DEBUG` service registration.

**Tech Stack:** .NET 10, WinUI 3 XAML, CommunityToolkit.Mvvm, xUnit.

---

### Task 1: ViewModel Demo Command

**Files:**
- Modify: `src/App/ViewModels/LoginViewModel.cs`
- Test: `tests/App.Tests/LoginViewModelAuthTests.cs`

- [ ] **Step 1: Write the failing test**

Add a test that constructs `LoginViewModel` with a fake `IDemoVaultSessionService`, executes `UseDemoVaultCommand`, and asserts that the demo service was called and the success callback ran.

```csharp
[Fact]
public async Task UseDemoVaultCommand_WhenDemoServiceExists_OpensDemoVault()
{
    var auth = new FakeAuthService();
    var demo = new FakeDemoVaultSessionService();
    var vm = new LoginViewModel(auth, demo);
    var success = false;
    vm.SetSuccessCallback(() => success = true);
    vm.Status = "previous";

    await vm.UseDemoVaultCommand.ExecuteAsync(null);

    Assert.True(demo.Opened);
    Assert.True(success);
    Assert.Equal(string.Empty, vm.Status);
    Assert.True(vm.CanUseDemoVault);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/App.Tests/App.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~LoginViewModelAuthTests.UseDemoVaultCommand_WhenDemoServiceExists_OpensDemoVault"`

Expected: compile failure because `IDemoVaultSessionService`, `LoginViewModel(IAuthService, IDemoVaultSessionService?)`, `UseDemoVaultCommand`, or `CanUseDemoVault` is missing.

- [ ] **Step 3: Write minimal implementation**

Create `IDemoVaultSessionService` in the App services namespace and update `LoginViewModel`:

```csharp
public interface IDemoVaultSessionService
{
    Task OpenDemoVaultAsync(CancellationToken ct = default);
}
```

```csharp
private readonly IDemoVaultSessionService? _demoVault;

public LoginViewModel(IAuthService auth)
    : this(auth, null)
{
}

public LoginViewModel(IAuthService auth, IDemoVaultSessionService? demoVault)
{
    _auth = auth;
    _demoVault = demoVault;
}

public bool CanUseDemoVault => _demoVault is not null;

[RelayCommand(CanExecute = nameof(CanUseDemoVault))]
private async Task UseDemoVaultAsync()
{
    if (_demoVault is null)
        return;

    await _demoVault.OpenDemoVaultAsync();
    Status = string.Empty;
    MasterPassword = string.Empty;
    TwoFactorCode = string.Empty;
    IsTwoFactorStage = false;
    IsUnlockStage = false;
    _onSuccess?.Invoke();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run the same filtered test. Expected: PASS.

### Task 2: Demo Vault Session Service

**Files:**
- Create: `src/App/Services/DemoVaultSessionService.cs`
- Test: `tests/App.Tests/DemoVaultSessionServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Add a test that opens the demo vault and verifies snapshot content and lack of persisted tokens.

```csharp
[Fact]
public async Task OpenDemoVaultAsync_PopulatesUnlockedSnapshotWithoutTokens()
{
    var session = new VaultSession();
    var service = new DemoVaultSessionService(session);

    await service.OpenDemoVaultAsync(TestContext.Current.CancellationToken);

    Assert.Equal(VaultState.Unlocked, session.State);
    Assert.Equal("demo@winvaultwarden.local", session.Account.Email);
    Assert.Null(session.AccessToken);
    Assert.Null(session.RefreshToken);
    Assert.NotNull(session.UserKey);
    Assert.Contains(session.Ciphers, c => c.Type == CipherType.Login);
    Assert.Contains(session.Ciphers, c => c.Type == CipherType.Card);
    Assert.Contains(session.Ciphers, c => c.Type == CipherType.Identity);
    Assert.Contains(session.Ciphers, c => c.Type == CipherType.SecureNote);
    Assert.Contains(session.Ciphers, c => c.Type == CipherType.SshKey);
    Assert.Contains(session.Ciphers, c => c.DeletedDate is not null);
    Assert.Single(session.Folders);
    Assert.Single(session.Devices);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/App.Tests/App.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~DemoVaultSessionServiceTests.OpenDemoVaultAsync_PopulatesUnlockedSnapshotWithoutTokens"`

Expected: compile failure because `DemoVaultSessionService` does not exist.

- [ ] **Step 3: Write minimal implementation**

Implement `DemoVaultSessionService` using `VaultSession.SetUnlockedKey`, `SetSnapshot`, and `SetDevices`. Use a non-secret deterministic 64-byte key only to satisfy unlocked state.

- [ ] **Step 4: Run test to verify it passes**

Run the same filtered test. Expected: PASS.

### Task 3: Debug Registration And Login Page Button

**Files:**
- Modify: `src/App/Services/ServiceConfiguration.cs`
- Modify: `src/App/Views/LoginPage.xaml`
- Test: `tests/App.Tests/CipherEditorXamlTests.cs` or a new focused XAML test file

- [ ] **Step 1: Write the failing XAML/registration tests**

Add a pure file-content XAML test asserting the login page has `UseDemoVaultCommand` and “使用演示保险库”. Add a DI test asserting `IDemoVaultSessionService` resolves in Debug.

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/App.Tests/App.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~Demo"`

Expected: failures because the button and registration are missing.

- [ ] **Step 3: Add Debug-only registration and button**

In `ServiceConfiguration.Build()`:

```csharp
#if DEBUG
services.AddSingleton<IDemoVaultSessionService, DemoVaultSessionService>();
#endif
```

In `LoginPage.xaml`, add a secondary full-width button below the real login button:

```xml
<Button Content="使用演示保险库"
        Command="{x:Bind ViewModel.UseDemoVaultCommand, Mode=OneWay}"
        Visibility="{x:Bind ViewModel.CanUseDemoVault, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}"
        HorizontalAlignment="Stretch" />
```

- [ ] **Step 4: Run tests and build**

Run:

```powershell
dotnet test tests/App.Tests/App.Tests.csproj -p:Platform=x64
dotnet build src/App/App.csproj -c Debug -p:Platform=x64
```

Expected: tests pass and Debug build succeeds.

### Task 4: Final Verification And Commit

**Files:**
- All changed files.

- [ ] **Step 1: Run full verification**

Run:

```powershell
dotnet test -p:Platform=x64
dotnet build src/App/App.csproj -c Debug -p:Platform=x64
git diff --check
```

Expected: tests pass, build succeeds, and diff check reports no whitespace errors.

- [ ] **Step 2: Commit and push if requested**

Stage only files changed for this feature and commit:

```powershell
git add docs/superpowers/specs/2026-06-24-debug-mock-login-design.md docs/superpowers/plans/2026-06-24-debug-mock-login.md src/App/Services/DemoVaultSessionService.cs src/App/Services/ServiceConfiguration.cs src/App/ViewModels/LoginViewModel.cs src/App/Views/LoginPage.xaml tests/App.Tests/DemoVaultSessionServiceTests.cs tests/App.Tests/LoginViewModelAuthTests.cs
git commit -m "feat: add debug demo vault login"
```

Push only if the user asks for it.
