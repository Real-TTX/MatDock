/* The offline page looks for the server by itself: every 10 s and as soon as the browser says it is online again, it asks
   /api/health (never cached: the service worker leaves /api alone) and, once the server answers, goes back to the app. */
(function () {
    "use strict";
    var status = document.getElementById("offline-status");

    function probe() {
        if (!window.fetch) { return; }
        fetch("/api/health", { cache: "no-store", credentials: "omit" })
            .then(function (response) { if (response.ok) { location.replace("/"); } })
            .catch(function () { if (status) { status.textContent = "Still offline. Trying again every few seconds."; } });
    }

    window.addEventListener("online", probe);
    setInterval(probe, 10000);
    probe();
})();
