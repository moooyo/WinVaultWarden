const HOST_NAME = "com.winvaultwarden.browser";

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.target !== "winvaultwarden-native") {
    return false;
  }

  chrome.runtime.sendNativeMessage(HOST_NAME, message.body, (response) => {
    const lastError = chrome.runtime.lastError;
    if (lastError) {
      sendResponse({
        ok: false,
        type: "error",
        error: {
          code: "native_host_unavailable",
          message: lastError.message,
        },
      });
      return;
    }

    sendResponse(response);
  });

  return true;
});
