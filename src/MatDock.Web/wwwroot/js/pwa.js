/* MatDock PWA bootstrap.
 *
 * Keeps an installed app up to date on the device WITHOUT a reinstall:
 *  - registers the service worker,
 *  - actively checks for a new version (on load, when the app regains focus, hourly),
 *  - reloads once when a freshly-activated worker takes control, so the user lands on
 *    the new version automatically. The SW's skipWaiting() makes that hand-over immediate.
 * All best-effort: failures (e.g. non-secure origins) are swallowed. */
(function () {
    "use strict";
    if (!("serviceWorker" in navigator)) { return; }

    var refreshing = false;
    // Was a worker already controlling this page when it loaded? If not, this is the
    // first install — the first controllerchange is expected and must NOT trigger a reload.
    var hadController = !!navigator.serviceWorker.controller;

    navigator.serviceWorker.addEventListener("controllerchange", function () {
        if (refreshing || !hadController) { return; }
        refreshing = true;
        window.location.reload();
    });

    window.addEventListener("load", function () {
        navigator.serviceWorker.register("/sw.js").then(function (reg) {
            function check() { reg.update().catch(function () { /* ignore */ }); }

            check();                                   // check right away
            setInterval(check, 60 * 60 * 1000);        // and hourly for long-lived installs
            document.addEventListener("visibilitychange", function () {
                if (document.visibilityState === "visible") { check(); }
            });
            window.addEventListener("online", check);  // recheck as soon as we're back online
        }).catch(function () { /* best effort */ });
    });
})();
