/* MatDock operation console — follows the output of a running operation (a deploy) by polling /ui/operations/{id}, with search
   (highlight + prev/next), follow, wrap, copy and download. When an operation that was running at page load ends, the page reloads
   once so its lists show the new state (the console stays: the operation is still in the address). Vanilla JS, no dependencies. */
(function () {
    "use strict";

    var root = document.querySelector("[data-console]");
    if (!root) { return; }

    var $ = function (sel) { return root.querySelector(sel); };
    var url = root.getAttribute("data-console");
    var linesEl = $("[data-console-lines]");
    var paneEl = $("[data-console-pane]");
    var stateEl = $("[data-console-state]");
    var searchInput = $("[data-console-search]");
    var matchesEl = $("[data-console-matches]");
    var followBtn = $("[data-console-follow]");
    var wrapBtn = $("[data-console-wrap]");
    var wasRunning = root.getAttribute("data-console-running") === "true";

    var state = { text: "", offset: 0, follow: true, search: "", matches: [], index: -1, done: false };

    // Escape sequences (colours, cursor moves) that a tool may write even without a terminal.
    var ANSI = /\u001b\[[0-9;?]*[ -\/]*[@-~]/g;

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

    function render() {
        var lines = state.text.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
        if (lines.length && lines[lines.length - 1] === "") { lines.pop(); }
        var html = "";
        for (var i = 0; i < lines.length; i++) {
            html += "<div class=\"md-log__line\">" + (highlight(lines[i], state.search) || "&nbsp;") + "</div>";
        }
        linesEl.innerHTML = html;
        state.matches = Array.prototype.slice.call(linesEl.querySelectorAll("mark"));
        if (state.index >= state.matches.length) { state.index = state.matches.length - 1; }
        updateMatches();
        if (state.follow && !state.search) { paneEl.scrollTop = paneEl.scrollHeight; }
    }

    function updateMatches() {
        if (!state.search) { matchesEl.textContent = ""; return; }
        state.matches.forEach(function (m, i) { m.classList.toggle("is-current", i === state.index); });
        matchesEl.textContent = state.matches.length ? (state.index + 1) + "/" + state.matches.length : "0";
    }

    function jump(step) {
        if (!state.matches.length) { return; }
        state.index = (state.index + step + state.matches.length) % state.matches.length;
        updateMatches();
        state.matches[state.index].scrollIntoView({ block: "center" });
    }

    function setState(text, tone) {
        stateEl.textContent = text;
        stateEl.className = "mat-pill mat-pill--" + tone;
    }

    function poll() {
        fetch(url + "?offset=" + state.offset, { credentials: "same-origin", headers: { "Accept": "application/json" } })
            .then(function (r) {
                if (r.status === 404) { throw new Error("gone"); }
                if (!r.ok) { throw new Error("http " + r.status); }
                return r.json();
            })
            .then(function (data) {
                if (data.text) {
                    state.text += data.text.replace(ANSI, "");
                    render();
                }
                state.offset = data.next;
                if (!data.finished) {
                    window.setTimeout(poll, 800);
                    return;
                }
                state.done = true;
                setState(data.ok ? "Done" : "Failed", data.ok ? "ok" : "error");
                if (wasRunning) {
                    // The lists of the page still show the state from before the operation.
                    window.setTimeout(function () { window.location.reload(); }, 600);
                }
            })
            .catch(function (e) {
                if (e.message === "gone") {
                    setState("Gone", "plain");
                    if (!state.text) { linesEl.textContent = "The output is no longer kept (an hour after the end, or MatDock restarted)."; }
                    return;
                }
                window.setTimeout(poll, 2500);
            });
    }

    searchInput.addEventListener("input", function () {
        state.search = searchInput.value;
        state.index = -1;
        render();
        if (state.matches.length) { jump(1); }
    });
    searchInput.addEventListener("keydown", function (e) {
        if (e.key === "Enter") { e.preventDefault(); jump(e.shiftKey ? -1 : 1); }
    });
    $("[data-console-prev]").addEventListener("click", function () { jump(-1); });
    $("[data-console-next]").addEventListener("click", function () { jump(1); });

    followBtn.addEventListener("click", function () {
        state.follow = !state.follow;
        followBtn.classList.toggle("is-active", state.follow);
        followBtn.setAttribute("aria-pressed", state.follow ? "true" : "false");
        if (state.follow) { paneEl.scrollTop = paneEl.scrollHeight; }
    });
    paneEl.addEventListener("scroll", function () {
        // Scrolling up stops following; back at the bottom it follows again.
        var atBottom = paneEl.scrollHeight - paneEl.scrollTop - paneEl.clientHeight < 24;
        if (state.follow !== atBottom && !state.search) {
            state.follow = atBottom;
            followBtn.classList.toggle("is-active", atBottom);
            followBtn.setAttribute("aria-pressed", atBottom ? "true" : "false");
        }
    });

    wrapBtn.addEventListener("click", function () {
        var on = !paneEl.classList.contains("is-wrap");
        paneEl.classList.toggle("is-wrap", on);
        wrapBtn.classList.toggle("is-active", on);
        wrapBtn.setAttribute("aria-pressed", on ? "true" : "false");
    });

    $("[data-console-copy]").addEventListener("click", function () {
        if (navigator.clipboard) { navigator.clipboard.writeText(state.text); }
    });
    $("[data-console-download]").addEventListener("click", function () {
        var blob = new Blob([state.text], { type: "text/plain;charset=utf-8" });
        var a = document.createElement("a");
        a.href = URL.createObjectURL(blob);
        a.download = (root.getAttribute("data-console-file") || "output") + ".log";
        document.body.appendChild(a);
        a.click();
        window.setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 0);
    });

    poll();
})();
