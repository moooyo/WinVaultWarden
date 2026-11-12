# Browser passkey bridge

The extension connects WebAuthn requests to the native Windows vault. It does not expose a stored-password UI. Registration uses the vault's platform authenticator; requests explicitly selecting a cross-platform authenticator are passed to the browser with their original options. Conditional and silent retrieval also use native browser WebAuthn.

## Browser permissions and trusted context

Both Chrome and Edge manifests require `nativeMessaging` for the native host and `webNavigation` to inspect the current document ancestry. Request IDs and the MAIN/ISOLATED channel only correlate traffic; page-supplied values never establish origin or ancestor trust.

The isolated script reads the document's real `publickey-credentials-get` and `publickey-credentials-create` policy verdicts, including in the top document. A missing policy API or a denied feature does not grant access.

Before native dispatch, the background worker obtains a fresh `getAllFrames` snapshot and verifies the requesting frame's `documentId`. Each ancestor must be active, connected through its own extension runtime port, and bound to the browser's sender origin and document ID. A URL alone is insufficient because sandboxed documents can have an HTTPS URL and an opaque origin. Missing proofs, opaque origins, insecure ancestors, inconsistent ancestry, and replaced documents fail closed. Only HTTPS and exact localhost/loopback HTTP origins are accepted.

`sameOriginWithAncestors` considers the full chain. An A-to-B-to-A frame chain remains cross-origin even when its leaf and top origins match. The worker rebuilds the native context and `origin`, `crossOrigin`, and `topOrigin`; arbitrary top-level fields, pipe nonces, and page-supplied trust metadata are not forwarded.

Chrome's [webNavigation API](https://developer.chrome.com/docs/extensions/reference/api/webNavigation) documents frame/document identities. The [WebAuthn client-data definition](https://www.w3.org/TR/webauthn-3/#dictionary-client-data) defines ancestor-related origin semantics.

## Document and worker lifecycle

`pagehide` cancels active ceremonies, revokes response delivery, disconnects the document port, and stops reconnect timers. `pageshow` establishes a fresh port after BFCache restoration. Active documents also make up to five delayed reconnection attempts after an unexpected disconnect to restore the ancestor-identity registry after worker restart. A `frame.ready` acknowledgement resets the retry budget. Reconnection rebuilds identity only; it never replays an interrupted registration or assertion.

Native operations are bound to their frame and ancestor connection capabilities. Losing an ancestor cancels the dependent operation. Messages from retired connections cannot complete requests on replacement ports, including when a page reuses an ID. Cancellation during browser-context lookup prevents native dispatch.

This follows Chrome's [BFCache extension messaging guidance](https://developer.chrome.com/blog/bfcache-extension-messaging-changes). Missing trustworthy ancestor metadata during startup or reconnection is an explicit denial; a later request can proceed after identity is restored.

## Verification

Run the existing Node test suite only in an environment authorized for the current task. The suite covers both manifests, actual policy verdict propagation, complete and opaque ancestor handling, navigation races, admission limits, cancellation, multi-cycle BFCache restoration, worker reconnect, and late responses. Mock tests do not establish that real Chrome/Edge signer-pinning, Windows user verification, or a relying party's acceptance passed; those require the separate native-browser acceptance workflow.

The bridge's extension outputs are limited to implemented WebAuthn functionality. PRF and large-blob extension support are not advertised. Packaging and native-host installation must follow the distribution's supported registration path.
