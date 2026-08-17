mergeInto(LibraryManager.library, {
  JOTR_NexaPay_OpenCheckout: function (
    checkoutPtr, originPtr, objectPtr, returnPtr, errorPtr
  ) {
    var checkoutUrl = UTF8ToString(checkoutPtr);
    var allowedOrigin = UTF8ToString(originPtr);
    var targetObject = UTF8ToString(objectPtr);
    var returnMethod = UTF8ToString(returnPtr);
    var errorMethod = UTF8ToString(errorPtr);

    if (!checkoutUrl.startsWith("https://nexapay.one/") &&
        !checkoutUrl.match(/^https:\/\/[^/]+\.nexapay\.one\//i)) {
      SendMessage(targetObject, errorMethod, "The checkout URL is not an approved NexaPay host.");
      return 0;
    }

    if (window.JOTRNexaPayCheckout && !window.JOTRNexaPayCheckout.closed) {
      window.JOTRNexaPayCheckout.close();
    }

    function onMessage(event) {
      if (allowedOrigin && event.origin !== allowedOrigin) return;
      if (!event.data || event.data.type !== "JOTR_NEXAPAY_RETURN") return;
      window.removeEventListener("message", onMessage);
      SendMessage(targetObject, returnMethod, JSON.stringify(event.data.payload || {}));
      if (window.JOTRNexaPayCheckout && !window.JOTRNexaPayCheckout.closed) {
        window.JOTRNexaPayCheckout.close();
      }
      window.JOTRNexaPayCheckout = null;
    }

    window.addEventListener("message", onMessage);
    var popup = window.open(
      checkoutUrl,
      "jotr-nexapay-checkout",
      "popup=yes,width=520,height=760,resizable=yes,scrollbars=yes"
    );
    if (!popup) {
      window.removeEventListener("message", onMessage);
      SendMessage(targetObject, errorMethod, "Allow pop-ups to continue to NexaPay checkout.");
      return 0;
    }
    popup.opener = window;
    window.JOTRNexaPayCheckout = popup;
    return 1;
  },

  JOTR_NexaPay_CloseCheckout: function () {
    if (window.JOTRNexaPayCheckout && !window.JOTRNexaPayCheckout.closed) {
      window.JOTRNexaPayCheckout.close();
    }
    window.JOTRNexaPayCheckout = null;
  }
});
