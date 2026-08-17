mergeInto(LibraryManager.library, {
  JOTR_Chat_StartOAuth: function (urlPtr, objectPtr, callbackPtr, originPtr) {
    var url = UTF8ToString(urlPtr);
    var targetObject = UTF8ToString(objectPtr);
    var callbackMethod = UTF8ToString(callbackPtr);
    var allowedOrigin = UTF8ToString(originPtr);
    var popup = null;
    var completed = false;

    function finish(payload) {
      if (completed) return;
      completed = true;
      window.removeEventListener("message", onMessage);
      if (popup && !popup.closed) popup.close();
      SendMessage(targetObject, callbackMethod, JSON.stringify(payload));
    }

    function onMessage(event) {
      if (allowedOrigin && event.origin !== allowedOrigin) return;
      if (!event.data || event.data.type !== "JOTR_SNAPCHAT_CHAT_AUTH") return;
      finish(event.data.payload || {
        success: false,
        error: "Snapchat returned no authenticated profile."
      });
    }

    window.addEventListener("message", onMessage);
    popup = window.open(url, "jotr-snapchat-chat-auth", "popup=yes,width=520,height=720");
    if (!popup) finish({ success: false, error: "Allow pop-ups to sign in with Snapchat." });
  },

  JOTR_Chat_WebSocketConnect: function (
    urlPtr, objectPtr, openPtr, messagePtr, errorPtr, closePtr
  ) {
    var url = UTF8ToString(urlPtr);
    var targetObject = UTF8ToString(objectPtr);
    var openMethod = UTF8ToString(openPtr);
    var messageMethod = UTF8ToString(messagePtr);
    var errorMethod = UTF8ToString(errorPtr);
    var closeMethod = UTF8ToString(closePtr);

    if (!url.startsWith("wss://") || typeof window.WebSocket !== "function") return -1;
    if (!window.JOTRChatSockets) {
      window.JOTRChatSockets = { nextHandle: 1, sockets: Object.create(null) };
    }

    var store = window.JOTRChatSockets;
    var handle = store.nextHandle++;
    var socket;
    try {
      socket = new WebSocket(url, ["jotr-chat-v1"]);
    } catch (error) {
      SendMessage(targetObject, errorMethod, String(error && error.message || "WebSocket creation failed."));
      return -1;
    }
    store.sockets[handle] = socket;

    socket.onopen = function () {
      SendMessage(targetObject, openMethod, "");
    };
    socket.onmessage = function (event) {
      if (typeof event.data === "string") {
        SendMessage(targetObject, messageMethod, event.data);
        return;
      }
      if (event.data instanceof Blob) {
        event.data.text().then(function (text) {
          SendMessage(targetObject, messageMethod, text);
        }).catch(function () {
          SendMessage(targetObject, errorMethod, "Unable to decode a binary chat message.");
        });
      }
    };
    socket.onerror = function () {
      SendMessage(targetObject, errorMethod, "Secure chat connection error.");
    };
    socket.onclose = function (event) {
      delete store.sockets[handle];
      SendMessage(targetObject, closeMethod, JSON.stringify({
        code: event.code,
        reason: event.reason || "",
        wasClean: event.wasClean
      }));
    };
    return handle;
  },

  JOTR_Chat_WebSocketSend: function (handle, messagePtr) {
    var store = window.JOTRChatSockets;
    var socket = store && store.sockets[handle];
    if (!socket || socket.readyState !== WebSocket.OPEN) return 0;
    try {
      socket.send(UTF8ToString(messagePtr));
      return 1;
    } catch (error) {
      return 0;
    }
  },

  JOTR_Chat_WebSocketClose: function (handle, code, reasonPtr) {
    var store = window.JOTRChatSockets;
    var socket = store && store.sockets[handle];
    if (!socket) return;
    var reason = UTF8ToString(reasonPtr).substring(0, 123);
    try {
      socket.close(code, reason);
    } catch (error) {
      socket.close();
    }
  }
});
