/* MatDock time editor (_CronEditor): how often, the time, the days and the next runs instead of a cron field. It reads the saved cron line into the
   controls (what it cannot show stays "Cron") and writes the cron line back into the field the form sends. Times are of a zone: a new schedule takes the
   zone of the browser (CRON_TZ=…), a saved one keeps its own (UTC before there were zones). The next runs come from /ui/cron. Vanilla JS. */
(function () {
    "use strict";

    var PREFIX = "CRON_TZ=";
    var browserZone = (window.Intl && Intl.DateTimeFormat().resolvedOptions().timeZone) || "UTC";

    function num(s) { return /^\d+$/.test(s) ? parseInt(s, 10) : null; }

    // A cron line into { zone, mode and the values of the controls }; mode "custom" when it is none of the simple forms.
    function parse(line) {
        var text = (line || "").trim(), zone = null;
        if (text.indexOf(PREFIX) === 0) {
            var sp = text.indexOf(" ");
            zone = sp < 0 ? text.slice(PREFIX.length) : text.slice(PREFIX.length, sp);
            text = sp < 0 ? "" : text.slice(sp + 1).trim();
        }
        var f = text.split(/\s+/), r = { zone: zone, mode: "custom", text: line || "" };
        if (f.length !== 5 || f[3] !== "*") { return r; }
        var min = f[0], hour = f[1], dom = f[2], dow = f[4];
        if (/^\*\/\d+$/.test(min) && hour === "*" && dom === "*" && dow === "*") { r.mode = "minutes"; r.every = num(min.slice(2)); return r; }
        if (num(min) !== null && dom === "*" && dow === "*" && (hour === "*" || /^\*\/\d+$/.test(hour))) {
            r.mode = "hourly"; r.minute = num(min); r.hours = hour === "*" ? 1 : num(hour.slice(2)); return r;
        }
        if (num(min) === null || num(hour) === null) { return r; }
        r.time = ("0" + num(hour)).slice(-2) + ":" + ("0" + num(min)).slice(-2);
        if (dom === "*" && dow === "*") { r.mode = "daily"; return r; }
        if (dom === "*" && /^[0-7](,[0-7])*$/.test(dow)) { r.mode = "weekly"; r.days = dow.split(",").map(function (d) { return parseInt(d, 10) % 7; }); return r; }
        if (dow === "*" && num(dom) !== null && num(dom) <= 28) { r.mode = "monthly"; r.dom = num(dom); return r; }
        return r;
    }

    function init(root) {
        var $ = function (s) { return root.querySelector(s); };
        var field = $("[data-cron-value]"), ui = root.querySelector(".md-cron__ui");
        var out = $("[data-cron-out]"), zoneSel = $("[data-cron-zone]"), custom = $("[data-cron-text]");
        var saved = field.value.trim();
        var state = parse(saved);
        // A saved line without a zone is UTC; a new one takes the browser's zone.
        var isSaved = root.getAttribute("data-cron-saved") === "true";
        var zone = state.zone || (isSaved && saved ? "UTC" : browserZone);
        [browserZone, "UTC", zone].filter(function (z, i, all) { return all.indexOf(z) === i; }).forEach(function (z) {
            var o = document.createElement("option"); o.value = z; o.textContent = z === "UTC" ? "UTC" : z.replace(/_/g, " "); zoneSel.appendChild(o);
        });
        zoneSel.value = zone;
        var mode = saved ? state.mode : "daily";

        // The controls from what was saved.
        if (state.every) { $("[data-cron-every]").value = String(state.every); }
        if (state.hours) { $("[data-cron-hours]").value = String(state.hours); }
        if (state.minute !== undefined && state.minute !== null) { $("[data-cron-minute]").value = String(state.minute - state.minute % 5); }
        if (state.time) { $("[data-cron-time]").value = state.time; }
        if (state.dom) { $("[data-cron-dom]").value = String(state.dom); }
        var days = state.days || [1, 2, 3, 4, 5];
        root.querySelectorAll("[data-cron-day]").forEach(function (b) {
            b.setAttribute("aria-pressed", days.indexOf(parseInt(b.getAttribute("data-cron-day"), 10)) >= 0 ? "true" : "false");
        });
        custom.value = saved || "0 4 * * *";

        // The editor instead of the plain field.
        field.type = "hidden";
        field.classList.remove("mat-input");
        ui.hidden = false;

        function build() {
            var t = ($("[data-cron-time]").value || "02:00").split(":"), h = parseInt(t[0], 10), m = parseInt(t[1], 10);
            var z = zoneSel.value === "UTC" ? "" : PREFIX + zoneSel.value + " ";
            switch (mode) {
                case "minutes": return "*/" + $("[data-cron-every]").value + " * * * *";
                case "hourly": var hs = $("[data-cron-hours]").value; return $("[data-cron-minute]").value + " " + (hs === "1" ? "*" : "*/" + hs) + " * * *";
                case "daily": return z + m + " " + h + " * * *";
                case "weekly":
                    var picked = Array.prototype.filter.call(root.querySelectorAll("[data-cron-day]"), function (b) { return b.getAttribute("aria-pressed") === "true"; })
                        .map(function (b) { return b.getAttribute("data-cron-day"); });
                    return z + m + " " + h + " * * " + (picked.length ? picked.join(",") : "1");
                case "monthly": return z + m + " " + h + " " + $("[data-cron-dom]").value + " * *";
                default: return custom.value.trim();
            }
        }

        var timer = null;
        function update() {
            root.querySelectorAll("[data-cron-mode]").forEach(function (b) { b.setAttribute("aria-pressed", b.getAttribute("data-cron-mode") === mode ? "true" : "false"); });
            root.querySelectorAll("[data-cron-part]").forEach(function (p) { p.hidden = p.getAttribute("data-cron-part").split(" ").indexOf(mode) < 0; });
            field.value = build();
            window.clearTimeout(timer);
            timer = window.setTimeout(preview, 250);
        }

        function preview() {
            var url = root.getAttribute("data-cron-preview") + "?expr=" + encodeURIComponent(field.value);
            fetch(url, { credentials: "same-origin", headers: { "Accept": "application/json" } })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (d) {
                    if (!d) { return; }
                    out.classList.toggle("is-error", !d.valid);
                    out.textContent = d.valid ? d.text + " · next: " + d.next.join(", ") : "Not a valid time: " + (d.error || "check the five fields.");
                })
                .catch(function () { /* the preview is a help, nothing more */ });
        }

        root.addEventListener("click", function (e) {
            var modeBtn = e.target.closest("[data-cron-mode]");
            if (modeBtn) { mode = modeBtn.getAttribute("data-cron-mode"); if (mode === "custom") { custom.value = field.value; } update(); return; }
            var day = e.target.closest("[data-cron-day]");
            if (day) { day.setAttribute("aria-pressed", day.getAttribute("aria-pressed") === "true" ? "false" : "true"); update(); }
        });
        root.addEventListener("input", update);
        root.addEventListener("change", update);
        update();
    }

    document.querySelectorAll("[data-cron-editor]").forEach(init);
})();
