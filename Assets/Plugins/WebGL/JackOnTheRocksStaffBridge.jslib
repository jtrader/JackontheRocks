mergeInto(LibraryManager.library, {
  JOTR_Staff_StartSnapchatOAuth: function (urlPtr, objectPtr, callbackPtr, originPtr) {
    var url = UTF8ToString(urlPtr);
    var targetObject = UTF8ToString(objectPtr);
    var callbackMethod = UTF8ToString(callbackPtr);
    var allowedOrigin = UTF8ToString(originPtr);
    var popup = null;

    function finish(payload) {
      try {
        SendMessage(targetObject, callbackMethod, JSON.stringify(payload));
      } catch (error) {
        console.error("Jack On The Rocks OAuth callback failed", error);
      }
    }

    function onMessage(event) {
      if (allowedOrigin && event.origin !== allowedOrigin) return;
      if (!event.data || event.data.type !== "JOTR_SNAPCHAT_OAUTH") return;
      window.removeEventListener("message", onMessage);
      if (popup && !popup.closed) popup.close();
      finish(event.data.payload || { success: false, error: "Snapchat returned no profile." });
    }

    window.addEventListener("message", onMessage);
    popup = window.open(url, "jotr-snapchat-oauth", "popup=yes,width=520,height=720");
    if (!popup) {
      window.removeEventListener("message", onMessage);
      finish({ success: false, error: "Allow pop-ups to sign in with Snapchat." });
    }
  },

  JOTR_Staff_StartLocationWatch: function (objectPtr, successPtr, errorPtr) {
    var targetObject = UTF8ToString(objectPtr);
    var successMethod = UTF8ToString(successPtr);
    var errorMethod = UTF8ToString(errorPtr);
    if (!navigator.geolocation || !window.isSecureContext) {
      SendMessage(targetObject, errorMethod, JSON.stringify({
        code: 0,
        message: window.isSecureContext ? "Geolocation is not supported." : "Geolocation requires HTTPS."
      }));
      return -1;
    }

    return navigator.geolocation.watchPosition(function (position) {
      SendMessage(targetObject, successMethod, JSON.stringify({
        latitude: position.coords.latitude,
        longitude: position.coords.longitude,
        accuracy: position.coords.accuracy,
        timestamp: position.timestamp
      }));
    }, function (error) {
      SendMessage(targetObject, errorMethod, JSON.stringify({
        code: error.code || 0,
        message: error.message || "Location is unavailable."
      }));
    }, {
      enableHighAccuracy: true,
      maximumAge: 5000,
      timeout: 20000
    });
  },

  JOTR_Staff_StopLocationWatch: function (watchId) {
    if (navigator.geolocation && watchId >= 0) navigator.geolocation.clearWatch(watchId);
  },

  JOTR_Staff_ShowAlert: function (messagePtr) {
    window.alert(UTF8ToString(messagePtr));
  },

  JOTR_Staff_LeafletInit: function (containerPtr, objectPtr, clickPtr, lat, lng, zoom) {
    var containerId = UTF8ToString(containerPtr);
    var targetObject = UTF8ToString(objectPtr);
    var clickMethod = UTF8ToString(clickPtr);

    function ensureContainer() {
      var container = document.getElementById(containerId);
      if (!container) {
        container = document.createElement("div");
        container.id = containerId;
        container.setAttribute("aria-label", "Active staff service map");
        container.style.cssText = [
          "position:fixed", "z-index:20", "right:16px", "top:80px",
          "width:min(48vw,720px)", "height:calc(100vh - 112px)",
          "min-width:320px", "min-height:360px", "border-radius:12px",
          "overflow:hidden", "box-shadow:0 10px 40px rgba(0,0,0,.45)",
          "background:#16161d", "pointer-events:auto"
        ].join(";");
        document.body.appendChild(container);
      }
      return container;
    }

    function initialize() {
      if (!window.L) return;
      var container = ensureContainer();
      if (window.JOTRStaffLeaflet && window.JOTRStaffLeaflet.map) {
        window.JOTRStaffLeaflet.map.remove();
      }
      var map = window.L.map(container, { zoomControl: true }).setView([lat, lng], zoom);
      window.L.tileLayer("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", {
        maxZoom: 19,
        attribution: "&copy; OpenStreetMap contributors"
      }).addTo(map);
      window.JOTRStaffLeaflet = {
        map: map,
        layer: window.L.layerGroup().addTo(map),
        targetObject: targetObject,
        clickMethod: clickMethod,
        containerId: containerId
      };
      window.setTimeout(function () { map.invalidateSize(); }, 0);
      SendMessage(targetObject, "OnLeafletMapReady", "");
    }

    ensureContainer();
    if (window.L) {
      initialize();
      return;
    }

    if (!document.querySelector('link[data-jotr-leaflet="true"]')) {
      var css = document.createElement("link");
      css.rel = "stylesheet";
      css.href = "https://unpkg.com/leaflet@1.9.4/dist/leaflet.css";
      css.integrity = "sha256-p4NxAoJBhIINfQ3ynhHdQ5kljMZecZ3uYu6Oa3p4qjM=";
      css.crossOrigin = "anonymous";
      css.dataset.jotrLeaflet = "true";
      document.head.appendChild(css);
    }

    var existing = document.querySelector('script[data-jotr-leaflet="true"]');
    if (existing) {
      existing.addEventListener("load", initialize, { once: true });
      return;
    }
    var script = document.createElement("script");
    script.src = "https://unpkg.com/leaflet@1.9.4/dist/leaflet.js";
    script.integrity = "sha256-20nQCchB9co0qIjJZRGuk2/Z9VM+kNiyxNV1lvTlZBo=";
    script.crossOrigin = "anonymous";
    script.dataset.jotrLeaflet = "true";
    script.onload = initialize;
    script.onerror = function () { console.error("Unable to load Leaflet. Bundle it locally for restrictive CSP/offline deployments."); };
    document.head.appendChild(script);
  },

  JOTR_Staff_LeafletRefresh: function (jsonPtr) {
    var state = window.JOTRStaffLeaflet;
    if (!state || !state.map || !state.layer || !window.L) return;
    var payload;
    try { payload = JSON.parse(UTF8ToString(jsonPtr)); } catch (error) { return; }
    state.layer.clearLayers();

    (payload.markers || []).forEach(function (staff) {
      var lat = Number(staff.latitude);
      var lng = Number(staff.longitude);
      if (!Number.isFinite(lat) || !Number.isFinite(lng) || (lat === 0 && lng === 0)) return;

      var active = staff.online === true;
      var manager = staff.role === "AreaManager";
      var color = !active ? "#d64545" : (manager ? "#2585e6" : "#24a35a");
      var icon = window.L.divIcon({
        className: "jotr-staff-marker",
        html: '<span style="display:block;width:18px;height:18px;border-radius:50% 50% 50% 0;' +
          'transform:rotate(-45deg);background:' + color + ';border:2px solid white;' +
          'box-shadow:0 2px 7px rgba(0,0,0,.45)"></span>',
        iconSize: [22, 22], iconAnchor: [11, 22]
      });
      var marker = window.L.marker([lat, lng], { icon: icon }).addTo(state.layer);
      var tooltip = document.createElement("div");
      var name = document.createElement("strong");
      name.textContent = staff.displayName || "Unnamed staff";
      tooltip.appendChild(name);
      tooltip.appendChild(document.createElement("br"));
      tooltip.appendChild(document.createTextNode((staff.role || "Staff") + " · " + (staff.status || "Unknown")));
      marker.bindTooltip(tooltip);
      marker.on("click", function () {
        SendMessage(state.targetObject, state.clickMethod, String(staff.staffId || ""));
      });

      if (manager || !active) {
        window.L.circle([lat, lng], {
          radius: Math.max(100, Number(staff.radiusKm || 5) * 1000),
          color: color,
          fillColor: color,
          fillOpacity: active ? 0.12 : 0.035,
          opacity: active ? 0.7 : 0.35,
          dashArray: active ? null : "7 8",
          interactive: false
        }).addTo(state.layer);
      }
    });
  },

  JOTR_Staff_LeafletSetVisible: function (visible) {
    var state = window.JOTRStaffLeaflet;
    if (!state) return;
    var container = document.getElementById(state.containerId);
    if (container) container.style.display = visible ? "block" : "none";
    if (visible && state.map) window.setTimeout(function () { state.map.invalidateSize(); }, 0);
  },

  JOTR_Staff_LeafletDestroy: function () {
    var state = window.JOTRStaffLeaflet;
    if (!state) return;
    if (state.map) state.map.remove();
    var container = document.getElementById(state.containerId);
    if (container && container.parentNode) container.parentNode.removeChild(container);
    window.JOTRStaffLeaflet = null;
  }
});
