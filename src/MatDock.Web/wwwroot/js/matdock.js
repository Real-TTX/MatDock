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
            var value = control.value;
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
        input.addEventListener("input", function () {
            var term = input.value.trim().toLowerCase();
            var shown = 0;
            table.querySelectorAll("tbody tr").forEach(function (row) {
                if (row.hasAttribute("data-no-filter")) { return; }
                var match = row.textContent.toLowerCase().indexOf(term) !== -1;
                row.style.display = match ? "" : "none";
                if (match) { shown++; }
            });
            if (counter) { counter.textContent = shown; }
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
