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
