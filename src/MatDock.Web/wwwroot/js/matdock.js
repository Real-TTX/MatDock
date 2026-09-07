/* MatDock front-end enhancements — vanilla JS, no dependencies. */
(function () {
    "use strict";

    var root = document.documentElement;

    /* ---------------- Theme ---------------- */
    var media = window.matchMedia("(prefers-color-scheme: dark)");

    function currentPref() {
        return root.getAttribute("data-theme-pref") || localStorage.getItem("matdock-theme") || "system";
    }

    function applyTheme(pref) {
        var dark = pref === "dark" || (pref === "system" && media.matches);
        root.setAttribute("data-theme", dark ? "dark" : "light");
        root.setAttribute("data-theme-pref", pref);
        try { localStorage.setItem("matdock-theme", pref); } catch (e) { }
        document.querySelectorAll("[data-theme-set]").forEach(function (btn) {
            btn.classList.toggle("is-active", btn.getAttribute("data-theme-set") === pref);
        });
        // Keep the PWA / browser chrome colour in sync with the active surface.
        var meta = document.getElementById("meta-theme-color");
        if (meta) { meta.setAttribute("content", dark ? "#15202e" : "#ffffff"); }
    }

    document.querySelectorAll("[data-theme-set]").forEach(function (btn) {
        btn.addEventListener("click", function () { applyTheme(btn.getAttribute("data-theme-set")); });
    });

    media.addEventListener("change", function () {
        if (currentPref() === "system") { applyTheme("system"); }
    });

    applyTheme(currentPref());

    /* ---------------- Dependent fields ----------------
       Markup: <div data-show-when="AuthType=1"> ... </div>
       Shows the block only when control #AuthType has value 1 (comma = OR).      */
    function evaluateDependents() {
        document.querySelectorAll("[data-show-when]").forEach(function (el) {
            var spec = el.getAttribute("data-show-when");
            var eq = spec.indexOf("=");
            if (eq < 0) { return; }
            var id = spec.slice(0, eq);
            var wanted = spec.slice(eq + 1).split(",");
            var control = document.getElementById(id);
            if (!control) { return; }
            // Checkboxes expose a static .value ("true"); use the checked state so toggles can drive reveals.
            var value = control.type === "checkbox" ? (control.checked ? "true" : "false") : control.value;
            el.classList.toggle("is-shown", wanted.indexOf(value) !== -1);
        });
    }

    document.querySelectorAll("[data-show-when]").forEach(function (el) {
        var id = el.getAttribute("data-show-when").split("=")[0];
        var control = document.getElementById(id);
        if (control && !control.dataset.matDependentBound) {
            control.dataset.matDependentBound = "1";
            control.addEventListener("change", evaluateDependents);
            control.addEventListener("input", evaluateDependents);
        }
    });
    evaluateDependents();

    /* ---------------- Toolbar auto-submit ----------------
       Forms marked .js-autosubmit submit on select change and on debounced text input. */
    document.querySelectorAll("form.js-autosubmit").forEach(function (form) {
        var timer;
        form.querySelectorAll("select").forEach(function (sel) {
            sel.addEventListener("change", function () { form.submit(); });
        });
        form.querySelectorAll("input[type=search], input[type=text]").forEach(function (input) {
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
                row.style.display = match ? "" : "none";
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

    /* ---------------- Migrate page: source-volume multi-select + filter ---------------- */
    (function () {
        var picker = document.querySelector("[data-migrate-volumes]");
        if (!picker) { return; }
        var checks = Array.prototype.slice.call(picker.querySelectorAll(".mig-vol"));
        var rows = Array.prototype.slice.call(picker.querySelectorAll(".vol-picker__row"));
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
                    var c = r.querySelector(".mig-vol");
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

    /* ---------------- Client-side tabs ----------------
       <div data-tabs> <button data-tab="x"> … </button> <div data-tab-panel="x"> … </div> </div> */
    document.querySelectorAll("[data-tabs]").forEach(function (group) {
        var tabs = Array.prototype.slice.call(group.querySelectorAll("[data-tab]"));
        var panels = Array.prototype.slice.call(group.querySelectorAll("[data-tab-panel]"));
        tabs.forEach(function (tab) {
            tab.addEventListener("click", function () {
                var name = tab.getAttribute("data-tab");
                tabs.forEach(function (t) { t.classList.toggle("is-active", t === tab); });
                panels.forEach(function (p) { p.hidden = p.getAttribute("data-tab-panel") !== name; });
            });
        });
    });

    /* ---------------- Confirm destructive actions ---------------- */
    document.addEventListener("submit", function (e) {
        var form = e.target;
        if (form.matches("[data-confirm]")) {
            if (!window.confirm(form.getAttribute("data-confirm"))) {
                e.preventDefault();
            }
        }
    });
})();

/* File explorer: prompt for a name, write it into the button's form (input marked
   data-prompt-target) and submit. Used for New folder / New file / Rename so the
   toolbar and rows stay uncluttered. */
function matdockPromptSubmit(btn, message, current) {
    var value = window.prompt(message, current || "");
    if (value === null) { return; }
    value = value.trim();
    if (value.length === 0) { return; }
    var form = btn.closest("form");
    if (!form) { return; }
    var input = form.querySelector("[data-prompt-target]");
    if (input) { input.value = value; }
    if (form.requestSubmit) { form.requestSubmit(); } else { form.submit(); }
}

/* Expandable table rows (Stacks list): toggle the detail row that immediately follows the
   clicked row and rotate the chevron. The detail row is a sibling <tr class="stack-detail">. */
function matdockToggleRow(btn) {
    var row = btn.closest("tr");
    if (!row) { return; }
    var detail = row.nextElementSibling;
    if (!detail || !detail.classList.contains("stack-detail")) { return; }
    var wasHidden = detail.hasAttribute("hidden");
    if (wasHidden) { detail.removeAttribute("hidden"); } else { detail.setAttribute("hidden", ""); }
    btn.classList.toggle("is-open", wasHidden);
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
