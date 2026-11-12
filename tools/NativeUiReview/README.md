# Native WinUI review fixture

This explicitly opted-in Debug entry point renders the production application's compiled WinUI visual trees with `RenderTargetBitmap`. Ordinary Debug builds and all Release builds exclude every file in this directory. Setting `NativeUiReview=true` in Release fails before compilation.

The fixture instantiates the actual `App.xaml`, `MainWindow.xaml`, and production pages. A separate application launch path injects synthetic in-memory services and preferences before any page is created. It does not execute normal startup, inspect another app instance, read saved accounts or usage records, use the system clipboard, invoke Windows Hello, register the tray or browser bridge, contact a server, or inject operating-system input.

Startup registration, the SSH host and the Windows passkey provider use memory-only fixture implementations. The account-management page uses a synthetic availability result and rejects every account mutation. Review runs assert that no platform-registration or account-management action was invoked. These dependencies support native layout review; they are not evidence of registry, named-pipe, COM, hardware-authentication or server behavior.

The fixture defaults to an explicit `zh-CN` selection after installing its in-memory preferences. The localization-only profile can instead select `zh-TW`, `en-US` or `ja-JP` for a separate process. It initializes the desktop `Microsoft.Windows` MRT Core runtime before application XAML is created. This is a process-local unpackaged override; the fixture never uses the UWP `Windows.Globalization` override. Source fingerprints include `.resw` and `.json` resource/catalog inputs alongside code and XAML.

Run only in a verification environment authorized for the current task:

```powershell
.\scripts\ui-review\Invoke-NativeUiReview.ps1 -RoundName round-1
.\scripts\ui-review\Invoke-NativeUiReview.ps1 -RoundName round-2
```

A bounded localization-only run creates the real synthetic shell, compares an MRT `x:Uid` label with the typed label and expected translation, checks the root language, and captures native images at 1280, 920 and 720 effective pixels in the light theme. It also checks title-bar siblings for overlap with longer localized labels. It can reuse a matching isolated build without recompiling:

```powershell
.\scripts\ui-review\Invoke-NativeUiReview.ps1 -Action Run -RoundName locale-en `
    -ExistingBuildDirectory <absolute-build-directory> -LocalizationProbeOnly -LanguagePreference en-US
```

Use `-EssentialOnly` while developing the fixture. The full matrix covers 1280 × 800, 1440 × 900, 920 × 720 and 720 × 720 effective pixels in light and dark themes. It includes all eight vault detail kinds, an analyzed healthy login, login editing and custom fields, the native folder flyout, password-history masking and verification behavior, generator modes, Send detail/editor/empty states, reports, settings and authentication pages. The 720-pixel viewport verifies actual list/detail visibility, widths and the production back handlers for both vault and Send workspaces.

Each run writes isolated build products, PNGs, a source fingerprint, an assembly hash/MVID, native dimensions, named element bounds, and semantic assertions under `artifacts/ui-review/v4/native-selftest`. A changed source fingerprint invalidates a clean round. The manifest states the renderer and isolation choices. Only synthetic values can appear in screenshots.

While implementation is changing, copy the declared source inputs into a separate artifact snapshot and run its own controller. Preserve the snapshot's input manifest, hashes and any explicitly recorded discovery revisions. Such runs are discovery evidence for that snapshot, not acceptance of subsequently changed production source. Final acceptance must use a final source snapshot without relaxing the controller's fingerprint checks.

Viewport directory names use effective pixels. PNG dimensions also reflect the captured `RasterizationScale`; for example, a 1280 × 800 effective-pixel client produces a 1920 × 1200 PNG at 150% scaling. The manifest records both units. A full profile must complete all eight light/dark and size combinations. Passing native assertions is separate from visual comparison and review sign-off.

`RenderTargetBitmap` captures native XAML content rather than DWM-composed window decoration. The fixture uses the production `WardenMicaBrush` fallback instead of a system Mica backdrop. Open popup trees are exported as separate native raster layers because WinUI popups are not descendants of the main client tree. WinUI's external `PopupWindowSiteBridge` can return a transparent XAML raster; the fallback uses `PrintWindow` exclusively on visible HWNDs repeatedly verified to belong to the synthetic fixture process. It never requests another process's window text, geometry or pixels. Flyout opening animations are disabled for deterministic capture. Images are never reconstructed from HTML or composited to hide layout failures.

Native raster dimensions and pixel-buffer length are validated before PNG encoding. A popup proven retired while rendering is recorded explicitly. If a still-active external popup exposes no XAML pixel buffer, the run requires a retained own-process native window layer with matching dimensions and visible content; otherwise it fails. Such a native fallback remains labeled separately from an XAML raster.

The driver directly invokes component methods and fixture-only partial hooks inside its own process. Password-history checks use the production sensitive-action service with a synthetic verifier; they exercise per-action approval, denial, inline fallback cancellation/approval, explicit grace, and lock cleanup. Inline fallback completion supplies a synthetic result directly to the prompt's pending completion; no system input or credential UI is automated.

Frame navigation transitions are suppressed only inside the fixture so captures show settled static page states. Animation timing, system-composed Mica, DWM caption controls, and hardware authentication are outside this review's evidence.

Generate labeled review thumbnails without changing the original evidence:

```powershell
.\scripts\ui-review\New-NativeUiReviewContactSheet.ps1 -RunDirectory <absolute-run-directory> -OwnerPrefix native-harness
```

Each owner prefix gets an independent contact-sheet directory and index so parallel reviewers do not overwrite each other's galleries.
