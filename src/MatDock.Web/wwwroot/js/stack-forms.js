/* The forms of stacks and sync jobs (Stacks/Edit, Stacks/SyncJobs/Edit). Everything comes from data-* attributes, never from script source. */
(function () {
    "use strict";

    function field(name) { return document.querySelector('[name="' + name + '"]'); }

    /* A saved repository (select[data-repo-picker]): its option carries the URL, the branch and the credential, which fill the fields. */
    document.querySelectorAll("select[data-repo-picker]").forEach(function (picker) {
        picker.addEventListener("change", function () {
            var opt = picker.selectedOptions[0];
            if (!opt || !opt.value) { return; }
            var url = field("Input.GitRepoUrl");
            var ref = field("Input.GitReference");
            var cred = field("Input.GitCredentialId");
            if (url) { url.value = opt.getAttribute("data-url") || ""; }
            if (ref) { ref.value = opt.getAttribute("data-ref") || ""; }
            if (cred) { cred.value = opt.getAttribute("data-cred") || ""; }
        });
    });

    /* Secrets: "Add secret" adds a row from the template (its index is the next free one), the x drops a new row. */
    var secretAdd = document.querySelector("[data-secret-add]");
    var secretRows = document.querySelector("[data-secret-rows]");
    var secretTemplate = document.querySelector("template[data-secret-template]");
    if (secretAdd && secretRows && secretTemplate) {
        var next = parseInt(secretAdd.getAttribute("data-secret-next"), 10) || 0;
        secretAdd.addEventListener("click", function () {
            var html = secretTemplate.innerHTML.replace(/\[#\]/g, "[" + next + "]");
            next++;
            secretRows.insertAdjacentHTML("beforeend", html);
            var key = secretRows.lastElementChild && secretRows.lastElementChild.querySelector("[data-secret-key]");
            if (key) { key.focus(); }
        });
        secretRows.addEventListener("click", function (e) {
            var drop = e.target.closest("[data-secret-drop]");
            if (drop) { drop.closest("[data-secret-row]").remove(); lintSoon(); }
        });
        secretRows.addEventListener("input", function (e) { if (e.target.matches("[data-secret-key]")) { lintSoon(); } });
    }

    /* The live check: while the compose file or the .env is typed, the rules (Stacks/Edit?handler=Lint) answer what they find; a finding's line
       moves the cursor there, its fix (handler=Fix) edits the text. Advisory only: nothing here blocks a save. */
    var lint = document.querySelector("[data-lint]");
    var compose = field("Input.ComposeYaml");
    var lintTimer = 0;
    var lintSeq = 0;
    function token() { var t = document.querySelector('input[name="__RequestVerificationToken"]'); return t ? t.value : ""; }
    function post(handler, data) {
        var body = new URLSearchParams(data);
        body.append("__RequestVerificationToken", token());
        return fetch(window.location.pathname + "?handler=" + handler, {
            method: "POST", credentials: "same-origin", body: body,
            headers: { "Accept": "application/json", "Content-Type": "application/x-www-form-urlencoded" }
        });
    }
    function secretKeys() {
        return Array.prototype.slice.call(document.querySelectorAll("[data-secret-key]"))
            .filter(function (k) { var row = k.closest("[data-secret-row]"); var rm = row && row.querySelector('input[type=checkbox][name$=".Remove"]'); return !(rm && rm.checked); })
            .map(function (k) { return k.value.trim(); }).filter(Boolean).join(",");
    }
    function lintSoon() { if (!lint) { return; } window.clearTimeout(lintTimer); lintTimer = window.setTimeout(runLint, 600); }
    function gotoLine(n) {
        if (!compose) { return; }
        var lines = compose.value.split("\n");
        var start = 0;
        for (var i = 0; i < n - 1 && i < lines.length; i++) { start += lines[i].length + 1; }
        compose.focus();
        compose.setSelectionRange(start, start + (lines[n - 1] || "").length);
        var lineHeight = parseFloat(window.getComputedStyle(compose).lineHeight) || 18;
        compose.scrollTop = Math.max(0, (n - 4) * lineHeight);
    }
    function render(findings) {
        var list = lint.querySelector("[data-lint-list]");
        var summary = lint.querySelector("[data-lint-summary]");
        list.innerHTML = "";
        var counts = { error: 0, warning: 0, suggestion: 0 };
        findings.forEach(function (f) {
            counts[f.severity] = (counts[f.severity] || 0) + 1;
            var li = document.createElement("li");
            li.className = "md-lint__item md-lint__item--" + f.severity;
            var pill = document.createElement("span");
            pill.className = "mat-pill " + (f.severity === "error" ? "mat-pill--error" : f.severity === "warning" ? "mat-pill--warn" : "mat-pill--plain");
            pill.textContent = f.severity === "error" ? "Error" : f.severity === "warning" ? "Warning" : "Suggestion";
            var line = document.createElement("button");
            line.type = "button";
            line.className = "mat-btn mat-btn--ghost mat-btn--sm md-lint__line";
            line.textContent = "Line " + f.line;
            line.addEventListener("click", function () { gotoLine(f.line); });
            var text = document.createElement("span");
            text.className = "md-lint__text";
            text.textContent = f.message;
            li.appendChild(pill);
            li.appendChild(line);
            li.appendChild(text);
            if (f.fix) {
                var fix = document.createElement("button");
                fix.type = "button";
                fix.className = "mat-btn mat-btn--secondary mat-btn--sm";
                fix.textContent = f.fixLabel || "Fix";
                fix.addEventListener("click", function () {
                    post("Fix", { yaml: compose.value, fix: f.fix }).then(function (r) { return r.ok ? r.json() : null; }).then(function (d) {
                        if (!d || typeof d.yaml !== "string") { return; }
                        compose.value = d.yaml;
                        compose.dispatchEvent(new Event("input", { bubbles: true })); // the highlighter, and a new check
                    });
                });
                li.appendChild(fix);
            }
            list.appendChild(li);
        });
        var parts = [];
        if (counts.error) { parts.push(counts.error + " error" + (counts.error === 1 ? "" : "s")); }
        if (counts.warning) { parts.push(counts.warning + " warning" + (counts.warning === 1 ? "" : "s")); }
        if (counts.suggestion) { parts.push(counts.suggestion + " suggestion" + (counts.suggestion === 1 ? "" : "s")); }
        summary.textContent = parts.length ? parts.join(", ") : "Nothing found.";
    }
    function runLint() {
        if (!lint || !compose || lint.closest("[hidden]")) { return; }
        var seq = ++lintSeq;
        var env = field("Input.EnvContent");
        var id = field("Input.Id");
        var envId = field("Input.EnvironmentId");
        var src = document.getElementById("Source");
        post("Lint", {
            yaml: compose.value, env: env && !env.disabled ? env.value : "", id: id ? id.value : "", environmentId: envId ? envId.value : "",
            secretKeys: secretKeys(), source: src ? src.value : "inline"
        }).then(function (r) { return r.ok ? r.json() : null; }).then(function (d) {
            if (d && seq === lintSeq) { render(d.findings || []); }
        }).catch(function () { /* the next keystroke tries again */ });
    }
    if (lint && compose) {
        compose.addEventListener("input", lintSoon);
        var envField = field("Input.EnvContent");
        if (envField) { envField.addEventListener("input", lintSoon); }
        var envSelect = field("Input.EnvironmentId");
        if (envSelect) { envSelect.addEventListener("change", lintSoon); }
        var srcSelect = document.getElementById("Source");
        if (srcSelect) { srcSelect.addEventListener("change", lintSoon); }
        runLint();
    }

    /* The icon of a stack: a toolkit icon, the logo of a known app (fetched by the server, Stacks/Edit?handler=Logo) or an uploaded image (256 KB at most)
       goes into the hidden field; the preview shows it, an empty field the compose file's icon. */
    var iconPick = document.querySelector("[data-icon-pick]");
    if (iconPick) {
        var iconValue = iconPick.querySelector("[data-icon-value]");
        var iconPreview = iconPick.querySelector("[data-icon-preview]");
        var iconChoice = iconPick.querySelector("[data-icon-choice]");
        var iconMessage = iconPick.parentElement.querySelector("[data-icon-message]");
        var say = function (text) { if (iconMessage) { iconMessage.textContent = text || ""; } };
        var showIcon = function (icon) {
            iconPreview.textContent = "";
            var value = icon || iconPreview.getAttribute("data-compose-icon") || "";
            if (!value) { return; }
            if (/^(data:|https:\/\/)/i.test(value)) {
                var img = document.createElement("img");
                img.className = "md-app-icon";
                img.alt = "";
                img.src = value;
                iconPreview.appendChild(img);
                return;
            }
            var span = document.createElement("span");
            span.className = "md-app-icon md-app-icon--glyph";
            if (/^[a-z0-9-]+$/.test(value)) {
                var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
                svg.setAttribute("class", "mat-icon");
                svg.setAttribute("aria-hidden", "true");
                var use = document.createElementNS("http://www.w3.org/2000/svg", "use");
                use.setAttribute("href", "#mat-i-" + value);
                svg.appendChild(use);
                span.appendChild(svg);
            } else {
                span.textContent = value;
            }
            iconPreview.appendChild(span);
        };
        var setIcon = function (icon) { iconValue.value = icon || ""; showIcon(icon); };

        iconChoice.addEventListener("change", function () { setIcon(iconChoice.value); say(""); });
        iconPick.querySelector("[data-icon-clear]").addEventListener("click", function () { iconChoice.value = ""; setIcon(""); say(""); });
        iconPick.querySelector("[data-icon-fetch]").addEventListener("click", function () {
            var name = iconPick.querySelector("[data-icon-logo]").value.trim();
            if (!name) { say("Type the name of an app first."); return; }
            say("Fetching …");
            post("Logo", { name: name }).then(function (r) {
                return r.json().then(function (d) { return { ok: r.ok, d: d }; });
            }).then(function (res) {
                if (!res.ok || !res.d.icon) { say(res.d.error || "No logo found."); return; }
                iconChoice.value = "";
                setIcon(res.d.icon);
                say("Logo of " + name + ".");
            }).catch(function () { say("The logo could not be fetched."); });
        });
        iconPick.querySelector("[data-icon-upload]").addEventListener("change", function (e) {
            var file = e.target.files && e.target.files[0];
            if (!file) { return; }
            if (file.size > 256 * 1024) { say("The image is larger than 256 KB."); return; }
            var reader = new FileReader();
            reader.onload = function () { iconChoice.value = ""; setIcon(String(reader.result)); say(file.name); };
            reader.readAsDataURL(file);
        });
    }

    /* The App Store picker of a new stack: the search narrows the apps; a pick fills the name and the compose file
       and switches the source to the editor. */
    var appList = document.getElementById("app-list");
    if (!appList) { return; }
    var picks = Array.prototype.slice.call(appList.querySelectorAll("[data-compose]"));
    var appSearch = document.getElementById("app-search");
    if (appSearch) {
        appSearch.addEventListener("input", function () {
            var q = appSearch.value.trim().toLowerCase();
            picks.forEach(function (b) {
                b.hidden = !(q === "" || (b.getAttribute("data-search") || "").indexOf(q) !== -1);
            });
        });
    }
    picks.forEach(function (b) {
        b.addEventListener("click", function () {
            var nameEl = field("Input.Name");
            var composeEl = field("Input.ComposeYaml");
            if (nameEl && !nameEl.readOnly && !nameEl.value) { nameEl.value = b.getAttribute("data-slug") || ""; }
            if (composeEl) {
                composeEl.value = b.getAttribute("data-compose") || "";
                composeEl.dispatchEvent(new Event("input", { bubbles: true })); // refresh the highlighter
            }
            var src = document.getElementById("Source");
            if (src) { src.value = "inline"; src.dispatchEvent(new Event("change", { bubbles: true })); }
        });
    });
})();
