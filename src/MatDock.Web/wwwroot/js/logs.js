/* MatDock live log viewer — streams `docker logs -f` over a WebSocket with
   live filter, search (highlight + prev/next), follow (auto-scroll), pause/resume,
   wrap, clear and download. Vanilla JS, no dependencies. */
(function () {
    "use strict";

    var root = document.getElementById("logview");
    if (!root) { return; }

    var MAX_LINES = 5000;
    var envId = root.dataset.envid;
    var container = root.dataset.container;

    var $ = function (sel) { return root.querySelector(sel); };
    var linesEl = $("[data-log-lines]");
    var paneEl = $("[data-log-pane]");
    var statusEl = $("[data-log-status]");
    var liveBtn = $("[data-log-live]");
    var liveLabel = $("[data-log-live-label]");
    var followBtn = $("[data-log-follow]");
    var filterInput = $("[data-log-filter]");
    var searchInput = $("[data-log-search]");
    var matchesEl = $("[data-log-matches]");

    var state = {
        ws: null, live: true, follow: true, wrap: false,
        tail: parseInt(root.dataset.tail, 10) || 200,
        filter: "", search: "",
        lines: [], pending: "", decoder: new TextDecoder("utf-8"),
        firstConnect: true, matches: [], matchIdx: -1
    };

    /* ---------- helpers ---------- */
    function esc(s) { return s.replace(/[&<>]/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]; }); }

    function highlight(raw, term) {
        if (!term) { return esc(raw); }
        var out = "", low = raw.toLowerCase(), t = term.toLowerCase(), i = 0, j;
        while ((j = low.indexOf(t, i)) >= 0) {
            out += esc(raw.slice(i, j)) + "<mark>" + esc(raw.slice(j, j + t.length)) + "</mark>";
            i = j + t.length;
        }
        return out + esc(raw.slice(i));
    }

    function status(text, cls) {
        statusEl.textContent = text;
        statusEl.className = "logview__status" + (cls ? " " + cls : "");
    }

    function atBottom() { return paneEl.scrollHeight - paneEl.scrollTop - paneEl.clientHeight < 24; }
    function scrollToBottom() { paneEl.scrollTop = paneEl.scrollHeight; }

    /* ---------- line rendering ---------- */
    function renderLine(el) {
        var raw = el._raw;
        el.hidden = state.filter && raw.toLowerCase().indexOf(state.filter) === -1;
        if (!el.hidden && state.search) { el.innerHTML = highlight(raw, state.search); }
        else { el.textContent = raw; }
    }

    function pushLine(raw) {
        var el = document.createElement("div");
        el.className = "logview__line";
        el._raw = raw;
        renderLine(el);
        linesEl.appendChild(el);
        state.lines.push(el);
        if (state.lines.length > MAX_LINES) {
            var old = state.lines.shift();
            if (old) { old.remove(); }
        }
    }

    function refreshAll() {
        for (var i = 0; i < state.lines.length; i++) { renderLine(state.lines[i]); }
        rebuildMatches();
    }

    function clearLines() {
        state.lines = [];
        linesEl.textContent = "";
        state.matches = [];
        state.matchIdx = -1;
        matchesEl.textContent = "";
    }

    /* ---------- search navigation ---------- */
    function rebuildMatches() {
        state.matches = Array.prototype.slice.call(linesEl.querySelectorAll("mark"));
        if (!state.search) { matchesEl.textContent = ""; state.matchIdx = -1; return; }
        matchesEl.textContent = state.matches.length + (state.matches.length === 1 ? " match" : " matches");
        if (state.matchIdx >= state.matches.length) { state.matchIdx = state.matches.length - 1; }
    }

    function gotoMatch(delta) {
        if (!state.matches.length) { return; }
        state.matchIdx = (state.matchIdx + delta + state.matches.length) % state.matches.length;
        state.matches.forEach(function (m) { m.classList.remove("is-current"); });
        var m = state.matches[state.matchIdx];
        m.classList.add("is-current");
        m.scrollIntoView({ block: "center" });
        matchesEl.textContent = (state.matchIdx + 1) + "/" + state.matches.length;
    }

    /* ---------- websocket ---------- */
    function connect(resume) {
        closeWs();
        var proto = location.protocol === "https:" ? "wss:" : "ws:";
        var t = resume ? 0 : state.tail;
        var url = proto + "//" + location.host + "/logs/ws?envId=" + encodeURIComponent(envId) +
            "&container=" + encodeURIComponent(container) + "&tail=" + t;
        status("Connecting…");
        var ws = new WebSocket(url);
        ws.binaryType = "arraybuffer";
        state.ws = ws;
        ws.onopen = function () { status("● Live", "is-live"); };
        ws.onmessage = function (e) {
            var bytes = typeof e.data === "string" ? new TextEncoder().encode(e.data) : new Uint8Array(e.data);
            handleData(bytes);
        };
        ws.onclose = function () { if (state.live) { status("Disconnected", "is-error"); } };
        ws.onerror = function () { status("Connection error", "is-error"); };
    }

    function closeWs() {
        if (state.ws) { try { state.ws.close(); } catch (e) { } state.ws = null; }
    }

    function handleData(bytes) {
        state.pending += state.decoder.decode(bytes, { stream: true });
        var idx;
        var appended = false;
        while ((idx = state.pending.indexOf("\n")) >= 0) {
            var line = state.pending.slice(0, idx).replace(/\r$/, "");
            state.pending = state.pending.slice(idx + 1);
            pushLine(line);
            appended = true;
        }
        if (appended) {
            if (state.search) { rebuildMatches(); }
            if (state.follow) { scrollToBottom(); }
        }
    }

    /* ---------- UI wiring ---------- */
    function setLiveUI(on) {
        liveBtn.classList.toggle("is-live", on);
        liveLabel.textContent = on ? "Live" : "Paused";
        liveBtn.title = on ? "Pause live stream" : "Resume live stream";
    }

    liveBtn.addEventListener("click", function () {
        state.live = !state.live;
        setLiveUI(state.live);
        if (state.live) { connect(!state.firstConnect); }
        else { closeWs(); status("Paused"); }
    });

    followBtn.addEventListener("click", function () {
        state.follow = !state.follow;
        followBtn.classList.toggle("is-active", state.follow);
        if (state.follow) { scrollToBottom(); }
    });

    // Scrolling up with the wheel pauses follow; scrolling back to the bottom re-arms it.
    paneEl.addEventListener("wheel", function (e) {
        if (e.deltaY < 0 && state.follow) { state.follow = false; followBtn.classList.remove("is-active"); }
    });
    paneEl.addEventListener("scroll", function () {
        if (!state.follow && atBottom()) { state.follow = true; followBtn.classList.add("is-active"); }
    });

    root.querySelectorAll("[data-log-tail]").forEach(function (btn) {
        btn.addEventListener("click", function () {
            state.tail = parseInt(btn.dataset.logTail, 10) || 200;
            root.querySelectorAll("[data-log-tail]").forEach(function (b) { b.classList.remove("mat-btn--primary"); });
            btn.classList.add("mat-btn--primary");
            clearLines();
            state.live = true; setLiveUI(true);
            connect(false);
        });
    });

    var filterTimer;
    filterInput.addEventListener("input", function () {
        clearTimeout(filterTimer);
        filterTimer = setTimeout(function () {
            state.filter = filterInput.value.trim().toLowerCase();
            refreshAll();
            if (state.follow) { scrollToBottom(); }
        }, 150);
    });

    var searchTimer;
    searchInput.addEventListener("input", function () {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(function () {
            state.search = searchInput.value.trim();
            state.matchIdx = -1;
            refreshAll();
            if (state.matches.length) { gotoMatch(1); }
        }, 150);
    });
    searchInput.addEventListener("keydown", function (e) {
        if (e.key === "Enter") { e.preventDefault(); gotoMatch(e.shiftKey ? -1 : 1); }
    });
    $("[data-log-next]").addEventListener("click", function () { gotoMatch(1); });
    $("[data-log-prev]").addEventListener("click", function () { gotoMatch(-1); });

    $("[data-log-wrap]").addEventListener("click", function () {
        state.wrap = !state.wrap;
        paneEl.classList.toggle("is-wrap", state.wrap);
        this.classList.toggle("is-active", state.wrap);
    });

    $("[data-log-clear]").addEventListener("click", clearLines);

    $("[data-log-download]").addEventListener("click", function () {
        var text = state.lines.map(function (el) { return el._raw; }).join("\n");
        var blob = new Blob([text], { type: "text/plain" });
        var a = document.createElement("a");
        a.href = URL.createObjectURL(blob);
        a.download = (container || "container") + ".log";
        document.body.appendChild(a); a.click(); a.remove();
        setTimeout(function () { URL.revokeObjectURL(a.href); }, 1000);
    });

    window.addEventListener("beforeunload", closeWs);

    /* ---------- start ---------- */
    setLiveUI(true);
    connect(false);
    state.firstConnect = false;
})();
