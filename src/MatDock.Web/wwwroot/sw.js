/* MatDock service worker — offline-capable app shell, no dependencies.
 *
 * Strategy, deliberately conservative for an authenticated admin tool:
 *  - Navigations: network-first, falling back to a generic offline page. Authenticated
 *    HTML is NEVER written to the cache, so no user data can leak between accounts on a
 *    shared device.
 *  - Static assets (/css, /js, /img, /lib, manifest): stale-while-revalidate. Versioned
 *    (?v=) URLs make cached copies self-invalidate when the file changes.
 *  - Everything else (POST, WebSocket upgrades, cross-origin, webhooks): passthrough.
 *
 * Bump CACHE_VERSION to force old caches out on the next activation.
 */
const CACHE_VERSION = "v4";
const CACHE = "matdock-" + CACHE_VERSION;

const PRECACHE = [
    "/offline.html",
    "/js/offline.js",
    "/matui/matui.css",
    "/matui/matui.boot.js",
    "/manifest.webmanifest",
    "/img/icon.svg",
    "/img/favicon.svg",
    "/img/logo.svg",
    "/img/icon-192.png",
    "/img/icon-512.png",
    "/img/icon-maskable-512.png",
];

self.addEventListener("install", (event) => {
    // Best-effort precache: a single changed/removed asset must never block the new
    // worker from installing, or updates would stall. skipWaiting() lets the fresh
    // worker activate immediately instead of waiting for every tab to close.
    event.waitUntil(
        caches.open(CACHE)
            .then((cache) => Promise.allSettled(PRECACHE.map((url) => cache.add(url))))
            .then(() => self.skipWaiting())
    );
});

self.addEventListener("activate", (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
            .then(() => self.clients.claim())
    );
});

function isStaticAsset(url) {
    return url.origin === self.location.origin &&
        (/^\/(css|js|img|lib|matui)\//.test(url.pathname) || url.pathname === "/manifest.webmanifest");
}

self.addEventListener("fetch", (event) => {
    const req = event.request;
    if (req.method !== "GET") { return; }

    const url = new URL(req.url);
    if (url.origin !== self.location.origin) { return; }

    // Top-level navigations: always try the network first; only fall back to the
    // offline page when the network is unreachable. Never cache the response.
    if (req.mode === "navigate") {
        event.respondWith(
            fetch(req).catch(() => caches.match("/offline.html"))
        );
        return;
    }

    // Static assets: serve from cache immediately, refresh in the background.
    if (isStaticAsset(url)) {
        event.respondWith(
            caches.open(CACHE).then((cache) =>
                cache.match(req).then((cached) => {
                    const network = fetch(req).then((resp) => {
                        if (resp && resp.ok) { cache.put(req, resp.clone()); }
                        return resp;
                    }).catch(() => cached);
                    return cached || network;
                })
            )
        );
    }
});

// Let the page tell a waiting worker to take over immediately.
self.addEventListener("message", (event) => {
    if (event.data === "skipWaiting") { self.skipWaiting(); }
});

// ---- Notifications (Web Push; MatDock sends every notification to the devices that switched them on, pwa.js subscribes) ----
// A message is JSON: title, body, url (opened by a tap), tag (a newer one with the same tag replaces the old one on the device).
self.addEventListener("push", (event) => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch (error) { data = { body: event.data ? event.data.text() : "" }; }
    event.waitUntil(self.registration.showNotification(data.title || "MatDock", {
        body: data.body || "",
        icon: "/img/icon-192.png",
        badge: "/img/icon-192.png",
        tag: data.tag || undefined,
        renotify: !!data.tag,
        timestamp: Date.now(),
        data: { url: data.url || "/" },
    }));
});

self.addEventListener("notificationclick", (event) => {
    event.notification.close();
    const target = new URL((event.notification.data && event.notification.data.url) || "/", self.location.origin);
    if (target.origin !== self.location.origin) { return; }
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
        const open = windows.find((client) => new URL(client.url).origin === self.location.origin);
        if (open) {
            await open.focus();
            if ("navigate" in open) { try { await open.navigate(target.href); return; } catch (error) { /* not controlled: open one */ } }
        }
        await self.clients.openWindow(target.href);
    })());
});
