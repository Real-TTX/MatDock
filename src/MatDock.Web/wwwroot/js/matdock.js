/* MatDock front-end enhancements — vanilla JS, no dependencies. */
(function () {
    "use strict";

    /* ---------------- Toolbar auto-submit ----------------
       Forms marked .js-autosubmit submit on select change and on debounced text input. Their fields may stand
       elsewhere on the page (form="id", e.g. in a .mat-toolbar), so form.elements is used. */
    document.querySelectorAll("form.js-autosubmit").forEach(function (form) {
        var timer;
        var fields = Array.prototype.slice.call(form.elements);
        fields.filter(function (el) { return el.tagName === "SELECT"; }).forEach(function (sel) {
            sel.addEventListener("change", function () { form.submit(); });
        });
        fields.filter(function (el) { return el.tagName === "INPUT" && (el.type === "search" || el.type === "text"); }).forEach(function (input) {
            input.addEventListener("input", function () {
                clearTimeout(timer);
                timer = setTimeout(function () { form.submit(); }, 400);
            });
        });
    });

    /* ---------------- Client-side table filter ----------------
       <input data-filter-table="#vol-table"> hides non-matching rows in that table's tbody. */
    document.querySelectorAll("[data-filter-table]").forEach(function (input) {
        var table = document.querySelector(input.getAttribute("data-filter-table"));
        if (!table) { return; }
        var counter = input.getAttribute("data-filter-count")
            ? document.querySelector(input.getAttribute("data-filter-count"))
            : null;
        // Works for both tables (tbody tr) and card grids (elements marked [data-filter-row]).
        var rows = table.querySelectorAll("[data-filter-row]");
        if (!rows.length) { rows = table.querySelectorAll("tbody tr"); }
        input.addEventListener("input", function () {
            var term = input.value.trim().toLowerCase();
            var shown = 0;
            rows.forEach(function (row) {
                if (row.hasAttribute("data-no-filter")) { return; }
                var match = row.textContent.toLowerCase().indexOf(term) !== -1;
                row.hidden = !match;
                if (match) { shown++; }
            });
            if (counter) { counter.textContent = shown; }
        });
    });

    /* ---------------- Select that navigates to a URL on change (data-nav-select="…{v}…") ------- */
    document.querySelectorAll("[data-nav-select]").forEach(function (sel) {
        sel.addEventListener("change", function () {
            var tpl = sel.getAttribute("data-nav-select");
            window.location.href = tpl.replace("{v}", encodeURIComponent(sel.value));
        });
    });

    /* ---------------- Read-only fields that select their whole text on click ---------------- */
    document.querySelectorAll("[data-select-on-click]").forEach(function (el) {
        el.addEventListener("click", function () { el.select(); });
    });

    /* ---------------- Migrate page: source-volume multi-select + filter ---------------- */
    (function () {
        var picker = document.querySelector("[data-migrate-volumes]");
        if (!picker) { return; }
        var checks = Array.prototype.slice.call(picker.querySelectorAll("[data-vol-check]"));
        var rows = Array.prototype.slice.call(picker.querySelectorAll("[data-vol-row]"));
        var countEl = document.getElementById("mig-count");
        var startBtn = document.getElementById("mig-start");
        var rename = document.getElementById("mig-rename");
        var multiNote = document.getElementById("mig-multi-note");
        var selectAll = picker.querySelector("[data-vol-all]");
        var selectNone = picker.querySelector("[data-vol-none]");
        var filter = picker.querySelector("[data-vol-filter]");
        var shownEl = picker.querySelector("[data-vol-shown]");

        function update() {
            var n = checks.filter(function (c) { return c.checked; }).length;
            if (countEl) { countEl.textContent = n; }
            if (startBtn) { startBtn.disabled = n === 0; }
            if (rename) { rename.hidden = n !== 1; }
            if (multiNote) { multiNote.hidden = n <= 1; }
        }

        function applyFilter() {
            var q = (filter ? filter.value : "").trim().toLowerCase();
            var shown = 0;
            rows.forEach(function (r) {
                var name = (r.getAttribute("data-name") || "").toLowerCase();
                var vis = q === "" || name.indexOf(q) !== -1;
                r.hidden = !vis;
                if (vis) { shown++; }
            });
            if (shownEl) { shownEl.textContent = shown; }
        }

        // "Select all" / "None" act on the currently visible (filtered) rows only.
        function setVisible(value) {
            return function (e) {
                e.preventDefault();
                rows.forEach(function (r) {
                    if (r.hidden) { return; }
                    var c = r.querySelector("[data-vol-check]");
                    if (c) { c.checked = value; }
                });
                update();
            };
        }
        if (selectAll) { selectAll.addEventListener("click", setVisible(true)); }
        if (selectNone) { selectNone.addEventListener("click", setVisible(false)); }
        if (filter) { filter.addEventListener("input", applyFilter); }
        checks.forEach(function (c) { c.addEventListener("change", update); });
        applyFilter();
        update();
    })();

    /* ---------------- Lightweight code editor (YAML / .env) ----------------
       Overlay highlighter: a transparent <textarea> sits over a syntax-coloured <pre>,
       scroll-synced. No dependencies/CDN; activated on <textarea data-code="yaml|env">. */
    (function () {
        function esc(s) {
            return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
        }

        function hlYaml(src) {
            return esc(src).replace(
                /(^[ \t]*#.*$)|("(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')|(\$\{[^}]*\}|\$[A-Za-z_][A-Za-z0-9_]*)|(^[ \t]*(?:- )?)([A-Za-z0-9_.\-]+)(:)(?=\s|$)|(#.*$)/gm,
                function (m, cfull, str, varr, kpre, key, colon, cinl) {
                    if (cfull !== undefined) { return '<span class="mat-code__com">' + cfull + '</span>'; }
                    if (str !== undefined) { return '<span class="mat-code__str">' + str + '</span>'; }
                    if (varr !== undefined) { return '<span class="mat-code__attr">' + varr + '</span>'; }
                    if (key !== undefined) { return kpre + '<span class="mat-code__kw">' + key + '</span>' + colon; }
                    if (cinl !== undefined) { return '<span class="mat-code__com">' + cinl + '</span>'; }
                    return m;
                });
        }

        function hlEnv(src) {
            return esc(src).replace(
                /(^[ \t]*#.*$)|(\$\{[^}]*\}|\$[A-Za-z_][A-Za-z0-9_]*)|(^[ \t]*(?:export[ \t]+)?)([A-Za-z_][A-Za-z0-9_]*)(=)/gm,
                function (m, com, varr, kpre, key, eq) {
                    if (com !== undefined) { return '<span class="mat-code__com">' + com + '</span>'; }
                    if (varr !== undefined) { return '<span class="mat-code__attr">' + varr + '</span>'; }
                    if (key !== undefined) { return kpre + '<span class="mat-code__kw">' + key + '</span>' + eq; }
                    return m;
                });
        }

        document.querySelectorAll("textarea[data-code]").forEach(function (ta) {
            var hl = ta.getAttribute("data-code") === "env" ? hlEnv : hlYaml;

            var wrap = document.createElement("div");
            wrap.className = "md-code";
            var pre = document.createElement("pre");
            pre.className = "md-code__hl";
            pre.setAttribute("aria-hidden", "true");
            var code = document.createElement("code");
            pre.appendChild(code);

            ta.parentNode.insertBefore(wrap, ta);
            wrap.appendChild(pre);
            wrap.appendChild(ta);

            function render() { code.innerHTML = hl(ta.value) + "\n"; }
            function sync() { pre.scrollTop = ta.scrollTop; pre.scrollLeft = ta.scrollLeft; }

            ta.addEventListener("input", function () { render(); sync(); });
            ta.addEventListener("scroll", sync);
            // Tab inserts two spaces instead of moving focus (indentation-friendly).
            ta.addEventListener("keydown", function (e) {
                if (e.key === "Tab" && !e.shiftKey) {
                    e.preventDefault();
                    var s = ta.selectionStart, en = ta.selectionEnd;
                    ta.value = ta.value.slice(0, s) + "  " + ta.value.slice(en);
                    ta.selectionStart = ta.selectionEnd = s + 2;
                    render(); sync();
                }
            });
            render();
        });
    })();

    /* ---------------- Container list filter ----------------
       #container-filter (text) and #container-status narrow the [data-item] rows/tiles; #container-count shows how many
       are left. Applied again after a live refresh replaced the rows. */
    (function () {
        var input = document.getElementById("container-filter");
        var status = document.getElementById("container-status");
        var count = document.getElementById("container-count");
        var total = document.getElementById("container-total");
        if (!input) { return; }
        function apply() {
            var q = input.value.trim().toLowerCase();
            var st = status ? status.value : "";
            var shown = 0, all = 0;
            document.querySelectorAll("[data-item]").forEach(function (el) {
                var show = (!q || (el.dataset.search || "").indexOf(q) !== -1) && (!st || el.dataset.state === st);
                el.hidden = !show;
                all++;
                if (show) { shown++; }
            });
            if (count) { count.textContent = shown; }
            if (total) { total.textContent = all; }
        }
        input.addEventListener("input", apply);
        if (status) { status.addEventListener("change", apply); }
        document.addEventListener("matdock:live", apply);
    })();

    /* ---------------- Live regions ----------------
       <div id="…" data-live="20"> is read again every 20 s (at least 10) while the tab is visible: the page is fetched
       once more and the region's content replaced by the new one. Skipped while something inside has the focus or a
       POST is running, so nothing changes under the user's hand. Fires "matdock:live" on document afterwards. */
    (function () {
        var regions = Array.prototype.slice.call(document.querySelectorAll("[data-live][id]"));
        if (regions.length === 0 || !window.fetch || !window.DOMParser) { return; }
        var seconds = Math.max(10, parseInt(regions[0].getAttribute("data-live"), 10) || 20);
        var busy = false;
        function refresh() {
            if (busy || document.hidden || document.querySelector(".is-loading")) { return; }
            var focused = document.activeElement;
            if (focused && focused !== document.body && regions.some(function (r) { return r.contains(focused); })) { return; }
            busy = true;
            fetch(location.href, { credentials: "same-origin", headers: { "X-Requested-With": "matdock-live" } })
                .then(function (r) { return r.ok && !r.redirected ? r.text() : null; })
                .then(function (html) {
                    if (!html) { return; }
                    var doc = new DOMParser().parseFromString(html, "text/html");
                    regions.forEach(function (region) {
                        var fresh = doc.getElementById(region.id);
                        if (fresh) {
                            region.innerHTML = fresh.innerHTML;
                            if (window.MatUi) { window.MatUi.refresh(region); }
                        }
                    });
                    document.dispatchEvent(new CustomEvent("matdock:live"));
                })
                .catch(function () { /* next round */ })
                .then(function () { busy = false; });
        }
        setInterval(refresh, seconds * 1000);
        document.addEventListener("visibilitychange", function () { if (!document.hidden) { refresh(); } });
    })();

    /* ---------------- Network map: hovering a network or a container lights its lines ---------------- */
    document.querySelectorAll(".md-netmap__svg").forEach(function (svg) {
        function light(node, on) {
            var net = node.getAttribute("data-net"), con = node.getAttribute("data-con");
            svg.querySelectorAll(".md-netmap__edge").forEach(function (edge) {
                var hit = (net && edge.getAttribute("data-net") === net) || (con && edge.getAttribute("data-con") === con);
                edge.classList.toggle("is-lit", on && !!hit);
            });
        }
        svg.querySelectorAll(".md-netmap__node").forEach(function (node) {
            node.addEventListener("mouseenter", function () { light(node, true); });
            node.addEventListener("mouseleave", function () { light(node, false); });
            node.addEventListener("focusin", function () { light(node, true); });
            node.addEventListener("focusout", function () { light(node, false); });
        });
    });

    /* ---------------- A POST that takes a while (prune, migrate, backup, deploy) ----------------
       The button that sent the form turns into a spinner (MatUi .is-loading). Registered after the toolkit's confirm, so a
       declined question (defaultPrevented) shows nothing. Visual only: no disabled, which would drop the button's name/value. */
    document.addEventListener("submit", function (e) {
        if (e.defaultPrevented) { return; }
        var form = e.target;
        if (!(form instanceof HTMLFormElement)) { return; }
        if ((form.method || "get").toLowerCase() !== "post") { return; }
        if (form.classList.contains("js-autosubmit")) { return; }
        var btn = e.submitter || form.querySelector('button[type="submit"], button:not([type])');
        if (btn) { btn.classList.add("is-loading"); }
    });

    // A back/forward cache restore can show the page mid-request again.
    window.addEventListener("pageshow", function () {
        document.querySelectorAll(".is-loading").forEach(function (b) { b.classList.remove("is-loading"); });
    });
})();

/* File explorer: prompt for a name, write it into the button's form (input marked
   data-prompt-target) and submit. Used for New folder / New file / Rename so the
   toolbar and rows stay uncluttered. */
/* A button with data-prompt asks for a value (data-prompt-value is the default) and sends its form with it in the
   field [data-prompt-target]. The texts come from attributes, never from script source, so a file name is only text. */
document.addEventListener("click", function (e) {
    var btn = e.target.closest ? e.target.closest("button[data-prompt]") : null;
    if (btn) { matdockPromptSubmit(btn, btn.getAttribute("data-prompt"), btn.getAttribute("data-prompt-value")); return; }
    var toggle = e.target.closest ? e.target.closest("[data-toggle-row]") : null;
    if (toggle) { matdockToggleRow(toggle); }
});

/* The environment switch in the menu: a menu item [data-env-set="id"] (0 = all). */
document.addEventListener("click", function (e) {
    var item = e.target.closest ? e.target.closest("[data-env-set]") : null;
    if (item) { matdockSetEnv(item.getAttribute("data-env-set")); }
});

function matdockPromptSubmit(btn, message, current) {
    var form = btn.closest("form");
    if (!form) { return; }
    // The toolkit's dialog (MatUi.prompt) resolves with the text, or null when cancelled.
    window.MatUi.prompt(message, { value: current || "" }).then(function (value) {
        if (value === null || value === undefined) { return; }
        value = String(value).trim();
        if (value.length === 0) { return; }
        var input = form.querySelector("[data-prompt-target]");
        if (input) { input.value = value; }
        if (form.requestSubmit) { form.requestSubmit(); } else { form.submit(); }
    });
}

/* Expandable table rows (Stacks list): toggle the detail row that immediately follows the
   clicked row and turn the chevron. The detail row is a sibling <tr class="md-stack-detail">. */
function matdockToggleRow(btn) {
    var row = btn.closest("tr");
    if (!row) { return; }
    var detail = row.nextElementSibling;
    if (!detail || !detail.classList.contains("md-stack-detail")) { return; }
    var wasHidden = detail.hasAttribute("hidden");
    if (wasHidden) { detail.removeAttribute("hidden"); } else { detail.setAttribute("hidden", ""); }
    btn.setAttribute("aria-expanded", wasHidden ? "true" : "false");
}

/* Global environment selector (sidebar): persist the choice in a cookie and navigate to the current
   top-level section's list. Avoids mangling detail-page URLs (which would drop required id params). */
function matdockSetEnv(value) {
    var v = value === "0" ? "all" : encodeURIComponent(value);
    document.cookie = "matdock.env=" + v + ";path=/;max-age=31536000;samesite=lax";
    var seg = "/" + (location.pathname.split("/")[1] || "");
    var lists = ["/Containers", "/Volumes", "/Stacks", "/Environments"];
    location.href = lists.indexOf(seg) >= 0 ? seg : "/";
}
