# Native implementation notes

The approved Fluent redesign has been applied to the WinUI application. The HTML redesign remains a review prototype; the images in this directory are captures of the actual application using synthetic data on the isolated Vaultwarden review instance.

## Scope

- Contextual navigation keeps vault scopes in the vault workspace. Returning restores search, type, facets, sort, scroll position, and focus within the current unlock session without automatically revealing a credential.
- Vault details show complete website addresses, explicit copy actions, protected-field states, ownership and permission information, and collapsible secondary content. Editor actions stay visible at the bottom of the window.
- Send separates access status, access expiration, and server deletion time. Copy-link actions remain available on disabled links. Creation and editing use a bounded, scrollable dialog with fixed actions.
- The generator is a separate workspace with a shared compact editor dialog. Each mode retains its result; only successful copies enter the in-memory history, which is cleared on lock.
- Import and export use two responsive cards with source-file feedback, preview validation, format-specific scope, and plaintext-export disclosure.
- Settings use local Security, Account, Preferences, and About sections. Devices distinguish registration time from recent activity. Emergency access describes real permissions and takeover consequences.
- Login and registration use a consistent form width, actual account/server context, and stage-specific controls. HTTP 429 is reported as rate limiting and preserves retryable authentication challenges.

## Runtime corrections discovered during review

- Dynamic vault detail bindings now have explicit Native AOT metadata. CsWinRT 2.3.1 fixes generated namespace qualification and bindable-property vtables without disabling trimming or AOT.
- DetailCard updates its content through its dependency-property callback.
- Reused-password results use an observable UI collection instead of assigning the service array directly to a nested ItemsRepeater.
- The cipher editor uses a non-null presentation draft instead of unreliable native deferred loading. Cancelling or locking replaces the presentation with empty values; hidden custom fields never populate an ordinary text input. Equal-value placement and type updates do not re-enter two-way bindings.
- A refresh-token-only offline session restores its access token before manual synchronization, retaining the original session lease and existing refresh serialization.
- Clipboard feedback is emitted only after the protected OS write succeeds. TOTP copying recomputes the current code and respects a custom period.
- Send and vault list accessibility names expose the visible item name, not record internals or a Send decryption link.
- Locking evicts cached pages and navigation journals, clears generator history and workspace context, and clears decrypted folder/organization/collection labels from the shell.
- Dialogs inherit the window's explicit theme. Hidden status bars collapse their layout space, and the top command bar opens its creation menu below the button.

## Review environment and boundaries

- Windows native client, .NET 10, Windows App SDK 2.2.0, CsWinRT 2.3.1.
- Dedicated `test-env` Vaultwarden 1.37.2 instance reached through a loopback SSH tunnel. All captured account and vault data are synthetic.
- The test account master password is stored only as DPAPI ciphertext and supplied to the UI in memory. No memory dumps were generated. Temporary diagnostics used method names only and were removed from application source.
- Backend-dependent capabilities remain gated. This review instance does not support newer cipher types or trusted-device capability; the implementation does not fabricate support.
- Real external authentication, Windows Hello enrollment, live email delivery, account deletion, and external breach-check services are not exercised by the screenshot review.

See `validation.json` for final build, automated-test, and native-interaction results. Captures are representative application states, not exhaustive evidence for every prototype scenario.
