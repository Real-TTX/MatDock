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
const CACHE_VERSION = "v1";
const CACHE = "matdock-" + CACHE_VERSION;

const PRECACHE = [
    "/offline.html",
    "/manifest.webmanifest",
    "/img/icon.svg",
    "/img/favicon.svg",
    "/img/logo.svg",
    "/img/icon-192.png",
    "/img/icon-512.png",
];

self.addEventListener("install", (event) => {
    event.waitUntil(
        caches.open(CACHE)
            .then((cache) => cache.addAll(PRECACHE))
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
        (/^\/(css|js|img|lib)\//.test(url.pathname) || url.pathname === "/manifest.webmanifest");
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
