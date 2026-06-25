const PAGE_SOURCE = "winvaultwarden-page";
const CONTENT_SOURCE = "winvaultwarden-content";

injectPageScript();

window.addEventListener("message", (event) => {
  if (event.source !== window || event.data?.source !== PAGE_SOURCE) {
    return;
  }

  const request = event.data.request;
  if (!request?.id || !request?.type) {
    return;
  }

  chrome.runtime.sendMessage(
    {
      target: "winvaultwarden-native",
      body: request,
    },
    (response) => {
      const lastError = chrome.runtime.lastError;
      window.postMessage(
        {
          source: CONTENT_SOURCE,
          id: request.id,
          response: lastError
            ? {
                id: request.id,
                ok: false,
                type: "error",
                error: {
                  code: "extension_bridge_error",
                  message: lastError.message,
                },
              }
            : response,
        },
        window.location.origin,
      );
    },
  );
});

function injectPageScript() {
  const script = document.createElement("script");
  script.src = chrome.runtime.getURL("src/page-webauthn.js");
  script.async = false;
  script.onload = () => script.remove();

  const root = document.documentElement || document.head || document;
  root.prepend(script);
}
