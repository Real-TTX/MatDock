(function () {
    "use strict";

    var el = document.getElementById("terminal");
    if (!el || typeof window.Terminal !== "function") {
        return;
    }

    var envId = el.getAttribute("data-env-id");
    var container = el.getAttribute("data-container") || "";

    var term = new window.Terminal({
        cursorBlink: true,
        fontFamily: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
        fontSize: 13,
        scrollback: 5000,
        theme: {
            background: "#0b0f14",
            foreground: "#d5dbe3",
            cursor: "#d5dbe3"
        }
    });

    var FitAddonCtor = (window.FitAddon && window.FitAddon.FitAddon) || window.FitAddon;
    var fit = FitAddonCtor ? new FitAddonCtor() : null;
    if (fit) {
        term.loadAddon(fit);
    }

    term.open(el);
    if (fit) {
        try { fit.fit(); } catch (e) { /* ignore */ }
    }

    // Guard against a not-yet-laid-out container producing a degenerate size (e.g. 2 cols).
    if (term.cols < 20 || term.rows < 6) {
        try { term.resize(Math.max(term.cols, 80), Math.max(term.rows, 24)); } catch (e) { /* ignore */ }
    }

    var proto = location.protocol === "https:" ? "wss:" : "ws:";
    var url = proto + "//" + location.host + "/terminal/ws?envId=" + encodeURIComponent(envId) +
        "&cols=" + term.cols + "&rows=" + term.rows +
        (container ? "&container=" + encodeURIComponent(container) : "");

    var ws = new WebSocket(url);
    ws.binaryType = "arraybuffer";

    ws.onopen = function () {
        term.focus();
    };
    ws.onmessage = function (ev) {
        if (typeof ev.data === "string") {
            term.write(ev.data);
        } else {
            term.write(new Uint8Array(ev.data));
        }
    };
    ws.onclose = function () {
        term.write("\r\n\x1b[33m[Verbindung getrennt]\x1b[0m\r\n");
    };
    ws.onerror = function () {
        term.write("\r\n\x1b[31m[Verbindungsfehler]\x1b[0m\r\n");
    };

    var enc = new TextEncoder();
    term.onData(function (data) {
        if (ws.readyState === WebSocket.OPEN) {
            ws.send(enc.encode(data));
        }
    });

    window.addEventListener("resize", function () {
        if (fit) {
            try { fit.fit(); } catch (e) { /* ignore */ }
        }
    });
})();
