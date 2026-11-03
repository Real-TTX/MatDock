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

/* Install the app: where the browser offers it (Chrome, Edge, Android), the buttons [data-pwa-install] show and ask; on iPhone and iPad it is
   "Share → Add to Home Screen", which no page can trigger. Hidden in the installed app. */
(function () {
    "use strict";
    var offer = null;
    function buttons() { return document.querySelectorAll("[data-pwa-install]"); }
    window.addEventListener("beforeinstallprompt", function (e) {
        e.preventDefault();
        offer = e;
        buttons().forEach(function (b) { b.hidden = false; });
    });
    window.addEventListener("appinstalled", function () { offer = null; buttons().forEach(function (b) { b.hidden = true; }); });
    document.addEventListener("click", function (e) {
        var b = e.target.closest("[data-pwa-install]");
        if (!b || !offer) { return; }
        offer.prompt();
        offer.userChoice.finally(function () { offer = null; buttons().forEach(function (x) { x.hidden = true; }); });
    });
})();

/* Notifications on this device (profile, [data-push-device]): on = the browser's permission, a push subscription with this installation's key, and the
   subscription registered for the signed-in user. The page names the subscriptions the server knows, so one the server forgot shows as off. */
(function () {
    "use strict";
    var card = document.querySelector("[data-push-device]");
    if (!card) { return; }

    var stateText = card.querySelector("[data-push-state]");
    var enable = card.querySelector("[data-push-enable]");
    var disable = card.querySelector("[data-push-disable]");
    var test = card.querySelector("[data-push-test]");
    function say(text) { stateText.textContent = text; }
    var known = JSON.parse(card.getAttribute("data-push-endpoints") || "[]");
    var standalone = window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
    var ios = /iPhone|iPad|iPod/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);

    function post(url, body) {
        return fetch(url, { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json", "X-MatDock-Request": "1" }, body: JSON.stringify(body || {}) });
    }

    // A device in the list can be removed from any browser.
    card.querySelectorAll("[data-push-remove]").forEach(function (b) {
        b.addEventListener("click", function () {
            post("/ui/push/devices/remove", { id: parseInt(b.getAttribute("data-push-remove"), 10) }).then(function () { location.reload(); });
        });
    });

    if (!window.isSecureContext) { say("Notifications need MatDock on https (or localhost); this page is not."); return; }
    if (!("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window)) {
        say(ios && !standalone
            ? "On iPhone and iPad, notifications work in the installed app: Share → Add to Home Screen, then open MatDock from the Home Screen and turn them on here."
            : "This browser cannot show notifications from a web app.");
        return;
    }

    function ready() {
        return Promise.race([navigator.serviceWorker.ready, new Promise(function (_, reject) {
            setTimeout(function () { reject(new Error("the app's service worker is not running; reload the page")); }, 8000);
        })]);
    }
    function keyBytes(base64Url) {
        var base64 = (base64Url + "=".repeat((4 - base64Url.length % 4) % 4)).replace(/-/g, "+").replace(/_/g, "/");
        return Uint8Array.from(atob(base64), function (c) { return c.charCodeAt(0); });
    }

    function show() {
        return ready().then(function (reg) { return reg.pushManager.getSubscription(); }).then(function (sub) {
            var on = sub !== null && known.indexOf(sub.endpoint) >= 0;
            enable.hidden = on || Notification.permission === "denied";
            disable.hidden = !sub;
            test.hidden = !on;
            say(Notification.permission === "denied"
                ? "Notifications are blocked for this site in the browser's settings: allow them there, then come back."
                : on ? "Notifications are on for this device: every notification of MatDock reaches it." : "Notifications are off for this device.");
        });
    }

    enable.addEventListener("click", function () {
        enable.disabled = true;
        Notification.requestPermission().then(function (permission) {
            if (permission !== "granted") { say("Notifications were not allowed."); return null; }
            return fetch("/ui/push/key", { credentials: "same-origin", headers: { Accept: "application/json" } })
                .then(function (r) { return r.json(); })
                .then(function (k) {
                    return ready().then(function (reg) {
                        return reg.pushManager.getSubscription().then(function (sub) {
                            return sub || reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(k.publicKey) });
                        });
                    });
                })
                .then(function (sub) { return post("/ui/push/devices", sub.toJSON()); })
                .then(function (r) {
                    if (r.ok) { location.reload(); return; }
                    return r.json().catch(function () { return {}; }).then(function (d) { say("MatDock did not take this device: " + (d.error || r.status)); });
                });
        }).catch(function (error) { say("Notifications could not be turned on: " + error.message); })
          .then(function () { enable.disabled = false; });
    });

    disable.addEventListener("click", function () {
        disable.disabled = true;
        ready().then(function (reg) { return reg.pushManager.getSubscription(); }).then(function (sub) {
            if (!sub) { return null; }
            return post("/ui/push/devices/remove", { endpoint: sub.endpoint }).then(function () { return sub.unsubscribe(); });
        }).then(function () { location.reload(); })
          .catch(function (error) { say("Notifications could not be turned off: " + error.message); disable.disabled = false; });
    });

    test.addEventListener("click", function () {
        test.disabled = true;
        post("/ui/push/test").then(function (r) { return r.json(); }).then(function (d) {
            say(d.reached > 0 ? "Sent: it should appear on this device in a moment." : "The push service did not take it; try turning notifications off and on.");
        }).catch(function (error) { say("Not sent: " + error.message); }).then(function () { test.disabled = false; });
    });

    show().catch(function (error) { say("Notifications are not available: " + error.message); });
})();
