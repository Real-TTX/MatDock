/* MatDock PWA bootstrap — registers the service worker so the app is installable
   and works offline. Best-effort: failures are swallowed (e.g. non-secure origins). */
(function () {
    "use strict";
    if (!("serviceWorker" in navigator)) { return; }
    window.addEventListener("load", function () {
        navigator.serviceWorker.register("/sw.js").catch(function () { /* ignore */ });
    });
})();
