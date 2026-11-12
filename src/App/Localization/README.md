# Application localization

The app ships four MRT Core resource sets: Simplified Chinese (`zh-CN`), Traditional Chinese (`zh-TW`), English (`en-US`), and Japanese (`ja-JP`). The language selector also offers the system preference. The declared default is `zh-CN`, preserving existing installations and deterministic pure view-model tests.

`AppLocalization.Initialize` runs before application XAML is loaded. It restores `AppPreferencesData.LanguagePreference` into **Microsoft.Windows.Globalization.ApplicationLanguages**, then creates the MRT Core `ResourceLoader`. The Windows App SDK override is only process-local in an unpackaged app, so package-independent preference persistence is required. The implementation is described in the [official MRT Core source](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/MRTCore/mrt/Microsoft.Windows.ApplicationModel.Resources/src/ApplicationLanguages.cpp).

A language change is persisted first and takes effect on the next startup. Existing editors, cached pages, and approval dialogs are not reconstructed or partially translated while they contain sensitive state. Failed preference writes do not report success. A missing resource runtime falls back to the embedded default catalog and disables the language selector instead of failing application startup.

System mode retains an empty preference in the app's settings, but resolves `Windows.System.UserProfile.GlobalizationPreferences.Languages` to a supported language on every startup before applying the MRT override. It does not reuse an old packaged-app override as the system preference or pass an empty language tag to the MRT setter.

Static XAML captions use explicit `x:Uid` resources. Code uses `UiText.Get(UiStringKey)` or `UiText.Format(UiStringKey, arguments)`. Only approved application strings have typed keys. No visual-tree text scanning or runtime replacement of vault values is performed. Custom field names, item names, websites, credentials, server messages, and service-layer diagnostic text retain their original content.

`Window` is not a `FrameworkElement`: do not put `x:Uid` on a `Window` root to assign `Window.Title`. Keep fixed brand titles literal, or assign a localized title in code after `InitializeComponent`. Framework element roots receive the frozen `UiText.DisplayCulture` for formatting and native controls.

Send date choices use stable `SendDatePreset` identities, independently translated labels, and `ComboBoxItem.Tag`. Old Chinese date labels are retained only as compatibility identifiers. Login field errors, unlock prompts, and password-hint cleanup use explicit presentation state; translated text never determines those behaviors. Fixed application error messages are localized while arbitrary error strings continue to be redacted.

## Maintaining resources

The committed `LocalizationCatalog.json` contains exact approved source strings, stable typed keys, source contexts, and XAML property mappings. Translation maps live in `Translations`. A key is `Text` plus the first 16 uppercase hexadecimal digits of SHA-256 over its UTF-8 source string or composite-format template. Existing keys do not change when a caption is used on another screen.

Run all generation and verification in an environment authorized by repository policy. The default is `ssh test-env`.

```powershell
dotnet run --project tools/LocalizationCatalog -- inventory <repository-root> <incoming-catalog.json>
python tools/LocalizationCatalog/merge-catalog.py src/App/Localization/LocalizationCatalog.json <incoming-catalog.json> <merged-catalog.json>
# Review the new UI contexts and add every required translation before replacing the catalog.
python tools/LocalizationCatalog/render-resources.py <repository-root> src/App/Localization/LocalizationCatalog.json
dotnet run --project tools/LocalizationCatalog -- apply <repository-root> src/App/Localization/LocalizationCatalog.json --include-settings
dotnet test tests/App.Tests/App.Tests.csproj --filter "FullyQualifiedName~Localization|FullyQualifiedName~LanguagePreference"
```

The renderer rejects missing translations and changed composite-format arguments. It emits `UiStringKey.g.cs`, the embedded `DefaultResources.json`, and each `Strings/<language>/Resources.resw`. Approved date-format literals are adapted for English while preserving argument indices, alignment, and meaning. `App.csproj` embeds the fallback JSON and lets WinUI build the `.resw` files into PRI resources.

The migration tool conservatively skips matching operands, dictionary keys, regular expressions, compile-time-only contexts, and demo data. Every reported UI exception needs semantic review; a successful tool run alone is not proof of complete localization. The source and resource tests cover missing keys, case-insensitive MRT uniqueness, static caption targets, argument counts, explicit language selection, restart persistence, and invalid preferences. Real PRI lookup and window rendering still require native Windows verification.
