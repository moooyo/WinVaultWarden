# Localization catalog migration

This developer tool inventories explicit user-interface strings and applies resource references without inspecting or translating runtime vault data. It uses the Roslyn assemblies bundled with the selected .NET SDK; no compiler package is downloaded.

Run the tool only in an environment authorized for verification or source transformation. Repository rules require remote verification by default.

```powershell
dotnet run --project tools/LocalizationCatalog -- self-test
dotnet run --project tools/LocalizationCatalog -- inventory <repository-root> <catalog.json>
dotnet run --project tools/LocalizationCatalog -- apply <repository-root> <catalog.json> --xaml-only
dotnet run --project tools/LocalizationCatalog -- apply <repository-root> <catalog.json> --csharp-only --only=src/App/Views/ExamplePage.xaml.cs
```

Catalog keys are `Text` followed by the first 16 uppercase hexadecimal digits of SHA-256 over the UTF-8 source string or composite-format template. Each entry contains exact source text and file/line contexts. XAML mappings also contain the stable element UID and complete MRTCore property resource name, including qualified attached-property syntax.

Application stages every change before writing. A source text absent from the approved catalog stops the operation. SettingsPage files are excluded from application until `--include-settings` is explicitly supplied. Multiple `--only=<relative-file>` options select exact files. Literal fallback values, handlers, bindings, names, and whitespace are preserved when adding XAML UIDs.

The C# migration uses fixed enum keys with `UiText.Get` and `UiText.Format`. Interpolation order, alignment and format clauses remain explicit. Changed const fields become static readonly and changed const locals become ordinary locals. Constant-only attribute, pattern, default-argument and known const-reference contexts are reported for manual migration rather than rewritten unsafely. Matching, regular-expression, replacement and lookup-key operands also require manual semantic review. Mock and demo vault data seeders are excluded from C# migration.

English text is eligible only in explicit UI property assignments or known presentation calls, such as dialog properties, feedback, accessible names, tooltips and `UiOperationResult.Failure`. Paths, HTTP addresses, protocol identifiers and generic exception or log diagnostics are not inferred to be UI text. Existing source-text keys are independent of context and remain stable as new UI contexts are added.

Review generated changes and resource coverage before integration. Resources must be generated from the same catalog; this tool does not invent translations or claim that skipped constant-only contexts have been localized.

`Window.Title` is an explicit exception: `Window` does not support FrameworkElement `x:Uid` assignment. The tool removes root Window UIDs while preserving title literals; assign translated window titles in code after `InitializeComponent`. Custom control `Label` and `Caption` properties are explicit localizable targets. Stable protocol IDs and selection identities remain outside resource lookup.

After the initial migration, merge new inventories with `src/App/Localization/LocalizationCatalog.json` using `merge-catalog.py` before rendering resources with `render-resources.py`. This preserves keys already referenced by code, which are no longer discoverable as source literals. Generated assets must always come from the approved committed catalog and complete translation maps.
