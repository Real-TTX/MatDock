/* ==========================================================================
   Version 0.2.0 of the toolkit MatUi (what changed in each version: CHANGELOG.md; how to update an app: docs/VERSIONING.md).
   `node tools/bump.js` writes the version here, in matui.boot.js and in MatUiVersion (Directory.Build.props); window.MatUi.version has it.
   MatUi · behaviour
   Dependency-free. Everything is wired by delegated listeners and data-attributes, so server-rendered and dynamically added
   markup behave the same. The public API is window.MatUi (dialog, confirm, alert, prompt, toast, theme, scheme, accent).

   Texts: what the script writes itself is English or German (chosen by <html lang>); an app changes any text with an attribute on <html>
     data-pk-<name>                         one text of the pickers and of the theme button (the names are the keys of PK_TEXT, in lower case)
     data-theme-label="Theme: {0}. Click for {1}."   the label and the tooltip of the theme button ({0} is this theme, {1} the next one)
     data-theme-name-<system|light|dark>    the name of a theme in that label
     data-dlg-ok / data-dlg-cancel / data-dlg-close / data-dlg-confirm / data-dlg-notice / data-dlg-input   the dialogs, the toasts and their X
     data-msg-required / data-msg-type / data-msg-pattern / data-msg-range   the messages of a form (also on a field); data-copied   the tip after Copy

   Data-attributes
     data-theme-toggle                      the 3-state button (system -> light -> dark); data-theme-save-url posts the choice
     data-theme-set="system|light|dark"     a button of a segmented control in the settings that chooses the theme (aria-pressed follows)
     data-scheme-set="paper|contrast|oled|standard"   a button of a segmented control that chooses the colour scheme, data-scheme on <html> (COLOR-09; aria-pressed follows);
                                            data-scheme-save-url (on the button or its group) posts scheme=<name or standard> like the theme. MatUi.scheme.get() / .set(name)
     data-nav-toggle / data-nav-close       open / close the menu drawer (phones)
     data-nav-branch="key"                  the parent of a submenu in the menu (aria-expanded; the choice is remembered per app)
     data-nav-set="id" / data-nav-panel="id"  the menu switcher: an entry of its menu shows the panel of items with the same id; an entry that is a link
                                            (<a href>, the start page of the menu) loads its page instead, and that page shows its own menu;
                                            the menu names the element with the switcher and the panels: data-nav-scope="id". There can be a switcher
                                            under the logo and one at the foot of the menu (both in the scope, both kept in step), or one at the end of the menu
     data-nav-group                         the parent of some menus in the popup of the switcher (aria-expanded, aria-controls = the element with its menus;
                                            the menus carry data-nav-parent="Title of the parent"); choosing a menu opens its group
     data-scrollspy                         an index of #links to sections of the page ("On this page"): the link of the section in view is marked
     data-validation-summary                an alert in a form that lists the fields with an error, each a link to its field (hidden while there are none)
     formnovalidate                         on a submit button: the form is sent without the check (Delete, Save as draft), as in the browser
     data-output="id"                       a range input writes its value into the element with this id (the filled part of the track is drawn for every .mat-range)
     data-slider                            a slider (.mat-slider around a native range input) or, with .mat-slider--range, a range slider (two inputs, from and to); the script draws the
                                            track, the thumbs and the bubbles (FORM-17). Options: data-format="{0} GB" (every value shown), data-bubble="always", data-ticks="10"
                                            (marks; empty: one per step), data-tick-labels="5" (a label under every fifth mark), data-labels="none" (no minimum and maximum at the ends),
                                            data-output="id" on the wrapper (the value; for a range data-range-format="From {0} to {1}"), data-min-gap (range: the least distance between
                                            the thumbs), data-min / data-max / data-step (copied to the inputs). The texts of the names: data-sl-<name> on <html>
     data-counter="id"                      a text field writes "length / maxlength" into the element with this id
     data-indeterminate                     a checkbox that starts in the indeterminate state
     data-password-toggle="id"              the eye button of a password field: shows and hides the password (aria-pressed)
     data-selectable                        a table, a gallery or a list of cards with a data-select-row check per item, maybe a data-select-all check and a [data-bulk-bar] with [data-bulk-count];
                                            the item of a ticked check is marked is-selected: its table row, its .mat-tile, or the nearest element with data-select-item
                                            (the same around a .mat-itemlist: the rows are marked is-selected and select-all works on the rows that are shown)
     data-itemlist                         a list of rows (.mat-itemlist of .mat-item, LIST-15): the script adds sliding actions (a row with .mat-item__options: a swipe, the More
                                            button or the arrow keys; an option sends mat:item-action, data-item-action names it) and, with data-reorder, reordering by a
                                            .mat-item__handle (pointer, or Space + arrow keys; mat:reorder, data-item-key names a row). The texts: data-il-<name> on <html>
     data-item-filter="id"                  a search field that hides the rows of the list with that id which do not contain its text (.mat-itemlist__empty is shown when none is left;
                                            data-item-search on a row replaces the text that is searched; mat:item-filter)
     data-stepper                           a number field with a button for less and one for more (FORM-20): <div class="mat-stepper" data-stepper> around an input type=number
                                            (min, max, step). data-unit="GB" shows a suffix, data-precision="2" the number of decimals shown. The texts: data-stp-<name> on <html>
     data-picker="select|date|daterange|time|timerange|colour|tags|slot"
                                            a field that is filled in a panel (FORM-15); the real input inside is .mat-picker__input. Options of the wrapper:
                                            data-placeholder, data-clearable (a button that clears the value), data-min / data-max / data-week-start / data-flex (dates),
                                            data-step / data-hour12 (times), data-swatches (colours), data-search / data-multiple / data-sep (select; the options are
                                            .mat-picker__option with data-value), data-select-all (select with data-multiple: a first row "Select all" with a box in three
                                            states for the options that are shown). The texts change with data-pk-<name> on <html>, the format of dates with data-locale.
                                            tags is a field of chips and has no panel: data-sep, data-max (number of tags), data-max-length (of one tag),
                                            data-duplicates (allow a tag twice), data-strict (only the options, which are the suggestions, can be added)
                                            slot books an appointment (FORM-18): a month with the days that have a free time, then the times of the chosen day. It posts
                                            2026-10-14T09:30 (with data-duration: 2026-10-14T09:30..09:45). data-slots = the availability as JSON ({"2026-10-14": ["09:00",
                                            "09:15"]} for free times, {"free": [...], "taken": [...]} to show the taken ones too); without it (or with data-lazy) a month that
                                            is shown and not known fires mat:slots-needed (detail: month "2026-10", from, to, picker) and the page answers with
                                            MatUi.picker.setSlots(picker, data, month). data-min / data-max (a date, today, tomorrow, +2d), data-min-notice ("2h", "30m", "1d":
                                            what starts sooner is not offered), data-unavailable="mark|hide" (days and times that are not free: dimmed and struck, or absent),
                                            data-group (morning, afternoon, evening), data-duration (minutes)
                                            A select can create and suggest (FORM-16): data-create-url="/x" (POST name=<text>, the answer { value, name, description? }: a first row "Create <text>"
                                            for a text that is not an option; data-create-label is its text) and data-suggest-url="/y" (GET, the answer [ { value } | { name } ] or { items }:
                                            "Suggestions" above "All" while nothing is chosen). MatUi.picker.set(wrapper, value), .addOption(wrapper, { value, name, description, tags })
                                            and .suggest(wrapper, items) do the same from code.
     data-calendar                         a calendar that is always shown (FORM-18); the real input inside is .mat-calendar__input and posts 2026-10-10, a period
                                            2026-10-10..2026-10-14 or several dates 2026-10-10,2026-10-12. data-mode="single|range|multiple", data-min / data-max (a date,
                                            today, tomorrow, +2d, -1w, +3m, +1y), data-disabled ("2026-10-14,2026-10-20..2026-10-24"), data-disabled-weekdays ("0,6", 0 is Sunday),
                                            data-events (JSON: {"2026-10-14": [{"title": "Lunch", "time": "12:30"}]}) with dots under the days, data-agenda (the events of
                                            the chosen day under the calendar), data-week-numbers, data-week-start (0 Sunday, default 1), data-months (1 or 2), data-max-dates
                                            (multiple), data-clearable. API: MatUi.calendar.init / setEvents / setDisabled / setValue / go
     data-otp                              a code input (FORM-19): .mat-otp around the real input (.mat-otp__input); the script draws one box per character and posts them joined.
                                            data-length (3 to 12, default 6), data-chars (digits | letters | alphanumeric | hex | a class such as [A-Z2-7]), data-case (upper | lower),
                                            data-mask (dots, for a PIN), data-group ("3" a gap after every 3 boxes, "4-4" two groups with a dash), data-submit (submit the form when the code is
                                            complete, or click the button "#id"). The wrapper fires mat:otp-complete (detail.value). The texts: data-otp-<name> on <html>
     data-carousel                         a strip of slides (.mat-carousel__track > .mat-carousel__slide) that scrolls and snaps; the script adds Previous, the dots and Next
                                            (LIST-13). data-loop: Next on the last page goes to the first; data-autoplay="ms": it moves by itself (Pause button; it waits
                                            while the pointer or the focus is inside and never starts for a user who asked for less motion). The texts: data-car-<name> on <html>
     data-schedule                         a schedule (LIST-14): the events (data-events, JSON) one below another in the views Agenda, Day, Week, Month and Year; the script draws it all.
                                            data-view (agenda|day|week|month|year), data-views (the ones offered, "agenda,week,month"), data-date (the day shown), data-week-start (0 to 6, 1 is
                                            Monday), data-week-numbers (calendar weeks, ISO 8601), data-min / data-max (paging stops there), data-agenda ("week", "month" or a number of days),
                                            data-day-start / data-day-end (the hours of the time grid), data-empty-days (the agenda lists days without events), data-heading-level (the day
                                            headings, 3), data-lazy (a loading state until MatUi.schedule.setEvents), data-now (pins the clock: a demo or a test), data-empty (the text of an empty
                                            schedule). Events: mat:schedule-select, mat:schedule-range, mat:schedule-day. The texts: data-sch-<name> on <html>
     data-colour="#rrggbb"                  a colour a user chose (COLOR-10), on .mat-tag (a dot before the text), .mat-dot, .mat-icon (the colour of the icon) or an option of a select
                                            picker (a dot before the name): written into --mat-swatch; #rgb works, anything else is ignored; MatUi.refresh(scope) for markup added later
     data-menu-toggle="id"                  open the context menu #id next to the button; data-menu-place="top|bottom"
     data-dialog-open="id" / data-dialog-close   open / close a <dialog class="mat-dialog">
     data-confirm="Question?"               ask in a dialog before a submit button, a form or a link goes ahead
     data-show-if="#id | name" data-show-value="a,b | * | true"   show a dependent field only when it is needed
     data-copy="text | #id"                 copy to the clipboard
     data-tabs                              a tab group (role=tablist buttons + role=tabpanel panels)
     data-groups="key"                      a table whose groups (<tbody class="mat-group" data-group="name">) open and close (LIST-16): the toggle in the heading row
                                            (data-group-toggle, aria-expanded) opens or closes its group, and the choice is remembered per table key and group; a group with
                                            data-group-fixed stays as the server drew it (the one with a search hit). data-groups-set="open|closed|toggle" on a button does it
                                            for every group of the table that data-groups-target="#id" names (else the nearest .mat-list, else the page). Event: mat:group-toggle
                                            (detail.group, detail.open). API: MatUi.groups.set(group, open) / setAll(scope, open)
     data-dismiss                           close the alert around it
     data-view-switch="list|gallery"        switch between the two views of a list (remembered per data-view-key)
     data-shortcut="/"                      key that clicks the element, or focuses it when it is a text field (not while typing)
     data-rating                            a rating (FORM-22): a fieldset (.mat-rating) of native radio buttons that the script draws as stars (mat-rating--heart, --dot: hearts, dots); the
                                            pointer shows the value it is on, a click chooses it; or, with .mat-rating--readonly, a value shown (data-value="4.5", data-max 3 to 10, data-count
                                            "128" adds "(128)"). data-clearable (a click on the chosen value clears it), data-show-value (the value as text beside the shapes). Event:
                                            mat:rating-change (detail.value). API: MatUi.rating.init / get / set. The texts: data-rt-<name> on <html>
     data-tree                              a tree view (LIST-17): <ul class="mat-tree" data-tree aria-label="..."> of <li class="mat-tree__item"> with a nested <ul class="mat-tree__group">; the script gives it
                                            the roles of the ARIA tree pattern, one tab stop, the keys (arrows, Home, End, * and the first letters), a chevron, and the level of every item.
                                            data-multiple (a box per item; a parent is in between when some of its children are checked), data-expand-all, data-expanded on an item,
                                            data-tree-key="key" (what is open is remembered), data-tree-toggle="row" (a click on a parent's row opens it too), data-lazy on an item whose
                                            children are loaded when it opens (mat:tree-load, answered by MatUi.tree.setChildren), data-tree-id / data-value / data-tree-search / data-tree-fixed (stays as drawn) on an item.
                                            data-tree-filter="id" on a search field hides the items that do not match and keeps their parents open; data-tree-set="open|closed|toggle" with
                                            data-tree-target="#id" on a button does it for every parent. Events: mat:tree-select, mat:tree-toggle, mat:tree-load, mat:tree-filter.
                                            API: MatUi.tree.init / expand / collapse / select / setChildren / setAll / get. The texts: data-tr-<name> on <html>
     data-upload                            a file upload (FORM-21): .mat-upload around the real file input (.mat-upload__input; accept, multiple and required are its own); the script draws a drop
                                            zone, the list of the chosen files (name, size, a thumbnail for an image, a remove button) and what was refused and why. data-max-size="10MB" (KB, MB, GB;
                                            1 KB is 1024 bytes), data-max-files="5" (with multiple), data-paste (images from the clipboard, while the focus is in the control), data-preview="none" (no
                                            thumbnails). Without multiple a new file replaces the old one. Events: mat:upload-change (detail.files, added, removed, source), mat:upload-refused
                                            (detail.refused). API: MatUi.upload.init / files / add / remove / clear / setProgress / setError. The texts: data-up-<name> on <html>
   ========================================================================== */
(function () {
  'use strict';

  var root = document.documentElement;
  var doc = document;

  // ---------------------------------------------------------------------------------------------------- helpers
  function $(sel, ctx) { return (ctx || doc).querySelector(sel); }
  function $$(sel, ctx) { return Array.prototype.slice.call((ctx || doc).querySelectorAll(sel)); }
  function esc(s) { return String(s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  function label(attr, fallback) { return root.getAttribute(attr) || fallback; }
  function icon(name) {
    var sprite = root.getAttribute('data-mat-sprite') || '';
    return '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="' + sprite + '#mat-i-' + name + '"></use></svg>';
  }
  var uid = 0;
  function nextId(prefix) { uid += 1; return prefix + '-' + uid; }

  // ------------------------------------------------------------------------------- scrollbars only while scrolling
  var scrollTimers = new WeakMap();
  doc.addEventListener('scroll', function (e) {
    var el = e.target === doc ? root : e.target;
    if (!el || !el.classList) { return; }
    el.classList.add('is-scrolling');
    clearTimeout(scrollTimers.get(el));
    scrollTimers.set(el, setTimeout(function () { el.classList.remove('is-scrolling'); }, 900));
  }, true);

  // ---------------------------------------------------------------------------------------------------- theme
  // system -> light -> dark -> system. "system" means no data-theme attribute: the CSS follows the OS.
  var THEMES = ['system', 'light', 'dark'];

  // The label and the tooltip of the theme button are in the language of the page ("Theme: Light. Click for Dark." / "Design: Hell. Klicken für Dunkel."): the texts are
  // PK_TEXT (below), <html data-theme-label="..."> is the whole text ({0} this theme, {1} the next one) and data-theme-name-system / -light / -dark the three names.
  function themeName(t) { return root.getAttribute('data-theme-name-' + t) || pkText('theme' + t.charAt(0).toUpperCase() + t.slice(1)); }
  function themeLabel(theme, next) {
    var own = root.getAttribute('data-theme-label');
    if (!own) { return pkText('themeLabel', themeName(theme), themeName(next)); }
    return own.replace('{0}', function () { return themeName(theme); }).replace('{1}', function () { return themeName(next); });
  }

  function currentTheme() {
    var t = root.getAttribute('data-theme');
    return t === 'light' || t === 'dark' ? t : 'system';
  }

  function applyTheme(theme, persist) {
    if (theme === 'light' || theme === 'dark') { root.setAttribute('data-theme', theme); } else { root.removeAttribute('data-theme'); theme = 'system'; }
    if (persist !== false && !root.hasAttribute('data-preview')) { try { localStorage.setItem('mat-theme', theme); } catch (e) { /* ignore */ } }
    $$('[data-theme-toggle]').forEach(function (btn) {
      var next = THEMES[(THEMES.indexOf(theme) + 1) % THEMES.length];
      btn.setAttribute('data-theme-state', theme);
      var text = themeLabel(theme, next);
      btn.setAttribute('aria-label', text);
      btn.setAttribute('title', text);
    });
    $$('[data-theme-set]').forEach(function (btn) { btn.setAttribute('aria-pressed', btn.getAttribute('data-theme-set') === theme ? 'true' : 'false'); });
    root.dispatchEvent(new CustomEvent('mat:theme', { detail: { theme: theme } }));
  }

  function cycleTheme(btn) {
    chooseTheme(btn, THEMES[(THEMES.indexOf(currentTheme()) + 1) % THEMES.length]);
  }

  // The 3-state button cycles; a segmented control in the account settings chooses (data-theme-set="light"). Both save the same way.
  function chooseTheme(btn, theme) {
    if (THEMES.indexOf(theme) < 0) { return; }
    applyTheme(theme);
    var url = btn && btn.getAttribute('data-theme-save-url');
    if (url) {
      var body = new URLSearchParams({ theme: theme });
      var token = $('input[name="__RequestVerificationToken"]');
      if (token) { body.set('__RequestVerificationToken', token.value); }
      fetch(url, { method: 'POST', body: body, credentials: 'same-origin' }).catch(function () { /* the choice stays in this browser */ });
    }
  }

  // A free colour: the on-accent colour (white or dark) is the one with the better contrast, like the toolkit does for the apps.
  function luminance(hex) {
    var v = [1, 3, 5].map(function (i) { var c = parseInt(hex.substr(i, 2), 16) / 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); });
    return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2];
  }
  function customAccent(hex) {
    if (!/^#[0-9a-fA-F]{6}$/.test(hex || "")) { return false; }
    var l = luminance(hex);
    var onWhite = 1.05 / (l + 0.05), onInk = (l + 0.05) / (luminance("#0b1020") + 0.05);
    root.style.setProperty("--mat-custom-accent", hex);
    root.style.setProperty("--mat-custom-on-accent", onWhite >= 4.5 || onWhite >= onInk ? "#ffffff" : "#0b1020");
    root.setAttribute("data-accent", "custom");
    root.dispatchEvent(new CustomEvent("mat:accent", { detail: { accent: "custom", color: hex } }));
    return true;
  }

  function setAccent(name) {
    if (name) { root.setAttribute('data-accent', name); } else { root.removeAttribute('data-accent'); }
    try { if (name) { localStorage.setItem('mat-accent', name); } else { localStorage.removeItem('mat-accent'); } } catch (e) { /* ignore */ }
    root.dispatchEvent(new CustomEvent('mat:accent', { detail: { accent: name || null } }));
  }

  // ---------------------------------------------------------------------------------------------- colour scheme (COLOR-09)
  // <html data-scheme="paper|contrast|oled"> changes the surfaces; absent, "standard" or any other value is the standard look. The choice is kept in the browser like the
  // accent (not by a preview frame) and by the account where the button names data-scheme-save-url; matui.boot.js applies the saved one before the first paint.
  var SCHEMES = ['paper', 'contrast', 'oled'];

  function currentScheme() {
    var s = root.getAttribute('data-scheme');
    return SCHEMES.indexOf(s) >= 0 ? s : 'standard';
  }

  function syncScheme() {
    var scheme = currentScheme();
    $$('[data-scheme-set]').forEach(function (btn) { btn.setAttribute('aria-pressed', btn.getAttribute('data-scheme-set') === scheme ? 'true' : 'false'); });
  }

  function applyScheme(name, persist) {
    if (SCHEMES.indexOf(name) >= 0) { root.setAttribute('data-scheme', name); } else { root.removeAttribute('data-scheme'); name = 'standard'; }
    if (persist !== false && !root.hasAttribute('data-preview')) {
      try { if (name === 'standard') { localStorage.removeItem('mat-scheme'); } else { localStorage.setItem('mat-scheme', name); } } catch (e) { /* ignore */ }
    }
    syncScheme();
    root.dispatchEvent(new CustomEvent('mat:scheme', { detail: { scheme: name } }));
  }

  // A segmented control chooses (data-scheme-set="paper"); the address to save it to (data-scheme-save-url) is on the button or on the group around it. Saved like the theme.
  function chooseScheme(btn, name) {
    if (name !== 'standard' && SCHEMES.indexOf(name) < 0) { return; }
    applyScheme(name);
    var holder = btn && btn.closest('[data-scheme-save-url]');
    var url = holder && holder.getAttribute('data-scheme-save-url');
    if (url) {
      var body = new URLSearchParams({ scheme: name });
      var token = $('input[name="__RequestVerificationToken"]');
      if (token) { body.set('__RequestVerificationToken', token.value); }
      fetch(url, { method: 'POST', body: body, credentials: 'same-origin' }).catch(function () { /* the choice stays in this browser */ });
    }
  }

  // ---------------------------------------------------------------------------------------------- menu drawer (phones)
  function app() { return $('.mat-app'); }
  var drawerOpener = null;

  function setNav(open, opener) {
    var shell = app();
    if (!shell) { return; }
    shell.classList.toggle('is-nav-open', open);
    root.classList.toggle('mat-lock', open);
    $$('[data-nav-toggle]').forEach(function (b) { b.setAttribute('aria-expanded', open ? 'true' : 'false'); });
    var nav = $('.mat-nav', shell);
    if (open) {
      drawerOpener = opener || doc.activeElement;
      var first = nav && $('.mat-nav__scroll:not([hidden]) .mat-nav__item', nav);
      if (first) { first.focus({ preventScroll: true }); }
    } else if (drawerOpener && drawerOpener.focus) {
      drawerOpener.focus({ preventScroll: true });
      drawerOpener = null;
    }
  }

  // ------------------------------------------------------------------------------ menu: submenus and menu sets (SHELL-13, SHELL-15)
  // A parent item is a button (data-nav-branch="key", aria-expanded) that opens its children. The branch with the current page is open
  // (server side: is-open + is-current); what the user opened or closed is remembered per app. A menu set is a panel of items
  // (data-nav-panel="id"); the switcher under the logo (data-nav-set="id" in its menu) shows one panel at a time.
  var BRANCH_KEY = 'mat-nav-branches';

  function branchStore() {
    try { return JSON.parse(localStorage.getItem(BRANCH_KEY) || '{}') || {}; } catch (e) { return {}; }
  }

  function branchId(btn) { return (root.getAttribute('data-app') || '') + ':' + btn.getAttribute('data-nav-branch'); }

  function setBranch(btn, open, remember) {
    var branch = btn.closest('.mat-nav__branch');
    if (!branch) { return; }
    if (!open && branch.contains(doc.activeElement) && doc.activeElement !== btn) { btn.focus({ preventScroll: true }); }
    branch.classList.toggle('is-open', open);
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (remember) {
      var store = branchStore();
      store[branchId(btn)] = open;
      try { localStorage.setItem(BRANCH_KEY, JSON.stringify(store)); } catch (e) { /* ignore */ }
    }
  }

  function restoreBranches() {
    var store = branchStore();
    $$('[data-nav-branch]').forEach(function (btn) {
      var saved = store[branchId(btn)];
      // the branch of the current page stays open whatever was saved
      if (typeof saved === 'boolean' && !btn.closest('.mat-nav__branch').classList.contains('is-current')) { setBranch(btn, saved, false); }
    });
  }

  // The popup of the switcher names the element that holds the switcher(s) and the panels (data-nav-scope="id"), so a menu in a
  // documentation example never touches the menu of the page. There can be two switchers in the scope (under the logo and at the foot).
  // Menus can have a parent in the popup (data-nav-group on its button, data-nav-parent on the menus below it): one level deep, the group
  // that has the chosen menu is open, and a closed parent carries a dot when a menu inside it (not the chosen one) asks for attention.
  function setNavGroup(btn, open) {
    var group = doc.getElementById(btn.getAttribute('aria-controls'));
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (group) {
      if (!open && group.contains(doc.activeElement)) { btn.focus({ preventScroll: true }); }
      group.hidden = !open;
    }
    // the popup changes its height: it is placed again, so that it never covers the switcher it opened from
    if (openMenu && openMenu.menu.contains(btn)) { placeMenu(openMenu.menu, openMenu.btn, openMenu.btn.getAttribute('data-menu-place')); }
  }

  function setNavSet(id, popup) {
    var menu = popup || $('.mat-menu[data-nav-scope]');
    var scope = menu && doc.getElementById(menu.getAttribute('data-nav-scope'));
    if (!scope) { return; }
    var panels = $$('[data-nav-panel]', scope);
    if (!panels.some(function (p) { return p.getAttribute('data-nav-panel') === id; })) { return; }
    panels.forEach(function (p) { p.hidden = p.getAttribute('data-nav-panel') !== id; });
    var chosen = null;
    var buttons = $$('[data-nav-set]', menu);
    buttons.forEach(function (b) {
      var on = b.getAttribute('data-nav-set') === id;
      b.setAttribute('aria-checked', on ? 'true' : 'false');
      if (on) { chosen = b; }
    });
    var others = function (b) { return b !== chosen && b.hasAttribute('data-nav-attention'); };
    $$('.mat-switcher', scope).forEach(function (switcher) {
      if (!chosen) { return; }
      var name = $('.mat-switcher__name', switcher);
      if (name) { name.textContent = chosen.getAttribute('data-nav-title') || ''; }
      var small = $('.mat-switcher__text small', switcher);
      if (small) { small.textContent = chosen.getAttribute('data-nav-parent') || small.getAttribute('data-nav-label') || small.textContent; }
      var use = $('.mat-switcher__icon use', switcher);
      var from = $('use', chosen);
      if (use && from) { use.setAttribute('href', from.getAttribute('href')); }
      var dot = $('.mat-switcher__dot', switcher);
      if (dot) { dot.hidden = !buttons.some(others); }
    });
    $$('[data-nav-group]', menu).forEach(function (btn) {
      var group = doc.getElementById(btn.getAttribute('aria-controls'));
      var inside = group ? $$('[data-nav-set]', group) : [];
      if (chosen && inside.indexOf(chosen) >= 0) { setNavGroup(btn, true); }
      var dot = $('.mat-nav__badge--sum', btn);
      if (dot) { dot.hidden = !inside.some(others); }
    });
    var shown = $('[data-nav-panel]:not([hidden])', scope);
    if (shown) { shown.scrollTop = 0; }
    root.dispatchEvent(new CustomEvent('mat:navset', { detail: { set: id } }));
  }

  // ------------------------------------------------------------------------------------------------ lists of rows (LIST-15, matui.lists.css)
  // A list of rows is <ul class="mat-itemlist" data-itemlist> of <li class="mat-item">. Without the script it is a list: rows that are links, buttons or labels, lines between them, and the
  // actions of a row inline at its end. The script adds what the markup asks for, all of it optional:
  //   sliding actions   a row (.mat-item--sliding) with .mat-item__options--start / --end of .mat-item__option buttons slides sideways to show them: by a swipe or a drag with the pointer,
  //                     by the More button that the script adds to the row, or by ArrowLeft / ArrowRight on the row. The options are inert while they are hidden, so the Tab key never
  //                     enters them; an option sends mat:item-action { item, action, list } (data-item-action names it) and closes the row; Escape closes it too.
  //   reordering        data-reorder on the list and a .mat-item__handle in each row: drag the handle, or focus it, press Space to pick the row up, the arrow keys (Home, End) to move it,
  //                     Space or Enter to drop it and Escape to put it back. A live region says what happens. mat:reorder { list, item, from, to, order } (data-item-key names a row)
  //   filter            a search field with data-item-filter="id of the list" hides the rows that do not contain its text (and the dividers with no row left), shows .mat-itemlist__empty
  //                     when none is left and sends mat:item-filter { list, text, shown, total }
  //   selection         data-selectable (the one of tables) around the list, a data-select-row check in each row and a data-select-all in the header; the rows are marked is-selected
  // The texts are English and German (IL_TEXT: both must have every key); data-il-<name> on <html> changes one. Sliding and reordering respect prefers-reduced-motion (the CSS drops the transitions).
  // (The language keys of IL_TEXT are quoted on purpose: the test of the texts of the pickers takes the first table in the file whose English key is not quoted.)
  var IL_TEXT = {
    'en': { more: 'More actions for {0}', reorder: 'Reorder {0}', pickedUp: '{0} picked up, position {1} of {2}. Arrow keys move it, Space drops it, Escape cancels.', moved: '{0}, position {1} of {2}', dropped: '{0} dropped, position {1} of {2}', cancelled: 'Reordering cancelled, {0} is back at position {1}', empty: 'Nothing matches', shown: '{0} of {1} shown' },
    'de': { more: 'Weitere Aktionen für {0}', reorder: '{0} verschieben', pickedUp: '{0} aufgenommen, Position {1} von {2}. Pfeiltasten verschieben, Leertaste legt ab, Escape bricht ab.', moved: '{0}, Position {1} von {2}', dropped: '{0} abgelegt, Position {1} von {2}', cancelled: 'Verschieben abgebrochen, {0} ist wieder an Position {1}', empty: 'Nichts passt', shown: '{0} von {1} werden gezeigt' }
  };

  function ilText(key, a, b, c) {
    var table = IL_TEXT[(root.lang || 'en').slice(0, 2).toLowerCase()] || IL_TEXT.en;
    var s = root.getAttribute('data-il-' + key.toLowerCase()) || table[key] || IL_TEXT.en[key] || key;
    return s.replace('{0}', function () { return a === undefined ? '' : a; }).replace('{1}', function () { return b === undefined ? '' : b; }).replace('{2}', function () { return c === undefined ? '' : c; });
  }

  // the name of a row for a screen reader: its title without the hidden words ("Unread, ")
  function ilTitle(item) {
    var t = $('.mat-item__title', item) || $('.mat-item__main', item) || item;
    var copy = t.cloneNode(true);
    $$('.mat-sr-only', copy).forEach(function (n) { n.remove(); });
    return copy.textContent.replace(/\s+/g, ' ').trim();
  }
  function ilKey(item) { return item.getAttribute('data-item-key') || item.id || ilTitle(item); }
  function ilSay(list, text) { if (list._matLive) { list._matLive.textContent = text; } }
  // the rows that take part when one is moved: the rows of the same parent (the list or its group) that are shown
  function ilSiblings(item) { return Array.prototype.filter.call(item.parentNode.children, function (c) { return c.classList.contains('mat-item') && !c.hidden; }); }
  function ilPlace(item, to) {
    var others = ilSiblings(item).filter(function (r) { return r !== item; });
    if (to >= others.length) { if (others.length) { others[others.length - 1].parentNode.insertBefore(item, others[others.length - 1].nextSibling); } }
    else { others[to].parentNode.insertBefore(item, others[to]); }
  }
  // rows that have moved in the page slide from where they were to where they are (not with reduced motion)
  function ilFlip(rows, before) {
    if (reducedMotion()) { return; }
    rows.forEach(function (r, i) {
      var d = before[i] - r.getBoundingClientRect().top;
      if (!d) { return; }
      r.style.transition = 'none';
      r.style.transform = 'translateY(' + d + 'px)';
      void r.offsetHeight;
      r.style.transition = 'transform var(--mat-dur) var(--mat-ease)';
      r.style.transform = '';
    });
    window.setTimeout(function () { rows.forEach(function (r) { r.style.transition = ''; }); }, 260);
  }

  // ---- sliding actions
  function ilWidth(item, side) { var o = $('.mat-item__options--' + side, item); return o ? o.offsetWidth : 0; }
  function ilFirstOption(item, side) { return $('.mat-item__options--' + side + ' .mat-item__option', item); }

  // side: "start", "end" or "" (closed). The row slides to the width of that side's options; the options of the other side stay inert.
  function ilSettle(item, side) {
    var w = side ? ilWidth(item, side) : 0;
    item.classList.remove('is-swiping');
    item.classList.toggle('is-open', !!side);
    item.classList.toggle('is-open-start', side === 'start');
    item.classList.toggle('is-open-end', side === 'end');
    item.style.setProperty('--mat-item-x', (side === 'end' ? -w : w) + 'px');
    item._matSide = side;
    $$('.mat-item__options', item).forEach(function (o) { o.toggleAttribute('inert', !(side && o.classList.contains('mat-item__options--' + side))); });
    var more = $('.mat-item__more', item);
    if (more) { more.setAttribute('aria-expanded', side ? 'true' : 'false'); }
  }
  function ilCloseAll(except) {
    $$('.mat-item.is-open').forEach(function (item) { if (item !== except) { ilSettle(item, ''); } });
  }
  // after a keyboard user has closed a row the focus goes back to where the row can be reached (the More button slides out of sight with a row that was open at the start)
  function ilFocusBack(item, was) {
    var target = (was !== 'start' && $('.mat-item__more', item)) || $('.mat-item__main', item);
    if (target && target.focus) { target.focus({ preventScroll: true }); }
  }
  // open a side with the keyboard or the More button: the first option takes the focus
  function ilOpen(item, side) {
    if (!side || !ilFirstOption(item, side)) { return; }
    ilCloseAll(item);
    ilSettle(item, side);
    ilFirstOption(item, side).focus({ preventScroll: true });
  }
  // the More button opens the end side (the start side when the row has no end side) and closes the row again; the start side of a row that has both is opened by the Right arrow
  function ilToggle(item) {
    if (item._matSide) { ilSettle(item, ''); return; }
    ilOpen(item, ilFirstOption(item, 'end') ? 'end' : 'start');
  }

  function ilSlideInit(item) {
    if (item._matSlide) { return; }
    var row = $('.mat-item__row', item);
    var groups = $$('.mat-item__options', item);
    if (!row || groups.length === 0) { return; }
    item._matSlide = true;
    groups.forEach(function (g) { if (!g.id) { g.id = nextId('mat-opts'); } g.setAttribute('inert', ''); });
    var aside = $('.mat-item__aside', row);
    if (!aside) {
      aside = doc.createElement('div');
      aside.className = 'mat-item__aside';
      row.insertBefore(aside, $('.mat-item__options--end', row));
    }
    var more = doc.createElement('button');
    more.type = 'button';
    more.className = 'mat-btn mat-btn--ghost mat-btn--icon mat-item__more';
    more.setAttribute('aria-label', ilText('more', ilTitle(item)));
    more.setAttribute('aria-expanded', 'false');
    more.setAttribute('aria-controls', groups.map(function (g) { return g.id; }).join(' '));
    more.innerHTML = icon('more-horizontal');
    aside.insertBefore(more, aside.firstChild);
  }

  // what a swipe ends with: far enough, or fast enough toward a side that has options
  function ilDecide(g) {
    var x = g.x;
    if (g.endW && (x < -g.endW * 0.4 || (g.vx < -0.4 && x < -12))) { return 'end'; }
    if (g.startW && (x > g.startW * 0.4 || (g.vx > 0.4 && x > 12))) { return 'start'; }
    return '';
  }

  // ---- reordering with the pointer: the row follows the pointer, the others make room; the DOM changes when it is dropped
  var ilGesture = null;
  var ilSuppress = 0;
  var ilPicked = null;

  function ilScrollTop(g) { return g.scroller ? g.scroller.scrollTop : window.pageYOffset; }

  function ilDragUpdate(g) {
    var dy = (g.y - g.y0) + (ilScrollTop(g) - g.s0);
    var h = g.heights[g.index];
    var center = g.tops[g.index] + dy + h / 2;
    var to = 0;
    g.rows.forEach(function (r, j) { if (j !== g.index && g.tops[j] + g.heights[j] / 2 < center) { to += 1; } });
    g.to = to;
    g.item.style.setProperty('--mat-item-dy', dy + 'px');
    g.rows.forEach(function (r, j) {
      if (j === g.index) { return; }
      var shift = (g.index < to && j > g.index && j <= to) ? -h : (g.index > to && j >= to && j < g.index) ? h : 0;
      r.style.setProperty('--mat-item-dy', shift + 'px');
    });
  }

  // near the top or the bottom edge of what scrolls, the list scrolls with the held row
  function ilAutoScroll(g) {
    if (ilGesture !== g) { return; }
    var box = g.scroller ? g.scroller.getBoundingClientRect() : { top: 0, bottom: window.innerHeight };
    var edge = 56, speed = 0;
    if (g.y < box.top + edge) { speed = -Math.min(18, Math.ceil((box.top + edge - g.y) / 3)); }
    else if (g.y > box.bottom - edge) { speed = Math.min(18, Math.ceil((g.y - (box.bottom - edge)) / 3)); }
    if (speed) {
      if (g.scroller) { g.scroller.scrollTop += speed; } else { window.scrollBy(0, speed); }
      ilDragUpdate(g);
    }
    g.raf = window.requestAnimationFrame(function () { ilAutoScroll(g); });
  }

  function ilDragStart(e, handle) {
    var item = handle.closest('.mat-item');
    var list = handle.closest('[data-itemlist][data-reorder]');
    if (!item || !list) { return; }
    var rows = ilSiblings(item);
    var rects = rows.map(function (r) { return r.getBoundingClientRect(); });
    var g = { kind: 'drag', id: e.pointerId, item: item, list: list, rows: rows, index: rows.indexOf(item), y0: e.clientY, y: e.clientY, scroller: scrollContainer(item), raf: 0,
              tops: rects.map(function (r) { return r.top; }), heights: rects.map(function (r) { return r.height; }) };
    g.to = g.index;
    g.s0 = ilScrollTop(g);
    ilGesture = g;
    try { handle.setPointerCapture(e.pointerId); } catch (x) { /* the pointer is gone */ }
    list.classList.add('is-reordering');
    item.classList.add('is-dragging');
    ilDragUpdate(g);
    g.raf = window.requestAnimationFrame(function () { ilAutoScroll(g); });
  }

  function ilReordered(list, item, from, to) {
    item.dispatchEvent(new CustomEvent('mat:reorder', { bubbles: true, detail: { list: list, item: item, from: from, to: to, order: $$('.mat-item', list).map(ilKey) } }));
  }

  function ilDragEnd(g, commit) {
    window.cancelAnimationFrame(g.raf);
    var before = g.rows.map(function (r) { return r.getBoundingClientRect().top; });
    var to = commit ? g.to : g.index;
    g.rows.forEach(function (r) { r.style.removeProperty('--mat-item-dy'); });
    g.list.classList.remove('is-reordering');
    g.item.classList.remove('is-dragging');
    if (to !== g.index) { ilPlace(g.item, to); }
    ilFlip(g.rows, before);
    if (to !== g.index) {
      ilReordered(g.list, g.item, g.index, to);
      ilSay(g.list, ilText('dropped', ilTitle(g.item), to + 1, g.rows.length));
    }
  }

  // ---- reordering with the keyboard: Space picks the row up, the arrow keys move it, Space or Enter drops it, Escape puts it back
  function ilStep(item, to) {
    var rows = ilSiblings(item), from = rows.indexOf(item);
    to = Math.max(0, Math.min(rows.length - 1, to));
    if (to === from) { return from; }
    var before = rows.map(function (r) { return r.getBoundingClientRect().top; });
    var handle = $('.mat-item__handle', item);
    ilPlace(item, to);
    ilFlip(rows, before);
    if (handle) { handle.focus(); }
    return to;
  }

  function ilPick(handle) {
    var item = handle.closest('.mat-item');
    var list = handle.closest('[data-itemlist]');
    var rows = ilSiblings(item);
    ilCloseAll();
    ilPicked = { item: item, handle: handle, list: list, from: rows.indexOf(item) };
    item.classList.add('is-picked');
    handle.setAttribute('aria-pressed', 'true');
    ilSay(list, ilText('pickedUp', ilTitle(item), ilPicked.from + 1, rows.length));
  }

  function ilDrop(cancel) {
    var p = ilPicked;
    if (!p) { return; }
    ilPicked = null;
    p.item.classList.remove('is-picked');
    p.handle.setAttribute('aria-pressed', 'false');
    var rows = ilSiblings(p.item), at = rows.indexOf(p.item);
    if (cancel) {
      if (at !== p.from) { ilStep(p.item, p.from); }
      ilSay(p.list, ilText('cancelled', ilTitle(p.item), p.from + 1));
      return;
    }
    ilSay(p.list, ilText('dropped', ilTitle(p.item), at + 1, rows.length));
    if (at !== p.from) { ilReordered(p.list, p.item, p.from, at); }
  }

  function ilHandleKey(e, handle) {
    if (!handle.closest('[data-itemlist][data-reorder]')) { return false; }
    var mine = ilPicked && ilPicked.handle === handle;
    if (e.key === ' ' || e.key === 'Enter') {
      e.preventDefault();
      if (mine) { ilDrop(false); } else { ilPick(handle); }
      return true;
    }
    if (!mine) { return false; }
    if (e.key === 'Escape') { e.preventDefault(); ilDrop(true); return true; }
    if (e.key === 'Tab') { ilDrop(false); return false; }
    var item = ilPicked.item, rows = ilSiblings(item), at = rows.indexOf(item), to;
    if (e.key === 'ArrowUp') { to = at - 1; } else if (e.key === 'ArrowDown') { to = at + 1; } else if (e.key === 'Home') { to = 0; } else if (e.key === 'End') { to = rows.length - 1; } else { return false; }
    e.preventDefault();
    to = ilStep(item, to);
    ilSay(ilPicked.list, ilText('moved', ilTitle(item), to + 1, rows.length));
    return true;
  }

  // ---- the events of the pointer: a press on a handle holds a row, a press on a sliding row may become a swipe
  doc.addEventListener('pointerdown', function (e) {
    var t = e.target;
    if (!t.closest || (e.pointerType === 'mouse' && e.button !== 0)) { return; }
    ilSuppress = 0;
    var own = t.closest('.mat-item');
    ilCloseAll(own);
    if (ilPicked && !(own && own === ilPicked.item)) { ilDrop(false); }
    var handle = t.closest('.mat-item__handle');
    if (handle && handle.closest('[data-itemlist][data-reorder]')) {
      e.preventDefault();
      ilDragStart(e, handle);
      return;
    }
    var row = t.closest('.mat-item__row');
    var item = row && row.closest('.mat-item--sliding');
    if (item && item._matSlide && !t.closest('input, textarea, select, .mat-item__options, .mat-item__more')) {
      ilGesture = { kind: 'swipe', id: e.pointerId, item: item, row: row, x0: e.clientX, y0: e.clientY, lastX: e.clientX, lastT: e.timeStamp, vx: 0, x: 0, active: false,
                    startW: ilWidth(item, 'start'), endW: ilWidth(item, 'end'),
                    base: item._matSide === 'end' ? -ilWidth(item, 'end') : item._matSide === 'start' ? ilWidth(item, 'start') : 0 };
      ilGesture.x = ilGesture.base;
    }
  });

  doc.addEventListener('pointermove', function (e) {
    var g = ilGesture;
    if (!g || e.pointerId !== g.id) { return; }
    if (g.kind === 'drag') { g.y = e.clientY; ilDragUpdate(g); return; }
    var dx = e.clientX - g.x0, dy = e.clientY - g.y0;
    if (!g.active) {
      if (Math.abs(dx) < 6 && Math.abs(dy) < 6) { return; }
      // a move that is mostly vertical is the page scrolling, not a swipe
      if (Math.abs(dx) < Math.abs(dy) * 1.2) { ilGesture = null; return; }
      g.active = true;
      g.item.classList.add('is-swiping');
      try { g.row.setPointerCapture(e.pointerId); } catch (x) { /* the pointer is gone */ }
    }
    g.x = Math.max(-g.endW, Math.min(g.startW, g.base + dx));
    g.vx = (e.clientX - g.lastX) / Math.max(1, e.timeStamp - g.lastT);
    g.lastX = e.clientX;
    g.lastT = e.timeStamp;
    g.item.style.setProperty('--mat-item-x', g.x + 'px');
  });

  function ilRelease(e) {
    var g = ilGesture;
    if (!g || e.pointerId !== g.id) { return; }
    ilGesture = null;
    if (g.kind === 'drag') { ilDragEnd(g, e.type !== 'pointercancel'); return; }
    if (!g.active) { return; }
    // the click that ends a swipe is not a press on what lies under the pointer
    ilSuppress = Date.now() + 400;
    ilSettle(g.item, e.type === 'pointercancel' ? (g.item._matSide || '') : ilDecide(g));
  }
  doc.addEventListener('pointerup', ilRelease);
  doc.addEventListener('pointercancel', ilRelease);
  // a link or a picture that is dragged natively would take the pointer away from the swipe
  doc.addEventListener('dragstart', function (e) { if (e.target.closest && e.target.closest('.mat-item--sliding, .mat-item__handle')) { e.preventDefault(); } });

  // the click that ends a swipe is dropped; a press on a row that is slid open closes it instead of doing what the row does
  doc.addEventListener('click', function (e) {
    var t = e.target;
    if (!t.closest) { return; }
    if (ilSuppress > Date.now()) { ilSuppress = 0; e.preventDefault(); e.stopPropagation(); return; }
    var open = t.closest('.mat-item.is-open');
    if (open && t.closest('.mat-item__row') && !t.closest('.mat-item__options, .mat-item__more')) { e.preventDefault(); e.stopPropagation(); ilSettle(open, ''); }
  }, true);

  doc.addEventListener('click', function (e) {
    var t = e.target;
    if (!t.closest) { return; }
    var opt = t.closest('.mat-item__option');
    if (opt) {
      // an option that asks first (data-confirm) is done when the question is answered: the handler of data-confirm clicks again
      if (opt.hasAttribute('data-confirm') && !confirmed.has(opt)) { return; }
      var item = opt.closest('.mat-item'), list = item.closest('[data-itemlist]');
      var action = opt.getAttribute('data-item-action') || opt.textContent.trim().toLowerCase();
      var inside = item.contains(doc.activeElement), was = item._matSide;
      ilSettle(item, '');
      if (inside) { ilFocusBack(item, was); }
      item.dispatchEvent(new CustomEvent('mat:item-action', { bubbles: true, detail: { item: item, action: action, list: list } }));
      return;
    }
    var more = t.closest('.mat-item__more');
    if (more) { ilToggle(more.closest('.mat-item')); }
  });

  doc.addEventListener('keydown', function (e) {
    var t = e.target;
    if (!t.closest) { return; }
    if (ilGesture && ilGesture.kind === 'drag' && e.key === 'Escape') { var g = ilGesture; ilGesture = null; ilDragEnd(g, false); return; }
    var handle = t.closest('.mat-item__handle');
    if (handle && ilHandleKey(e, handle)) { return; }
    var item = t.closest('.mat-item--sliding');
    if (!item || !item._matSlide) { return; }
    if (e.key === 'Escape' && item.classList.contains('is-open')) { var was = item._matSide; e.preventDefault(); ilSettle(item, ''); ilFocusBack(item, was); return; }
    // among the options of an open side the arrow keys walk from one to the next
    var option = t.closest('.mat-item__option');
    if (option && (e.key === 'ArrowLeft' || e.key === 'ArrowRight') && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
      var options = $$('.mat-item__option', option.parentNode), to = options.indexOf(option) + (e.key === 'ArrowRight' ? 1 : -1);
      e.preventDefault();
      if (options[to]) { options[to].focus({ preventScroll: true }); }
      return;
    }
    if ((e.key === 'ArrowLeft' || e.key === 'ArrowRight') && t.classList.contains('mat-item__main') && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
      var want = e.key === 'ArrowLeft' ? 'end' : 'start', side = item._matSide || '';
      if (side && side !== want) { e.preventDefault(); ilSettle(item, ''); } else if (!side && ilFirstOption(item, want)) { e.preventDefault(); ilOpen(item, want); }
    }
  });

  // a row that is slid open by the keyboard closes when the focus leaves it
  doc.addEventListener('focusout', function (e) {
    var open = e.target.closest && e.target.closest('.mat-item.is-open');
    if (open && e.relatedTarget && !open.contains(e.relatedTarget)) { ilSettle(open, ''); }
  });

  // ---- filter: a field says which rows are shown
  function ilFold(s) { return String(s).normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/\s+/g, ' ').toLowerCase().trim(); }

  function ilFilter(field) {
    var list = doc.getElementById(field.getAttribute('data-item-filter'));
    if (!list) { return; }
    var text = ilFold(field.value);
    var rows = $$('.mat-item', list);
    var shown = 0;
    rows.forEach(function (r) {
      var main = $('.mat-item__main', r) || r;
      var match = !text || ilFold(r.getAttribute('data-item-search') || main.textContent).indexOf(text) >= 0;
      r.hidden = !match;
      if (match) { shown += 1; }
    });
    // a group (or a flat divider) with no row left is hidden with its rows
    $$('.mat-itemlist__group', list).forEach(function (g) { g.hidden = !$$('.mat-item', g).some(function (r) { return !r.hidden; }); });
    $$('li.mat-itemlist__divider', list).forEach(function (d) {
      var any = false;
      for (var n = d.nextElementSibling; n && !n.classList.contains('mat-itemlist__divider'); n = n.nextElementSibling) { if (n.classList.contains('mat-item') && !n.hidden) { any = true; } }
      d.hidden = !!text && !any;
    });
    var empty = $('.mat-itemlist__empty', list);
    if (empty) {
      if (!empty.textContent.trim()) { empty.textContent = ilText('empty'); }
      empty.hidden = shown > 0;
    }
    ilSay(list, shown > 0 ? ilText('shown', shown, rows.length) : ilText('empty'));
    list.dispatchEvent(new CustomEvent('mat:item-filter', { bubbles: true, detail: { list: list, text: field.value, shown: shown, total: rows.length } }));
  }
  doc.addEventListener('input', function (e) { if (e.target.matches && e.target.matches('[data-item-filter]')) { ilFilter(e.target); } });

  // ---- selection: the checks of the rows are the ones of tables (data-select-row, data-select-all); the select-all check works on the rows that are shown
  function ilHolds(box) { return box.classList.contains('mat-itemlist') || !!$('.mat-itemlist', box); }
  function ilSelection(box) {
    updateSelection(box);
    var checks = $$('input[data-select-row]', box);
    checks.forEach(function (c) { var r = c.closest('.mat-item'); if (r) { r.classList.toggle('is-selected', c.checked); } });
    var shown = checks.filter(function (c) { var r = c.closest('.mat-item'); return !(r && r.hidden); });
    var all = $('input[data-select-all]', box);
    var n = shown.filter(function (c) { return c.checked; }).length;
    if (all) { all.checked = shown.length > 0 && n === shown.length; all.indeterminate = n > 0 && n < shown.length; }
  }
  doc.addEventListener('change', function (e) {
    var t = e.target;
    var box = t.closest && t.closest('[data-selectable]');
    if (!box || !t.matches || !ilHolds(box) || !t.matches('input[data-select-all], input[data-select-row]')) { return; }
    // the handler of tables would count the rows that are hidden by a filter: this one is for lists
    e.stopImmediatePropagation();
    if (t.matches('input[data-select-all]')) {
      $$('input[data-select-row]', box).forEach(function (c) { var r = c.closest('.mat-item'); if (!(r && r.hidden)) { c.checked = t.checked; } });
    }
    ilSelection(box);
  });

  function ilInit(list) {
    list.classList.add('is-ready');
    if (!list._matLive) {
      list._matLive = doc.createElement('span');
      list._matLive.className = 'mat-sr-only';
      list._matLive.setAttribute('role', 'status');
      list._matLive.setAttribute('aria-live', 'polite');
      list.insertAdjacentElement('afterend', list._matLive);
    }
    $$('.mat-item--sliding', list).forEach(ilSlideInit);
    if (list.hasAttribute('data-reorder')) {
      $$('.mat-item__handle', list).forEach(function (h) {
        if (!h.getAttribute('type')) { h.setAttribute('type', 'button'); }
        if (!h.getAttribute('aria-label')) { h.setAttribute('aria-label', ilText('reorder', ilTitle(h.closest('.mat-item')))); }
        if (!h.hasAttribute('aria-pressed')) { h.setAttribute('aria-pressed', 'false'); }
      });
    }
  }

  function initItemLists(scope) {
    $$('[data-itemlist]', scope).forEach(ilInit);
    $$('[data-selectable]', scope).forEach(function (box) { if (ilHolds(box)) { ilSelection(box); } });
    $$('[data-item-filter]', scope).forEach(function (f) { if (f.value) { ilFilter(f); } });
  }

  // ------------------------------------------------------------------------------------------ on this page: scrollspy (SHELL-16)
  // An index (data-scrollspy) of links to sections of the page; the link of the section that is at the top of the screen is marked
  // (is-active, aria-current="location"). It works for several indexes at once (the one beside the content and the one in the dialog).
  var spyPending = false;

  function scrollContainer(el) {
    for (var p = el.parentElement; p; p = p.parentElement) {
      var o = getComputedStyle(p).overflowY;
      if ((o === 'auto' || o === 'scroll') && p.scrollHeight > p.clientHeight) { return p; }
    }
    return null;
  }

  function keepInView(box, el) {
    var b = box.getBoundingClientRect(), r = el.getBoundingClientRect();
    if (r.top < b.top) { box.scrollTop -= b.top - r.top + 8; } else if (r.bottom > b.bottom) { box.scrollTop += r.bottom - b.bottom + 8; }
  }

  function updateSpy() {
    spyPending = false;
    $$('[data-scrollspy]').forEach(function (nav) {
      var entries = [];
      $$('a[href^="#"]', nav).forEach(function (a) {
        var target = doc.getElementById(decodeURIComponent(a.getAttribute('href').slice(1)));
        if (target) { entries.push({ a: a, target: target }); }
      });
      if (entries.length === 0) { return; }
      var targets = entries.map(function (e) { return e.target; }).filter(function (t, i, all) { return all.indexOf(t) === i; });
      targets.sort(function (a, b) { return a.compareDocumentPosition(b) & 4 ? -1 : 1; });
      var box = scrollContainer(targets[0]);
      var top = box ? box.getBoundingClientRect().top : 0;
      var line = top + (parseFloat(getComputedStyle(root).getPropertyValue('--mat-topbar-h')) || 56) + 24;
      var current = targets[0];
      targets.forEach(function (t) { if (t.getBoundingClientRect().top <= line) { current = t; } });
      // at the very end of the page the last section is the current one even when it is too short to reach the line
      if (box && box.scrollTop + box.clientHeight >= box.scrollHeight - 2) { current = targets[targets.length - 1]; }
      // The index follows the page only when the section in view changes. Scrolling the index itself is a scroll event too: following on every
      // event would pull the marked link back into view and the index could not be scrolled at all.
      var changed = nav._spyCurrent !== current;
      nav._spyCurrent = current;
      entries.forEach(function (e) {
        var on = e.target === current;
        e.a.classList.toggle('is-active', on);
        if (on) { e.a.setAttribute('aria-current', 'location'); } else { e.a.removeAttribute('aria-current'); }
        if (on && changed) { var aside = e.a.closest('.mat-toc-aside'); if (aside) { keepInView(aside, e.a); } }
      });
    });
  }

  function scheduleSpy() {
    if (!spyPending) { spyPending = true; window.requestAnimationFrame(updateSpy); }
  }

  // Arrow keys inside the menu: up and down walk the visible items, right opens a parent (or goes to its first child), left closes it (or goes to the parent).
  function navKeys(e) {
    var panel = e.target.closest('.mat-nav__scroll');
    if (!panel || e.ctrlKey || e.metaKey || e.altKey) { return false; }
    var keys = ['ArrowDown', 'ArrowUp', 'Home', 'End', 'ArrowRight', 'ArrowLeft'];
    if (keys.indexOf(e.key) < 0) { return false; }
    var items = $$('.mat-nav__item', panel).filter(function (el) { return !el.closest('.mat-nav__branch:not(.is-open)') || !el.classList.contains('mat-nav__item--sub'); });
    var current = e.target.closest('.mat-nav__item');
    var i = items.indexOf(current);
    if (i < 0) { return false; }
    var target = null;
    if (e.key === 'ArrowDown') { target = items[Math.min(items.length - 1, i + 1)]; }
    else if (e.key === 'ArrowUp') { target = items[Math.max(0, i - 1)]; }
    else if (e.key === 'Home') { target = items[0]; }
    else if (e.key === 'End') { target = items[items.length - 1]; }
    else if (e.key === 'ArrowRight' && current.hasAttribute('data-nav-branch')) {
      if (current.getAttribute('aria-expanded') !== 'true') { setBranch(current, true, true); }
      else { target = $('.mat-nav__sub .mat-nav__item', current.closest('.mat-nav__branch')); }
    } else if (e.key === 'ArrowLeft') {
      if (current.hasAttribute('data-nav-branch') && current.getAttribute('aria-expanded') === 'true') { setBranch(current, false, true); }
      else if (current.classList.contains('mat-nav__item--sub')) { target = $('[data-nav-branch]', current.closest('.mat-nav__branch')); }
    } else { return false; }
    e.preventDefault();
    if (target) { target.focus(); }
    return true;
  }

  // ------------------------------------------------------------------------------------------------ context menus
  var openMenu = null;

  function closeMenu() {
    if (!openMenu) { return; }
    var m = openMenu;
    openMenu = null;
    m.menu.hidden = true;
    m.btn.setAttribute('aria-expanded', 'false');
    if (m.restoreFocus) { m.btn.focus({ preventScroll: true }); }
  }

  function placeMenu(menu, btn, place) {
    menu.style.left = '0px'; menu.style.top = '0px'; menu.style.bottom = 'auto'; menu.style.minWidth = '';
    var r = btn.getBoundingClientRect();
    var vw = window.innerWidth, vh = window.innerHeight;
    var w = Math.max(menu.offsetWidth, Math.min(r.width, 320)), h = menu.offsetHeight;
    var below = place === 'bottom' || (place !== 'top' && r.top < h + 16);
    var top = below ? Math.min(r.bottom + 6, vh - h - 8) : Math.max(8, r.top - h - 6);
    var left = Math.min(Math.max(8, r.left), vw - w - 8);
    menu.style.minWidth = Math.max(menu.offsetWidth, Math.min(r.width, 320)) + 'px';
    menu.style.left = left + 'px';
    menu.style.top = Math.max(8, top) + 'px';
    menu.style.transformOrigin = (below ? 'top' : 'bottom') + ' left';
  }

  function toggleMenu(btn) {
    var menu = doc.getElementById(btn.getAttribute('data-menu-toggle'));
    if (!menu) { return; }
    if (openMenu && openMenu.menu === menu) { closeMenu(); return; }
    closeMenu();
    menu.hidden = false;
    placeMenu(menu, btn, btn.getAttribute('data-menu-place'));
    btn.setAttribute('aria-expanded', 'true');
    openMenu = { menu: menu, btn: btn, restoreFocus: false };
    var first = $('.mat-menu__item', menu);
    if (first) { first.focus({ preventScroll: true }); openMenu.restoreFocus = true; }
  }

  // ---------------------------------------------------------------------------------------------------- dialogs
  // The dialogs opened by openDialog, in the order they were opened: the last one is the topmost. A modal dialog is in the top layer of the browser, above everything
  // that is not inside of it (whatever its z-index), so the toasts live in the topmost one while a dialog is open (see toasts).
  var dialogStack = [];

  function dialogEl(ref) { return typeof ref === 'string' ? doc.getElementById(ref) : ref; }

  function syncLock() { root.classList.toggle('mat-lock', dialogStack.length > 0 || !!(app() && app().classList.contains('is-nav-open')) || !!(pickerOpen && pickerOpen.full)); }

  function openDialog(ref, opener) {
    var d = dialogEl(ref);
    if (!d || d.open) { return d; }
    d._matOpener = opener || doc.activeElement;
    if (typeof d.showModal === 'function') { d.showModal(); } else { d.setAttribute('open', ''); }
    dialogStack.push(d);
    syncLock();
    placeToasts();
    var target = $('[autofocus]', d) || $('input:not([type=hidden]):not([disabled]), select, textarea', d);
    if (target) { target.focus({ preventScroll: true }); if (target.select && target.type === 'search') { target.select(); } }
    d.dispatchEvent(new CustomEvent('mat:dialog-open', { bubbles: true }));
    return d;
  }

  function closeDialog(ref, result) {
    var d = dialogEl(ref);
    if (!d || !d.open) { return; }
    if (result !== undefined) { d.returnValue = result; }
    if (typeof d.close === 'function') { d.close(result); } else { d.removeAttribute('open'); d.dispatchEvent(new Event('close')); }
  }

  // every <dialog class="mat-dialog">: restore focus and the scroll lock when it closes, whoever closed it (button, Esc, script)
  doc.addEventListener('close', function (e) {
    var d = e.target;
    if (!d || !d.classList || !d.classList.contains('mat-dialog')) { return; }
    var at = dialogStack.indexOf(d);
    if (at >= 0) { dialogStack.splice(at, 1); }
    syncLock();
    placeToasts();
    var op = d._matOpener;
    d._matOpener = null;
    if (op && op.focus && doc.contains(op)) { op.focus({ preventScroll: true }); }
  }, true);

  // A click on the backdrop closes a dialog that is not a data-entry form; on a phone there is no backdrop.
  doc.addEventListener('click', function (e) {
    var d = e.target;
    if (d && d.classList && d.classList.contains('mat-dialog') && d.open && !d.hasAttribute('data-static')) {
      var r = d.getBoundingClientRect();
      if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) { closeDialog(d, 'cancel'); }
    }
  });

  function buildDialog(opts) {
    var id = nextId('mat-dlg');
    var d = doc.createElement('dialog');
    d.className = 'mat-dialog mat-dialog--sm';
    d.id = id;
    d.setAttribute('role', opts.role || 'dialog');
    d.setAttribute('aria-labelledby', id + '-t');
    d.setAttribute('aria-describedby', id + '-b');
    d.setAttribute('data-static', '');
    d.innerHTML =
      '<form method="dialog" class="mat-dialog__form">' +
      '<div class="mat-dialog__head">' + (opts.icon ? icon(opts.icon) : '') +
      '<h2 class="mat-dialog__title" id="' + id + '-t">' + esc(opts.title || '') + '</h2>' +
      '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-dialog__close" data-dialog-close aria-label="' + esc(label('data-dlg-close', 'Close')) + '">' + icon('x') + '</button></div>' +
      '<div class="mat-dialog__body" id="' + id + '-b">' + opts.body + '</div>' +
      '<div class="mat-dialog__foot"><div class="mat-btn-row mat-btn-row--plain">' + opts.buttons + '</div></div>' +
      '</form>';
    doc.body.appendChild(d);
    return d;
  }

  function show(opts) {
    return new Promise(function (resolve) {
      var d = buildDialog(opts);
      var done = false;
      function finish(value) {
        if (done) { return; }
        done = true;
        resolve(value);
      }
      d.addEventListener('close', function () {
        var v = d.returnValue;
        var out = opts.result ? opts.result(d, v) : v === 'ok';
        finish(out);
        setTimeout(function () { d.remove(); }, 0);
      });
      d.addEventListener('submit', function (ev) { ev.preventDefault(); closeDialog(d, 'ok'); });
      openDialog(d);
    });
  }

  function confirmDialog(message, options) {
    var o = options || {};
    var okText = o.ok || label('data-dlg-ok', 'OK');
    var cancelText = o.cancel || label('data-dlg-cancel', 'Cancel');
    // destructive: Cancel first, the destructive action at the end (positive -> negative); otherwise OK first.
    var buttons = o.danger
      ? '<button type="button" class="mat-btn mat-btn--secondary" data-dialog-close>' + esc(cancelText) + '</button><span class="mat-btn-row__spacer"></span>' +
        '<button type="submit" class="mat-btn mat-btn--danger mat-btn--solid" value="ok">' + esc(okText) + '</button>'
      : '<button type="submit" class="mat-btn mat-btn--primary" value="ok" autofocus>' + esc(okText) + '</button>' +
        '<button type="button" class="mat-btn mat-btn--secondary" data-dialog-close>' + esc(cancelText) + '</button>';
    return show({ title: o.title || label('data-dlg-confirm', 'Please confirm'), icon: o.danger ? 'alert' : 'help-circle', role: 'alertdialog', body: '<p>' + esc(message) + '</p>', buttons: buttons });
  }

  function alertDialog(message, options) {
    var o = options || {};
    return show({
      title: o.title || label('data-dlg-notice', 'Notice'), icon: o.icon || 'info', role: 'alertdialog',
      body: '<p>' + esc(message) + '</p>',
      buttons: '<button type="submit" class="mat-btn mat-btn--primary" value="ok" autofocus>' + esc(o.ok || label('data-dlg-ok', 'OK')) + '</button>',
      result: function () { return undefined; }
    });
  }

  function promptDialog(message, options) {
    var o = options || {};
    var id = nextId('mat-in');
    return show({
      title: o.title || label('data-dlg-input', 'Input'), icon: 'edit',
      body: '<div class="mat-field"><label class="mat-label" for="' + id + '">' + esc(message) + '</label>' +
            '<input class="mat-input" id="' + id + '" name="value" value="' + esc(o.value || '') + '" ' + (o.placeholder ? 'placeholder="' + esc(o.placeholder) + '"' : '') + ' autocomplete="off" autofocus></div>',
      buttons: '<button type="submit" class="mat-btn mat-btn--primary" value="ok">' + esc(o.ok || label('data-dlg-ok', 'OK')) + '</button>' +
               '<button type="button" class="mat-btn mat-btn--secondary" data-dialog-close>' + esc(o.cancel || label('data-dlg-cancel', 'Cancel')) + '</button>',
      result: function (d, v) { var i = $('input[name=value]', d); return v === 'ok' && i ? i.value : null; }
    });
  }

  // data-confirm="..." on a button, a link or a form
  var confirmed = new WeakSet();
  function guardConfirm(e, el, proceed) {
    if (confirmed.has(el)) { return; }
    var msg = el.getAttribute('data-confirm');
    if (!msg) { return; }
    e.preventDefault();
    e.stopPropagation();
    var danger = el.hasAttribute('data-confirm-danger') || el.classList.contains('mat-btn--danger');
    confirmDialog(msg, { danger: danger, ok: el.getAttribute('data-confirm-ok') || undefined, title: el.getAttribute('data-confirm-title') || undefined }).then(function (ok) {
      if (!ok) { return; }
      // proceed() dispatches synchronously, so the mark only has to live that long: the next activation asks again.
      confirmed.add(el);
      try { proceed(); } finally { confirmed.delete(el); }
    });
  }

  // ----------------------------------------------------------------------------------------------------- toasts
  // One host holds them all. It is at the end of <body>, except while a dialog is open: then it is inside the topmost one and goes back when that closes, so that a
  // toast is never hidden behind a dialog and its X can still be pressed. The toasts that are shown move with the host.
  function topDialog() {
    for (var i = dialogStack.length - 1; i >= 0; i--) { if (dialogStack[i].open && doc.contains(dialogStack[i])) { return dialogStack[i]; } }
    return null;
  }
  function toastHost() {
    var host = $('.mat-toasts');
    if (!host) { host = doc.createElement('div'); host.className = 'mat-toasts'; host.setAttribute('role', 'status'); host.setAttribute('aria-live', 'polite'); }
    var home = topDialog() || doc.body;
    if (host.parentNode !== home) { home.appendChild(host); }
    return host;
  }
  function placeToasts() { if ($('.mat-toasts')) { toastHost(); } }

  function toast(message, options) {
    var o = options || {};
    var host = toastHost();
    var t = doc.createElement('div');
    t.className = 'mat-toast' + (o.tone ? ' mat-toast--' + o.tone : '');
    t.innerHTML = icon(o.tone === 'danger' ? 'x-circle' : o.tone === 'success' ? 'check-circle' : o.tone === 'warning' ? 'alert' : 'info') +
      '<div class="mat-alert__body">' + esc(message) + '</div>' +
      '<button type="button" class="mat-alert__close" aria-label="' + esc(label('data-dlg-close', 'Close')) + '">' + icon('x') + '</button>';
    host.appendChild(t);
    function remove() { if (t.parentNode) { t.parentNode.removeChild(t); } }
    $('.mat-alert__close', t).addEventListener('click', remove);
    setTimeout(remove, o.timeout || 6000);
  }

  // ----------------------------------------------------------------------------------------- dependent form fields
  function controllerValue(ctl) {
    if (!ctl) { return ''; }
    if (ctl.type === 'checkbox') { return ctl.checked ? 'true' : 'false'; }
    if (ctl.type === 'radio') { var on = $('input[name="' + ctl.name + '"]:checked'); return on ? on.value : ''; }
    return ctl.value;
  }

  function findController(spec, scope) {
    if (!spec) { return null; }
    if (spec.charAt(0) === '#' || spec.charAt(0) === '.' || spec.charAt(0) === '[') { return $(spec, scope) || $(spec); }
    return $('[name="' + spec + '"]', scope) || $('[name="' + spec + '"]');
  }

  function refreshDependents(scope) {
    $$('[data-show-if]', scope).forEach(function (el) {
      var ctl = findController(el.getAttribute('data-show-if'), el.closest('form') || undefined);
      if (!ctl) { return; }
      var value = controllerValue(ctl);
      var want = el.getAttribute('data-show-value');
      var show;
      if (want === null || want === '*') { show = value !== '' && value !== 'false'; } else { show = want.split(',').map(function (s) { return s.trim(); }).indexOf(value) !== -1; }
      el.hidden = !show;
      // a hidden field posts nothing and does not block the form
      $$('input, select, textarea', el).forEach(function (i) { i.disabled = !show; });
      $$('.mat-picker', el).forEach(syncPicker);
      $$('[data-stepper]', el).forEach(function (w) { if (w._matStepper) { stepperSync(w._matStepper); } });
    });
  }

  // ---------------------------------------------------------------------------------------------------- validation
  // Browser bubbles are off (novalidate): the message goes to the reserved .mat-error line of the field.
  function fieldOf(input) { return input.closest('.mat-field'); }
  function showError(input, msg) {
    var f = fieldOf(input);
    if (!f) { return; }
    f.classList.toggle('is-invalid', !!msg);
    var e = $('.mat-error', f);
    if (e) { e.textContent = msg || ''; }
    input.setAttribute('aria-invalid', msg ? 'true' : 'false');
    if (input.classList.contains('mat-picker__input')) {
      var trigger = $('.mat-picker__trigger', f);
      if (trigger) { trigger.setAttribute('aria-invalid', msg ? 'true' : 'false'); }
      var entry = $('.mat-tagfield__input', f);
      if (entry) { entry.setAttribute('aria-invalid', msg ? 'true' : 'false'); }
    }
  }
  function messageFor(input) {
    var v = input.validity;
    if (v.valueMissing) { return input.getAttribute('data-msg-required') || label('data-msg-required', 'Please fill in this field.'); }
    if (v.typeMismatch) { return input.getAttribute('data-msg-type') || label('data-msg-type', 'Please enter a valid value.'); }
    if (v.patternMismatch) { return input.getAttribute('data-msg-pattern') || input.title || label('data-msg-pattern', 'The format is not valid.'); }
    if (v.tooShort || v.tooLong || v.rangeUnderflow || v.rangeOverflow) { return input.getAttribute('data-msg-range') || input.validationMessage; }
    return input.validationMessage;
  }
  function labelOf(input) {
    var l = input.id ? $('label[for="' + input.id.replace(/"/g, '\\"') + '"]', doc) : null;
    var f = fieldOf(input);
    var text = l ? l.textContent : (f && $('legend, .mat-label', f) ? $('legend, .mat-label', f).textContent : input.name);
    return (text || '').replace(/\*/g, '').trim();
  }
  // The summary at the top of a form (FORM-03): an alert with a link to every field that has an error. Server-rendered markup of the same shape works without the script.
  function summarize(form, bad) {
    var box = $('[data-validation-summary]', form);
    if (!box) { return; }
    var list = $('.mat-alert__list', box);
    if (list) {
      list.textContent = '';
      bad.forEach(function (i) {
        var li = doc.createElement('li');
        var a = doc.createElement('a');
        a.href = '#' + i.id;
        a.textContent = labelOf(i) + ': ' + messageFor(i);
        li.appendChild(a);
        list.appendChild(li);
      });
    }
    box.hidden = bad.length === 0;
  }
  doc.addEventListener('submit', function (e) {
    var form = e.target;
    if (!form.matches || !form.matches('form.mat-form, form[data-validate]') || e.defaultPrevented) { return; }
    // A submit button with formnovalidate (Delete, Save as draft) sends the form as it is, like the browser does: nothing is checked and no message is written.
    if (e.submitter && e.submitter.hasAttribute('formnovalidate')) { return; }
    var bad = $$('input, select, textarea', form).filter(function (i) { return !i.disabled && !i.checkValidity(); });
    $$('input, select, textarea', form).forEach(function (i) { if (!bad.includes(i)) { showError(i, ''); } });
    // a group of radio buttons has one message
    bad = bad.filter(function (i, n) { return i.type !== 'radio' || !i.name || bad.findIndex(function (o) { return o.type === 'radio' && o.name === i.name; }) === n; });
    summarize(form, bad);
    if (bad.length) {
      e.preventDefault();
      bad.forEach(function (i) { showError(i, messageFor(i)); });
      bad[0].focus();
    }
  }, true);
  // (typing in the text field of a tag input says nothing: the value is the real input's, and it changes when a tag is added)
  doc.addEventListener('input', function (e) { var i = e.target; if (i.closest && !i.classList.contains('mat-tagfield__input') && i.closest('.mat-field.is-invalid') && i.checkValidity()) { showError(i, ''); } });

  // ------------------------------------------------------------------------------------------------ form helpers (matui.forms.css)
  // range: the filled part of the track and an <output> (data-output="id"); counter: "12 / 80" (data-counter="id"); indeterminate checks;
  // password: the eye button (data-password-toggle="id"); dropzone: dragging files onto a label that holds a file input.
  function updateRange(input) {
    var min = parseFloat(input.min) || 0, max = isNaN(parseFloat(input.max)) ? 100 : parseFloat(input.max);
    input.style.setProperty('--mat-range', (max === min ? 0 : ((parseFloat(input.value) - min) / (max - min)) * 100) + '%');
    var out = input.getAttribute('data-output') && doc.getElementById(input.getAttribute('data-output'));
    if (out) { out.textContent = input.value; }
  }
  function updateCounter(input) {
    var out = doc.getElementById(input.getAttribute('data-counter'));
    if (out) { out.textContent = input.value.length + (input.maxLength > 0 ? ' / ' + input.maxLength : ''); }
  }
  function updateFiles(input) {
    var zone = input.closest('.mat-dropzone');
    var list = zone && $('.mat-dropzone__files', zone);
    if (list) { list.textContent = Array.prototype.map.call(input.files || [], function (f) { return f.name; }).join(', '); }
  }
  // ---- number stepper (FORM-20, matui.forms.css)
  // <div class="mat-stepper" data-stepper><input class="mat-input" type="number" id="" name="" min="" max="" step="" value=""></div> is the native number field
  // without the script. The script adds a button for less and one for more (mat-stepper--stacked: two small arrows at the right on a mouse), names them for a
  // screen reader, draws data-unit as a suffix and marks the wrapper is-ready, which is what hides the spinners of the browser. A press changes the value by step
  // (Shift: ten steps), holding the button repeats faster and faster, and ArrowUp, ArrowDown, PageUp and PageDown do the same in the field. The value stays inside
  // min and max and on the grid of step (counted from min, as the browser counts), with no float errors: the arithmetic is done in whole numbers. data-precision
  // is the number of decimals that are shown (default: what the step needs). A button that cannot go further is aria-disabled, never disabled, so the focus stays.
  var STEP_TEXT = {
    en: { decrease: 'Decrease', increase: 'Increase', decreaseField: 'Decrease {0}', increaseField: 'Increase {0}' },
    de: { decrease: 'Verringern', increase: 'Erhöhen', decreaseField: '{0} verringern', increaseField: '{0} erhöhen' }
  };

  function stepText(key, a) {
    var table = STEP_TEXT[(root.lang || 'en').slice(0, 2).toLowerCase()] || STEP_TEXT.en;
    var s = root.getAttribute('data-stp-' + key.toLowerCase()) || table[key] || STEP_TEXT.en[key] || key;
    return s.replace('{0}', function () { return a === undefined ? '' : a; });
  }
  function stepNumber(text) { var n = parseFloat(text); return isFinite(n) ? n : null; }
  function stepDecimals(n) {
    var s = String(n), exp = /e([+-]?\d+)$/i.exec(s), dot = /\.(\d+)/.exec(s.replace(/e[+-]?\d+$/i, ''));
    return Math.max(0, (dot ? dot[1].length : 0) - (exp ? +exp[1] : 0));
  }
  function stepMod(a, b) { return ((a % b) + b) % b; }
  function stepAttr(s, name) { return s.wrap.getAttribute(name) || s.input.getAttribute(name) || ''; }

  // What the browser calls the step base: min, or else the value attribute, or else 0. step="any" has no grid: a press adds 1.
  function stepperContext(s) {
    var i = s.input, raw = (i.getAttribute('step') || '').trim().toLowerCase();
    var any = raw === 'any', step = any ? 1 : parseFloat(raw);
    if (!(step > 0)) { step = 1; }
    var min = stepNumber(i.getAttribute('min')), max = stepNumber(i.getAttribute('max'));
    var base = min !== null ? min : (stepNumber(i.getAttribute('value')) || 0);
    var cur = stepNumber(i.value), empty = cur === null;
    if (empty) { cur = 0; }
    var p = Math.min(10, Math.max(stepDecimals(step), stepDecimals(base), stepDecimals(max === null ? 0 : max), stepDecimals(cur)));
    var k = Math.pow(10, p);
    return {
      any: any, empty: empty, p: p,
      S: Math.round(step * k), B: Math.round(base * k), V: Math.round(cur * k),
      lo: min === null ? null : Math.round(min * k), hi: max === null ? null : Math.round(max * k), k: k
    };
  }
  // inside min and max, and on the grid: the largest value of the grid that is not above max, the smallest that is not below min
  function stepperClamp(c, t) {
    if (c.hi !== null && t > c.hi) { t = c.any ? c.hi : c.hi - stepMod(c.hi - c.B, c.S); }
    if (c.lo !== null && t < c.lo) { t = c.any ? c.lo : c.lo + (c.S - stepMod(c.lo - c.B, c.S)) % c.S; }
    return t;
  }
  function stepperFormat(s, c, t) {
    var n = t / c.k, fixed = parseInt(stepAttr(s, 'data-precision'), 10);
    return fixed >= 0 && fixed <= 10 ? n.toFixed(fixed) : String(Number(n.toFixed(c.p)));
  }

  // The text of the value one step further in a direction (dir 1 or -1, times 1 or 10), or null when it cannot go that way. An empty field counts as 0, as the
  // native spinner does. A value that is not on the grid goes to the next value of the grid first; a value outside min and max can only go back in.
  function stepperTarget(s, dir, times) {
    var c = stepperContext(s), t;
    if (c.any) { t = c.V + dir * times * c.S; }
    else {
      var off = stepMod(c.V - c.B, c.S);
      t = (dir > 0 ? c.V + (c.S - off) : c.V - (off || c.S)) + dir * (times - 1) * c.S;
    }
    t = stepperClamp(c, t);
    if (!c.empty && ((dir > 0 && t <= c.V) || (dir < 0 && t >= c.V))) { return null; }
    return stepperFormat(s, c, t);
  }
  // What is typed is put inside min and max and on the nearest value of the grid when the user leaves the field (the browser's own change event comes first).
  function stepperCommit(s) {
    var c = stepperContext(s);
    if (c.empty) { return; }
    var t = c.V;
    if (!c.any) { var off = stepMod(c.V - c.B, c.S); t = off * 2 >= c.S ? c.V + (c.S - off) : c.V - off; }
    var text = stepperFormat(s, c, stepperClamp(c, t));
    if (text !== s.input.value) { s.input.value = text; }
  }

  function stepperSync(s) {
    var i = s.input, off = i.disabled, ro = i.readOnly || i.matches(':disabled');
    [[s.dec, -1], [s.inc, 1]].forEach(function (b) {
      b[0].disabled = off;
      b[0].setAttribute('aria-disabled', ro || stepperTarget(s, b[1], 1) === null ? 'true' : 'false');
    });
  }
  // a screen reader hears the new value when the buttons change it (the focus is not in the field)
  function stepperSay(s) {
    clearTimeout(s.sayTimer);
    s.sayTimer = setTimeout(function () { s.live.textContent = s.input.value + (s.unit ? ' ' + s.unit.textContent : ''); }, 250);
  }
  function stepperSet(s, text) {
    if (s.input.value === text) { return; }
    s.input.value = text;
    s.input.dispatchEvent(new Event('input', { bubbles: true }));
    s.input.dispatchEvent(new Event('change', { bubbles: true }));
    stepperSync(s);
  }
  function stepperMove(s, dir, times, say) {
    var i = s.input;
    if (i.matches(':disabled') || i.readOnly) { return false; }
    var text = stepperTarget(s, dir, times);
    if (text === null) { stepperSync(s); return false; }
    stepperSet(s, text);
    if (say) { stepperSay(s); }
    return true;
  }

  // Holding a button repeats: after a short wait, faster and faster up to about thirty steps a second.
  var stepperHeld = null, stepperPressed = null;
  function stepperStop() {
    if (stepperHeld) { clearTimeout(stepperHeld.timer); stepperHeld = null; }
  }
  // The end of a press: the repeating stops. The click that ends a press follows at once and was done on pointerdown; one that comes later is the keyboard's.
  function stepperRelease() {
    stepperStop();
    var s = stepperPressed;
    stepperPressed = null;
    if (s) { setTimeout(function () { s.pressed = false; }, 50); }
  }
  function stepperHold(s, dir, times) {
    stepperStop();
    if (!stepperMove(s, dir, times, true)) { return; }
    var held = stepperHeld = { timer: 0, count: 0 };
    function tick() {
      if (stepperHeld !== held || !stepperMove(s, dir, times, true)) { stepperStop(); return; }
      held.count += 1;
      held.timer = setTimeout(tick, Math.max(35, Math.round(220 * Math.pow(0.87, held.count))));
    }
    held.timer = setTimeout(tick, 450);
  }

  function stepperButton(kind, label, sign, arrow, controls) {
    var b = doc.createElement('button');
    b.type = 'button';
    b.className = 'mat-stepper__btn mat-stepper__btn--' + kind;
    // the field has the keys (the arrows and Page Up and Down), so the buttons are not one more stop of the Tab key
    b.tabIndex = -1;
    b.setAttribute('aria-label', label);
    b.setAttribute('aria-controls', controls);
    b.innerHTML = '<span class="mat-stepper__sign">' + icon(sign) + '</span><span class="mat-stepper__arrow">' + icon(arrow) + '</span>';
    return b;
  }

  function stepperInit(wrap) {
    if (wrap._matStepper) { stepperSync(wrap._matStepper); return; }
    var input = $('input[type=number]', wrap);
    if (!input || input.parentNode !== wrap) { return; }
    var s = { wrap: wrap, input: input, timer: 0 };
    wrap._matStepper = s;
    if (!input.id) { input.id = nextId('mat-st'); }
    var name = labelOf(input);
    s.dec = stepperButton('dec', name ? stepText('decreaseField', name) : stepText('decrease'), 'minus', 'chevron-down', input.id);
    s.inc = stepperButton('inc', name ? stepText('increaseField', name) : stepText('increase'), 'plus', 'chevron-up', input.id);
    wrap.insertBefore(s.dec, input);
    wrap.appendChild(s.inc);
    var unit = stepAttr(s, 'data-unit');
    if (unit) {
      s.unit = doc.createElement('span');
      s.unit.className = 'mat-stepper__unit';
      s.unit.id = input.id + '-unit';
      s.unit.textContent = unit;
      // space for the unit, in characters, so that the number stays in the middle of what is left
      wrap.style.setProperty('--mat-stepper-unit', (unit.length * 1.15) + 'ch');
      wrap.appendChild(s.unit);
      input.setAttribute('aria-describedby', ((input.getAttribute('aria-describedby') || '') + ' ' + s.unit.id).trim());
    }
    s.live = doc.createElement('span');
    s.live.className = 'mat-sr-only';
    s.live.setAttribute('role', 'status');
    s.live.setAttribute('aria-live', 'polite');
    wrap.appendChild(s.live);
    wrap.classList.add('is-ready');
    stepperSync(s);
  }
  function initSteppers(scope) { $$('[data-stepper]', scope).forEach(stepperInit); }

  function stepperOf(el) {
    var wrap = el && el.parentNode;
    return wrap && wrap._matStepper && (wrap._matStepper.input === el || wrap._matStepper.dec === el || wrap._matStepper.inc === el) ? wrap._matStepper : null;
  }

  doc.addEventListener('pointerdown', function (e) {
    var b = e.target.closest && e.target.closest('.mat-stepper__btn'), s = stepperOf(b);
    if (!s || (e.pointerType === 'mouse' && e.button !== 0)) { return; }
    e.preventDefault();
    s.pressed = true;
    stepperPressed = s;
    if (b.setPointerCapture) { try { b.setPointerCapture(e.pointerId); } catch (x) { /* the pointer is gone */ } }
    stepperHold(s, b === s.inc ? 1 : -1, e.shiftKey ? 10 : 1);
  });
  ['pointerup', 'pointercancel', 'contextmenu'].forEach(function (name) { doc.addEventListener(name, stepperRelease); });
  window.addEventListener('blur', stepperRelease);
  // a press keeps the focus where it was (a field that is being typed in stays the field)
  doc.addEventListener('mousedown', function (e) { if (e.target.closest && e.target.closest('.mat-stepper__btn')) { e.preventDefault(); } });
  // A click that is not a press (Enter or Space on a button that has the focus, or an assistive technology) steps once; the click that ends a press was done on pointerdown.
  doc.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('.mat-stepper__btn'), s = stepperOf(b);
    if (!s || e.detail !== 0 || s.pressed) { return; }
    stepperMove(s, b === s.inc ? 1 : -1, e.shiftKey ? 10 : 1, true);
  });
  doc.addEventListener('keydown', function (e) {
    var s = stepperOf(e.target);
    if (s && e.target !== s.input) { s.pressed = false; return; }
    if (!s || e.ctrlKey || e.metaKey || e.altKey || e.isComposing) { return; }
    var dir = e.key === 'ArrowUp' || e.key === 'PageUp' ? 1 : e.key === 'ArrowDown' || e.key === 'PageDown' ? -1 : 0;
    if (!dir) { return; }
    e.preventDefault();
    stepperMove(s, dir, e.shiftKey || e.key.slice(0, 4) === 'Page' ? 10 : 1, false);
  });
  // typed text is put in order before anybody else hears of the change (capture); every other change of the value only updates the buttons
  doc.addEventListener('change', function (e) { var s = stepperOf(e.target); if (s && e.target === s.input) { stepperCommit(s); stepperSync(s); } }, true);
  doc.addEventListener('input', function (e) { var s = stepperOf(e.target); if (s && e.target === s.input) { stepperSync(s); } });
  doc.addEventListener('reset', function (e) {
    setTimeout(function () { $$('[data-stepper]', e.target).forEach(function (w) { if (w._matStepper) { stepperSync(w._matStepper); } }); }, 0);
  }, true);

  function initForms(scope) {
    $$('input[data-indeterminate]', scope).forEach(function (i) { i.indeterminate = true; });
    $$('input.mat-range', scope).forEach(updateRange);
    initSliders(scope);
    $$('[data-counter]', scope).forEach(updateCounter);
    $$('[data-selectable]', scope).forEach(updateSelection);
    initSteppers(scope);
  }
  doc.addEventListener('input', function (e) {
    var t = e.target;
    if (t.matches && t.matches('input.mat-range')) { updateRange(t); }
    if (t.matches && t.matches('[data-counter]')) { updateCounter(t); }
  });
  doc.addEventListener('change', function (e) {
    var t = e.target;
    if (t.matches && t.matches('.mat-dropzone input[type=file]')) { updateFiles(t); }
  });
  ['dragenter', 'dragover'].forEach(function (name) {
    doc.addEventListener(name, function (e) { var z = e.target.closest && e.target.closest('.mat-dropzone'); if (z) { e.preventDefault(); z.classList.add('is-drag'); } });
  });
  ['dragleave', 'drop'].forEach(function (name) {
    doc.addEventListener(name, function (e) {
      var z = e.target.closest && e.target.closest('.mat-dropzone');
      if (!z) { return; }
      e.preventDefault();
      z.classList.remove('is-drag');
      if (name === 'drop' && e.dataTransfer && e.dataTransfer.files.length) {
        var input = $('input[type=file]', z);
        if (input) { input.files = e.dataTransfer.files; input.dispatchEvent(new Event('change', { bubbles: true })); }
      }
    });
  });

  // ------------------------------------------------------------------------------------------ selectable items: rows, tiles, cards
  // An element with data-selectable holds a table, a gallery or a list of cards with a checkbox per item (data-select-row), maybe one that selects all of them
  // (data-select-all) and a bulk bar ([data-bulk-bar] with [data-bulk-count]) that is shown only while something is selected. The item of a ticked check is marked
  // is-selected: its table row, its .mat-tile, or the nearest element with data-select-item (a card, the row of a list of the app's own).
  function updateSelection(box) {
    var rows = $$('input[data-select-row]', box);
    var all = $('input[data-select-all]', box);
    var selected = rows.filter(function (r) { return r.checked; }).length;
    rows.forEach(function (r) {
      var item = r.closest('tr, .mat-tile, [data-select-item]');
      if (item && box.contains(item)) { item.classList.toggle('is-selected', r.checked); }
    });
    if (all) { all.checked = rows.length > 0 && selected === rows.length; all.indeterminate = selected > 0 && selected < rows.length; }
    var bar = $('[data-bulk-bar]', box);
    if (bar) {
      bar.hidden = selected === 0;
      var count = $('[data-bulk-count]', bar);
      if (count) { count.textContent = String(selected); }
    }
  }
  doc.addEventListener('change', function (e) {
    var t = e.target;
    var box = t.closest && t.closest('[data-selectable]');
    if (!box || !t.matches) { return; }
    if (t.matches('input[data-select-all]')) {
      $$('input[data-select-row]', box).forEach(function (r) { r.checked = t.checked; });
      updateSelection(box);
    } else if (t.matches('input[data-select-row]')) {
      updateSelection(box);
    }
  });
  // A tap on the cell around the check of a row is a tap on the check: the check is 18 px, the cell is the target of 44 px on a touch screen (MOBILE-04).
  doc.addEventListener('click', function (e) {
    var cell = e.target.closest && e.target.closest('.mat-table__select');
    if (!cell || e.target.closest('input, label, a, button') || !cell.closest('[data-selectable]')) { return; }
    var check = $('input[data-select-row], input[data-select-all]', cell);
    if (check && !check.disabled) { check.click(); }
  });
  // The same for a row of a list that is a field (a label and a .mat-item__input in a .mat-item__main): a tap on the row, not only on the line of the field, puts the focus in it.
  doc.addEventListener('click', function (e) {
    var main = e.target.closest && e.target.closest('.mat-item__main');
    if (!main || e.target.closest('input, select, textarea, label, a, button')) { return; }
    var field = $('.mat-item__input', main);
    if (field && !field.disabled && !field.readOnly) { field.focus(); }
  });

  // ------------------------------------------------------------------------------------------------ groups in a table (LIST-16, matui.lists.css)
  // <table data-groups="key"> holds <tbody class="mat-group" data-group="name"> whose first row (.mat-group__head) has the toggle button (data-group-toggle,
  // aria-expanded). A closed group has is-collapsed (the CSS hides its other rows). What the user opened or closed is remembered per table and group; a group with
  // data-group-fixed stays as drawn: the server keeps open the groups that have a search hit or the current item.
  var GROUP_KEY = 'mat-groups';

  function groupStore() {
    try { return JSON.parse(localStorage.getItem(GROUP_KEY) || '{}') || {}; } catch (e) { return {}; }
  }

  function groupId(group) {
    var table = group.closest('[data-groups]');
    return (root.getAttribute('data-app') || '') + ':' + (table ? table.getAttribute('data-groups') : '') + ':' + group.getAttribute('data-group');
  }

  function groupsOf(scope) { return $$('.mat-group[data-group]', scope); }

  function setGroup(group, open, remember) {
    var btn = $('[data-group-toggle]', group);
    var changed = group.classList.contains('is-collapsed') === open;
    group.classList.toggle('is-collapsed', !open);
    if (btn) { btn.setAttribute('aria-expanded', open ? 'true' : 'false'); }
    if (remember) {
      var store = groupStore();
      store[groupId(group)] = open;
      try { localStorage.setItem(GROUP_KEY, JSON.stringify(store)); } catch (e) { /* ignore */ }
    }
    if (changed) { group.dispatchEvent(new CustomEvent('mat:group-toggle', { bubbles: true, detail: { group: group.getAttribute('data-group'), open: open } })); }
  }

  function setAllGroups(scope, open) {
    groupsOf(scope).forEach(function (group) { setGroup(group, open, true); });
  }

  function restoreGroups(scope) {
    var store = groupStore();
    groupsOf(scope).forEach(function (group) {
      var saved = store[groupId(group)];
      if (typeof saved === 'boolean' && !group.hasAttribute('data-group-fixed')) { setGroup(group, saved, false); }
    });
  }

  doc.addEventListener('click', function (e) {
    var t = e.target;
    if (!t.closest) { return; }
    var toggle = t.closest('[data-group-toggle]');
    var group = toggle && toggle.closest('.mat-group');
    if (group) { setGroup(group, group.classList.contains('is-collapsed'), true); return; }
    var all = t.closest('[data-groups-set]');
    if (all) {
      var target = all.getAttribute('data-groups-target');
      var scope = (target && $(target)) || all.closest('.mat-list') || doc;
      var mode = all.getAttribute('data-groups-set');
      var groups = groupsOf(scope);
      // "toggle" opens everything when everything is closed, and closes everything otherwise
      setAllGroups(scope, mode === 'open' ? true : mode === 'closed' ? false : groups.length > 0 && groups.every(function (g) { return g.classList.contains('is-collapsed'); }));
    }
  });

  // ------------------------------------------------------------------------------------------------------- tabs
  function selectTab(tab, focus) {
    var group = tab.closest('[data-tabs]');
    if (!group) { return; }
    $$('[role=tab]', group).forEach(function (t) {
      var on = t === tab;
      t.setAttribute('aria-selected', on ? 'true' : 'false');
      t.tabIndex = on ? 0 : -1;
      var panel = doc.getElementById(t.getAttribute('aria-controls'));
      if (panel) { panel.hidden = !on; }
    });
    if (focus) { tab.focus(); }
  }

  // --------------------------------------------------------------------------------------------- list / gallery view
  function setView(btn) {
    var scope = btn.closest('[data-view-scope]') || doc;
    var view = btn.getAttribute('data-view-switch');
    $$('[data-view-switch]', scope).forEach(function (b) { b.setAttribute('aria-pressed', b === btn ? 'true' : 'false'); });
    $$('[data-view]', scope).forEach(function (v) { v.hidden = v.getAttribute('data-view') !== view; });
    var key = btn.getAttribute('data-view-key');
    if (key) { try { localStorage.setItem('mat-view-' + key, view); } catch (e) { /* ignore */ } }
  }

  // ---------------------------------------------------------------------------------------------------- clipboard
  function copy(text, el) {
    function done() { if (!el) { return; } el.classList.add('is-done'); var old = el.getAttribute('data-tip'); el.setAttribute('data-tip', label('data-copied', 'Copied')); setTimeout(function () { el.classList.remove('is-done'); if (old) { el.setAttribute('data-tip', old); } else { el.removeAttribute('data-tip'); } }, 1400); }
    if (navigator.clipboard && window.isSecureContext) { navigator.clipboard.writeText(text).then(done); return; }
    var ta = doc.createElement('textarea'); ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0'; doc.body.appendChild(ta); ta.select();
    try { doc.execCommand('copy'); done(); } catch (e) { /* ignore */ }
    ta.remove();
  }

  // ----------------------------------------------------------------------------------------- delegated click handling
  doc.addEventListener('click', function (e) {
    var t = e.target;
    if (!t.closest) { return; }

    var themeBtn = t.closest('[data-theme-toggle]');
    if (themeBtn) { cycleTheme(themeBtn); return; }
    var themeSet = t.closest('[data-theme-set]');
    if (themeSet) { chooseTheme(themeSet, themeSet.getAttribute('data-theme-set')); return; }
    var schemeSet = t.closest('[data-scheme-set]');
    if (schemeSet) { chooseScheme(schemeSet, schemeSet.getAttribute('data-scheme-set')); return; }

    var branchBtn = t.closest('[data-nav-branch]');
    if (branchBtn) { setBranch(branchBtn, branchBtn.getAttribute('aria-expanded') !== 'true', true); return; }
    // the parent of some menus in the popup of the switcher only opens or closes its menus: the popup stays
    var groupBtn = t.closest('[data-nav-group]');
    if (groupBtn) { setNavGroup(groupBtn, groupBtn.getAttribute('aria-expanded') !== 'true'); return; }

    var navToggle = t.closest('[data-nav-toggle]');
    if (navToggle) { setNav(!(app() && app().classList.contains('is-nav-open')), navToggle); return; }
    // a link in the drawer closes it; a parent of a submenu only opens its children
    if (t.closest('[data-nav-close]') || t.classList.contains('mat-backdrop') || (t.closest('.mat-nav__item:not([data-nav-branch])') && app() && app().classList.contains('is-nav-open'))) { setNav(false); }

    var menuBtn = t.closest('[data-menu-toggle]');
    if (menuBtn) { e.preventDefault(); toggleMenu(menuBtn); return; }
    if (openMenu && !t.closest('.mat-menu')) { closeMenu(); }
    else if (openMenu && t.closest('.mat-menu__item')) { setTimeout(closeMenu, 0); }

    // an entry with an address (<a href>) is a start page: the browser loads it (a new tab, a modifier click) and the page shows its own menu; only a button swaps the items
    var setBtn = t.closest('[data-nav-set]');
    if (setBtn) { if (!setBtn.hasAttribute('href')) { setNavSet(setBtn.getAttribute('data-nav-set'), setBtn.closest('.mat-menu')); } return; }

    // a link to a section of the page inside a dialog (the index on a phone) closes the dialog and lets the browser jump
    var jump = t.closest('dialog a[href^="#"]');
    if (jump) { closeDialog(jump.closest('dialog'), 'jump'); setTimeout(scheduleSpy, 50); return; }

    var opener = t.closest('[data-dialog-open]');
    if (opener) { e.preventDefault(); openDialog(opener.getAttribute('data-dialog-open'), opener); return; }
    var closer = t.closest('[data-dialog-close]');
    if (closer) { var d = closer.closest('dialog'); if (d) { e.preventDefault(); closeDialog(d, 'cancel'); } return; }

    var cf = t.closest('[data-confirm]');
    if (cf && !cf.matches('form')) {
      guardConfirm(e, cf, function () {
        if (cf.tagName === 'A') { window.location.href = cf.href; }
        else if (cf.form && cf.type === 'submit') { cf.form.requestSubmit(cf); }
        else { cf.click(); }
      });
      if (e.defaultPrevented) { return; }
    }

    var pw = t.closest('[data-password-toggle]');
    if (pw) {
      var field = doc.getElementById(pw.getAttribute('data-password-toggle'));
      if (field) {
        var reveal = field.type === 'password';
        field.type = reveal ? 'text' : 'password';
        pw.setAttribute('aria-pressed', reveal ? 'true' : 'false');
      }
      return;
    }

    // a link of the summary of a form moves the focus to the field instead of only scrolling
    var summaryLink = t.closest('[data-validation-summary] a[href^="#"]');
    if (summaryLink) {
      var target = doc.getElementById(summaryLink.getAttribute('href').slice(1));
      if (target) { e.preventDefault(); target.focus(); }
      return;
    }

    var cp = t.closest('[data-copy]');
    if (cp) {
      var spec = cp.getAttribute('data-copy');
      var src = spec.charAt(0) === '#' ? $(spec) : null;
      copy(src ? (src.value !== undefined && src.tagName !== 'DIV' ? src.value : src.textContent.trim()) : spec, cp);
      return;
    }

    var tab = t.closest('[role=tab]');
    if (tab && tab.closest('[data-tabs]')) { selectTab(tab); return; }

    var dismiss = t.closest('[data-dismiss]');
    if (dismiss) { var al = dismiss.closest('.mat-alert, .mat-toast'); if (al) { al.remove(); } return; }

    var view = t.closest('[data-view-switch]');
    if (view) { setView(view); }
  });

  doc.addEventListener('submit', function (e) {
    var form = e.target;
    // the button that was pressed goes along (its name and value, its formnovalidate), as it does for a button with data-confirm
    if (form.matches && form.matches('form[data-confirm]')) { guardConfirm(e, form, function () { form.requestSubmit(e.submitter || undefined); }); }
  }, true);

  doc.addEventListener('change', function (e) {
    if (e.target.closest && e.target.closest('form, body')) { refreshDependents(e.target.closest('form') || undefined); }
  });

  // ---------------------------------------------------------------------------------------------------- keyboard
  doc.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
      if (openMenu) { openMenu.restoreFocus = true; closeMenu(); return; }
      var shell = app();
      if (shell && shell.classList.contains('is-nav-open')) { setNav(false); }
      return;
    }

    // an entry of a menu that is a link (the start page of a menu in the switcher) is chosen with the Space key as well, like a button
    var focused = doc.activeElement;
    if (openMenu && e.key === ' ' && focused && focused.tagName === 'A' && focused.classList.contains('mat-menu__item') && openMenu.menu.contains(focused)) { e.preventDefault(); focused.click(); return; }

    if (openMenu && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
      // the menus of a closed parent are not walked
      var items = $$('.mat-menu__item', openMenu.menu).filter(function (el) { return !el.closest('[hidden]'); });
      var i = items.indexOf(doc.activeElement);
      e.preventDefault();
      items[(i + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length].focus();
      return;
    }

    // in the popup of the switcher: right opens a parent (or goes to its first menu), left closes it (or goes back to the parent)
    if (openMenu && (e.key === 'ArrowRight' || e.key === 'ArrowLeft')) {
      var cur = doc.activeElement;
      var inGroup = cur && cur.closest ? cur.closest('.mat-menu__sub') : null;
      if (cur && cur.hasAttribute && cur.hasAttribute('data-nav-group')) {
        var isOpen = cur.getAttribute('aria-expanded') === 'true';
        if (e.key === 'ArrowRight') {
          e.preventDefault();
          if (!isOpen) { setNavGroup(cur, true); } else { var firstChild = $('.mat-menu__item', doc.getElementById(cur.getAttribute('aria-controls'))); if (firstChild) { firstChild.focus(); } }
          return;
        }
        if (isOpen) { e.preventDefault(); setNavGroup(cur, false); return; }
      } else if (e.key === 'ArrowLeft' && inGroup) {
        var parent = $('[data-nav-group][aria-controls="' + inGroup.id + '"]', openMenu.menu);
        if (parent) { e.preventDefault(); setNavGroup(parent, false); parent.focus(); return; }
      }
    }

    if (!openMenu && e.target.closest && navKeys(e)) { return; }

    var tab = e.target.closest && e.target.closest('[role=tab]');
    if (tab && (e.key === 'ArrowRight' || e.key === 'ArrowLeft' || e.key === 'Home' || e.key === 'End')) {
      var tabs = $$('[role=tab]', tab.closest('[data-tabs]'));
      var idx = tabs.indexOf(tab);
      var to = e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : (idx + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
      e.preventDefault();
      selectTab(tabs[to], true);
      return;
    }

    // shortcuts such as "/" for the search dialog; never while typing
    var tag = (e.target.tagName || '').toLowerCase();
    if (e.ctrlKey || e.metaKey || e.altKey || tag === 'input' || tag === 'textarea' || tag === 'select' || e.target.isContentEditable) { return; }
    var sc = $$('[data-shortcut]').filter(function (el) { return el.getAttribute('data-shortcut') === e.key; })[0];
    if (sc && !sc.disabled) {
      e.preventDefault();
      if (sc.matches('input, textarea')) { sc.focus(); sc.select(); } else { sc.click(); }
    }
  });

  window.addEventListener('resize', function () { closeMenu(); scheduleSpy(); });
  doc.addEventListener('scroll', function (e) { if (openMenu && !e.target.closest) { closeMenu(); } scheduleSpy(); }, true);
  window.addEventListener('hashchange', scheduleSpy);

  // ------------------------------------------------------------------------------------------------ pickers (FORM-15, matui.pickers.css)
  // A picker is a field that is filled in a panel. The markup is a wrapper (data-picker="select|date|daterange|time|timerange|colour|tags") around the real
  // input (.mat-picker__input), which holds the value that is posted, always in the same format whatever the language of the page:
  //   date 2026-10-10 · daterange 2026-10-10..2026-10-14 (and ~2 for "plus or minus two days") · time 09:30 · timerange 09:00..17:30 · colour #b4235f
  //   select: the data-value of the option; with data-multiple the values joined by data-sep (default ", ") · tags: the tags joined by data-sep (default ", ")
  // The script adds the button that shows the value (the label of the field names it) and the panel. On a phone the panel is full screen with an X.
  var PK_TEXT = {
    en: {
      apply: 'Apply', cancel: 'Cancel', clear: 'Clear', close: 'Close', done: 'Done', today: 'Today', now: 'Now',
      prevMonth: 'Previous month', nextMonth: 'Next month',
      phSelect: 'Choose', phDate: 'Choose a date', phPeriod: 'Choose a period', phTime: 'Choose a time', phTimeRange: 'Choose a time range', phColour: 'Choose a colour',
      search: 'Search', noResults: 'Nothing found', selectedCount: '{0} selected',
      selectAll: 'Select all', selectFound: 'Select all found', allSelected: 'All selected',
      themeLabel: 'Theme: {0}. Click for {1}.', themeSystem: 'System', themeLight: 'Light', themeDark: 'Dark', am: 'AM', pm: 'PM', phTimeEntry: 'hh:mm', phTimeEntryAmPm: 'hh:mm am',
      create: 'Create "{0}"', creating: 'Creating "{0}" …', created: 'Created "{0}"', createFailed: 'It could not be created. Please try again.', createHint: 'Type a name to create it.',
      all: 'All', suggestionCount: '{0} suggestions', suggestionOne: '1 suggestion',
      startDate: 'Choose the start date', endDate: 'Choose the end date', daysCount: '{0} days', oneDay: '1 day',
      exactDates: 'Exact dates', flexible: 'Flexible dates', flexDays: '± {0} days', flexDay: '± 1 day',
      from: 'From', to: 'To', hour: 'Hour', minute: 'Minute', ampm: 'AM or PM', typeTime: 'Type a time', nextDay: 'next day', durHM: '{0} h {1} min', durH: '{0} h', durM: '{0} min',
      colours: 'Colours', hue: 'Hue', area: 'Saturation and brightness', areaValue: 'Saturation {0}%, brightness {1}%', hex: 'Hex colour',
      whiteText: 'White text', darkText: 'Dark text', lvAAA: 'AAA', lvAA: 'AA', lvLarge: 'large text only', lvLow: 'too low',
      phTags: 'Add a tag', removeTag: 'Remove tag {0}', tagRemoved: 'Removed {0}', tagDuplicate: '{0} is already there', tagMax: 'At most {0} tags', tagStrict: 'Choose one of the suggestions', suggestions: 'Suggestions'
    },
    de: {
      apply: 'Übernehmen', cancel: 'Abbrechen', clear: 'Löschen', close: 'Schließen', done: 'Fertig', today: 'Heute', now: 'Jetzt',
      prevMonth: 'Vorheriger Monat', nextMonth: 'Nächster Monat',
      phSelect: 'Auswählen', phDate: 'Datum wählen', phPeriod: 'Zeitraum wählen', phTime: 'Uhrzeit wählen', phTimeRange: 'Zeitbereich wählen', phColour: 'Farbe wählen',
      search: 'Suchen', noResults: 'Nichts gefunden', selectedCount: '{0} gewählt',
      selectAll: 'Alle auswählen', selectFound: 'Alle Treffer auswählen', allSelected: 'Alle gewählt',
      themeLabel: 'Design: {0}. Klicken für {1}.', themeSystem: 'System', themeLight: 'Hell', themeDark: 'Dunkel', am: 'AM', pm: 'PM', phTimeEntry: 'hh:mm', phTimeEntryAmPm: 'hh:mm am',
      create: 'Anlegen: "{0}"', creating: '"{0}" wird angelegt …', created: '"{0}" angelegt', createFailed: 'Das ließ sich nicht anlegen. Bitte erneut versuchen.', createHint: 'Name eintippen, um etwas Neues anzulegen.',
      all: 'Alle', suggestionCount: '{0} Vorschläge', suggestionOne: '1 Vorschlag',
      startDate: 'Startdatum wählen', endDate: 'Enddatum wählen', daysCount: '{0} Tage', oneDay: '1 Tag',
      exactDates: 'Genaue Daten', flexible: 'Flexible Daten', flexDays: '± {0} Tage', flexDay: '± 1 Tag',
      from: 'Von', to: 'Bis', hour: 'Stunde', minute: 'Minute', ampm: 'Vor- oder nachmittags', typeTime: 'Uhrzeit eintippen', nextDay: 'am nächsten Tag', durHM: '{0} Std. {1} Min.', durH: '{0} Std.', durM: '{0} Min.',
      colours: 'Farben', hue: 'Farbton', area: 'Sättigung und Helligkeit', areaValue: 'Sättigung {0} %, Helligkeit {1} %', hex: 'Hex-Farbe',
      whiteText: 'Weiße Schrift', darkText: 'Dunkle Schrift', lvAAA: 'AAA', lvAA: 'AA', lvLarge: 'nur große Schrift', lvLow: 'zu niedrig',
      phTags: 'Tag hinzufügen', removeTag: 'Tag {0} entfernen', tagRemoved: '{0} entfernt', tagDuplicate: '{0} ist schon vorhanden', tagMax: 'Höchstens {0} Tags', tagStrict: 'Bitte einen Vorschlag wählen', suggestions: 'Vorschläge'
    }
  };
  var PK_SWATCHES = ['#dc2626', '#ea580c', '#d97706', '#ca8a04', '#65a30d', '#16a34a', '#0d9488', '#0891b2', '#0284c7', '#2563eb', '#4f46e5', '#7c3aed', '#a21caf', '#db2777', '#e11d48', '#475569', '#1f2937', '#78716c'];
  var PK_INK = '#0b1020';
  var KINDS = {};
  var pickerOpen = null;
  var pkLocaleName = null;
  var pkFormats = null;

  function pkText(key, a, b) {
    var lang = (root.lang || 'en').slice(0, 2).toLowerCase();
    var table = PK_TEXT[lang] || PK_TEXT.en, own = CAL_TEXT[lang] || CAL_TEXT.en;
    var s = root.getAttribute('data-pk-' + key.toLowerCase()) || table[key] || own[key] || PK_TEXT.en[key] || CAL_TEXT.en[key] || key;
    // function replacers: what a person typed (a tag) may hold a "$&" that a plain replacement string would expand
    return s.replace('{0}', function () { return a === undefined ? '' : a; }).replace('{1}', function () { return b === undefined ? '' : b; });
  }
  function pkLocale() {
    if (!pkLocaleName) {
      var l = root.getAttribute('data-locale') || root.lang || navigator.language || 'en-GB';
      if (/^en$/i.test(l)) { l = 'en-GB'; }
      try { Intl.DateTimeFormat(l); } catch (e) { l = 'en-GB'; }
      pkLocaleName = l;
    }
    return pkLocaleName;
  }
  function formats() {
    if (!pkFormats) {
      var l = pkLocale();
      pkFormats = {
        day: new Intl.DateTimeFormat(l, { dateStyle: 'medium' }),
        full: new Intl.DateTimeFormat(l, { dateStyle: 'full' }),
        month: new Intl.DateTimeFormat(l, { month: 'long', year: 'numeric' }),
        dow: new Intl.DateTimeFormat(l, { weekday: 'short' }),
        dowLong: new Intl.DateTimeFormat(l, { weekday: 'long' })
      };
    }
    return pkFormats;
  }
  function isPhone() { return window.matchMedia('(max-width: 640px)').matches; }
  function clamp(n, lo, hi) { return Math.min(hi, Math.max(lo, n)); }
  function p2(n) { return (n < 10 ? '0' : '') + n; }

  // ---- dates
  function isoOf(d) { return d.getFullYear() + '-' + p2(d.getMonth() + 1) + '-' + p2(d.getDate()); }
  function dateOf(s) {
    var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s || '');
    if (!m) { return null; }
    var d = new Date(+m[1], +m[2] - 1, +m[3]);
    return d.getFullYear() === +m[1] && d.getMonth() === +m[2] - 1 && d.getDate() === +m[3] ? d : null;
  }
  function dayStart(d) { return new Date(d.getFullYear(), d.getMonth(), d.getDate()); }
  function firstOf(d) { return new Date(d.getFullYear(), d.getMonth(), 1); }
  function addDays(d, n) { return new Date(d.getFullYear(), d.getMonth(), d.getDate() + n); }
  function addMonths(d, n) {
    var x = new Date(d.getFullYear(), d.getMonth() + n, 1);
    x.setDate(Math.min(d.getDate(), new Date(x.getFullYear(), x.getMonth() + 1, 0).getDate()));
    return x;
  }
  function limitDate(d, min, max) { return min && d < min ? min : max && d > max ? max : d; }
  // A limit of a calendar (data-min, data-max): a date (2026-10-10), the words today, tomorrow and yesterday, or a distance from today: +2d, -1w, +3m, +1y
  // (days, weeks, months, years). So "nobody can pick the past" is data-min="today" and "at least three days ahead" is data-min="+3d".
  function dateExpr(text) {
    var s = (text || '').trim().toLowerCase();
    if (!s) { return null; }
    var today = dayStart(new Date());
    if (s === 'today') { return today; }
    if (s === 'tomorrow') { return addDays(today, 1); }
    if (s === 'yesterday') { return addDays(today, -1); }
    var m = /^([+-])\s*(\d+)\s*([dwmy])$/.exec(s);
    if (m) {
      var n = (m[1] === '-' ? -1 : 1) * +m[2];
      return m[3] === 'd' ? addDays(today, n) : m[3] === 'w' ? addDays(today, 7 * n) : addMonths(today, m[3] === 'm' ? n : 12 * n);
    }
    return dateOf(s);
  }
  // "2026-10-14, 2026-10-20..2026-10-24" -> a map of the ISO dates (a period is every day of it)
  function dateSet(text) {
    var set = {};
    (text || '').split(',').forEach(function (part) {
      var ends = part.trim().split('..'), a = dateOf(ends[0].trim()), b = ends.length > 1 ? dateOf(ends[1].trim()) : a;
      if (!a || !b) { return; }
      for (var d = a, i = 0; d <= b && i < 3700; d = addDays(d, 1), i += 1) { set[isoOf(d)] = true; }
    });
    return set;
  }
  // The number of the week by ISO 8601: the week of the Thursday decides the year, and the first week is the one with the 4th of January.
  // (isoWeek, the number of a calendar week, is defined with the schedule below and shared by the calendars)

  // ---- times
  function timeOf(s) {
    var m = /^(\d{1,2}):(\d{2})$/.exec((s || '').trim());
    if (!m || +m[1] > 23 || +m[2] > 59) { return null; }
    return { h: +m[1], m: +m[2] };
  }
  function timeStr(t) { return p2(t.h) + ':' + p2(t.m); }
  // What a person types: 9, 930, 0930, 9:30, 9.30, 9:30 pm, 930pm
  function typedTime(text) {
    var s = (text || '').toLowerCase().replace(/\s+/g, '');
    var mer = /([ap])\.?m?\.?$/.exec(s);
    if (mer) { s = s.slice(0, mer.index); }
    var h, n;
    var parts = s.split(/[:.]/);
    if (parts.length === 2) { h = parts[0]; n = parts[1]; }
    else if (/^\d{1,2}$/.test(s)) { h = s; n = '0'; }
    else if (/^\d{3,4}$/.test(s)) { h = s.slice(0, s.length - 2); n = s.slice(-2); }
    else { return null; }
    if (!/^\d{1,2}$/.test(h) || !/^\d{1,2}$/.test(n)) { return null; }
    h = +h; n = +n;
    if (mer) { if (h < 1 || h > 12) { return null; } h = h % 12 + (mer[1] === 'p' ? 12 : 0); }
    return h < 24 && n < 60 ? { h: h, m: n } : null;
  }
  function timeText(p, t) {
    return new Intl.DateTimeFormat(pkLocale(), { hour: '2-digit', minute: '2-digit', hourCycle: p.hour12 ? 'h12' : 'h23' }).format(new Date(2000, 0, 1, t.h, t.m));
  }

  // ---- colours
  function normHex(s) {
    var m = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i.exec((s || '').trim());
    if (!m) { return null; }
    var h = m[1].toLowerCase();
    if (h.length === 3) { h = h.charAt(0) + h.charAt(0) + h.charAt(1) + h.charAt(1) + h.charAt(2) + h.charAt(2); }
    return '#' + h;
  }
  function hsvToHex(h, s, v) {
    var c = v * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = v - c, r = 0, g = 0, b = 0;
    if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; } else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
    return '#' + [r, g, b].map(function (n) { var k = Math.round((n + m) * 255); return (k < 16 ? '0' : '') + k.toString(16); }).join('');
  }
  function hexToHsv(hex) {
    var c = [1, 3, 5].map(function (i) { return parseInt(hex.substr(i, 2), 16) / 255; });
    var max = Math.max(c[0], c[1], c[2]), min = Math.min(c[0], c[1], c[2]), d = max - min, h = 0;
    if (d) {
      if (max === c[0]) { h = 60 * (((c[1] - c[2]) / d) % 6); } else if (max === c[1]) { h = 60 * ((c[2] - c[0]) / d + 2); } else { h = 60 * ((c[0] - c[1]) / d + 4); }
    }
    return { h: h < 0 ? h + 360 : h, s: max ? d / max : 0, v: max };
  }
  // The text colour with the better contrast on this colour, and how good it is.
  function textOn(hex) {
    var l = luminance(hex);
    var white = 1.05 / (l + 0.05), dark = (l + 0.05) / (luminance(PK_INK) + 0.05);
    return white >= dark ? { name: 'whiteText', ratio: white, color: '#ffffff' } : { name: 'darkText', ratio: dark, color: PK_INK };
  }
  function levelOf(ratio) { return ratio >= 7 ? 'lvAAA' : ratio >= 4.5 ? 'lvAA' : ratio >= 3 ? 'lvLarge' : 'lvLow'; }

  // ---- the shared parts: the button, the panel, opening and closing
  function placeholderOf(p) {
    var own = p.wrap.getAttribute('data-placeholder');
    if (own) { return own; }
    return pkText({ select: 'phSelect', date: 'phDate', daterange: 'phPeriod', time: 'phTime', timerange: 'phTimeRange', colour: 'phColour', tags: 'phTags', slot: 'phSlot' }[p.kind]);
  }

  function pkPanel(p) {
    if (p.panel) { return p.panel; }
    var id = nextId('mat-pk');
    var panel = doc.createElement('div');
    panel.className = 'mat-picker__panel';
    panel.id = id;
    panel.hidden = true;
    panel.innerHTML = '<div class="mat-picker__head"><span class="mat-picker__heading" id="' + id + '-h"></span>' +
      '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon" data-picker-close aria-label="' + esc(pkText('close')) + '">' + icon('x') + '</button></div>' +
      '<div class="mat-picker__body"></div><div class="mat-picker__foot" hidden></div>';
    if (p.kind !== 'select') { panel.setAttribute('role', 'dialog'); panel.setAttribute('aria-labelledby', id + '-h'); }
    p.wrap.appendChild(panel);
    p.panel = panel;
    p.heading = $('.mat-picker__heading', panel);
    p.body = $('.mat-picker__body', panel);
    p.foot = $('.mat-picker__foot', panel);
    p.heading.textContent = labelOf(p.input) || placeholderOf(p);
    return panel;
  }

  // "apply": Apply and Cancel, "done": Done, nothing: no foot. The buttons are under the content, positive first.
  function setFoot(p, kind) {
    p.foot.innerHTML = kind === 'apply'
      ? '<button type="button" class="mat-btn mat-btn--primary" data-picker-action="apply">' + esc(pkText('apply')) + '</button>' +
        '<button type="button" class="mat-btn mat-btn--secondary" data-picker-action="cancel">' + esc(pkText('cancel')) + '</button>'
      : kind === 'done' ? '<button type="button" class="mat-btn mat-btn--primary" data-picker-action="cancel">' + esc(pkText('done')) + '</button>' : '';
    p.foot.hidden = !kind;
  }
  function refreshFoot(p) {
    var apply = $('[data-picker-action="apply"]', p.foot);
    if (apply) { apply.disabled = !p.spec.ready(p); }
  }

  function syncPicker(wrap) {
    var p = wrap._matPicker;
    if (!p) { return; }
    // a picker that is a field of its own (the tags) has no button and no panel that is opened: it draws itself from the value
    if (p.spec.inline) { p.spec.sync(p); return; }
    p.trigger.disabled = p.input.disabled;
    var out = $('.mat-picker__value', p.trigger);
    out.textContent = '';
    p.spec.display(p, out);
    var has = p.input.value !== '';
    out.classList.toggle('is-placeholder', !has);
    if (p.clear) { p.clear.hidden = !has || p.input.disabled; }
  }

  function setPickerValue(p, value) {
    if (p.input.value === value) { return; }
    p.input.value = value;
    syncPicker(p.wrap);
    p.input.dispatchEvent(new Event('input', { bubbles: true }));
    p.input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  function placePanel(p) {
    var panel = p.panel;
    p.full = isPhone();
    panel.style.left = ''; panel.style.top = ''; panel.style.minWidth = ''; panel.style.maxHeight = '';
    if (p.full) { return; }
    var r = p.trigger.getBoundingClientRect();
    var vw = window.innerWidth, vh = window.innerHeight;
    if (p.kind === 'select') { panel.style.minWidth = Math.max(280, r.width) + 'px'; }
    panel.style.left = '0px'; panel.style.top = '0px'; panel.style.maxHeight = (vh - 16) + 'px';
    var w = panel.offsetWidth, h = panel.offsetHeight;
    var below = vh - r.bottom - 10, above = r.top - 10;
    // below the field if it fits, else above it; when neither side has room the panel covers the field and stays inside the window
    var top, origin = 'top left';
    if (h <= below) { top = r.bottom + 6; }
    else if (h <= above) { top = r.top - h - 6; origin = 'bottom left'; }
    else { top = vh - h - 8; origin = 'center left'; }
    panel.style.transformOrigin = origin;
    panel.style.left = Math.max(8, Math.min(r.left, vw - w - 8)) + 'px';
    panel.style.top = Math.max(8, top) + 'px';
  }

  function openPicker(wrap) {
    var p = wrap._matPicker;
    if (!p || p.trigger.disabled) { return; }
    if (pickerOpen === p) { closePicker(true); return; }
    closePicker(false);
    closeMenu();
    pkPanel(p);
    p.full = isPhone();
    p.relayout = function () { if (pickerOpen === p) { placePanel(p); } };
    var focusEl = p.spec.open(p);
    // the panel is not drawn before it has its place, whatever the browser does between the two steps
    p.panel.style.visibility = 'hidden';
    p.panel.hidden = false;
    p.trigger.setAttribute('aria-expanded', 'true');
    pickerOpen = p;
    placePanel(p);
    p.panel.style.visibility = '';
    syncLock();
    if (p.spec.shown) { p.spec.shown(p); }
    if (focusEl) {
      focusEl.focus({ preventScroll: true });
      // a field to type in starts selected, so that typing replaces what is there
      if (focusEl.select && /^(text|search)$/.test(focusEl.type || '')) { focusEl.select(); }
    }
  }

  function closePicker(restoreFocus) {
    var p = pickerOpen;
    if (!p) { return; }
    pickerOpen = null;
    p.panel.hidden = true;
    p.trigger.setAttribute('aria-expanded', 'false');
    p.state = null;
    syncLock();
    if (restoreFocus) { p.trigger.focus({ preventScroll: true }); }
  }

  function panelAction(p, action) {
    if (action === 'apply') {
      var v = p.spec.ready(p) ? p.spec.value(p) : null;
      if (v === null) { return; }
      setPickerValue(p, v);
    }
    closePicker(true);
  }

  function buildPicker(wrap) {
    if (wrap._matPicker) { return; }
    var kind = wrap.getAttribute('data-picker');
    var spec = KINDS[kind];
    var input = $('.mat-picker__input', wrap);
    if (!spec || !input) { return; }
    var p = { wrap: wrap, input: input, kind: kind, spec: spec, state: null };
    wrap._matPicker = p;
    if (!input.id) { input.id = nextId('mat-pk-in'); }
    input.tabIndex = -1;
    input.setAttribute('aria-hidden', 'true');
    input.setAttribute('autocomplete', 'off');
    if (spec.inline) {
      spec.init(p);
      syncPicker(wrap);
      return;
    }

    var trigger = doc.createElement('button');
    trigger.type = 'button';
    trigger.className = 'mat-input mat-picker__trigger';
    trigger.id = input.id + '-trigger';
    trigger.setAttribute('aria-haspopup', kind === 'select' ? 'listbox' : 'dialog');
    trigger.setAttribute('aria-expanded', 'false');
    trigger.innerHTML = '<span class="mat-picker__value" id="' + trigger.id + '-value"></span>' + icon(spec.icon);
    wrap.insertBefore(trigger, input.nextSibling);
    p.trigger = trigger;
    var lab = $('label[for="' + input.id + '"]');
    if (lab) {
      if (!lab.id) { lab.id = input.id + '-label'; }
      trigger.setAttribute('aria-labelledby', lab.id + ' ' + trigger.id + '-value');
    }
    var desc = input.getAttribute('aria-describedby');
    if (desc) { trigger.setAttribute('aria-describedby', desc); }

    if (wrap.hasAttribute('data-clearable')) {
      var clear = doc.createElement('button');
      clear.type = 'button';
      clear.className = 'mat-picker__clear';
      clear.setAttribute('aria-label', pkText('clear'));
      clear.innerHTML = icon('x');
      wrap.insertBefore(clear, trigger.nextSibling);
      p.clear = clear;
      trigger.classList.add('is-clearable');
    }
    spec.init(p);
    syncPicker(wrap);
    // leaving the picker with the Tab key closes it (the panel is not in the order of the page)
    wrap.addEventListener('focusout', function (e) {
      if (pickerOpen === p && e.relatedTarget && !wrap.contains(e.relatedTarget)) { closePicker(false); }
    });
  }

  function initPickers(scope) {
    $$('.mat-picker[data-picker]', scope).forEach(buildPicker);
    $$('.mat-picker', scope).forEach(syncPicker);
  }

  // ---- select: an option has a picture, a title, a description and tags
  function selValues(p) {
    var v = p.input.value;
    if (!p.multiple) { return v === '' ? [] : [v]; }
    return v.split(p.sep.trim() || p.sep).map(function (s) { return s.trim(); }).filter(Boolean);
  }
  function optionName(o) {
    var n = $('.mat-picker__name', o);
    return (n ? n.textContent : o.textContent).trim();
  }
  function focusOption(p, o) {
    $$('.mat-picker__option', p.list).forEach(function (x) { x.tabIndex = -1; });
    o.tabIndex = 0;
    o.focus({ preventScroll: true });
    keepInView(p.list, o);
  }
  // (a row in a group that is hidden, the suggestions while something is searched or chosen, is not in the way of the arrow keys)
  function visibleOptions(p) { return $$('.mat-picker__option:not([hidden]):not(:disabled)', p.list).filter(function (o) { return !o.closest('.mat-picker__group[hidden]'); }); }
  function filterOptions(p, query) {
    var q = query.trim().toLowerCase(), shown = 0;
    realOptions(p).forEach(function (o) {
      var hit = !q || (o.textContent + ' ' + (o.getAttribute('data-keywords') || '')).toLowerCase().indexOf(q) >= 0;
      o.hidden = !hit;
      if (hit) { shown += 1; }
    });
    var offer = updateCreate(p, query);
    updateSuggestions(p);
    // "Nothing found" unless the list offers to create it; a list that is empty to begin with says how to fill it
    p.empty.hidden = shown > 0 || offer;
    if (p.create) { p.empty.textContent = q ? pkTextOf(p, 'noResults') : pkTextOf(p, 'createHint'); }
    selAllSync(p);
    p.relayout();
  }
  // The rows that the arrow keys walk: "Select all" (when it is there and shown), then the options that are shown.
  function selRows(p) { return (p.all && !p.all.hidden ? [p.all] : []).concat(visibleOptions(p)); }
  function focusRow(p, row) { if (row === p.all) { row.focus({ preventScroll: true }); } else { focusOption(p, row); } }
  // The options of the list itself that are shown: not the row that creates and not the suggested copies (those are not a choice of their own).
  function shownReal(p) { return visibleOptions(p).filter(isReal); }
  // The chosen values in the order of the options (a value that no option has comes first and is kept).
  function selSet(p, vals) {
    var order = realOptions(p).map(function (x) { return x.getAttribute('data-value') || ''; });
    vals.sort(function (a, b) { return order.indexOf(a) - order.indexOf(b); });
    setPickerValue(p, vals.join(p.sep));
  }
  // "Select all" (data-select-all, several choices): the box says whether all, some or none of the options that are shown (the search narrows them) are chosen,
  // and a click chooses them all, or, when they all are chosen, takes them away. Options that cannot be chosen are left as they are.
  function selAllSync(p) {
    if (!p.all) { return; }
    var shown = shownReal(p), vals = selValues(p);
    var on = shown.filter(function (o) { return vals.indexOf(o.getAttribute('data-value') || '') >= 0; }).length;
    p.all.hidden = shown.length === 0;
    p.all.setAttribute('aria-checked', on === 0 ? 'false' : on === shown.length ? 'true' : 'mixed');
    $('.mat-picker__all-text', p.all).textContent = pkText(p.search && p.search.value.trim() ? 'selectFound' : 'selectAll');
  }
  function selToggleAll(p) {
    var keys = shownReal(p).map(function (o) { return o.getAttribute('data-value') || ''; }), vals = selValues(p);
    if (!keys.length) { return; }
    var every = keys.every(function (k) { return vals.indexOf(k) >= 0; });
    selSet(p, every ? vals.filter(function (v) { return keys.indexOf(v) < 0; }) : vals.concat(keys.filter(function (k) { return vals.indexOf(k) < 0; })));
    p.all.focus({ preventScroll: true });
  }

  // ---- select: create what is missing, and suggestions (FORM-16)
  // data-create-url on the wrapper: the panel has the search field and, while the text in it is not the name of an option, a first row "Create <text>". Choosing it POSTs
  // name=<text> (and the anti-forgery token of the page) to the address and takes { "value": "...", "name": "...", "description"?: "..." } as the new option, which is chosen.
  // data-suggest-url (or MatUi.picker.suggest): [ { "value": "7" }, { "name": "New Corp" } ] (or { "items": [...] }): an existing option, or, with data-create-url, a name that
  // creates one. While nothing is chosen and nothing is searched the panel shows them under "Suggestions" and the whole list under "All", and the closed button counts them.
  function isReal(o) { return !o.classList.contains('mat-picker__create') && !o.classList.contains('mat-picker__option--suggest'); }
  function realOptions(p) { return $$('.mat-picker__option', p.list).filter(isReal); }
  function optionByValue(p, v) { return realOptions(p).filter(function (o) { return (o.getAttribute('data-value') || '') === v; })[0] || null; }
  function optionByName(p, name) { var n = name.trim().toLowerCase(); return realOptions(p).filter(function (o) { return optionName(o).toLowerCase() === n; })[0] || null; }
  function fillText(s, a, b) { return s.replace('{0}', function () { return a === undefined ? '' : a; }).replace('{1}', function () { return b === undefined ? '' : b; }); }
  // a text of one picker: its own data-pk-<name> (the create row also data-create-label), then <html data-pk-<name>>, then the tables
  function pkTextOf(p, key, a, b) {
    var own = p.wrap.getAttribute('data-pk-' + key.toLowerCase()) || (key === 'create' ? p.wrap.getAttribute('data-create-label') : null);
    return own ? fillText(own, a, b) : pkText(key, a, b);
  }
  function announce(p, text) {
    if (!p.live) { return; }
    clearTimeout(p.liveTimer);
    p.live.textContent = text;
    p.liveTimer = setTimeout(function () { p.live.textContent = ''; }, 4000);
  }

  // the row "Create <text>": shown for a text that is not the name of an option (compared without regard to case)
  function updateCreate(p, query) {
    if (!p.createRow || p.creating) { return !!p.createRow && !p.createRow.hidden; }
    var q = query.trim();
    var show = !!q && !optionByName(p, q);
    p.createName = show ? q : '';
    p.createRow.hidden = !show;
    if (show) { $('.mat-picker__name', p.createRow).textContent = pkTextOf(p, 'create', q); }
    return show;
  }
  function createError(p, message) {
    p.createMsg.textContent = message || '';
    p.createMsg.hidden = !message;
  }
  function setBusy(p, row, on) {
    p.creating = on;
    row.classList.toggle('is-busy', on);
    if (on) { row.setAttribute('aria-busy', 'true'); row.setAttribute('aria-disabled', 'true'); } else { row.removeAttribute('aria-busy'); row.removeAttribute('aria-disabled'); }
    if (row === p.createRow) { $('.mat-picker__name', row).textContent = on ? pkTextOf(p, 'creating', p.createName) : pkTextOf(p, 'create', p.createName); }
  }

  // An option as the markup of the toolkit has it, made with textContent (what a person typed is never markup).
  function pickerAddOption(x, spec) {
    var p = pickerOf(x);
    if (!p || p.kind !== 'select' || !spec) { return null; }
    var value = spec.value === undefined || spec.value === null ? '' : String(spec.value);
    var known = optionByValue(p, value);
    if (known) { return known; }
    var o = doc.createElement('button');
    o.type = 'button';
    o.className = 'mat-picker__option';
    o.setAttribute('data-value', value);
    o.setAttribute('role', 'option');
    o.setAttribute('aria-selected', 'false');
    o.tabIndex = -1;
    if (spec.colour) { o.setAttribute('data-colour', String(spec.colour)); markColour(o); }
    var text = doc.createElement('span');
    text.className = 'mat-picker__text';
    var name = doc.createElement('span');
    name.className = 'mat-picker__name';
    name.textContent = spec.name === undefined || spec.name === null ? value : String(spec.name);
    text.appendChild(name);
    if (spec.description) {
      var desc = doc.createElement('span');
      desc.className = 'mat-picker__desc';
      desc.textContent = String(spec.description);
      text.appendChild(desc);
    }
    if (spec.tags && spec.tags.length) {
      var tags = doc.createElement('span');
      tags.className = 'mat-picker__tags';
      Array.prototype.forEach.call(spec.tags, function (t) { var tag = doc.createElement('span'); tag.className = 'mat-tag'; tag.textContent = String(t); tags.appendChild(tag); });
      text.appendChild(tags);
    }
    o.appendChild(text);
    o.insertAdjacentHTML('beforeend', '<span class="mat-picker__check" aria-hidden="true">' + icon('check') + '</span>');
    p.list.appendChild(o);
    // a suggested name that is an option now is that option
    var turned = false;
    (p.suggestions || []).forEach(function (s) {
      if (s.name !== undefined && s.name.toLowerCase() === name.textContent.trim().toLowerCase()) { delete s.name; s.value = value; turned = true; }
    });
    if (turned) { renderSuggestions(p); } else { syncPicker(p.wrap); }
    if (pickerOpen === p && p.search) { filterOptions(p, p.search.value); }
    return o;
  }

  // A new option is chosen: a single select takes it and closes, one with several choices adds it, keeps the panel open and starts the search again.
  function pkCreated(p, data, typed) {
    var value = String(data.value);
    var name = typeof data.name === 'string' && data.name ? data.name : typed;
    pickerAddOption(p.wrap, { value: value, name: name, description: data.description, colour: data.colour });
    createError(p, '');
    announce(p, pkTextOf(p, 'created', name));
    if (p.search) { p.search.value = ''; }
    if (!p.multiple) {
      setPickerValue(p, value);
      if (pickerOpen === p) { closePicker(true); }
      return;
    }
    var vals = selValues(p);
    if (vals.indexOf(value) < 0) {
      vals.push(value);
      var order = realOptions(p).map(function (x) { return x.getAttribute('data-value') || ''; });
      vals.sort(function (a, b) { return order.indexOf(a) - order.indexOf(b); });
      setPickerValue(p, vals.join(p.sep));
    }
    if (pickerOpen === p) {
      filterOptions(p, '');
      var again = p.full ? optionByValue(p, value) : p.search;
      if (again) { if (again === p.search) { again.focus({ preventScroll: true }); } else { focusOption(p, again); } }
    }
  }

  function pkCreate(p, name, row) {
    name = (name || '').trim();
    if (!name || p.creating || !p.createUrl) { return; }
    row = row || p.createRow;
    p.createName = name;
    createError(p, '');
    setBusy(p, row, true);
    var body = new URLSearchParams({ name: name });
    var token = $('input[name="__RequestVerificationToken"]');
    if (token) { body.set('__RequestVerificationToken', token.value); }
    function failed(message) {
      setBusy(p, row, false);
      var text = message || pkTextOf(p, 'createFailed');
      if (pickerOpen === p) { createError(p, text); } else { announce(p, text); }
    }
    fetch(p.createUrl, { method: 'POST', body: body, credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(function (res) {
        return res.json().catch(function () { return null; }).then(function (data) {
          if (!res.ok) { throw { refused: true, message: data && typeof data.error === 'string' && data.error ? data.error : null }; }
          if (!data || data.value === undefined || data.value === null || String(data.value) === '') { throw { refused: true, message: null }; }
          return data;
        });
      })
      .then(function (data) { setBusy(p, row, false); pkCreated(p, data, name); })
      .catch(function (err) { failed(err && err.refused ? err.message : null); });
  }

  // ---- suggestions: heading, rows, the count on the closed button
  function suggestionShown(p) {
    return !!p.suggestions && p.suggestions.length > 0 && selValues(p).length === 0 && !(p.search && p.search.value.trim());
  }
  function updateSuggestions(p) {
    if (!p.suggestGroup) { return; }
    var on = suggestionShown(p);
    p.suggestGroup.hidden = !on;
    p.allHead.hidden = !on;
  }
  function suggestionRow(p, s) {
    var row;
    if (s.value !== undefined) {
      row = optionByValue(p, s.value).cloneNode(true);
      row.removeAttribute('id');
      row.removeAttribute('hidden');
      row.setAttribute('aria-selected', 'false');
    } else {
      row = doc.createElement('button');
      row.type = 'button';
      row.setAttribute('role', 'option');
      row.setAttribute('aria-selected', 'false');
      row.setAttribute('data-suggest-name', s.name);
      row.setAttribute('aria-label', pkTextOf(p, 'create', s.name));
      row.innerHTML = '<span class="mat-picker__plus" aria-hidden="true">' + icon('plus') + '</span><span class="mat-picker__text"><span class="mat-picker__name"></span></span>';
      $('.mat-picker__name', row).textContent = s.name;
      row.classList.add('mat-picker__option--new');
    }
    row.classList.add('mat-picker__option', 'mat-picker__option--suggest');
    row.tabIndex = -1;
    return row;
  }
  function renderSuggestions(p) {
    if (p.suggestGroup) { p.suggestGroup.remove(); p.allHead.remove(); p.suggestGroup = null; p.allHead = null; }
    if (p.suggestions && p.suggestions.length) {
      var id = nextId('mat-pk-sg');
      var group = doc.createElement('div');
      group.className = 'mat-picker__group';
      group.setAttribute('role', 'group');
      group.setAttribute('aria-labelledby', id);
      var head = doc.createElement('div');
      head.className = 'mat-picker__group-head';
      head.id = id;
      head.textContent = pkTextOf(p, 'suggestions');
      group.appendChild(head);
      p.suggestions.forEach(function (s) { group.appendChild(suggestionRow(p, s)); });
      var all = doc.createElement('div');
      all.className = 'mat-picker__group-head mat-picker__group-head--all';
      all.setAttribute('aria-hidden', 'true');
      all.textContent = pkTextOf(p, 'all');
      var after = p.createRow ? p.createRow.nextSibling : p.list.firstChild;
      p.list.insertBefore(group, after);
      p.list.insertBefore(all, after);
      p.suggestGroup = group;
      p.allHead = all;
    }
    updateSuggestions(p);
    syncPicker(p.wrap);
    if (pickerOpen === p) { p.relayout(); }
  }

  // [ { value } | { name } ] or { items: [...] } for a select: an option that is there, or a name that is not an option yet (only where data-create-url can make it). Returns the count.
  function pickerSuggest(x, items) {
    var p = pickerOf(x);
    if (!p || p.kind !== 'select') { return 0; }
    var list = Array.isArray(items) ? items : items && Array.isArray(items.items) ? items.items : [];
    var seen = {}, found = [];
    list.forEach(function (item) {
      if (!item || typeof item !== 'object') { return; }
      var hasValue = item.value !== undefined && item.value !== null && String(item.value) !== '';
      var name = typeof item.name === 'string' ? item.name.trim() : '';
      var option = hasValue ? optionByValue(p, String(item.value)) : name ? optionByName(p, name) : null;
      var entry = option ? { value: option.getAttribute('data-value') || '' } : !hasValue && name && p.create ? { name: name } : null;
      var key = entry && (entry.value !== undefined ? 'v:' + entry.value : 'n:' + entry.name.toLowerCase());
      if (entry && !seen[key]) { seen[key] = true; found.push(entry); }
    });
    p.suggestions = found;
    p.suggestLoaded = true;
    renderSuggestions(p);
    return found.length;
  }
  function fetchSuggestions(p) {
    p.suggestLoading = true;
    fetch(p.suggestUrl, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(function (res) { if (!res.ok) { throw new Error('suggestions: ' + res.status); } return res.json(); })
      .then(function (data) { p.suggestLoading = false; pickerSuggest(p.wrap, data); })
      .catch(function () { p.suggestLoading = false; /* no suggestions: the list is all there is, and it is asked again when the panel opens next */ });
  }

  // choose an option, or any value, from code (the whole value of a select with several choices: a string joined by data-sep, or an array)
  function pickerSet(x, value) {
    var p = pickerOf(x);
    if (!p) { return false; }
    setPickerValue(p, value === null || value === undefined ? '' : Array.isArray(value) ? value.join(p.sep || ', ') : String(value));
    return true;
  }
  function pickerOf(x) {
    var el = typeof x === 'string' ? $(x) : x;
    var wrap = el && el.closest ? el.closest('.mat-picker') : null;
    if (!wrap) { return null; }
    if (!wrap._matPicker) { buildPicker(wrap); }
    return wrap._matPicker || null;
  }

  KINDS.select = {
    icon: 'chevron-down',
    init: function (p) {
      p.multiple = p.wrap.hasAttribute('data-multiple');
      p.sep = p.wrap.hasAttribute('data-sep') ? p.wrap.getAttribute('data-sep') : ', ';
      p.createUrl = p.wrap.getAttribute('data-create-url') || '';
      p.create = !!p.createUrl;
      p.suggestUrl = p.wrap.getAttribute('data-suggest-url') || '';
      p.list = $('.mat-picker__options', p.wrap) || doc.createElement('div');
      var panel = pkPanel(p);
      panel.classList.add('mat-picker__panel--list');
      p.list.setAttribute('role', 'listbox');
      p.list.setAttribute('aria-label', labelOf(p.input) || placeholderOf(p));
      if (p.multiple) { p.list.setAttribute('aria-multiselectable', 'true'); p.wrap.classList.add('mat-picker--multiple'); }
      $$('.mat-picker__option', p.list).forEach(function (o) {
        o.type = 'button';
        o.setAttribute('role', 'option');
        o.setAttribute('aria-selected', 'false');
        o.tabIndex = -1;
        if (o.disabled) { o.setAttribute('aria-disabled', 'true'); }
        if (!$('.mat-picker__check', o)) { o.insertAdjacentHTML('beforeend', '<span class="mat-picker__check" aria-hidden="true">' + icon('check') + '</span>'); }
      });
      // creating needs the text that is typed, so a select that can create always has the search field
      if (p.wrap.hasAttribute('data-search') || p.create) {
        var search = doc.createElement('div');
        search.className = 'mat-picker__search';
        search.innerHTML = icon('search') + '<input type="search" class="mat-input" autocomplete="off" aria-label="' + esc(pkText('search')) + '" placeholder="' + esc(pkText('search')) + '">';
        p.body.appendChild(search);
        p.search = $('input', search);
      }
      if (p.multiple && p.wrap.hasAttribute('data-select-all')) {
        p.all = doc.createElement('button');
        p.all.type = 'button';
        p.all.className = 'mat-picker__all';
        p.all.setAttribute('role', 'checkbox');
        p.all.setAttribute('aria-checked', 'false');
        p.all.innerHTML = '<span class="mat-picker__check" aria-hidden="true">' + icon('check') + '</span><span class="mat-picker__all-text"></span>';
        p.body.appendChild(p.all);
      }
      p.list.hidden = false;
      p.body.appendChild(p.list);
      p.empty = doc.createElement('p');
      p.empty.className = 'mat-picker__empty';
      p.empty.setAttribute('role', 'status');
      p.empty.hidden = true;
      p.empty.textContent = pkText('noResults');
      p.body.appendChild(p.empty);
      if (p.create) {
        // the row that creates, the message that says why it did not work (read aloud when it changes) and the place where the result is announced
        p.createMsg = doc.createElement('p');
        p.createMsg.className = 'mat-picker__status mat-picker__create-msg';
        p.createMsg.setAttribute('aria-live', 'polite');
        p.createMsg.hidden = true;
        p.body.insertBefore(p.createMsg, p.list);
        p.createRow = doc.createElement('button');
        p.createRow.type = 'button';
        p.createRow.className = 'mat-picker__option mat-picker__create';
        p.createRow.setAttribute('role', 'option');
        p.createRow.setAttribute('aria-selected', 'false');
        p.createRow.tabIndex = -1;
        p.createRow.hidden = true;
        p.createRow.innerHTML = '<span class="mat-picker__plus" aria-hidden="true">' + icon('plus') + '</span><span class="mat-picker__text"><span class="mat-picker__name"></span></span>';
        p.list.insertBefore(p.createRow, p.list.firstChild);
        p.live = doc.createElement('span');
        p.live.className = 'mat-sr-only';
        p.live.setAttribute('role', 'status');
        p.live.setAttribute('aria-live', 'polite');
        p.wrap.appendChild(p.live);
        p.empty.textContent = pkTextOf(p, 'createHint');
        p.empty.hidden = realOptions(p).length > 0;
      }
      setFoot(p, p.multiple ? 'done' : null);
    },
    display: function (p, out) {
      var vals = selValues(p);
      var chosen = [], all = realOptions(p);
      out.classList.remove('is-suggest');
      all.forEach(function (o) {
        var on = vals.indexOf(o.getAttribute('data-value') || '') >= 0;
        o.setAttribute('aria-selected', on ? 'true' : 'false');
        if (on) { chosen.push(o); }
      });
      updateSuggestions(p);
      selAllSync(p);
      if (chosen.length === 0 && !vals.length && p.suggestions && p.suggestions.length) {
        // nothing is chosen but something is suggested: the closed button says so, in the colour of a link, instead of the placeholder
        out.textContent = p.suggestions.length === 1 ? pkTextOf(p, 'suggestionOne') : pkTextOf(p, 'suggestionCount', p.suggestions.length);
        out.classList.add('is-suggest');
        return;
      }
      if (chosen.length === 0) { out.textContent = vals.length ? vals.join(p.sep) : placeholderOf(p); return; }
      if (p.multiple) {
        // the names while there are one or two of them, "All selected" when every one of more than two is chosen, else the count
        var names = chosen.map(optionName);
        out.textContent = chosen.length === all.length && all.length > 2 ? pkText('allSelected') : chosen.length <= 2 ? names.join(', ') : pkText('selectedCount', chosen.length);
        return;
      }
      var img = $('.mat-picker__img', chosen[0]);
      if (img) { out.appendChild(img.cloneNode(true)); }
      if (chosen[0].classList.contains('is-coloured')) {
        // the colour of the option (data-colour) goes with its name into the closed field
        var dot = doc.createElement('span');
        dot.className = 'mat-dot';
        dot.setAttribute('aria-hidden', 'true');
        dot.setAttribute('data-colour', chosen[0].getAttribute('data-colour'));
        markColour(dot);
        out.appendChild(dot);
      }
      var name = doc.createElement('span');
      name.textContent = optionName(chosen[0]);
      out.appendChild(name);
    },
    open: function (p) {
      if (p.createMsg) { createError(p, ''); }
      if (p.search) { p.search.value = ''; filterOptions(p, ''); }
      selAllSync(p);
      // suggestions are fetched when the panel is opened for the first time with nothing chosen (and asked for again if that failed)
      if (p.suggestUrl && !p.suggestLoaded && !p.suggestLoading && !selValues(p).length) { fetchSuggestions(p); }
      var chosen = $('.mat-picker__option[aria-selected="true"]:not([hidden])', p.list);
      var first = chosen || visibleOptions(p)[0];
      if (first) { $$('.mat-picker__option', p.list).forEach(function (x) { x.tabIndex = -1; }); first.tabIndex = 0; }
      if (p.search && !p.full) { return p.search; }
      return first || p.search;
    },
    click: function (p, t) {
      if (p.all && t.closest('.mat-picker__all')) { selToggleAll(p); return; }
      var o = t.closest('.mat-picker__option');
      if (!o || o.disabled) { return; }
      if (o === p.createRow) { pkCreate(p, p.createName); return; }
      if (o.hasAttribute('data-suggest-name')) { pkCreate(p, o.getAttribute('data-suggest-name'), o); return; }
      var v = o.getAttribute('data-value') || '';
      if (!p.multiple) { setPickerValue(p, v); closePicker(true); return; }
      var vals = selValues(p), i = vals.indexOf(v);
      if (i >= 0) { vals.splice(i, 1); } else { vals.push(v); }
      selSet(p, vals);
      // a suggested row goes away once something is chosen: the focus stays on the option itself
      focusOption(p, isReal(o) ? o : optionByValue(p, v) || o);
    },
    input: function (p, t) { if (t === p.search) { if (p.createMsg) { createError(p, ''); } filterOptions(p, t.value); } },
    // Arrow keys, Home and End walk the rows ("Select all" first); Space (or Enter) on a row is the click of its button, so it toggles the option
    keys: function (p, e) {
      var t = e.target, opts = visibleOptions(p), rows = selRows(p), o = t.closest('.mat-picker__option'), onAll = !!p.all && t === p.all;
      var i = rows.indexOf(onAll ? p.all : o), to = null;
      if (e.key === 'ArrowDown') { to = rows[Math.min(rows.length - 1, i + 1)]; }
      else if (e.key === 'ArrowUp') {
        if (i <= 0 && p.search) { e.preventDefault(); p.search.focus(); return; }
        to = rows[Math.max(0, i - 1)];
      }
      else if (e.key === 'Home' && (o || onAll)) { to = rows[0]; }
      else if (e.key === 'End' && (o || onAll)) { to = rows[rows.length - 1]; }
      else if (e.key === 'Enter' && t === p.search) {
        // with a text in the field Enter takes the first match, and where nothing matches it creates; in an empty field it takes the first row, as it always did,
        // but never a suggested name: that would create something nobody asked for
        e.preventDefault();
        var typed = p.search.value.trim();
        var pick = typed ? (p.create && optionByName(p, typed)) || opts.filter(isReal)[0] : opts.filter(function (x) { return !x.hasAttribute('data-suggest-name'); })[0];
        if (pick) { p.spec.click(p, pick); } else if (p.createRow && !p.createRow.hidden) { pkCreate(p, p.createName); }
        return;
      }
      else if (e.key === ' ' && !p.typed) { return; }
      else if (o && !p.search && e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
        // type the first letters of a title to jump to it (a space in the middle of a word is a part of it, a space on its own is the click)
        p.typed = (p.typed || '') + e.key.toLowerCase();
        clearTimeout(p.typedTimer);
        p.typedTimer = setTimeout(function () { p.typed = ''; }, 700);
        var hit = opts.slice(opts.indexOf(o) + 1).concat(opts.slice(0, opts.indexOf(o) + 1)).filter(function (x) { return optionName(x).toLowerCase().indexOf(p.typed) === 0; })[0];
        if (e.key === ' ') { e.preventDefault(); }
        if (hit) { e.preventDefault(); focusOption(p, hit); }
        return;
      } else { return; }
      e.preventDefault();
      if (to) { focusRow(p, to); }
    }
  };

  // ---- date and period: a calendar
  function dateValue(p) {
    var raw = (p.input.value || '').trim(), flex = 0;
    var fm = /~(\d+)$/.exec(raw);
    if (fm) { flex = +fm[1]; raw = raw.replace(/~\d+$/, ''); }
    if (p.kind === 'date') { return { start: dateOf(raw), end: null, flex: 0 }; }
    var parts = raw.split('..');
    return { start: dateOf(parts[0]), end: dateOf(parts[1]), flex: flex };
  }
  function flexText(n) { return n === 1 ? pkText('flexDay') : pkText('flexDays', n); }
  function daysBetween(a, b) { return Math.round((dayStart(b) - dayStart(a)) / 86400000) + 1; }
  function rangeValue(s) { return isoOf(s.start) + '..' + isoOf(s.end) + (s.flex > 0 ? '~' + s.flex : ''); }

  // ---- the month grid that three things share: the panel of the date and period pickers, the calendar that is always shown (data-calendar) and the panel of the
  // appointment picker (data-picker="slot"). They keep a state s: mode ('single', 'range' or 'multiple'), start, end, dates (an array of ISO dates, for 'multiple'),
  // focus (the day the keyboard is on), view (the first day of the first month that is shown), level ('days', 'months' or 'years'), min, max, weekStart, months (how
  // many are shown) and, when the host wants them: off(date) (more days that cannot be chosen), look(date) ('unavailable' or 'absent' for such a day), marks(iso) (dots
  // under a day), describe(iso, date, off) (what a screen reader adds to the name of a day), jump (the title opens the choice of a month and a year), weekNumbers and
  // blocked (a map of ISO dates that a period may not run across). The host is {state, body (the element around the grid), relayout, spec.pick(host, date, event)}.
  var calFmt = null;
  function calFormat() {
    if (!calFmt) { calFmt = { monthShort: new Intl.DateTimeFormat(pkLocale(), { month: 'short' }), monthLong: new Intl.DateTimeFormat(pkLocale(), { month: 'long' }) }; }
    return calFmt;
  }
  function calOff(s, d) { return !!(s.min && d < s.min) || !!(s.max && d > s.max) || !!(s.off && s.off(d)); }
  // the closest day that can be chosen, looking in the direction of dir (1 later, -1 earlier) first and then the other way; null when there is none within a year
  function calNearest(s, d, dir) {
    for (var way = 0; way < 2; way += 1) {
      var step = way === 0 ? dir : -dir;
      for (var i = way; i < 400; i += 1) {
        var x = addDays(d, i * step);
        if (!calOff(s, x)) { return x; }
        if ((step > 0 && s.max && x > s.max) || (step < 0 && s.min && x < s.min)) { break; }
      }
    }
    return null;
  }
  // a day that is delta days on, skipping the days that cannot be chosen; null when the way ends at min or max
  function calMove(s, from, delta) {
    var x = addDays(from, delta);
    for (var i = 0; i < 400; i += 1, x = addDays(x, delta)) {
      if (!calOff(s, x)) { return x; }
      if ((delta > 0 && s.max && x > s.max) || (delta < 0 && s.min && x < s.min)) { return null; }
    }
    return null;
  }

  function calMonth(s, first, index, count) {
    var f = formats(), title = f.month.format(first);
    var lead = (first.getDay() - s.weekStart + 7) % 7;
    var total = new Date(first.getFullYear(), first.getMonth() + 1, 0).getDate();
    function nav(dir, name, ic) {
      // no way to a month in which every day is outside data-min or data-max
      var off = dir < 0 ? s.min && new Date(first.getFullYear(), first.getMonth(), 0) < s.min : s.max && new Date(first.getFullYear(), first.getMonth() + 1, 1) > s.max;
      return '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm" data-cal-nav="' + dir + '" aria-label="' + esc(pkText(name)) + '"' + (off ? ' disabled' : '') + '>' + icon(ic) + '</button>';
    }
    var heading = s.jump && index === 0
      ?'<button type="button" class="mat-cal__title mat-cal__title--button" data-cal-level="months" aria-label="' + esc(title + ', ' + pkText('jumpMonth')) + '">' + esc(title) + icon('chevron-down') + '</button>'
      : '<span class="mat-cal__title" aria-live="polite">' + esc(title) + '</span>';
    var h = '<div class="mat-cal__month"><div class="mat-cal__head">' +
      (index === 0 ? nav(-1, 'prevMonth', 'chevron-left') : '<span class="mat-cal__nav-gap"></span>') + heading +
      (index === count - 1 ? nav(1, 'nextMonth', 'chevron-right') : '<span class="mat-cal__nav-gap"></span>') +
      '</div><div class="mat-cal__grid' + (s.weekNumbers ? ' mat-cal__grid--weeks' : '') + '" role="grid" aria-label="' + esc(title) + '"><div class="mat-cal__row" role="row">';
    if (s.weekNumbers) { h += '<span class="mat-cal__dow" role="columnheader" aria-label="' + esc(pkText('week')) + '">' + esc(pkText('weekShort')) + '</span>'; }
    for (var w = 0; w < 7; w += 1) {
      var ref = new Date(2024, 0, 1 + (s.weekStart + 6 + w) % 7);   // 2024-01-01 is a Monday
      h += '<span class="mat-cal__dow" role="columnheader" aria-label="' + esc(f.dowLong.format(ref)) + '">' + esc(f.dow.format(ref)) + '</span>';
    }
    h += '</div>';
    var cells = lead + total;
    for (var c = 0; c < Math.ceil(cells / 7) * 7; c += 1) {
      if (c % 7 === 0) {
        h += '<div class="mat-cal__row" role="row">';
        if (s.weekNumbers) {
          var week = isoWeek(new Date(first.getFullYear(), first.getMonth(), 1 - lead + c));
          h += '<span class="mat-cal__wk" role="rowheader" aria-label="' + esc(pkText('weekNo', week)) + '">' + week + '</span>';
        }
      }
      var n = c - lead + 1;
      if (n < 1 || n > total) { h += '<span class="mat-cal__blank" role="gridcell" aria-hidden="true"></span>'; }
      else {
        var d = new Date(first.getFullYear(), first.getMonth(), n), iso = isoOf(d), off = calOff(s, d);
        var look = off && s.look ? s.look(d) : '';
        var marks = s.marks ? s.marks(iso) : 0;
        var extra = s.describe ? s.describe(iso, d, off) : '';
        h += '<button type="button" class="mat-cal__day' + (look ? ' is-' + look : '') + '" role="gridcell" data-date="' + iso + '" tabindex="-1" aria-selected="false" aria-label="' + esc(f.full.format(d) + (extra ? ', ' + extra : '')) + '"' +
          (off ? ' disabled' : '') + (look === 'absent' ? ' aria-hidden="true"' : '') + '>' + n +
          (marks ? '<span class="mat-cal__marks" aria-hidden="true">' + new Array(Math.min(marks, 3) + 1).join('<span class="mat-cal__mark"></span>') + '</span>' : '') + '</button>';
      }
      if (c % 7 === 6) { h += '</div>'; }
    }
    return h + '</div></div>';
  }

  // the choice of a month (twelve buttons of one year) and the choice of a year (twelve years): a title that is a button leads to them
  function calCell(value, text, name, disabled, current, today, attr) {
    return '<button type="button" class="mat-cal__cell' + (current ? ' is-current' : '') + (today ? ' is-today' : '') + '" ' + attr + '="' + value + '" tabindex="-1" aria-label="' + esc(name) + '"' +
      (current ? ' aria-pressed="true"' : ' aria-pressed="false"') + (disabled ? ' disabled' : '') + '>' + esc(text) + '</button>';
  }
  function calNavButton(dir, name, off) {
    return '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm" data-cal-nav="' + dir + '" aria-label="' + esc(pkText(name)) + '"' + (off ? ' disabled' : '') + '>' + icon(dir < 0 ? 'chevron-left' : 'chevron-right') + '</button>';
  }
  function calMonths(s) {
    var year = s.view.getFullYear(), cf = calFormat(), now = new Date();
    var h = '<div class="mat-cal__month"><div class="mat-cal__head">' +
      calNavButton(-1, 'prevYear', !!s.min && new Date(year, 0, 0) < s.min) +
      '<button type="button" class="mat-cal__title mat-cal__title--button" data-cal-level="years" aria-label="' + esc(year + ', ' + pkText('chooseYear')) + '">' + year + icon('chevron-down') + '</button>' +
      calNavButton(1, 'nextYear', !!s.max && new Date(year + 1, 0, 1) > s.max) +
      '</div><div class="mat-cal__pick" role="group" aria-label="' + esc(pkText('chooseMonth')) + '">';
    for (var m = 0; m < 12; m += 1) {
      var first = new Date(year, m, 1), last = new Date(year, m + 1, 0);
      var off = (s.min && last < s.min) || (s.max && first > s.max);
      h += calCell(m, cf.monthShort.format(first),cf.monthLong.format(first) + ' ' + year, off, m === s.view.getMonth(), year === now.getFullYear() && m === now.getMonth(), 'data-cal-month');
    }
    return h + '</div></div>';
  }
  function calYearBase(year) { return year - (((year % 12) + 12) % 12); }
  function calYears(s) {
    var base = s.yearBase, now = new Date();
    var h = '<div class="mat-cal__month"><div class="mat-cal__head">' +
      calNavButton(-1, 'prevYears', !!s.min && new Date(base, 0, 0) < s.min) +
      '<span class="mat-cal__title" aria-live="polite">' + base + ' – ' + (base + 11) + '</span>' +
      calNavButton(1, 'nextYears', !!s.max && new Date(base + 12, 0, 1) > s.max) +
      '</div><div class="mat-cal__pick" role="group" aria-label="' + esc(pkText('chooseYear')) + '">';
    for (var y = base; y < base + 12; y += 1) {
      var off = (s.min && new Date(y, 11, 31) < s.min) || (s.max && new Date(y, 0, 1) > s.max);
      h += calCell(y, String(y),String(y), off, y === s.view.getFullYear(), y === now.getFullYear(), 'data-cal-year');
    }
    return h + '</div></div>';
  }

  // the months and the years: one button is the one the arrow keys start from
  function calPaintCells(p) {
    var s = p.state, attr = s.level === 'months' ? 'data-cal-month' : 'data-cal-year';
    var cells = $$('.mat-cal__cell', p.body), stop = null;
    cells.forEach(function (b) { b.tabIndex = -1; if (!stop && !b.disabled && +b.getAttribute(attr) === s.cell) { stop = b; } });
    if (!stop) { stop = cells.filter(function (b) { return !b.disabled; })[0] || null; }
    if (stop) { stop.tabIndex = 0; s.cell = +stop.getAttribute(attr); }
  }

  function calStatus(s) {
    var f = formats().day;
    if (s.message) { return s.message; }
    if (s.mode === 'range') {
      return !s.start ? pkText('startDate') : !s.end ? pkText('endDate')
        : f.format(s.start) + ' – ' + f.format(s.end) + ' · ' + (daysBetween(s.start, s.end) === 1 ? pkText('oneDay') : pkText('daysCount', daysBetween(s.start, s.end)));
    }
    if (s.mode === 'multiple') { return !s.dates.length ? pkText('chooseDates') : s.dates.length === 1 ? pkText('oneDate') : pkText('datesCount', s.dates.length); }
    return '';
  }

  function calPaint(p) {
    var s = p.state, range = s.mode === 'range', multi = s.mode === 'multiple', today = isoOf(dayStart(new Date()));
    if (s.level !== 'days') { calPaintCells(p); return; }
    var hi = range && s.start && !s.end && s.hover && s.hover > s.start ? s.hover : s.end;
    var startIso = s.start ? isoOf(s.start) : '', hiIso = hi ? isoOf(hi) : '', focusIso = isoOf(s.focus);
    var stop = null, first = null;
    $$('.mat-cal__day', p.body).forEach(function (b) {
      var iso = b.getAttribute('data-date');
      var isStart = multi ? s.dates.indexOf(iso) >= 0 : iso === startIso, isEnd = range && iso === hiIso;
      var inside = range && !!s.start && !!hi && iso > startIso && iso < hiIso;
      var span = range && !!hi && hiIso > startIso;
      b.classList.toggle('is-today', iso === today);
      b.classList.toggle('is-selected', isStart || isEnd);
      b.classList.toggle('is-range-start', span && isStart);
      b.classList.toggle('is-range-end', span && isEnd);
      b.classList.toggle('is-in-range', inside);
      b.setAttribute('aria-selected', isStart || isEnd || inside ? 'true' : 'false');
      b.tabIndex = -1;
      if (!b.disabled) {
        if (!first) { first = b; }
        if (iso === focusIso) { stop = b; }
      }
    });
    // one day is where the Tab key arrives: the one the keyboard is on, or the first that can be chosen when that one cannot
    stop = stop || first;
    if (stop) { stop.tabIndex = 0; s.focus = dateOf(stop.getAttribute('data-date')); }
    var status = $('.mat-picker__status', p.body);
    if (status && (range || multi)) { status.textContent = calStatus(s); }
    $$('[data-cal-flex]', p.body).forEach(function (b) {
      var on = +b.getAttribute('data-cal-flex') === s.flex;
      b.setAttribute('aria-checked', on ? 'true' : 'false');
      b.classList.toggle('is-active', on);
      b.tabIndex = on ? 0 : -1;
    });
  }

  // refocus: a selector of what gets the focus when the grid is drawn again (the button that was used, or the day the keyboard is on)
  function calRender(p, refocus) {
    var s = p.state, box = $('.mat-cal__months', p.body), html = '';
    if (s.level === 'months') { html = calMonths(s); }
    else if (s.level === 'years') { html = calYears(s); }
    else {
      var last = addMonths(s.view, s.months - 1);
      if (s.focus < s.view) { s.view = firstOf(s.focus); }
      else if (s.focus >= new Date(last.getFullYear(), last.getMonth() + 1, 1)) { s.view = firstOf(addMonths(firstOf(s.focus), -(s.months - 1))); }
      for (var i = 0; i < s.months; i += 1) { html += calMonth(s, new Date(s.view.getFullYear(), s.view.getMonth() + i, 1), i, s.months); }
    }
    box.innerHTML = html;
    calPaint(p);
    if (refocus) { var b = $(refocus, p.body); if (b && !b.disabled) { b.focus({ preventScroll: true }); } }
    // the keyboard went to a month whose days were not there yet: the focus goes to a day as soon as there is one (and only if it is not somewhere else by then)
    if (s.pendingFocus && s.level === 'days') {
      var active = doc.activeElement, day = $(CAL_FOCUS, p.body);
      if (day && (!active || active === doc.body || p.body.contains(active))) { s.pendingFocus = false; day.focus({ preventScroll: true }); }
    }
    if (p.relayout) { p.relayout(); }
    // a host that loads what the days show (the appointment picker) learns which month is in view
    if (s.level === 'days' && p.spec.viewed) { p.spec.viewed(p); }
  }

  // The choice of a day, without the host: single takes it, multiple adds or removes it, a period starts with it or ends with it (an end before the start, or a
  // day behind a blocked one, starts again). False when the day was refused.
  function calSelect(s, d) {
    s.message = '';
    s.focus = d;
    if (s.mode === 'single') { s.start = d; return true; }
    if (s.mode === 'multiple') {
      var iso = isoOf(d), i = s.dates.indexOf(iso);
      if (i >= 0) { s.dates.splice(i, 1); return true; }
      if (s.maxDates > 0 && s.dates.length >= s.maxDates) { s.message = pkText('maxDates', s.maxDates); return false; }
      s.dates.push(iso);
      s.dates.sort();
      return true;
    }
    var across = false;
    if (s.start && !s.end && d > s.start && s.blocked) {
      for (var x = addDays(s.start, 1); x < d && !across; x = addDays(x, 1)) { across = !!s.blocked[isoOf(x)]; }
    }
    if (!s.start || s.end || d < s.start || across) { s.start = d; s.end = null; s.hover = null; } else { s.end = d; }
    return true;
  }

  function calPick(p, d) {
    var s = p.state;
    calSelect(s, d);
    if (p.kind === 'date') { setPickerValue(p, isoOf(d)); closePicker(true); return; }
    calPaint(p);
    refreshFoot(p);
    // a period without flexible dates is complete with its second day
    if (s.end && p.flexList.length === 0) { setPickerValue(p, rangeValue(s)); closePicker(true); }
  }

  var CAL_FOCUS = '.mat-cal__day[tabindex="0"]';

  function calDisplay(p, out) {
    var v = dateValue(p), f = formats().day;
    if (!p.input.value) { out.textContent = placeholderOf(p); return; }
    if (!v.start) { out.textContent = p.input.value; return; }
    var text = f.format(v.start);
    if (p.kind === 'daterange') { text += ' – ' + (v.end ? f.format(v.end) : '…') + (v.flex ? ' (' + flexText(v.flex) + ')' : ''); }
    out.textContent = text;
  }

  function calOpen(p) {
    var range = p.kind === 'daterange', v = dateValue(p);
    var min = dateExpr(p.wrap.getAttribute('data-min')), max = dateExpr(p.wrap.getAttribute('data-max'));
    var focus = limitDate(v.start || dayStart(new Date()), min, max);
    p.flexList = (p.wrap.getAttribute('data-flex') || '').split(',').map(function (x) { return parseInt(x, 10); }).filter(function (n) { return n > 0; });
    p.state = {
      mode: range ? 'range' : 'single', level: 'days', jump: true,
      start: v.start, end: v.end, flex: v.flex, focus: focus, hover: null, min: min, max: max, view: firstOf(focus),
      weekStart: parseInt(p.wrap.getAttribute('data-week-start') || root.getAttribute('data-week-start') || '1', 10) % 7,
      months: range && !p.full ? 2 : 1
    };
    var chips = '';
    if (range && p.flexList.length) {
      chips = '<div class="mat-picker__chips" role="radiogroup" aria-label="' + esc(pkText('flexible')) + '">' +
        [0].concat(p.flexList).map(function (n) {
          return '<button type="button" class="mat-btn mat-btn--secondary mat-btn--sm" role="radio" aria-checked="false" data-cal-flex="' + n + '">' + esc(n === 0 ? pkText('exactDates') : flexText(n)) + '</button>';
        }).join('') + '</div>';
    }
    p.body.innerHTML = '<div class="mat-cal"><div class="mat-cal__months"></div>' +
      (range ? '<p class="mat-picker__status" aria-live="polite"></p>' : '') + chips +
      '<div class="mat-cal__tools"><button type="button" class="mat-btn mat-btn--ghost mat-btn--sm" data-cal-today>' + esc(pkText('today')) + '</button></div></div>';
    setFoot(p, range && p.flexList.length ? 'apply' : null);
    calRender(p, null);
    refreshFoot(p);
    return $(CAL_FOCUS, p.body);
  }

  // a day of that month near the one the keyboard is on (the same number, or the last day of a shorter month)
  function calGoto(s, year, month) {
    var day = Math.min(s.focus.getDate(), new Date(year, month + 1, 0).getDate());
    s.focus = limitDate(new Date(year, month, day), s.min, s.max);
    s.view = firstOf(s.focus);
  }
  var CAL_CELL = '.mat-cal__cell[tabindex="0"]';

  function calClick(p, t, e) {
    var s = p.state;
    var nav = t.closest('[data-cal-nav]');
    if (nav) {
      var dir = +nav.getAttribute('data-cal-nav');
      if (s.level === 'months') { s.view = new Date(s.view.getFullYear() + dir, s.view.getMonth(), 1); }
      else if (s.level === 'years') { s.yearBase += dir * 12; }
      else { s.view = addMonths(s.view, dir); s.focus = limitDate(addMonths(s.focus, dir), s.min, s.max); }
      calRender(p, '[data-cal-nav="' + dir + '"]');
      return;
    }
    var day = t.closest('.mat-cal__day');
    if (day && !day.disabled) { p.spec.pick(p, dateOf(day.getAttribute('data-date')), e); return; }
    var level = t.closest('[data-cal-level]');
    if (level) {
      s.level = level.getAttribute('data-cal-level');
      s.cell = s.level === 'months' ? s.view.getMonth() : s.view.getFullYear();
      if (s.level === 'years') { s.yearBase = calYearBase(s.view.getFullYear()); }
      calRender(p, CAL_CELL);
      return;
    }
    var month = t.closest('[data-cal-month]');
    if (month && !month.disabled) {
      calGoto(s, s.view.getFullYear(), +month.getAttribute('data-cal-month'));
      s.level = 'days';
      calRender(p, CAL_FOCUS);
      return;
    }
    var year = t.closest('[data-cal-year]');
    if (year && !year.disabled) {
      s.view = new Date(+year.getAttribute('data-cal-year'), s.view.getMonth(), 1);
      s.level = 'months';
      s.cell = s.view.getMonth();
      calRender(p, CAL_CELL);
      return;
    }
    if (t.closest('[data-cal-today]')) {
      var today = limitDate(dayStart(new Date()), s.min, s.max);
      s.level = 'days';
      s.focus = calNearest(s, today, 1) || today;
      s.view = firstOf(s.focus);
      calRender(p, CAL_FOCUS);
      return;
    }
    var chip = t.closest('[data-cal-flex]');
    if (chip) { s.flex = +chip.getAttribute('data-cal-flex'); calPaint(p); }
  }

  // the first of these that exists and can be reached gets the focus: the day the keyboard is on, the title, an arrow
  function calFocus(p) {
    var b = $(CAL_FOCUS, p.body) || $('[data-cal-level]', p.body) || $('[data-cal-nav]:not(:disabled)', p.body);
    if (b) { b.focus({ preventScroll: true }); }
    return b;
  }

  // The first or the last day of the week that can be chosen.
  function calWeekEdge(s, from, dir) {
    for (var i = 0; i < 7; i += 1) {
      var x = addDays(from, i * dir);
      if (!calOff(s, x)) { return x; }
    }
    return null;
  }

  // months and years: three by four buttons, the arrow keys walk them, the page keys change the year (or twelve years)
  function calCellKeys(p, e) {
    var s = p.state, cell = e.target.closest('.mat-cal__cell');
    if (!cell) { return; }
    var attr = s.level === 'months' ? 'data-cal-month' : 'data-cal-year';
    var cells = $$('.mat-cal__cell:not(:disabled)', p.body), i = cells.indexOf(cell), to = null;
    if (e.key === 'ArrowLeft') { to = cells[Math.max(0, i - 1)]; }
    else if (e.key === 'ArrowRight') { to = cells[Math.min(cells.length - 1, i + 1)]; }
    else if (e.key === 'ArrowUp') { to = cells[i - 3 >= 0 ? i - 3 : i]; }
    else if (e.key === 'ArrowDown') { to = cells[i + 3 < cells.length ? i + 3 : i]; }
    else if (e.key === 'Home') { to = cells[0]; }
    else if (e.key === 'End') { to = cells[cells.length - 1]; }
    else if (e.key === 'PageUp' || e.key === 'PageDown') {
      var by = e.key === 'PageUp' ? -1 : 1;
      if (s.level === 'months') { s.view = new Date(s.view.getFullYear() + by, s.view.getMonth(), 1); } else { s.yearBase += by * 12; }
      e.preventDefault();
      calRender(p, CAL_CELL);
      return;
    }
    else { return; }
    e.preventDefault();
    if (to) { s.cell = +to.getAttribute(attr); calPaintCells(p); to.focus({ preventScroll: true }); }
  }

  function calKeys(p, e) {
    var s = p.state;
    if (s.level !== 'days') { calCellKeys(p, e); return; }
    if (!e.target.closest('.mat-cal__day')) { return; }
    var cur = s.focus, back = (cur.getDay() - s.weekStart + 7) % 7, to, page = 0;
    // a day that cannot be chosen is stepped over
    if (e.key === 'ArrowLeft') { to = calMove(s, cur, -1); }
    else if (e.key === 'ArrowRight') { to = calMove(s, cur, 1); }
    else if (e.key === 'ArrowUp') { to = calMove(s, cur, -7); }
    else if (e.key === 'ArrowDown') { to = calMove(s, cur, 7); }
    else if (e.key === 'Home') { to = calWeekEdge(s, addDays(cur, -back), 1); }
    else if (e.key === 'End') { to = calWeekEdge(s, addDays(cur, 6 - back), -1); }
    else if (e.key === 'PageUp' || e.key === 'PageDown') {
      page = (e.shiftKey ? 12 : 1) * (e.key === 'PageUp' ? -1 : 1);
      to = limitDate(addMonths(cur, page), s.min, s.max);
      if (calOff(s, to)) { to = calNearest(s, to, page); }
    }
    else { return; }
    e.preventDefault();
    if (!to && page) {
      // a month in which nothing can be chosen (yet, when its days are being loaded): it is shown and the focus waits at the title until there is a day
      s.focus = limitDate(addMonths(cur, page), s.min, s.max);
      s.view = firstOf(s.focus);
      s.pendingFocus = true;
      calRender(p, null);
      calFocus(p);
      return;
    }
    if (!to) { return; }
    s.focus = to;
    if (s.mode === 'range' && s.start && !s.end) { s.hover = s.focus; }
    var end = addMonths(s.view, s.months);
    if (s.focus >= s.view && s.focus < new Date(end.getFullYear(), end.getMonth(), 1)) {
      calPaint(p);
      var b = $(CAL_FOCUS, p.body);
      if (b) { b.focus({ preventScroll: true }); }
    } else { calRender(p, CAL_FOCUS); }
  }

  function calHover(p, t) {
    var s = p.state, day = t.closest && t.closest('.mat-cal__day');
    if (!day || s.mode !== 'range' || s.level !== 'days' || !s.start || s.end) { return; }
    var d = dateOf(day.getAttribute('data-date'));
    if (!sameHover(s.hover, d)) { s.hover = d; calPaint(p); }
  }
  function sameHover(a, b) { return !!a && !!b && isoOf(a) === isoOf(b); }

  KINDS.date = {
    icon: 'calendar',
    init: function () { /* the panel is built when it opens */ },
    display: calDisplay,
    open: calOpen,
    click: calClick,
    pick: calPick,
    keys: calKeys,
    hover: calHover,
    ready: function () { return true; },
    value: function () { return null; }
  };
  KINDS.daterange = {
    icon: 'calendar',
    init: function () { /* the panel is built when it opens */ },
    display: calDisplay,
    open: calOpen,
    click: calClick,
    pick: calPick,
    keys: calKeys,
    hover: calHover,
    ready: function (p) { return !!p.state && !!p.state.start && !!p.state.end; },
    value: function (p) { return rangeValue(p.state); }
  };

  // ---- time and time range: columns of hours and minutes, and a field to type in
  function timeNow(p) {
    var d = new Date(), step = p.state.step;
    var m = Math.round(d.getMinutes() / step) * step;
    return { h: (d.getHours() + (m >= 60 ? 1 : 0)) % 24, m: m % 60 };
  }
  function timeCurrent(p) { return p.state.parts[p.state.active]; }
  function timeSetPart(p, field, value) {
    var s = p.state, t = timeCurrent(p) || timeNow(p);
    t = { h: t.h, m: t.m };
    if (field === 'h') { t.h = value; }
    else if (field === 'hd') { t.h = value % 12 + (t.h >= 12 ? 12 : 0); }
    else if (field === 'm') { t.m = value; }
    else if (field === 'ap') { t.h = t.h % 12 + (value === 'pm' ? 12 : 0); }
    s.parts[s.active] = t;
    timePaint(p, false);
  }

  function timeColumn(name, label, items) {
    return '<div class="mat-timecol" role="listbox" aria-label="' + esc(label) + '" data-time-col="' + name + '">' +
      items.map(function (it) { return '<button type="button" class="mat-timecol__item" role="option" tabindex="-1" aria-selected="false" data-v="' + it.v + '">' + esc(it.text) + '</button>'; }).join('') + '</div>';
  }

  function timeDuration(a, b) {
    var diff = (b.h * 60 + b.m) - (a.h * 60 + a.m), next = false;
    if (diff < 0) { diff += 1440; next = true; }
    var h = Math.floor(diff / 60), m = diff % 60;
    var text = h && m ? pkText('durHM', h, m) : h ? pkText('durH', h) : pkText('durM', m);
    return next ? text + ' (' + pkText('nextDay') + ')' : text;
  }

  function timePaint(p, keepEntry, center) {
    var s = p.state, t = timeCurrent(p);
    $$('.mat-timecol', p.body).forEach(function (col) {
      var name = col.getAttribute('data-time-col'), want = null;
      if (t) {
        want = name === 'm' ? String(t.m) : name === 'ap' ? (t.h >= 12 ? 'pm' : 'am') : name === 'hd' ? String(t.h % 12 === 0 ? 12 : t.h % 12) : String(t.h);
      }
      var picked = null, first = null;
      $$('.mat-timecol__item', col).forEach(function (it) {
        if (!first) { first = it; }
        var on = it.getAttribute('data-v') === want;
        it.setAttribute('aria-selected', on ? 'true' : 'false');
        it.tabIndex = -1;
        if (on) { picked = it; }
      });
      var stop = picked || first;
      if (stop) { stop.tabIndex = 0; }
      if (picked) { if (center) { col.scrollTop = picked.offsetTop - (col.clientHeight - picked.offsetHeight) / 2; } else { keepInView(col, picked); } }
    });
    var entry = $('[data-time-entry]', p.body);
    if (entry && !keepEntry) { entry.value = t ? timeText(p, t) : ''; entry.removeAttribute('aria-invalid'); }
    if (s.range) {
      $$('[data-time-part]', p.body).forEach(function (b) {
        var part = b.getAttribute('data-time-part'), on = part === s.active, pt = s.parts[part];
        b.classList.toggle('is-active', on);
        b.setAttribute('aria-pressed', on ? 'true' : 'false');
        $('strong', b).textContent = pt ? timeText(p, pt) : '--:--';
      });
      var status = $('.mat-picker__status', p.body);
      if (status) { status.textContent = s.parts.from && s.parts.to ? timeDuration(s.parts.from, s.parts.to) : ''; }
    }
    refreshFoot(p);
  }

  function timeDisplay(p, out) {
    var parts = p.input.value.split('..'), a = timeOf(parts[0]), b = timeOf(parts[1]);
    if (!p.input.value) { out.textContent = placeholderOf(p); return; }
    if (!a) { out.textContent = p.input.value; return; }
    out.textContent = timeText(p, a) + (p.kind === 'timerange' ? ' – ' + (b ? timeText(p, b) : '…') : '');
  }

  function timeOpen(p) {
    var range = p.kind === 'timerange', parts = p.input.value.split('..');
    p.state = { range: range, active: 'from', step: Math.max(1, parseInt(p.wrap.getAttribute('data-step') || '5', 10) || 5), parts: { from: timeOf(parts[0]), to: range ? timeOf(parts[1]) : null } };
    var s = p.state, hours = [], minutes = [];
    if (p.hour12) { for (var i = 1; i <= 12; i += 1) { hours.push({ v: i, text: p2(i) }); } hours.unshift(hours.pop()); }
    else { for (var j = 0; j < 24; j += 1) { hours.push({ v: j, text: p2(j) }); } }
    for (var m = 0; m < 60; m += s.step) { minutes.push({ v: m, text: p2(m) }); }
    [s.parts.from, s.parts.to].forEach(function (t) {
      if (t && minutes.every(function (x) { return x.v !== t.m; })) { minutes.push({ v: t.m, text: p2(t.m) }); }
    });
    minutes.sort(function (a, b) { return a.v - b.v; });
    var id = nextId('mat-tm');
    p.body.innerHTML = '<div class="mat-time">' +
      (range ? '<div class="mat-btn-group" role="group" aria-label="' + esc(pkText('phTimeRange')) + '">' +
        '<button type="button" class="mat-btn mat-btn--secondary is-active" data-time-part="from" aria-pressed="true">' + esc(pkText('from')) + '&nbsp;<strong></strong></button>' +
        '<button type="button" class="mat-btn mat-btn--secondary" data-time-part="to" aria-pressed="false">' + esc(pkText('to')) + '&nbsp;<strong></strong></button></div>' : '') +
      '<input class="mat-input mat-time__entry" id="' + id + '" inputmode="numeric" autocomplete="off" data-time-entry aria-label="' + esc(pkText('typeTime')) + '" placeholder="' + esc(pkText(p.hour12 ? 'phTimeEntryAmPm' : 'phTimeEntry')) + '">' +
      '<div class="mat-time__cols">' + timeColumn(p.hour12 ? 'hd' : 'h', pkText('hour'), hours) + timeColumn('m', pkText('minute'), minutes) +
      (p.hour12 ? timeColumn('ap', pkText('ampm'), [{ v: 'am', text: pkText('am') }, { v: 'pm', text: pkText('pm') }]) : '') + '</div>' +
      (range ? '<p class="mat-picker__status" aria-live="polite"></p>' : '') +
      '<div class="mat-cal__tools"><button type="button" class="mat-btn mat-btn--ghost mat-btn--sm" data-time-now>' + esc(pkText('now')) + '</button></div></div>';
    setFoot(p, 'apply');
    timePaint(p, false, false);
    // typing is quicker on a wide screen; a phone would show its keyboard, so the focus goes to the chosen hour there
    return p.full ? $('.mat-timecol__item[tabindex="0"]', p.body) : $('[data-time-entry]', p.body);
  }

  KINDS.time = KINDS.timerange = {
    icon: 'clock',
    init: function (p) {
      var attr = p.wrap.getAttribute('data-hour12');
      p.hour12 = attr ? attr === 'true' : !!new Intl.DateTimeFormat(pkLocale(), { hour: 'numeric' }).resolvedOptions().hour12;
    },
    display: timeDisplay,
    open: timeOpen,
    shown: function (p) { timePaint(p, true, true); },
    ready: function (p) { var s = p.state; return !!s && !!s.parts.from && (!s.range || !!s.parts.to); },
    value: function (p) { var s = p.state; return s.range ? timeStr(s.parts.from) + '..' + timeStr(s.parts.to) : timeStr(s.parts.from); },
    click: function (p, t) {
      var s = p.state, item = t.closest('.mat-timecol__item');
      if (item) {
        var name = item.parentNode.getAttribute('data-time-col'), v = item.getAttribute('data-v');
        timeSetPart(p, name, name === 'ap' ? v : +v);
        item.focus({ preventScroll: true });
        return;
      }
      var part = t.closest('[data-time-part]');
      if (part) { s.active = part.getAttribute('data-time-part'); timePaint(p, false); return; }
      if (t.closest('[data-time-now]')) {
        s.parts[s.active] = timeNow(p);
        timePaint(p, false);
        var picked = $('.mat-timecol__item[tabindex="0"]', p.body);
        if (picked) { picked.focus({ preventScroll: true }); }
      }
    },
    input: function (p, t) {
      if (!t.hasAttribute('data-time-entry')) { return; }
      var parsed = typedTime(t.value);
      if (t.value.trim() === '') { t.removeAttribute('aria-invalid'); return; }
      if (!parsed) { t.setAttribute('aria-invalid', 'true'); return; }
      t.removeAttribute('aria-invalid');
      p.state.parts[p.state.active] = parsed;
      timePaint(p, true);
    },
    keys: function (p, e) {
      var t = e.target;
      if (t.hasAttribute && t.hasAttribute('data-time-entry')) {
        if (e.key === 'Enter') {
          e.preventDefault();
          var parsed = typedTime(t.value);
          // text that is not a time is not taken: the field says so and the panel stays
          if (t.value.trim() !== '' && !parsed) { t.setAttribute('aria-invalid', 'true'); return; }
          if (parsed) { p.state.parts[p.state.active] = parsed; }
          timePaint(p, false);
          if (p.state.range && p.state.active === 'from' && !p.state.parts.to) { p.state.active = 'to'; timePaint(p, false); return; }
          panelAction(p, 'apply');
        }
        return;
      }
      var item = t.closest('.mat-timecol__item');
      if (!item) { return; }
      var items = $$('.mat-timecol__item', item.parentNode), i = items.indexOf(item), to = null;
      if (e.key === 'ArrowDown') { to = items[Math.min(items.length - 1, i + 1)]; }
      else if (e.key === 'ArrowUp') { to = items[Math.max(0, i - 1)]; }
      else if (e.key === 'Home') { to = items[0]; }
      else if (e.key === 'End') { to = items[items.length - 1]; }
      else if (e.key === 'PageDown') { to = items[Math.min(items.length - 1, i + 6)]; }
      else if (e.key === 'PageUp') { to = items[Math.max(0, i - 6)]; }
      else { return; }
      e.preventDefault();
      // the choice follows the focus, like in a native select
      p.spec.click(p, to);
    }
  };

  // ---- colour: swatches, and the free choice with an area, a hue and a hex value
  function swatchList(p) {
    var raw = p.wrap.getAttribute('data-swatches');
    if (raw === null) { return PK_SWATCHES.map(function (h) { return { hex: h, name: h.toUpperCase() }; }); }
    return raw.split(',').map(function (x) {
      var cut = x.indexOf(':'), hex = normHex(cut < 0 ? x : x.slice(0, cut));
      return hex ? { hex: hex, name: cut < 0 ? hex.toUpperCase() : x.slice(cut + 1).trim() } : null;
    }).filter(Boolean);
  }

  function colourFromHsv(p) {
    p.state.hex = hsvToHex(p.state.h, p.state.s, p.state.v);
    colourPaint(p, false);
  }

  function colourPaint(p, keepHex) {
    var s = p.state, hex = s.hex, on = textOn(hex), box = $('.mat-colour', p.body);
    box.style.setProperty('--mat-hue', hsvToHex(s.h, 1, 1));
    box.style.setProperty('--mat-x', (s.s * 100) + '%');
    box.style.setProperty('--mat-y', ((1 - s.v) * 100) + '%');
    box.style.setProperty('--mat-swatch', hex);
    box.style.setProperty('--mat-swatch-on', on.color);
    var area = $('.mat-colour__area', box);
    area.setAttribute('aria-valuenow', String(Math.round(s.s * 100)));
    area.setAttribute('aria-valuetext', pkText('areaValue', Math.round(s.s * 100), Math.round(s.v * 100)));
    $('.mat-colour__hue', box).value = String(Math.round(s.h));
    var field = $('.mat-colour__hex', box);
    if (!keepHex) { field.value = hex; field.removeAttribute('aria-invalid'); }
    $('.mat-colour__contrast span:last-child', box).textContent = pkText(on.name) + ': ' + on.ratio.toFixed(1) + ' : 1 · ' + pkText(levelOf(on.ratio));
    $$('.mat-colour__swatch', box).forEach(function (b) { b.setAttribute('aria-pressed', b.getAttribute('data-hex') === hex ? 'true' : 'false'); });
  }

  function colourSet(p, hex, keepHex) {
    var h = hexToHsv(hex);
    if (h.s !== 0 && h.v !== 0) { p.state.h = h.h; }
    p.state.s = h.s;
    p.state.v = h.v;
    p.state.hex = hex;
    colourPaint(p, keepHex);
  }

  KINDS.colour = {
    icon: 'palette',
    init: function () { /* the panel is built when it opens */ },
    display: function (p, out) {
      var hex = normHex(p.input.value);
      if (!p.input.value) { out.textContent = placeholderOf(p); return; }
      if (!hex) { out.textContent = p.input.value; return; }
      var chip = doc.createElement('span');
      chip.className = 'mat-picker__swatch';
      chip.style.setProperty('--mat-swatch', hex);
      var text = doc.createElement('span');
      text.className = 'mat-mono';
      text.textContent = hex.toUpperCase();
      out.appendChild(chip);
      out.appendChild(text);
    },
    open: function (p) {
      var hex = normHex(p.input.value) || '#2563eb', start = hexToHsv(hex);
      p.state = { h: start.h, s: start.s, v: start.v, hex: hex };
      var swatches = swatchList(p);
      p.body.innerHTML = '<div class="mat-colour">' +
        (swatches.length ? '<div class="mat-colour__swatches" role="group" aria-label="' + esc(pkText('colours')) + '">' + swatches.map(function (sw) {
          return '<button type="button" class="mat-colour__swatch" tabindex="-1" aria-pressed="false" aria-label="' + esc(sw.name) + '" data-hex="' + sw.hex + '">' + icon('check') + '</button>';
        }).join('') + '</div>' : '') +
        '<div class="mat-colour__area" tabindex="0" role="slider" aria-label="' + esc(pkText('area')) + '" aria-valuemin="0" aria-valuemax="100"><span class="mat-colour__thumb"></span></div>' +
        '<input type="range" class="mat-colour__hue" min="0" max="360" step="1" aria-label="' + esc(pkText('hue')) + '">' +
        '<div class="mat-colour__row"><span class="mat-colour__preview"></span><input class="mat-input mat-colour__hex" maxlength="7" autocomplete="off" spellcheck="false" aria-label="' + esc(pkText('hex')) + '"></div>' +
        '<p class="mat-colour__contrast"><span class="mat-colour__sample" aria-hidden="true">Aa</span><span></span></p></div>';
      $$('.mat-colour__swatch', p.body).forEach(function (b) {
        var hx = b.getAttribute('data-hex');
        b.style.setProperty('--mat-swatch', hx);
        b.style.setProperty('--mat-swatch-on', textOn(hx).color);
      });
      setFoot(p, 'apply');
      colourPaint(p, false);
      var first = $('.mat-colour__swatch[aria-pressed="true"]', p.body) || $('.mat-colour__swatch', p.body);
      if (first) { first.tabIndex = 0; }
      return first || $('.mat-colour__area', p.body);
    },
    ready: function (p) { return !!p.state; },
    value: function (p) { return p.state.hex; },
    click: function (p, t) {
      var sw = t.closest('.mat-colour__swatch');
      if (sw) { setPickerValue(p, sw.getAttribute('data-hex')); closePicker(true); }
    },
    input: function (p, t) {
      if (t.classList.contains('mat-colour__hue')) { p.state.h = +t.value; colourFromHsv(p); }
      else if (t.classList.contains('mat-colour__hex')) {
        var hex = normHex(t.value);
        if (hex && (t.value.replace('#', '').length === 6)) { t.removeAttribute('aria-invalid'); colourSet(p, hex, true); }
        else { t.setAttribute('aria-invalid', t.value === '' ? 'false' : 'true'); }
      }
    },
    keys: function (p, e) {
      var t = e.target, s = p.state;
      if (t.classList.contains('mat-colour__hex')) {
        if (e.key === 'Enter') { e.preventDefault(); var hex = normHex(t.value); if (hex) { colourSet(p, hex, false); panelAction(p, 'apply'); } }
        return;
      }
      if (t.classList.contains('mat-colour__area')) {
        var step = e.shiftKey ? 0.1 : 0.01;
        if (e.key === 'ArrowLeft') { s.s = clamp(s.s - step, 0, 1); }
        else if (e.key === 'ArrowRight') { s.s = clamp(s.s + step, 0, 1); }
        else if (e.key === 'ArrowUp') { s.v = clamp(s.v + step, 0, 1); }
        else if (e.key === 'ArrowDown') { s.v = clamp(s.v - step, 0, 1); }
        else { return; }
        e.preventDefault();
        colourFromHsv(p);
        return;
      }
      var sw = t.closest('.mat-colour__swatch');
      if (!sw) { return; }
      var all = $$('.mat-colour__swatch', p.body), i = all.indexOf(sw), per = all.filter(function (b) { return b.offsetTop === all[0].offsetTop; }).length || 1, to = null;
      if (e.key === 'ArrowRight') { to = all[Math.min(all.length - 1, i + 1)]; }
      else if (e.key === 'ArrowLeft') { to = all[Math.max(0, i - 1)]; }
      else if (e.key === 'ArrowDown') { to = all[Math.min(all.length - 1, i + per)]; }
      else if (e.key === 'ArrowUp') { to = all[Math.max(0, i - per)]; }
      else if (e.key === 'Home') { to = all[0]; }
      else if (e.key === 'End') { to = all[all.length - 1]; }
      else { return; }
      e.preventDefault();
      all.forEach(function (b) { b.tabIndex = -1; });
      to.tabIndex = 0;
      to.focus({ preventScroll: true });
    }
  };

  // ---- calendar: a month grid that is always shown (FORM-18)
  // <div class="mat-calendar" data-calendar data-mode="single|range|multiple"><input class="mat-calendar__input" id="..." name="..." value="..."></div>
  // The real input holds the value in the fixed formats of the pickers: 2026-10-10, a period 2026-10-10..2026-10-14 (empty until it has both ends) or several
  // days 2026-10-10,2026-10-12; it gets input and change events like any field. The script draws the grid (the one of the date picker) into the element.
  //   data-min / data-max      the first and the last day that can be chosen: a date, today, tomorrow, +2d, -1w, +3m, +1y
  //   data-disabled            days that cannot be chosen: "2026-10-14,2026-10-20..2026-10-24"; a period may not run across them
  //   data-disabled-weekdays   "0,6": no Sundays and Saturdays (0 is Sunday); a period may run across them
  //   data-events              {"2026-10-14": [{"title": "Team lunch", "time": "12:30", "end": "13:30"}, "A day with a title only"]} or [{"date": ..., "title": ...}]:
  //                            a dot under the day for each event (up to three); data-agenda lists the events of the chosen day under the calendar
  //   data-week-numbers, data-week-start (0 Sunday, default 1), data-months (1 or 2), data-max-dates (multiple), data-clearable
  // The title of the month is a button: it leads to a choice of the month and then of the year. MatUi.calendar.setEvents / setDisabled / setValue / go change it later.
  var CAL_TEXT = {
    en: {
      phSlot: 'Choose an appointment', jumpMonth: 'choose month and year', chooseMonth: 'Choose a month', chooseYear: 'Choose a year',
      prevYear: 'Previous year', nextYear: 'Next year', prevYears: 'Earlier years', nextYears: 'Later years',
      week: 'Week', weekShort: 'Wk', weekNo: 'Week {0}',
      chooseDates: 'Choose one or more days', oneDate: '1 day chosen', datesCount: '{0} days chosen', maxDates: 'At most {0} days', unavailable: 'unavailable',
      eventOne: '1 event', eventCount: '{0} events', noEvents: 'Nothing on this day', agendaChoose: 'Choose a day to see what is on it', allDay: 'All day',
      nextFree: 'Next free appointment', slotsChoose: 'Choose a day to see the free appointments', slotsLoading: 'Loading the free appointments …',
      slotsFailed: 'The appointments could not be loaded.', retry: 'Try again', slotTaken: 'Taken', slotsNone: 'No free appointment on this day',
      slotLen: 'Appointments of {0}', slotNoneFound: 'No free appointment found', slotSeeking: 'Looking for the next free appointment …', slotSummaryNone: 'Choose a day and a time',
      morning: 'Morning', afternoon: 'Afternoon', evening: 'Evening', freeOne: '1 free appointment', freeCount: '{0} free appointments', noFree: 'no free appointment'
    },
    de: {
      phSlot: 'Termin wählen', jumpMonth: 'Monat und Jahr wählen', chooseMonth: 'Monat wählen', chooseYear: 'Jahr wählen',
      prevYear: 'Vorheriges Jahr', nextYear: 'Nächstes Jahr', prevYears: 'Frühere Jahre', nextYears: 'Spätere Jahre',
      week: 'Woche', weekShort: 'KW', weekNo: 'Kalenderwoche {0}',
      chooseDates: 'Einen oder mehrere Tage wählen', oneDate: '1 Tag gewählt', datesCount: '{0} Tage gewählt', maxDates: 'Höchstens {0} Tage', unavailable: 'nicht verfügbar',
      eventOne: '1 Termin', eventCount: '{0} Termine', noEvents: 'An diesem Tag steht nichts an', agendaChoose: 'Tag wählen, um zu sehen, was ansteht', allDay: 'Ganztägig',
      nextFree: 'Nächster freier Termin', slotsChoose: 'Tag wählen, um die freien Termine zu sehen', slotsLoading: 'Freie Termine werden geladen …',
      slotsFailed: 'Die Termine konnten nicht geladen werden.', retry: 'Erneut versuchen', slotTaken: 'Belegt', slotsNone: 'An diesem Tag ist kein Termin frei',
      slotLen: 'Termine à {0}', slotNoneFound: 'Kein freier Termin gefunden', slotSeeking: 'Der nächste freie Termin wird gesucht …', slotSummaryNone: 'Tag und Uhrzeit wählen',
      morning: 'Vormittag', afternoon: 'Nachmittag', evening: 'Abend', freeOne: '1 freier Termin', freeCount: '{0} freie Termine', noFree: 'kein freier Termin'
    }
  };

  function setCalValue(c, v) {
    if (c.input.value === v) { return; }
    c.input.value = v;
    c.input.dispatchEvent(new Event('input', { bubbles: true }));
    c.input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  // {"2026-10-14": [...]} or [{"date": "2026-10-14", ...}], as an object or as JSON text -> {iso: [{title, time, end}]}, in the order of the time (a day long event first)
  function calEventMap(src) {
    var data = src, map = {};
    if (typeof src === 'string') { try { data = JSON.parse(src); } catch (err) { data = null; } }
    function add(iso, item) {
      var ev = typeof item === 'string' ? { title: item } : item;
      if (!dateOf(iso) || !ev || !ev.title) { return; }
      (map[iso] = map[iso] || []).push({ title: String(ev.title), time: ev.time ? String(ev.time) : '', end: ev.end ? String(ev.end) : '' });
    }
    if (Array.isArray(data)) { data.forEach(function (ev) { if (ev && ev.date) { add(ev.date, ev); } }); }
    else if (data && typeof data === 'object') { Object.keys(data).forEach(function (iso) { [].concat(data[iso]).forEach(function (item) { add(iso, item); }); }); }
    Object.keys(map).forEach(function (iso) { map[iso].sort(function (a, b) { return a.time < b.time ? -1 : a.time > b.time ? 1 : 0; }); });
    return map;
  }

  // what the attributes say about the days that cannot be chosen (read again when MatUi.calendar.setDisabled changes them)
  function calcLimits(c) {
    var el = c.el, s = c.state, off = dateSet(el.getAttribute('data-disabled')), week = {};
    (el.getAttribute('data-disabled-weekdays') || '').split(',').forEach(function (x) { var n = parseInt(x, 10); if (n >= 0 && n <= 6) { week[n] = true; } });
    s.min = dateExpr(el.getAttribute('data-min'));
    s.max = dateExpr(el.getAttribute('data-max'));
    s.off = function (d) { return !!off[isoOf(d)] || !!week[d.getDay()]; };
    s.blocked = off;
  }

  // the value of the real input into the state
  function calcRead(c) {
    var s = c.state, raw = (c.input.value || '').trim(), parts;
    s.start = null; s.end = null; s.dates = [];
    if (s.mode === 'multiple') {
      s.dates = raw.split(',').map(function (x) { return x.trim(); }).filter(function (x, i, all) { return !!dateOf(x) && all.indexOf(x) === i; }).sort();
    } else if (s.mode === 'range') {
      parts = raw.split('..');
      s.start = dateOf(parts[0]); s.end = dateOf(parts[1]);
      if (!s.end) { s.start = null; }
    } else { s.start = dateOf(raw); }
  }
  function calcValue(s) {
    if (s.mode === 'multiple') { return s.dates.join(','); }
    if (s.mode === 'range') { return s.start && s.end ? isoOf(s.start) + '..' + isoOf(s.end) : ''; }
    return s.start ? isoOf(s.start) : '';
  }

  function calcEventTime(ev) {
    if (!ev.time) { return pkText('allDay'); }
    var a = timeOf(ev.time), b = ev.end ? timeOf(ev.end) : null, h12 = new Intl.DateTimeFormat(pkLocale(), { hour: 'numeric' }).resolvedOptions().hour12;
    if (!a) { return ev.time; }
    return timeText({ hour12: h12 }, a) + (b ? ' – ' + timeText({ hour12: h12 }, b) : '');
  }

  // the events of the chosen day (the last day that was clicked, for a period or several days)
  function calcAgenda(c) {
    var box = $('.mat-calendar__agenda', c.el), iso = c.state.agendaDate;
    if (!box) { return; }
    var d = iso ? dateOf(iso) : null, list = d ? c.events[iso] || [] : [];
    var h = '<p class="mat-calendar__agenda-title">' + esc(d ? formats().full.format(d) : pkText('agendaChoose')) + '</p>';
    if (d && list.length) {
      h += '<ul class="mat-calendar__events">' + list.map(function (ev) {
        return '<li class="mat-calendar__event"><span class="mat-calendar__time">' + esc(calcEventTime(ev)) + '</span><span class="mat-calendar__what">' + esc(ev.title) + '</span></li>';
      }).join('') + '</ul>';
    } else if (d) { h += '<p class="mat-calendar__none">' + esc(pkText('noEvents')) + '</p>'; }
    box.innerHTML = h;
  }

  // The state is drawn again, with the month in view where it was (after the events or the limits changed), or from the value: the month of the chosen day, or of today.
  function calcRefresh(c, fromValue) {
    var s = c.state;
    if (fromValue) {
      var start = s.mode === 'multiple' ? dateOf(s.dates[s.dates.length - 1]) : s.start;
      var want = limitDate(start || dayStart(new Date()), s.min, s.max);
      s.focus = calNearest(s, want, 1) || want;
      s.view = firstOf(s.focus);
      s.level = 'days';
    }
    calRender(c, null);
    calcAgenda(c);
  }

  var CALCTL = { pick: calcPick };

  function calcPick(c, d) {
    var s = c.state, ok = calSelect(s, d);
    s.agendaDate = isoOf(d);
    calPaint(c);
    if (ok) { setCalValue(c, calcValue(s)); }
    calcAgenda(c);
  }

  function calInit(el) {
    if (el._matCalendar) { return; }
    var input = $('.mat-calendar__input', el);
    if (!input) { return; }
    var mode = el.getAttribute('data-mode') === 'range' ? 'range' : el.getAttribute('data-mode') === 'multiple' ? 'multiple' : 'single';
    var c = { el: el, input: input, kind: 'calendar', spec: CALCTL, body: el, events: calEventMap(el.getAttribute('data-events')) };
    c.state = {
      mode: mode, level: 'days', jump: true, hover: null, message: '', start: null, end: null, dates: [], agendaDate: '',
      months: el.getAttribute('data-months') === '2' ? 2 : 1,
      weekStart: parseInt(el.getAttribute('data-week-start') || root.getAttribute('data-week-start') || '1', 10) % 7,
      weekNumbers: el.hasAttribute('data-week-numbers'),
      maxDates: Math.max(0, parseInt(el.getAttribute('data-max-dates') || '0', 10) || 0)
    };
    var s = c.state;
    s.marks = function (iso) { return (c.events[iso] || []).length; };
    s.describe = function (iso, d, off) {
      var n = (c.events[iso] || []).length, parts = [];
      if (n) { parts.push(n === 1 ? pkText('eventOne') : pkText('eventCount', n)); }
      if (off) { parts.push(pkText('unavailable')); }
      return parts.join(', ');
    };
    el._matCalendar = c;
    if (!input.id) { input.id = nextId('mat-cal-in'); }
    input.tabIndex = -1;
    input.setAttribute('aria-hidden', 'true');
    input.setAttribute('autocomplete', 'off');
    if (!el.hasAttribute('aria-label') && !el.hasAttribute('aria-labelledby')) { el.setAttribute('aria-label', labelOf(input) || input.name || pkText('phDate')); }
    el.setAttribute('role', 'group');
    el.insertAdjacentHTML('beforeend', '<div class="mat-cal"><div class="mat-cal__months"></div>' +
      (mode === 'single' ? '' : '<p class="mat-picker__status" role="status"></p>') +
      '<div class="mat-cal__tools"><button type="button" class="mat-btn mat-btn--ghost mat-btn--sm" data-cal-today>' + esc(pkText('today')) + '</button>' +
      (el.hasAttribute('data-clearable') ? '<button type="button" class="mat-btn mat-btn--ghost mat-btn--sm" data-cal-clear>' + esc(pkText('clear')) + '</button>' : '') + '</div></div>' +
      (el.hasAttribute('data-agenda') ? '<div class="mat-calendar__agenda" aria-live="polite"></div>' : ''));
    calcLimits(c);
    calcSync(c);
  }

  // The state from the value of the real input (at the start, after a reset or when a script changed it).
  function calcSync(c) {
    var s = c.state;
    calcRead(c);
    s.agendaDate = s.mode === 'multiple' ? s.dates[s.dates.length - 1] || '' : s.start ? isoOf(s.start) : '';
    s.message = '';
    calcRefresh(c, true);
  }

  function initCalendars(scope) { $$('[data-calendar]', scope).forEach(calInit); }

  function calendarOf(ref) {
    var el = typeof ref === 'string' ? $(ref) : ref;
    el = el && el.closest ? el.closest('.mat-calendar') : null;
    return el && el._matCalendar;
  }

  var CALENDAR_API = {
    init: initCalendars,
    // the events of the days: {"2026-10-14": [{"title": "...", "time": "09:30"}]}, or an array of {"date": ..., "title": ...}
    setEvents: function (ref, events) { var c = calendarOf(ref); if (c) { c.events = calEventMap(events); calcRefresh(c); } },
    // {dates: "2026-10-14,2026-10-20..2026-10-24" (or an array), weekdays: [0, 6], min: "today", max: "+90d"}: what is given replaces what was there
    setDisabled: function (ref, o) {
      var c = calendarOf(ref);
      if (!c || !o) { return; }
      var names = { dates: 'data-disabled', weekdays: 'data-disabled-weekdays', min: 'data-min', max: 'data-max' };
      Object.keys(names).forEach(function (k) {
        if (o[k] === undefined) { return; }
        var v = Array.isArray(o[k]) ? o[k].join(',') : o[k];
        if (v === null || v === '') { c.el.removeAttribute(names[k]); } else { c.el.setAttribute(names[k], String(v)); }
      });
      calcLimits(c);
      calcRefresh(c);
    },
    // like assigning the value of the input, without the events
    setValue: function (ref, value) { var c = calendarOf(ref); if (c) { c.input.value = value || ''; calcSync(c); } },
    // shows the month of a date (an ISO date or a Date)
    go: function (ref, date) {
      var c = calendarOf(ref), d = date instanceof Date ? dayStart(date) : dateOf(date);
      if (!c || !d) { return; }
      var s = c.state;
      s.focus = limitDate(d, s.min, s.max);
      s.view = firstOf(s.focus);
      s.level = 'days';
      calRender(c, null);
    }
  };

  doc.addEventListener('click', function (e) {
    var t = e.target, c = calendarOf(t.closest ? t.closest('.mat-calendar') : null);
    if (!c) { return; }
    if (t.closest('[data-cal-clear]')) {
      var s = c.state;
      s.start = null; s.end = null; s.dates = []; s.agendaDate = ''; s.message = '';
      calPaint(c);
      setCalValue(c, '');
      calcAgenda(c);
      return;
    }
    calClick(c, t, e);
  });
  doc.addEventListener('mouseover', function (e) {
    var c = calendarOf(e.target.closest ? e.target.closest('.mat-calendar') : null);
    if (c) { calHover(c, e.target); }
  });
  doc.addEventListener('keydown', function (e) {
    var c = calendarOf(e.target.closest ? e.target.closest('.mat-calendar') : null);
    if (!c || e.ctrlKey || e.metaKey || e.altKey) { return; }
    // Escape in the choice of a month or a year goes back to the days
    if (e.key === 'Escape' && c.state.level !== 'days') {
      e.preventDefault();
      e.stopPropagation();
      c.state.level = 'days';
      calRender(c, CAL_FOCUS);
      return;
    }
    calKeys(c, e);
  });
  // the real input is not drawn; whoever focuses it (a label, a validation message) lands on the day the keyboard is on
  doc.addEventListener('focusin', function (e) {
    var t = e.target;
    if (t.classList && t.classList.contains('mat-calendar__input')) {
      var c = calendarOf(t);
      if (c) { calFocus(c); }
    }
  });
  doc.addEventListener('reset', function (e) {
    setTimeout(function () { $$('.mat-calendar', e.target).forEach(function (el) { if (el._matCalendar) { calcSync(el._matCalendar); } }); }, 0);
  }, true);

  // ---- slot: a day and a time to book (FORM-18)
  // <div class="mat-picker" data-picker="slot" data-min="today" data-duration="15"><input class="mat-picker__input" id="..." name="..."></div> posts 2026-10-14T09:30
  // (with data-duration the end too: 2026-10-14T09:30..09:45). The panel shows a month in which the days with a free time are marked, then the times of the chosen day.
  // The availability is data: data-slots='{"2026-10-14": ["09:00", "09:15"], "2026-10-15": {"free": ["09:00"], "taken": ["09:15"]}}' (free times; with "taken" the
  // taken ones are shown too), or, without data-slots (or with data-lazy), it is asked for per month: when a month is shown that is not known the wrapper fires
  // mat:slots-needed (bubbles; detail: {month: "2026-10", from: "2026-10-01", to: "2026-10-31", picker: the wrapper}) and the page answers with
  // MatUi.picker.setSlots(detail.picker, data, detail.month), or MatUi.picker.slotsFailed(detail.picker, detail.month). What starts before now plus data-min-notice
  // ("2h", "30m", "1d") is never offered, and neither is a day before data-min or after data-max.
  function slotValue(raw) {
    var m = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?:\.\.(\d{2}:\d{2}))?$/.exec((raw || '').trim());
    var date = m && dateOf(m[1]), from = m && timeOf(m[2]);
    return date && from ? { date: date, from: from, to: m[3] ? timeOf(m[3]) : null } : null;
  }
  function slotEnd(from, minutes) {
    var total = from.h * 60 + from.m + minutes;
    return { h: Math.floor(total / 60) % 24, m: total % 60 };
  }
  // "2h", "30m", "1d" -> minutes
  function slotNotice(text) {
    var m = /^(\d+)\s*(m|min|h|d)?$/i.exec((text || '').trim());
    return m ? +m[1] * { m: 1, min: 1, h: 60, d: 1440 }[(m[2] || 'm').toLowerCase()] : 0;
  }
  function slotTimes(list) {
    var seen = {};
    return (Array.isArray(list) ? list : []).map(function (x) {
      var t = timeOf(typeof x === 'string' ? x : x && x.time);
      return t ? timeStr(t) : null;
    }).filter(function (t) { return t && !seen[t] && (seen[t] = true); }).sort();
  }
  function slotDay(entry) {
    var taken = entry && !Array.isArray(entry) && typeof entry === 'object' ? slotTimes(entry.taken) : [];
    var free = slotTimes(Array.isArray(entry) ? entry : entry && entry.free).filter(function (t) { return taken.indexOf(t) < 0; });
    return { free: free, taken: taken };
  }
  // the data of some days into the picker; the months of the days, and the month that was asked for, are known from now on
  function slotMerge(p, data, month) {
    var sl = p.slots;
    if (typeof data === 'string') { try { data = JSON.parse(data); } catch (err) { data = null; } }
    Object.keys(data && typeof data === 'object' ? data : {}).forEach(function (iso) {
      if (!dateOf(iso)) { return; }
      sl.days[iso] = slotDay(data[iso]);
      sl.loaded[iso.slice(0, 7)] = true;
    });
    if (month) { sl.loaded[month] = true; }
    Object.keys(sl.loaded).forEach(function (key) { delete sl.pending[key]; delete sl.failed[key]; });
  }
  // 'loaded' (also for every month when the data came with the page), 'pending', 'failed' or 'unknown'
  function slotMonth(p, key) {
    var sl = p.slots;
    return !p.lazy || sl.loaded[key] ? 'loaded' : sl.pending[key] ? 'pending' : sl.failed[key] ? 'failed' : 'unknown';
  }
  // the free and the taken times of a day that are still to come (now plus the notice), for as long as the panel is open as they were when it opened
  function slotLists(p, iso) {
    var s = p.state, cache = s && s.cache;
    if (cache && cache[iso]) { return cache[iso]; }
    var day = p.slots.days[iso], d = dateOf(iso), out = { free: [], taken: [] };
    if (day && d) {
      var earliest = (s ? s.now : Date.now()) + p.notice * 60000;
      var ahead = function (t) { var x = timeOf(t); return new Date(d.getFullYear(), d.getMonth(), d.getDate(), x.h, x.m).getTime() >= earliest; };
      out.free = day.free.filter(ahead);
      out.taken = day.taken.filter(ahead);
    }
    if (cache) { cache[iso] = out; }
    return out;
  }

  function slotText(p, v, full) {
    var day = full ? formats().full : new Intl.DateTimeFormat(pkLocale(), { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
    return day.format(v.date) + ' · ' + timeText(p, v.from) + (v.to ? ' – ' + timeText(p, v.to) : '');
  }
  function slotDisplay(p, out) {
    if (!p.input.value) { out.textContent = placeholderOf(p); return; }
    var v = slotValue(p.input.value);
    out.textContent = v ? slotText(p, v, false) : p.input.value;
  }

  function slotRequest(p, key) {
    var sl = p.slots, y = +key.slice(0, 4), m = +key.slice(5, 7);
    sl.pending[key] = true;
    delete sl.failed[key];
    p.wrap.dispatchEvent(new CustomEvent('mat:slots-needed', { bubbles: true, detail: { month: key, from: key + '-01', to: key + '-' + p2(new Date(y, m, 0).getDate()), picker: p.wrap } }));
  }

  // under the month: that the days are loading, that they could not be loaded (with a button to try again), that the next free appointment is looked for
  function slotPaintStatus(p) {
    var s = p.state, box = $('.mat-slots', p.body), el = $('.mat-slots__status', p.body);
    if (!box || !el) { return; }
    var st = slotMonth(p, isoOf(s.view).slice(0, 7)), html = '';
    if (p.seek) { html = '<span class="mat-spinner mat-spinner--sm" aria-hidden="true"></span>' + esc(pkText('slotSeeking')); }
    else if (st === 'pending') { html = '<span class="mat-spinner mat-spinner--sm" aria-hidden="true"></span>' + esc(pkText('slotsLoading')); }
    else if (st === 'failed') { html = esc(pkText('slotsFailed')) + '<button type="button" class="mat-btn mat-btn--ghost mat-btn--sm" data-slots-retry>' + esc(pkText('retry')) + '</button>'; }
    else if (s.note) { html = esc(s.note); }
    box.setAttribute('aria-busy', p.seek || st === 'pending' ? 'true' : 'false');
    el.innerHTML = html;
  }

  function slotPart(t) { var h = timeOf(t).h; return h < 12 ? 'morning' : h < 17 ? 'afternoon' : 'evening'; }

  // the times of the chosen day: a button for each, the free ones to choose and the taken ones (data-unavailable="mark") disabled and saying so
  function slotRenderDay(p) {
    var s = p.state, box = $('.mat-slots__scroll', p.body);
    if (!box) { return; }
    var had = doc.activeElement && doc.activeElement.closest ? doc.activeElement.closest('.mat-slot') : null, keep = had ? had.getAttribute('data-time') : null;
    if (!s.start) { box.innerHTML = '<p class="mat-slots__empty">' + esc(pkText('slotsChoose')) + '</p>'; return; }
    var iso = isoOf(s.start), lists = slotLists(p, iso), state = slotMonth(p, iso.slice(0, 7));
    var heading = formats().full.format(s.start);
    var h = '<p class="mat-slots__heading" id="' + p.panel.id + '-day">' + esc(heading) + '</p>';
    if (p.duration) {
      var hours = Math.floor(p.duration / 60), minutes = p.duration % 60;
      h += '<p class="mat-slots__hint">' + esc(pkText('slotLen', hours && minutes ? pkText('durHM', hours, minutes) : hours ? pkText('durH', hours) : pkText('durM', minutes))) + '</p>';
    }
    var items = lists.free.map(function (t) { return { t: t, taken: false }; });
    if (p.unavail !== 'hide') { items = items.concat(lists.taken.map(function (t) { return { t: t, taken: true }; })); }
    items.sort(function (a, b) { return a.t < b.t ? -1 : a.t > b.t ? 1 : 0; });
    if (state !== 'loaded') { h += '<p class="mat-slots__empty"><span class="mat-spinner mat-spinner--sm" aria-hidden="true"></span>' + esc(pkText('slotsLoading')) + '</p>'; }
    else {
      // a time that was chosen before and is not free any more is not a choice
      if (s.slot && lists.free.indexOf(s.slot) < 0) { s.slot = null; }
      if (!lists.free.length) { h += '<p class="mat-slots__empty">' + esc(pkText('slotsNone')) + '</p>'; }
      var button = function (it) {
        return '<button type="button" class="mat-slot" data-time="' + it.t + '" aria-pressed="' + (it.t === s.slot && !it.taken ? 'true' : 'false') + '" tabindex="-1"' + (it.taken ? ' disabled' : '') + '>' +
          '<span class="mat-slot__time">' + esc(timeText(p, timeOf(it.t))) + '</span>' + (it.taken ? '<span class="mat-slot__note">' + esc(pkText('slotTaken')) + '</span>' : '') + '</button>';
      };
      if (p.group) {
        ['morning', 'afternoon', 'evening'].forEach(function (part) {
          var some = items.filter(function (it) { return slotPart(it.t) === part; });
          if (!some.length) { return; }
          h += '<div class="mat-slots__group" role="group" aria-labelledby="' + p.panel.id + '-' + part + '"><p class="mat-slots__group-title" id="' + p.panel.id + '-' + part + '">' + esc(pkText(part)) + '</p>' +
            '<div class="mat-slots__list">' + some.map(button).join('') + '</div></div>';
        });
      } else if (items.length) { h += '<div class="mat-slots__list" role="group" aria-labelledby="' + p.panel.id + '-day">' + items.map(button).join('') + '</div>'; }
    }
    box.innerHTML = h;
    var buttons = $$('.mat-slot:not(:disabled)', box), stop = buttons.filter(function (b) { return b.getAttribute('aria-pressed') === 'true'; })[0] || buttons[0];
    if (stop) { stop.tabIndex = 0; }
    var back = keep && $('.mat-slot[data-time="' + keep + '"]:not(:disabled)', box);
    if (back) { back.focus({ preventScroll: true }); }
  }

  function slotPaintChoice(p) {
    var s = p.state, chosen = null;
    $$('.mat-slot', p.body).forEach(function (b) {
      var on = !b.disabled && b.getAttribute('data-time') === s.slot;
      b.setAttribute('aria-pressed', on ? 'true' : 'false');
      if (on) { chosen = b; }
    });
    if (chosen) { $$('.mat-slot', p.body).forEach(function (b) { b.tabIndex = b === chosen ? 0 : -1; }); }
    var el = $('.mat-slots__summary', p.foot), ok = !!s.start && !!s.slot;
    el.classList.toggle('is-empty', !ok);
    el.textContent = ok ? slotText(p, { date: s.start, from: timeOf(s.slot), to: p.duration ? slotEnd(timeOf(s.slot), p.duration) : null }, true) : pkText('slotSummaryNone');
    refreshFoot(p);
  }

  // a day was chosen: its times replace the ones that were there (the time is kept only on the same day)
  function slotPickDay(p, d, e) {
    var s = p.state, same = !!s.start && isoOf(s.start) === isoOf(d);
    s.start = d; s.focus = d; s.note = '';
    if (!same) { s.slot = null; }
    calPaint(p);
    slotRenderDay(p);
    slotPaintStatus(p);
    slotPaintChoice(p);
    var first = $('.mat-slot[tabindex="0"]', p.body);
    // from the keyboard (a click that is made by Enter has no detail) the focus goes on to the times
    if (e && e.detail === 0 && first) { first.focus({ preventScroll: true }); }
    else if (p.full) {
      // on a phone the times are below the month: they are brought into view when they start in the lower half of it
      var box = $('.mat-slots__day', p.body), r = box.getBoundingClientRect(), b = p.body.getBoundingClientRect();
      if (r.top > b.top + b.height * 0.55) { p.body.scrollTop += r.top - b.top - 16; }
    }
  }

  function slotChoose(p, time) {
    p.state.slot = time;
    slotPaintChoice(p);
  }

  // the day and the time of the shortcut: shown, chosen and focused
  function slotChoosePair(p, d, time) {
    var s = p.state;
    s.start = d; s.focus = d; s.view = firstOf(d); s.slot = time; s.level = 'days'; s.note = '';
    calRender(p, null);
    slotRenderDay(p);
    slotPaintStatus(p);
    slotPaintChoice(p);
    var b = $('.mat-slot[aria-pressed="true"]', p.body);
    if (b) {
      b.focus({ preventScroll: true });
      keepInView($('.mat-slots__scroll', p.body), b);
      keepInView(p.body, b);
    }
  }

  // The first day with a free time, from the first day that may be booked: in the months that are known, and for the months that are not (data-lazy) it asks for the
  // next one and goes on when the answer is there.
  function slotSeek(p) {
    var s = p.state, sk = p.seek, d = sk.from;
    for (var i = 0; d <= sk.limit && i < 800; i += 1, d = addDays(d, 1)) {
      var iso = isoOf(d), key = iso.slice(0, 7), st = slotMonth(p, key);
      if (st === 'unknown') { sk.from = d; slotRequest(p, key); slotPaintStatus(p); return; }
      if (st === 'pending') { sk.from = d; slotPaintStatus(p); return; }
      if (st === 'failed') {
        p.seek = null;
        s.view = firstOf(d);
        calRender(p, null);
        return;
      }
      var free = slotLists(p, iso).free;
      if (free.length) { p.seek = null; slotChoosePair(p, d, free[0]); return; }
    }
    p.seek = null;
    s.note = pkText('slotNoneFound');
    slotPaintStatus(p);
  }

  // new availability while the panel is open: the days, the times and what waits for them
  function slotRefresh(p) {
    var s = p.state, active = doc.activeElement;
    s.cache = {};
    if (active && active.closest && active.closest('.mat-cal__months') && p.panel.contains(active)) { s.pendingFocus = true; }
    calRender(p, null);
    slotRenderDay(p);
    slotPaintChoice(p);
    if (p.seek) { slotSeek(p); }
  }

  function setSlots(ref, data, month) {
    var wrap = typeof ref === 'string' ? $(ref) : ref;
    var p = wrap && wrap.closest && wrap.closest('.mat-picker') ? wrap.closest('.mat-picker')._matPicker : null;
    if (!p || p.kind !== 'slot') { return; }
    slotMerge(p, data, month);
    if (pickerOpen === p && p.state) { slotRefresh(p); }
  }
  function slotsFailed(ref, month) {
    var wrap = typeof ref === 'string' ? $(ref) : ref;
    var p = wrap && wrap.closest && wrap.closest('.mat-picker') ? wrap.closest('.mat-picker')._matPicker : null;
    if (!p || p.kind !== 'slot' || !month) { return; }
    delete p.slots.pending[month];
    p.slots.failed[month] = true;
    if (pickerOpen === p && p.state) { p.seek = null; slotPaintStatus(p); calRender(p, null); }
  }

  function slotViewed(p) {
    var key = isoOf(p.state.view).slice(0, 7);
    if (p.lazy && slotMonth(p, key) === 'unknown') { slotRequest(p, key); }
    slotPaintStatus(p);
  }

  // the times are a grid of buttons: the arrow keys go to the next one in a row or in a column (the geometry says which)
  function slotKeys(p, e) {
    var b = e.target.closest('.mat-slot');
    if (!b) { return false; }
    var list = $$('.mat-slot:not(:disabled)', p.body), i = list.indexOf(b), to = null;
    if (e.key === 'ArrowRight') { to = list[i + 1]; }
    else if (e.key === 'ArrowLeft') { to = list[i - 1]; }
    else if (e.key === 'Home') { to = list[0]; }
    else if (e.key === 'End') { to = list[list.length - 1]; }
    else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      var r = b.getBoundingClientRect(), best = Infinity, dir = e.key === 'ArrowDown' ? 1 : -1;
      list.forEach(function (x) {
        var q = x.getBoundingClientRect(), dy = dir * (q.top - r.top);
        if (x === b || dy < r.height / 2) { return; }
        var score = dy * 1000 + Math.abs(q.left - r.left);
        if (score < best) { best = score; to = x; }
      });
    } else { return false; }
    e.preventDefault();
    if (to) {
      list.forEach(function (x) { x.tabIndex = -1; });
      to.tabIndex = 0;
      to.focus({ preventScroll: true });
    }
    return true;
  }

  KINDS.slot = {
    icon: 'calendar',
    init: function (p) {
      var attr = p.wrap.getAttribute('data-hour12');
      p.hour12 = attr ? attr === 'true' : !!new Intl.DateTimeFormat(pkLocale(), { hour: 'numeric' }).resolvedOptions().hour12;
      p.notice = 0; p.duration = 0; p.unavail = 'mark'; p.group = false; p.seek = null;
      p.slots = { days: {}, loaded: {}, pending: {}, failed: {} };
      var raw = p.wrap.getAttribute('data-slots');
      p.lazy = raw === null || p.wrap.hasAttribute('data-lazy');
      if (raw) { slotMerge(p, raw, null); }
    },
    display: slotDisplay,
    open: function (p) {
      var w = p.wrap, v = slotValue(p.input.value), today = dayStart(new Date());
      p.notice = slotNotice(w.getAttribute('data-min-notice'));
      p.duration = Math.max(0, parseInt(w.getAttribute('data-duration') || '0', 10) || 0);
      p.unavail = w.getAttribute('data-unavailable') === 'hide' ? 'hide' : 'mark';
      p.group = w.hasAttribute('data-group');
      p.seek = null;
      // a time that has gone is never offered, so the first day is today at the earliest
      var min = dateExpr(w.getAttribute('data-min')), max = dateExpr(w.getAttribute('data-max'));
      if (!min || min < today) { min = today; }
      var focus = limitDate(v ? v.date : today, min, max);
      var s = p.state = {
        mode: 'single', level: 'days', jump: true, months: 1, hover: null, message: '', note: '', start: v ? v.date : null, end: null, dates: [],
        slot: v ? timeStr(v.from) : null, focus: focus, view: firstOf(focus), min: min, max: max, now: Date.now(), cache: {},
        weekStart: parseInt(w.getAttribute('data-week-start') || root.getAttribute('data-week-start') || '1', 10) % 7
      };
      s.off = function (d) { return slotLists(p, isoOf(d)).free.length === 0; };
      // a day that is not free is struck through, or absent; a day that is only outside the limits, or not known yet, is just dimmed
      s.look = function (d) {
        if (slotMonth(p, isoOf(d).slice(0, 7)) !== 'loaded') { return ''; }
        return p.unavail === 'hide' ? 'absent' : (s.min && d < s.min) || (s.max && d > s.max) ? '' : 'unavailable';
      };
      s.marks = function (iso) { return slotLists(p, iso).free.length ? 1 : 0; };
      s.describe = function (iso) {
        var n = slotLists(p, iso).free.length;
        return n ? (n === 1 ? pkText('freeOne') : pkText('freeCount', n)) : slotMonth(p, iso.slice(0, 7)) === 'loaded' ? pkText('noFree') : '';
      };
      p.panel.classList.add('mat-picker__panel--slots');
      p.body.innerHTML = '<div class="mat-slots" aria-busy="false"><div class="mat-slots__cal"><div class="mat-cal"><div class="mat-cal__months"></div>' +
        '<div class="mat-cal__tools"><button type="button" class="mat-btn mat-btn--secondary mat-btn--sm" data-slots-next>' + icon('arrow-right') + '<span>' + esc(pkText('nextFree')) + '</span></button></div>' +
        '<p class="mat-picker__status mat-slots__status" role="status"></p></div></div>' +
        '<div class="mat-slots__day"><div class="mat-slots__scroll"></div></div></div>';
      setFoot(p, 'apply');
      p.foot.insertAdjacentHTML('afterbegin', '<p class="mat-slots__summary" role="status"></p>');
      calRender(p, null);
      slotRenderDay(p);
      slotPaintChoice(p);
      return $(CAL_FOCUS, p.body) || $('[data-slots-next]', p.body);
    },
    // the panel is there: the chosen time is brought into view
    shown: function (p) {
      var b = $('.mat-slot[aria-pressed="true"]', p.body);
      if (b) { keepInView($('.mat-slots__scroll', p.body), b); keepInView(p.body, b); }
    },
    viewed: slotViewed,
    pick: slotPickDay,
    click: function (p, t, e) {
      var slot = t.closest('.mat-slot');
      if (slot) { if (!slot.disabled) { slotChoose(p, slot.getAttribute('data-time')); } return; }
      if (t.closest('[data-slots-next]')) {
        var s = p.state;
        p.seek = { from: s.min, limit: s.max || addMonths(s.min, 12) };
        s.note = '';
        slotSeek(p);
        return;
      }
      var retry = t.closest('[data-slots-retry]');
      if (retry) { slotRequest(p, isoOf(p.state.view).slice(0, 7)); slotPaintStatus(p); calRender(p, null); return; }
      calClick(p, t, e);
    },
    keys: function (p, e) { if (!slotKeys(p, e)) { calKeys(p, e); } },
    ready: function (p) { return !!p.state && !!p.state.start && !!p.state.slot; },
    value: function (p) {
      var s = p.state;
      return isoOf(s.start) + 'T' + s.slot + (p.duration ? '..' + timeStr(slotEnd(timeOf(s.slot), p.duration)) : '');
    }
  };

  // ---- tags: a field of chips that the user types into
  // The real input holds the tags joined by data-sep (default ", "). The script draws a field (.mat-tagfield) with a chip per tag and a text field to type in.
  // Enter or a comma adds what is typed, Backspace in the empty field removes the last chip, text that is pasted is split at commas and line breaks, and what is
  // typed when the field is left becomes a tag, so that it is not lost when the form is sent. A tag that is there is not added again (data-duplicates allows it),
  // data-max limits the number of tags and data-max-length the length of one. The options of the wrapper (.mat-picker__options, as in a select) are suggestions
  // for the text field; with data-strict only they can be added. There is no panel to open: the suggestions are a list under the field.
  var tagsOpen = null;

  function tagSep(p) { return p.sep.trim() || p.sep; }
  function tagList(p) { return p.input.value.split(tagSep(p)).map(function (s) { return s.trim(); }).filter(Boolean); }
  // a comma or a line break always starts the next tag; the separator of the value can not be in a tag
  function tagSplit(p, text) {
    return text.split(/[\r\n\t,]+/).reduce(function (all, part) { return all.concat(part.split(tagSep(p))); }, []);
  }
  function tagClean(p, text) { return text.replace(/\s+/g, ' ').trim().slice(0, p.maxLength).trim(); }
  function tagOptions(p) { return p.list ? $$('.mat-picker__option', p.list) : []; }
  function tagOptionValue(o) { return o.getAttribute('data-value') || o.textContent.trim(); }
  function tagSuggestion(p, text) {
    var t = text.toLowerCase();
    return tagOptions(p).filter(function (o) { return tagOptionValue(o).toLowerCase() === t; })[0] || null;
  }

  // what is said under the field, and to a screen reader
  function tagsStatus(p, text) {
    clearTimeout(p.statusTimer);
    p.status.textContent = text || '';
    p.status.hidden = !text;
    p.live.textContent = text || '';
    if (text) { p.statusTimer = setTimeout(function () { tagsStatus(p, ''); }, 4000); }
  }

  // The chips and the state of the field, drawn from the value of the real input (also after a reset, a script or a form that fills it).
  function tagsSync(p) {
    var vals = tagList(p), off = p.input.disabled, ro = p.input.readOnly, locked = off || ro;
    $$('.mat-chip', p.field).forEach(function (c) { c.remove(); });
    vals.forEach(function (v) {
      var chip = doc.createElement('span');
      chip.className = 'mat-chip';
      chip.setAttribute('data-tag', v);
      var text = doc.createElement('span');
      text.className = 'mat-chip__text';
      text.textContent = v;
      chip.appendChild(text);
      if (!locked) {
        var x = doc.createElement('button');
        x.type = 'button';
        x.className = 'mat-chip__x';
        x.setAttribute('aria-label', pkText('removeTag', v));
        x.innerHTML = icon('x');
        chip.appendChild(x);
      }
      p.field.insertBefore(chip, p.typing);
    });
    p.field.classList.toggle('is-disabled', off);
    p.field.classList.toggle('is-readonly', ro);
    p.field.classList.toggle('is-full', p.max > 0 && vals.length >= p.max);
    p.typing.disabled = off;
    p.typing.hidden = ro;
    p.typing.placeholder = vals.length === 0 && !locked ? placeholderOf(p) : '';
    if (locked) { tagsClose(p); }
  }

  function tagsSet(p, list) {
    var v = list.join(p.sep);
    if (p.input.value === v) { tagsSync(p); return; }
    p.input.value = v;
    tagsSync(p);
    p.input.dispatchEvent(new Event('input', { bubbles: true }));
    p.input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  // Adds what the text holds and returns the parts that were refused, so that the caller leaves them in the text field for the user to correct;
  // what was refused is said under the field.
  function tagsAdd(p, text) {
    var vals = tagList(p), refused = [], message = '', added = 0;
    tagSplit(p, text).forEach(function (raw) {
      var t = tagClean(p, raw);
      if (!t) { return; }
      var known = tagSuggestion(p, t);
      if (p.strict && !known) { message = pkText('tagStrict'); refused.push(t); return; }
      if (known) { t = tagOptionValue(known); }
      if (p.unique && vals.some(function (x) { return x.toLowerCase() === t.toLowerCase(); })) { message = pkText('tagDuplicate', t); refused.push(t); return; }
      if (p.max > 0 && vals.length >= p.max) { message = pkText('tagMax', p.max); refused.push(t); return; }
      vals.push(t);
      added += 1;
    });
    if (added) { tagsSet(p, vals); }
    tagsStatus(p, message);
    return refused;
  }

  // "typing": the focus goes to the text field, "x": to the next remove button (the keyboard stays where it was)
  function tagsRemove(p, tag, focus) {
    var vals = tagList(p), i = vals.indexOf(tag);
    if (i < 0) { return; }
    vals.splice(i, 1);
    tagsSet(p, vals);
    tagsStatus(p, pkText('tagRemoved', tag));
    var xs = $$('.mat-chip__x', p.field);
    (focus === 'x' && xs.length ? xs[Math.min(i, xs.length - 1)] : p.typing).focus({ preventScroll: true });
    if (p.suggest && !p.suggest.hidden) { tagsSuggest(p); }
  }

  // ---- the suggestions
  function tagsFilter(p) {
    var q = p.typing.value.trim().toLowerCase();
    var taken = tagList(p).map(function (s) { return s.toLowerCase(); }), shown = 0;
    tagOptions(p).forEach(function (o) {
      var v = tagOptionValue(o).toLowerCase();
      var hit = taken.indexOf(v) < 0 && (!q || (o.textContent + ' ' + v + ' ' + (o.getAttribute('data-keywords') || '')).toLowerCase().indexOf(q) >= 0);
      o.hidden = !hit;
      if (hit) { shown += 1; }
    });
    p.empty.hidden = !(p.strict && q && shown === 0);
    return shown;
  }
  function tagsVisible(p) { return $$('.mat-picker__option:not([hidden])', p.list); }
  function tagsActive(p, o) {
    $$('.mat-picker__option.is-active', p.list).forEach(function (x) { x.classList.remove('is-active'); x.removeAttribute('aria-selected'); });
    if (!o) { p.typing.removeAttribute('aria-activedescendant'); return; }
    o.classList.add('is-active');
    o.setAttribute('aria-selected', 'true');
    p.typing.setAttribute('aria-activedescendant', o.id);
    keepInView(p.list, o);
  }
  function tagsPlace(p) {
    var r = p.field.getBoundingClientRect(), panel = p.suggest, vw = window.innerWidth, vh = window.innerHeight;
    panel.style.left = '0px'; panel.style.top = '0px'; panel.style.maxHeight = '';
    panel.style.width = Math.min(r.width, vw - 16) + 'px';
    var h = panel.offsetHeight, below = vh - r.bottom - 10, above = r.top - 10, top;
    if (h <= below) { top = r.bottom + 4; }
    else if (h <= above) { top = r.top - h - 4; }
    else if (below >= above) { top = r.bottom + 4; panel.style.maxHeight = Math.max(120, below) + 'px'; }
    else { var room = Math.max(120, above); panel.style.maxHeight = room + 'px'; top = r.top - Math.min(h, room) - 4; }
    panel.style.left = Math.max(8, Math.min(r.left, vw - panel.offsetWidth - 8)) + 'px';
    panel.style.top = Math.max(8, top) + 'px';
  }
  function tagsClose(p) {
    if (!p.suggest) { return; }
    p.suggest.hidden = true;
    p.typing.setAttribute('aria-expanded', 'false');
    p.typing.removeAttribute('aria-activedescendant');
    if (tagsOpen === p) { tagsOpen = null; }
  }
  // (Re)draws the list for what is typed and opens it when there is something to show. In a strict field the first match is the one Enter takes.
  function tagsSuggest(p) {
    if (!p.suggest) { return; }
    if (p.typing.disabled || p.input.readOnly) { tagsClose(p); return; }
    var shown = tagsFilter(p);
    if (shown === 0 && p.empty.hidden) { tagsClose(p); return; }
    tagsActive(p, p.strict && p.typing.value.trim() && shown ? tagsVisible(p)[0] : null);
    p.suggest.hidden = false;
    p.typing.setAttribute('aria-expanded', 'true');
    tagsOpen = p;
    tagsPlace(p);
  }

  function tagsKeys(p, e) {
    if (e.ctrlKey || e.metaKey || e.altKey || e.isComposing) { return; }
    var t = p.typing, sep = tagSep(p), isOpen = !!p.suggest && !p.suggest.hidden;
    if (e.key === 'Enter') {
      var act = isOpen ? $('.mat-picker__option.is-active', p.list) : null;
      if (act) { e.preventDefault(); tagsAdd(p, tagOptionValue(act)); t.value = ''; tagsSuggest(p); }
      else if (t.value.trim()) { e.preventDefault(); t.value = tagsAdd(p, t.value).join(', '); tagsSuggest(p); }
      return;
    }
    if (e.key === ',' || (sep.length === 1 && e.key === sep)) {
      e.preventDefault();
      if (t.value.trim()) { t.value = tagsAdd(p, t.value).join(', '); }
      tagsSuggest(p);
      return;
    }
    if (e.key === 'Backspace' && t.value === '') {
      var vals = tagList(p);
      if (vals.length) { e.preventDefault(); tagsRemove(p, vals[vals.length - 1], 'typing'); }
      return;
    }
    if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && p.suggest) {
      e.preventDefault();
      if (!isOpen) { tagsSuggest(p); }
      var vis = tagsVisible(p), n = vis.length, cur = vis.indexOf($('.mat-picker__option.is-active', p.list));
      if (p.suggest.hidden || !n) { return; }
      tagsActive(p, vis[e.key === 'ArrowDown' ? (cur < 0 ? 0 : (cur + 1) % n) : (cur < 0 ? n - 1 : (cur - 1 + n) % n)]);
      return;
    }
    if (e.key === 'Escape' && isOpen) { e.preventDefault(); e.stopPropagation(); tagsClose(p); return; }
    if (e.key === 'ArrowLeft' && t.selectionStart === 0 && t.selectionEnd === 0) {
      var xs = $$('.mat-chip__x', p.field);
      if (xs.length) { e.preventDefault(); xs[xs.length - 1].focus(); }
    }
  }

  KINDS.tags = {
    inline: true,
    init: function (p) {
      var wrap = p.wrap, input = p.input, id = input.id;
      p.sep = wrap.hasAttribute('data-sep') ? wrap.getAttribute('data-sep') : ', ';
      p.max = parseInt(wrap.getAttribute('data-max'), 10) || 0;
      p.maxLength = parseInt(wrap.getAttribute('data-max-length'), 10) || 48;
      p.unique = !wrap.hasAttribute('data-duplicates');
      p.strict = wrap.hasAttribute('data-strict');

      p.field = doc.createElement('div');
      p.field.className = 'mat-input mat-tagfield';
      p.typing = doc.createElement('input');
      p.typing.type = 'text';
      p.typing.className = 'mat-tagfield__input';
      p.typing.id = id + '-entry';
      p.typing.setAttribute('autocomplete', 'off');
      p.typing.setAttribute('autocapitalize', 'off');
      p.typing.setAttribute('spellcheck', 'false');
      p.typing.setAttribute('enterkeyhint', 'done');
      var lab = $('label[for="' + id + '"]');
      if (lab) { if (!lab.id) { lab.id = id + '-label'; } p.typing.setAttribute('aria-labelledby', lab.id); }
      else { p.typing.setAttribute('aria-label', placeholderOf(p)); }
      if (input.getAttribute('aria-describedby')) { p.typing.setAttribute('aria-describedby', input.getAttribute('aria-describedby')); }
      p.field.appendChild(p.typing);
      input.parentNode.insertBefore(p.field, input.nextSibling);

      p.status = doc.createElement('p');
      p.status.className = 'mat-picker__status';
      p.status.setAttribute('aria-hidden', 'true');
      p.status.hidden = true;
      p.live = doc.createElement('span');
      p.live.className = 'mat-sr-only';
      p.live.setAttribute('role', 'status');
      p.live.setAttribute('aria-live', 'polite');
      p.field.parentNode.insertBefore(p.status, p.field.nextSibling);
      p.field.parentNode.insertBefore(p.live, p.status);

      p.list = $('.mat-picker__options', wrap);
      if (p.list) {
        p.list.id = p.list.id || id + '-list';
        p.list.setAttribute('role', 'listbox');
        p.list.setAttribute('aria-label', pkText('suggestions'));
        p.list.hidden = false;
        $$('.mat-picker__option', p.list).forEach(function (o, i) {
          o.type = 'button';
          o.setAttribute('role', 'option');
          o.tabIndex = -1;
          o.id = id + '-option-' + i;
        });
        p.empty = doc.createElement('p');
        p.empty.className = 'mat-picker__empty';
        p.empty.setAttribute('role', 'status');
        p.empty.hidden = true;
        p.empty.textContent = pkText('noResults');
        p.suggest = doc.createElement('div');
        p.suggest.className = 'mat-picker__panel mat-picker__panel--list mat-picker__panel--suggest';
        p.suggest.hidden = true;
        var body = doc.createElement('div');
        body.className = 'mat-picker__body';
        body.appendChild(p.list);
        body.appendChild(p.empty);
        p.suggest.appendChild(body);
        wrap.appendChild(p.suggest);
        p.typing.setAttribute('role', 'combobox');
        p.typing.setAttribute('aria-autocomplete', 'list');
        p.typing.setAttribute('aria-haspopup', 'listbox');
        p.typing.setAttribute('aria-expanded', 'false');
        p.typing.setAttribute('aria-controls', p.list.id);
        // a click on a suggestion must not take the focus from the text field
        p.suggest.addEventListener('mousedown', function (e) { e.preventDefault(); });
        p.suggest.addEventListener('click', function (e) {
          var o = e.target.closest('.mat-picker__option');
          if (!o) { return; }
          tagsAdd(p, tagOptionValue(o));
          p.typing.value = '';
          p.typing.focus({ preventScroll: true });
          tagsSuggest(p);
        });
      }

      p.field.addEventListener('click', function (e) {
        var x = e.target.closest('.mat-chip__x');
        if (x) { tagsRemove(p, x.closest('.mat-chip').getAttribute('data-tag'), e.detail === 0 ? 'x' : 'typing'); return; }
        if (p.typing.disabled || p.input.readOnly) { return; }
        p.typing.focus();
        tagsSuggest(p);
      });
      p.field.addEventListener('keydown', function (e) {
        var x = e.target.closest && e.target.closest('.mat-chip__x');
        if (!x) { return; }
        var xs = $$('.mat-chip__x', p.field), i = xs.indexOf(x);
        if (e.key === 'ArrowLeft') { e.preventDefault(); if (i > 0) { xs[i - 1].focus(); } }
        else if (e.key === 'ArrowRight') { e.preventDefault(); if (i < xs.length - 1) { xs[i + 1].focus(); } else { p.typing.focus(); } }
        else if (e.key === 'Backspace' || e.key === 'Delete') { e.preventDefault(); tagsRemove(p, x.closest('.mat-chip').getAttribute('data-tag'), 'x'); }
      });
      p.typing.addEventListener('keydown', function (e) { tagsKeys(p, e); });
      p.typing.addEventListener('input', function () { tagsStatus(p, ''); tagsSuggest(p); });
      p.typing.addEventListener('paste', function (e) {
        var text = (e.clipboardData || window.clipboardData).getData('text');
        if (!/[\r\n\t,]/.test(text) && text.indexOf(tagSep(p)) < 0) { return; }   // plain text: the field takes it as it is
        e.preventDefault();
        var all = p.typing.value.slice(0, p.typing.selectionStart) + text + p.typing.value.slice(p.typing.selectionEnd);
        p.typing.value = tagsAdd(p, all).join(', ');
        tagsSuggest(p);
      });
      wrap.addEventListener('focusout', function (e) {
        if (e.relatedTarget && wrap.contains(e.relatedTarget)) { return; }
        if (!e.relatedTarget && !doc.hasFocus()) { return; }   // the window lost the focus, not the field
        if (p.typing.value.trim()) { tagsAdd(p, p.typing.value); }
        p.typing.value = '';
        tagsClose(p);
      });
    },
    sync: tagsSync
  };

  // ------------------------------------------------------------------------------------------------ code input (FORM-19, matui.forms.css)
  // A code that is read from somewhere else (an authenticator or SMS code, a PIN, a recovery code) is typed into one box per character:
  //   <div class="mat-otp" data-otp data-length="6" data-chars="digits"><input class="mat-otp__input" id="code" name="code" autocomplete="one-time-code"></div>
  // The real input (.mat-otp__input) holds the characters joined and is what is validated, posted and reset; without the script it is a plain text field. The script hides it
  // and draws the boxes: a group named by the label, each box named "Digit 3 of 6". An incomplete code is not valid (the message of data-msg-pattern, data-msg-incomplete or the
  // text of the toolkit), so required, pattern and data-msg-* of the real input work as they do for any field. Options of the wrapper: data-length (3 to 12, default 6),
  // data-chars (digits, letters, alphanumeric, hex or a class such as [A-Z2-7]; a character that is not allowed is refused with a message under the boxes), data-case
  // (upper or lower: what is typed is converted), data-mask (the boxes show dots), data-group ("3": a gap after every 3 boxes; "4-4": groups of 4 with a dash between)
  // and data-submit (the form is submitted with requestSubmit() when the code is complete, so validation and data-confirm still run; data-submit="#id" clicks that button).
  // Keys: a character fills the box and moves on; Backspace clears the box (in an empty one it goes back and clears the one before); Delete clears; the arrow keys,
  // Home and End move; a click selects the box; a pasted code ("123 456", "A1B2-C3D4") is spread over the boxes and spaces and dashes are ignored. The boxes are ONE tab stop
  // (roving tabindex, like the buttons of a toolbar): Tab enters on the box where the code goes on, the arrow keys walk inside, Tab leaves. The first box has the
  // autocomplete of the real input (an SMS code is offered there and spread over the boxes), the real input is hidden from the Tab order and a screen reader.
  // The wrapper fires mat:otp-complete (bubbles, cancelable; detail.value) when the code becomes complete. API: MatUi.otp.clear / focus / setInvalid / value / init.
  var OTP_TEXT = {
    en: {
      boxDigit: 'Digit {0} of {1}', boxChar: 'Character {0} of {1}', code: 'Code',
      onlyDigits: 'Only digits are allowed.', onlyLetters: 'Only letters are allowed.', onlyAlnum: 'Only letters and digits are allowed.',
      onlyHex: 'Only the digits 0 to 9 and the letters A to F are allowed.', onlyChars: 'That character is not allowed.',
      incomplete: 'Please enter all {0} characters.', complete: 'Code complete'
    },
    de: {
      boxDigit: 'Ziffer {0} von {1}', boxChar: 'Zeichen {0} von {1}', code: 'Code',
      onlyDigits: 'Es sind nur Ziffern erlaubt.', onlyLetters: 'Es sind nur Buchstaben erlaubt.', onlyAlnum: 'Es sind nur Buchstaben und Ziffern erlaubt.',
      onlyHex: 'Es sind nur die Ziffern 0 bis 9 und die Buchstaben A bis F erlaubt.', onlyChars: 'Dieses Zeichen ist nicht erlaubt.',
      incomplete: 'Bitte alle {0} Zeichen eingeben.', complete: 'Code vollständig'
    }
  };
  var OTP_KINDS = {
    digits: { re: /^[0-9]$/, mode: 'numeric', name: 'boxDigit', only: 'onlyDigits' },
    letters: { re: /^[A-Za-z]$/, mode: 'text', name: 'boxChar', only: 'onlyLetters' },
    alphanumeric: { re: /^[A-Za-z0-9]$/, mode: 'text', name: 'boxChar', only: 'onlyAlnum' },
    hex: { re: /^[0-9A-Fa-f]$/, mode: 'text', name: 'boxChar', only: 'onlyHex' }
  };
  // what may stand between the characters of a code that is pasted or typed: spaces and dashes (they are skipped, not refused, unless the code allows them)
  var OTP_SEPARATOR = /^[\s\-_‐-―−]$/;

  function otpText(key, a, b) {
    var table = OTP_TEXT[(root.lang || 'en').slice(0, 2).toLowerCase()] || OTP_TEXT.en;
    var s = root.getAttribute('data-otp-' + key.toLowerCase()) || table[key] || OTP_TEXT.en[key] || key;
    return s.replace('{0}', function () { return a === undefined ? '' : a; }).replace('{1}', function () { return b === undefined ? '' : b; });
  }

  // data-chars: one of the names, or a class of characters such as [A-Z2-7] (base32); anything else, or a class that is not valid, is digits
  function otpKind(spec) {
    var key = (spec || 'digits').trim();
    if (Object.prototype.hasOwnProperty.call(OTP_KINDS, key)) { return OTP_KINDS[key]; }
    if (/^\[.+\]$/.test(key)) {
      try {
        var numeric = /^\[[0-9-]+\]$/.test(key);
        return { re: new RegExp('^' + key + '$'), mode: numeric ? 'numeric' : 'text', name: numeric ? 'boxDigit' : 'boxChar', only: 'onlyChars' };
      } catch (err) { /* not a valid class: digits */ }
    }
    return OTP_KINDS.digits;
  }

  // data-group: "3" is a group of 3 again and again, "4-4" or "3 3 2" are the sizes of the groups; the boxes in front of which a separator is drawn
  function otpBreaks(spec, len) {
    var sizes = (spec || '').split(/[^0-9]+/).filter(Boolean).map(Number).filter(function (n) { return n > 0; });
    var breaks = [], at = 0;
    if (sizes.length === 1) { for (at = sizes[0]; at < len; at += sizes[0]) { breaks.push(at); } }
    else { sizes.forEach(function (n) { at += n; if (at < len) { breaks.push(at); } }); }
    return breaks;
  }

  // the character as it is stored, or '' when it is not allowed (data-case converts first, so a lower-case letter is fine for [A-Z2-7] with data-case="upper")
  function otpNorm(o, ch) {
    var c = o.upper ? ch.toUpperCase() : o.lower ? ch.toLowerCase() : ch;
    if (c.length !== ch.length) { c = ch; }
    return o.kind.re.test(c) ? c : '';
  }
  function otpClean(o, text) {
    return Array.from(text || '').map(function (ch) { return otpNorm(o, ch); }).filter(Boolean).slice(0, o.len);
  }
  function otpJoined(o) { return o.boxes.map(function (b) { return b.value; }).join(''); }
  function otpCount(o) { return o.boxes.filter(function (b) { return b.value !== ''; }).length; }
  // where the code goes on: the first empty box, or the last one when every box is filled
  function otpHome(o) {
    for (var i = 0; i < o.boxes.length; i += 1) { if (o.boxes[i].value === '') { return i; } }
    return o.boxes.length - 1;
  }
  function otpRove(o, i) {
    o.cur = i;
    o.boxes.forEach(function (b, k) { b.tabIndex = k === i ? 0 : -1; });
  }
  function otpFocus(o, i, scroll) {
    var b = o.boxes[i];
    if (!b || b.disabled) { return; }
    b.focus({ preventScroll: !scroll });
    b.select();
  }

  // what is said to the user: a short line under the boxes for what was refused, and to a screen reader (a live region) also that the code is complete
  function otpSay(o, text, visible) {
    clearTimeout(o.timer);
    o.live.textContent = text || '';
    o.status.textContent = visible && text ? text : '';
    o.status.hidden = !(visible && text);
    if (text) { o.timer = setTimeout(function () { otpSay(o, ''); }, 4000); }
  }

  // an incomplete code is not valid: what the real input reports is what the form validation shows (a required and empty field says so first)
  function otpValidity(o) {
    var n = otpCount(o);
    o.input.setCustomValidity(n > 0 && n < o.len ? (o.input.getAttribute('data-msg-incomplete') || otpText('incomplete', o.len)) : '');
  }

  // the state of the real input is the one of the boxes: disabled, read-only, required, and the red mark that showError() puts on it
  function otpState(o) {
    var invalid = o.input.getAttribute('aria-invalid') === 'true';
    o.boxes.forEach(function (b) {
      b.disabled = o.input.disabled;
      b.readOnly = o.input.readOnly;
      if (o.input.required) { b.setAttribute('aria-required', 'true'); } else { b.removeAttribute('aria-required'); }
      if (invalid) { b.setAttribute('aria-invalid', 'true'); } else { b.removeAttribute('aria-invalid'); }
    });
  }

  // The boxes changed: the real input gets the joined code (and says so with input and change, as a field does), and when a person typed the last
  // character the code is complete.
  function otpCommit(o, byUser) {
    var v = otpJoined(o), changed = o.input.value !== v, full = otpCount(o) === o.len;
    o.prev = o.boxes.map(function (b) { return b.value; });
    o.input.value = v;
    otpValidity(o);
    o.wrap.classList.toggle('is-complete', full);
    if (changed) {
      o.busy = true;
      try {
        o.input.dispatchEvent(new Event('input', { bubbles: true }));
        o.input.dispatchEvent(new Event('change', { bubbles: true }));
      } finally { o.busy = false; }
    }
    if (full && changed && byUser) { otpDone(o, v); }
  }

  function otpDone(o, value) {
    otpSay(o, otpText('complete'), false);
    var go = o.wrap.dispatchEvent(new CustomEvent('mat:otp-complete', { bubbles: true, cancelable: true, detail: { value: value } }));
    // the last character stays in sight for a moment, and a code that was changed in the meantime is not sent
    if (go && o.wrap.hasAttribute('data-submit')) { setTimeout(function () { otpSubmit(o); }, 150); }
  }
  function otpSubmit(o) {
    if (!o.wrap.isConnected || o.input.disabled || otpCount(o) !== o.len) { return; }
    var spec = o.wrap.getAttribute('data-submit') || '';
    var button = spec.charAt(0) === '#' ? doc.getElementById(spec.slice(1)) : null;
    if (button) { if (!button.disabled) { button.click(); } return; }
    if (o.input.form && typeof o.input.form.requestSubmit === 'function') { o.input.form.requestSubmit(); }
  }

  // The characters of a text go into the boxes from box `start` on. A text as long as the code, or too long to fit behind `start`, starts at the first box (a code
  // that is pasted into the middle still fills the whole code). Characters that are not allowed are refused with a message; spaces and dashes are skipped.
  // The focus goes behind the last box that was filled.
  function otpFill(o, text, start) {
    if (o.input.disabled || o.input.readOnly) { return; }
    var chars = [], refused = false;
    Array.from(text || '').forEach(function (raw) {
      var c = otpNorm(o, raw);
      if (c) { chars.push(c); } else if (!OTP_SEPARATOR.test(raw)) { refused = true; }
    });
    otpSay(o, refused ? (o.wrap.getAttribute('data-msg-chars') || otpText(o.kind.only)) : '', true);
    if (!chars.length) { return; }
    if (chars.length >= o.len || start + chars.length > o.len) { start = 0; chars = chars.slice(0, o.len); }
    chars.forEach(function (c, k) { o.boxes[start + k].value = c; });
    otpCommit(o, true);
    otpFocus(o, Math.min(start + chars.length, o.len - 1));
  }

  function otpKeys(o, e, i) {
    // Ctrl+V and the like belong to the browser; an input method (keyCode 229) is handled when its text arrives
    if (e.ctrlKey || e.metaKey || e.altKey || e.isComposing || e.keyCode === 229) { return; }
    var box = o.boxes[i], k = e.key, locked = o.input.disabled || o.input.readOnly;
    if (k === 'ArrowLeft') { e.preventDefault(); otpFocus(o, Math.max(0, i - 1)); }
    else if (k === 'ArrowRight') { e.preventDefault(); otpFocus(o, Math.min(o.len - 1, i + 1)); }
    else if (k === 'Home') { e.preventDefault(); otpFocus(o, 0); }
    else if (k === 'End') { e.preventDefault(); otpFocus(o, o.len - 1); }
    else if (k === 'Backspace') {
      e.preventDefault();
      // a filled box is cleared and stays; an empty one goes back and clears the box before it (so Backspace walks backwards through a code)
      var j = box.value !== '' ? i : i - 1;
      if (j >= 0 && !locked) { o.boxes[j].value = ''; otpSay(o, ''); otpCommit(o, true); otpFocus(o, j); }
    } else if (k === 'Delete') {
      e.preventDefault();
      if (box.value !== '' && !locked) { box.value = ''; otpSay(o, ''); otpCommit(o, true); otpFocus(o, i); }
    } else if (k.length === 1) { e.preventDefault(); otpFill(o, k, i); }
  }

  // Text that arrives without a key we could read (a phone keyboard, autofill of an SMS code, a drop, an input method): the box holds what the browser put there.
  function otpInput(o, e, i) {
    var box = o.boxes[i], raw = box.value, prev = o.prev[i] || '';
    if (e.isComposing || raw === prev) { return; }
    if (raw === '') { otpSay(o, ''); otpCommit(o, true); return; }
    // a character typed into a box that kept its old one: the new one is the one that is not the old one
    var typed = raw.length === 2 && prev && raw.indexOf(prev) >= 0 ? raw.replace(prev, '') : raw;
    box.value = prev;
    otpFill(o, typed, i);
  }

  // The boxes show the real input: when the page is built, after a reset, or when a script has set its value.
  function otpFromInput(o) {
    var chars = otpClean(o, o.input.value);
    o.boxes.forEach(function (b, i) { b.value = chars[i] || ''; });
    o.prev = o.boxes.map(function (b) { return b.value; });
    if (o.input.value !== chars.join('')) { o.input.value = chars.join(''); }
    otpValidity(o);
    o.wrap.classList.toggle('is-complete', chars.length === o.len);
    otpRove(o, otpHome(o));
  }

  function otpInit(wrap) {
    if (wrap._matOtp) { return; }
    var input = $('.mat-otp__input', wrap);
    if (!input) { return; }
    var len = clamp(parseInt(wrap.getAttribute('data-length'), 10) || 6, 3, 12), kind = otpKind(wrap.getAttribute('data-chars'));
    var o = {
      wrap: wrap, input: input, len: len, kind: kind, boxes: [], prev: [], cur: 0, busy: false, timer: 0,
      upper: wrap.getAttribute('data-case') === 'upper', lower: wrap.getAttribute('data-case') === 'lower', mask: wrap.hasAttribute('data-mask')
    };
    wrap._matOtp = o;
    if (!input.id) { input.id = nextId('mat-otp-in'); }

    var group = doc.createElement('div');
    group.className = 'mat-otp__boxes';
    group.setAttribute('role', 'group');
    var lab = $('label[for="' + input.id + '"]');
    if (lab) { if (!lab.id) { lab.id = input.id + '-label'; } group.setAttribute('aria-labelledby', lab.id); }
    else { group.setAttribute('aria-label', input.getAttribute('aria-label') || otpText('code')); }
    if (input.getAttribute('aria-describedby')) { group.setAttribute('aria-describedby', input.getAttribute('aria-describedby')); }

    var spec = wrap.getAttribute('data-group') || '', breaks = otpBreaks(spec, len), dash = spec.indexOf('-') >= 0;
    var complete = input.getAttribute('autocomplete') || 'one-time-code';
    for (var i = 0; i < len; i += 1) {
      if (breaks.indexOf(i) >= 0) {
        var sep = doc.createElement('span');
        sep.className = 'mat-otp__sep' + (dash ? ' mat-otp__sep--dash' : '');
        sep.setAttribute('aria-hidden', 'true');
        group.appendChild(sep);
      }
      var box = doc.createElement('input');
      box.type = o.mask ? 'password' : 'text';
      box.className = 'mat-otp__box';
      box.id = input.id + '-' + (i + 1);
      box.tabIndex = -1;
      box.setAttribute('inputmode', kind.mode);
      box.setAttribute('autocomplete', i === 0 ? complete : 'off');
      box.setAttribute('autocapitalize', o.upper ? 'characters' : 'off');
      box.setAttribute('autocorrect', 'off');
      box.setAttribute('spellcheck', 'false');
      box.setAttribute('aria-label', otpText(kind.name, i + 1, len));
      group.appendChild(box);
      o.boxes.push(box);
    }
    // the real input is not offered to autofill (the first box is), not reached with the Tab key and not read out
    input.setAttribute('autocomplete', 'off');
    input.tabIndex = -1;
    input.setAttribute('aria-hidden', 'true');
    input.parentNode.insertBefore(group, input.nextSibling);

    o.status = doc.createElement('p');
    o.status.className = 'mat-otp__status';
    o.status.setAttribute('aria-hidden', 'true');
    o.status.hidden = true;
    o.live = doc.createElement('span');
    o.live.className = 'mat-sr-only';
    o.live.setAttribute('role', 'status');
    o.live.setAttribute('aria-live', 'polite');
    wrap.appendChild(o.status);
    wrap.appendChild(o.live);

    function at(e) { return o.boxes.indexOf(e.target); }
    group.addEventListener('keydown', function (e) { var i1 = at(e); if (i1 >= 0) { otpKeys(o, e, i1); } });
    // what happens inside a box is the business of the code: the field that changes, and says so, is the real input
    group.addEventListener('input', function (e) { var i1 = at(e); if (i1 >= 0) { e.stopPropagation(); otpInput(o, e, i1); } });
    group.addEventListener('change', function (e) { e.stopPropagation(); });
    group.addEventListener('compositionend', function (e) { var i1 = at(e); if (i1 >= 0) { otpInput(o, { isComposing: false }, i1); } });
    // Backspace of a phone keyboard in an empty box (no key we could read): it clears the box before it and goes back, as the key does
    group.addEventListener('beforeinput', function (e) {
      var i1 = at(e);
      if (i1 <= 0 || e.inputType !== 'deleteContentBackward' || o.boxes[i1].value !== '') { return; }
      e.preventDefault();
      if (o.input.disabled || o.input.readOnly) { return; }
      o.boxes[i1 - 1].value = '';
      otpSay(o, '');
      otpCommit(o, true);
      otpFocus(o, i1 - 1);
    });
    group.addEventListener('paste', function (e) {
      var i1 = at(e);
      if (i1 < 0) { return; }
      var data = e.clipboardData || window.clipboardData;
      e.preventDefault();
      otpFill(o, data ? data.getData('text') : '', i1);
    });
    group.addEventListener('focusin', function (e) { var i1 = at(e); if (i1 >= 0) { otpRove(o, i1); e.target.select(); } });
    group.addEventListener('click', function (e) { if (at(e) >= 0) { e.target.select(); } });
    // a script that sets the value of the real input (and says so with input or change) is shown in the boxes
    function follow() { if (!o.busy) { otpFromInput(o); } }
    input.addEventListener('input', follow);
    input.addEventListener('change', follow);
    if (typeof MutationObserver === 'function') {
      new MutationObserver(function () { otpState(o); }).observe(input, { attributes: true, attributeFilter: ['disabled', 'readonly', 'required', 'aria-invalid'] });
    }

    otpFromInput(o);
    otpState(o);
    wrap.classList.add('is-ready');
    if (doc.activeElement === input) { otpFocus(o, otpHome(o), true); }
  }

  function initOtps(scope) { $$('.mat-otp[data-otp]', scope).forEach(otpInit); }

  // ---- for an app: the code of an element (the wrapper, its real input, a box, or an id)
  function otpOf(ref) {
    var el = typeof ref === 'string' ? (doc.getElementById(ref) || $(ref)) : ref;
    var wrap = el && el.closest ? el.closest('.mat-otp') : null;
    return wrap && wrap._matOtp ? wrap._matOtp : null;
  }
  // The boxes are emptied, the mark and the message of an earlier error are taken away and the first box has the focus.
  function otpClear(ref) {
    var o = otpOf(ref);
    if (!o) { return; }
    o.boxes.forEach(function (b) { b.value = ''; });
    otpSay(o, '');
    otpSetInvalid(o.wrap, '');
    otpCommit(o, false);
    otpRove(o, 0);
    otpFocus(o, 0, true);
  }
  function otpFocusFirst(ref) {
    var o = otpOf(ref);
    if (o) { otpFocus(o, otpHome(o), true); }
  }
  // A code that the app rejected: the red mark, aria-invalid and the message at the field (the line of .mat-error, which is a live region). An empty message takes the mark away.
  function otpSetInvalid(ref, message) {
    var o = otpOf(ref);
    if (!o) { return; }
    var text = message ? String(message) : '';
    if (fieldOf(o.input)) { showError(o.input, text); }
    else {
      o.wrap.classList.toggle('is-invalid', !!text);
      o.input.setAttribute('aria-invalid', text ? 'true' : 'false');
      if (text) { otpSay(o, text, true); }
    }
    otpState(o);
  }
  function otpValue(ref, value) {
    var o = otpOf(ref);
    if (!o) { return ''; }
    if (value !== undefined) { o.input.value = String(value); otpFromInput(o); }
    return o.input.value;
  }

  // the real input is not drawn; whoever focuses it (a label, a message of the validation, the link in the summary) lands on the box where the code goes on
  doc.addEventListener('focusin', function (e) {
    var t = e.target, w = t.classList && t.classList.contains('mat-otp__input') ? t.closest('.mat-otp') : null;
    if (w && w._matOtp) { otpFocus(w._matOtp, otpHome(w._matOtp), true); }
  });
  doc.addEventListener('reset', function (e) {
    setTimeout(function () { $$('.mat-otp', e.target).forEach(function (w) { if (w._matOtp) { otpFromInput(w._matOtp); } }); }, 0);
  }, true);

  // ---- the events of the pickers
  doc.addEventListener('click', function (e) {
    var t = e.target;
    if (!t.closest) { return; }
    var inside = t.closest('.mat-picker__panel');
    var trigger = t.closest('.mat-picker__trigger');
    if (trigger) { openPicker(trigger.closest('.mat-picker')); return; }
    var clear = t.closest('.mat-picker__clear');
    if (clear) {
      var cp = clear.closest('.mat-picker')._matPicker;
      closePicker(false);
      setPickerValue(cp, '');
      cp.trigger.focus({ preventScroll: true });
      return;
    }
    if (!inside) { closePicker(false); return; }
    var p = pickerOpen;
    if (!p || !p.panel.contains(inside)) { return; }
    var action = t.closest('[data-picker-action]');
    if (action) { panelAction(p, action.getAttribute('data-picker-action')); return; }
    if (t.closest('[data-picker-close]')) { closePicker(true); return; }
    if (p.spec.click) { p.spec.click(p, t, e); }
  });

  doc.addEventListener('input', function (e) {
    var p = pickerOpen;
    if (p && p.spec.input && e.target.closest && p.panel.contains(e.target)) { p.spec.input(p, e.target, e); }
  });

  doc.addEventListener('mouseover', function (e) {
    var p = pickerOpen;
    if (p && p.spec.hover && p.panel.contains(e.target)) { p.spec.hover(p, e.target); }
  });

  doc.addEventListener('keydown', function (e) {
    var p = pickerOpen;
    if (p && e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closePicker(true); return; }
    var t = e.target;
    if (!t.closest) { return; }
    if (t.classList.contains('mat-picker__trigger') && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) { e.preventDefault(); openPicker(t.closest('.mat-picker')); return; }
    if (!p || !p.panel.contains(t)) { return; }
    // on a phone the panel covers the page, so the Tab key stays inside it
    if (e.key === 'Tab' && p.full) {
      var tabbable = $$('button, input, [tabindex="0"]', p.panel).filter(function (el) { return !el.disabled && el.tabIndex >= 0 && el.offsetParent !== null; });
      if (tabbable.length) {
        var first = tabbable[0], last = tabbable[tabbable.length - 1];
        if (e.shiftKey && t === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && t === last) { e.preventDefault(); first.focus(); }
      }
      return;
    }
    if (p.spec.keys && !e.ctrlKey && !e.metaKey && !e.altKey) { p.spec.keys(p, e); }
  }, true);

  // a dialog around the picker is closed by Escape only after the picker
  doc.addEventListener('cancel', function (e) {
    if (pickerOpen && e.target.classList && e.target.classList.contains('mat-dialog')) { e.preventDefault(); closePicker(true); }
    else if (tagsOpen && e.target.classList && e.target.classList.contains('mat-dialog')) { e.preventDefault(); tagsClose(tagsOpen); }
  }, true);

  // the real input is not drawn; whoever focuses it (a label, a validation message) lands on the button
  doc.addEventListener('focusin', function (e) {
    var t = e.target;
    if (t.classList && t.classList.contains('mat-picker__input')) {
      var p = t.closest('.mat-picker') && t.closest('.mat-picker')._matPicker;
      if (p) { (p.spec.inline ? p.typing : p.trigger).focus({ preventScroll: false }); }
    }
  });

  doc.addEventListener('reset', function (e) {
    setTimeout(function () { $$('.mat-picker', e.target).forEach(syncPicker); }, 0);
  }, true);

  // pointer: the area of the colour picker
  doc.addEventListener('pointerdown', function (e) {
    var area = e.target.closest && e.target.closest('.mat-colour__area');
    var p = pickerOpen;
    if (!area || !p || p.kind !== 'colour') { return; }
    e.preventDefault();
    area.focus({ preventScroll: true });
    function move(ev) {
      var r = area.getBoundingClientRect();
      p.state.s = clamp((ev.clientX - r.left) / r.width, 0, 1);
      p.state.v = 1 - clamp((ev.clientY - r.top) / r.height, 0, 1);
      colourFromHsv(p);
    }
    function up() {
      area.removeEventListener('pointermove', move);
      area.removeEventListener('pointerup', up);
      area.removeEventListener('pointercancel', up);
    }
    move(e);
    if (area.setPointerCapture) { area.setPointerCapture(e.pointerId); }
    area.addEventListener('pointermove', move);
    area.addEventListener('pointerup', up);
    area.addEventListener('pointercancel', up);
  });

  window.addEventListener('resize', function () { if (pickerOpen) { pickerOpen.relayout(); syncLock(); } if (tagsOpen) { tagsPlace(tagsOpen); } });
  doc.addEventListener('scroll', function (e) {
    if (pickerOpen && !pickerOpen.full && !pickerOpen.panel.contains(e.target)) { pickerOpen.relayout(); }
    if (tagsOpen && !tagsOpen.suggest.contains(e.target)) { tagsPlace(tagsOpen); }
  }, true);

  // ------------------------------------------------------------------------------------------------ slider and range slider (FORM-17, matui.forms.css)
  // A slider is a native <input type="range"> in a wrapper: it posts its value, has the keyboard and the role of the browser and works without the script.
  //   <div class="mat-slider" data-slider data-format="{0} GB"><input class="mat-slider__input" type="range" id="q" name="q" min="0" max="100" step="10" value="40"></div>
  // A range slider is the same with two native inputs (name="from" and name="to") in one wrapper, .mat-slider--range, and data-min-gap for the least distance between them.
  // The script draws one track with the filled part, a thumb for each input (a target of 44 px) with a bubble that shows its value, the minimum and the maximum at the
  // ends and the marks, and it moves the thumbs itself: a press on the track moves the nearest thumb, thumbs cannot cross, the one moved last is on top. The native inputs
  // stay where they are, invisible but focusable, and stay the truth: the value, the validity, the form and what a screen reader reads (aria-valuetext) come from them, and
  // they send input and change like any range input. A form reset and MatUi.refresh are picked up. The texts of the names are English and German (a test checks).
  var SL_TEXT = {
    en: { from: 'from', to: 'to', range: '{0} – {1}' },
    de: { from: 'von', to: 'bis', range: '{0} – {1}' }
  };

  function slFill(text, a, b) {
    return text.replace(/\{0\}/g, function () { return a === undefined ? '' : a; }).replace(/\{1\}/g, function () { return b === undefined ? '' : b; });
  }
  function slRaw(key) {
    var table = SL_TEXT[(root.lang || 'en').slice(0, 2).toLowerCase()] || SL_TEXT.en;
    return root.getAttribute('data-sl-' + key.toLowerCase()) || table[key] || SL_TEXT.en[key] || key;
  }
  function slText(key, a, b) { return slFill(slRaw(key), a, b); }
  function slNumber(v, fallback) { var n = parseFloat(v); return isNaN(n) ? fallback : n; }
  function slDecimals(n) { var t = String(n), e = /e-(\d+)$/.exec(t), i = t.indexOf('.'); return e ? +e[1] : i < 0 ? 0 : t.length - i - 1; }
  function slRound(v, decimals) { return Number(v.toFixed(Math.min(decimals, 20))); }
  function slEl(tag, cls) { var e = doc.createElement(tag); e.className = cls; return e; }
  function slOf(el) { var w = el && el.closest ? el.closest('.mat-slider') : null; return w && w._matSlider ? w._matSlider : null; }

  // the scale (the range, the step, the decimals, the least gap, the template of what is shown): read again by MatUi.slider.update
  function slScale(s) {
    var first = s.inputs[0], step = slNumber(first.step, 1);
    s.min = slNumber(first.min, 0);
    s.max = Math.max(s.min, slNumber(first.max, 100));
    s.any = first.step === 'any';
    s.step = s.any ? ((s.max - s.min) / 100 || 1) : step > 0 ? step : 1;
    s.decimals = s.any ? 2 : Math.max(slDecimals(s.step), slDecimals(s.min));
    s.gap = s.range ? clamp(slNumber(s.el.getAttribute('data-min-gap'), 0), 0, s.max - s.min) : 0;
    s.format = s.el.getAttribute('data-format') || '{0}';
    try { s.nf = new Intl.NumberFormat(pkLocale(), { minimumFractionDigits: s.decimals, maximumFractionDigits: s.decimals }); } catch (e) { s.nf = null; }
  }
  function slPct(s, v) { return s.max === s.min ? 0 : ((v - s.min) / (s.max - s.min)) * 100; }
  function slRead(s, i) { return slNumber(s.inputs[i].value, s.min); }
  function slFormat(s, v) { return slFill(s.format, s.nf ? s.nf.format(v) : String(v)); }

  // a value on the grid of the steps (mode -1 rounds down and 1 up: the end of a gap that is not a multiple of the step); never beyond what the input can have
  function slGrid(s, v, mode) {
    if (s.any) { return clamp(slRound(v, s.decimals), s.min, s.max); }
    var n = (v - s.min) / s.step;
    n = mode < 0 ? Math.floor(n + 1e-9) : mode > 0 ? Math.ceil(n - 1e-9) : Math.round(n);
    return slRound(s.min + clamp(n, 0, Math.floor((s.max - s.min) / s.step + 1e-9)) * s.step, s.decimals);
  }
  // the value a thumb may have: on the grid, and (range) not nearer to the other thumb than the gap, so the thumbs never cross
  function slConstrain(s, i, v) {
    v = slGrid(s, v, 0);
    if (!s.range) { return v; }
    var other = slRead(s, 1 - i);
    return i === 0 ? Math.min(v, slGrid(s, other - s.gap, -1)) : Math.max(v, slGrid(s, other + s.gap, 1));
  }
  // values from the markup or after a reset: on the grid and not closer than the gap (silently: nothing is sent)
  function slNormalize(s) {
    s.inputs.forEach(function (input, i) {
      var v = slGrid(s, slRead(s, i), 0);
      if (input.value !== String(v)) { input.value = String(v); }
    });
    if (s.range && slRead(s, 1) - slRead(s, 0) < s.gap - 1e-9) {
      var to = Math.max(slRead(s, 1), slGrid(s, slRead(s, 0) + s.gap, 1));
      var from = Math.min(slRead(s, 0), slGrid(s, to - s.gap, -1));
      s.inputs[0].value = String(from);
      s.inputs[1].value = String(to);
    }
  }
  // a value of a thumb, as if it had been typed: input is sent (change is the caller's business); false when nothing changed
  function slSet(s, i, v) {
    v = slConstrain(s, i, v);
    if (slRead(s, i) === v) { return false; }
    s.inputs[i].value = String(v);
    s.inputs[i].dispatchEvent(new Event('input', { bubbles: true }));
    return true;
  }

  // the bubbles: kept inside the slider, and one for both thumbs ("20 – 30") while they are so near that two would cover each other
  function slBubbles(s) {
    var width = s.el.clientWidth;
    if (!width) { return; }
    var railWidth = s.rail.clientWidth, inset = s.rail.offsetLeft;
    var at = s.inputs.map(function (input, i) { return inset + (slPct(s, slRead(s, i)) / 100) * railWidth; });
    var merged = false;
    if (s.range && s.el.getAttribute('data-bubble') === 'always') {
      merged = at[1] - at[0] < (s.bubbles[0].offsetWidth + s.bubbles[1].offsetWidth) / 2 + 6;
      if (merged) { s.bubbles[1].textContent = slText('range', slFormat(s, slRead(s, 0)), slFormat(s, slRead(s, 1))); }
    }
    s.el.classList.toggle('is-merged', merged);
    s.bubbles.forEach(function (b, i) {
      var centre = merged ? (at[0] + at[1]) / 2 : at[i], half = b.offsetWidth / 2;
      var shift = (merged ? centre - at[i] : 0) + Math.max(0, half - centre) - Math.max(0, centre + half - width);
      b.style.setProperty('--mat-slider-shift', shift + 'px');
    });
  }

  // what shows the values: the filled part, the thumbs, their bubbles and names for a screen reader (aria-valuetext), the marks that are inside the filled part, the output
  function slSync(s) {
    var a = slRead(s, 0), b = s.range ? slRead(s, 1) : a;
    var lo = s.range ? slPct(s, a) : 0, hi = slPct(s, b);
    s.el.style.setProperty('--mat-slider-from', lo + '%');
    s.el.style.setProperty('--mat-slider-to', hi + '%');
    s.inputs.forEach(function (input, i) {
      var v = i === 0 ? a : b, text = slFormat(s, v);
      s.thumbs[i].style.setProperty('--mat-slider-at', slPct(s, v) + '%');
      s.thumbs[i].classList.toggle('is-top', i === s.last);
      s.bubbles[i].textContent = text;
      input.setAttribute('aria-valuetext', text);
    });
    s.ticks.forEach(function (t) { t.el.classList.toggle('is-on', t.pct >= lo - 1e-9 && t.pct <= hi + 1e-9); });
    var id = s.el.getAttribute('data-output') || s.inputs[0].getAttribute('data-output');
    var out = id ? doc.getElementById(id) : null;
    if (out) { out.textContent = s.range ? slFill(s.el.getAttribute('data-range-format') || slRaw('range'), slFormat(s, a), slFormat(s, b)) : slFormat(s, a); }
    slBubbles(s);
  }

  // what depends on the scale: the marks (data-ticks="10": ten parts; empty: a mark at every step), the labels under them (data-tick-labels="5": every fifth) or the labels at the ends
  function slBuild(s) {
    $$('.mat-slider__ticks, .mat-slider__scale', s.el).forEach(function (n) { n.remove(); });
    s.ticks = [];
    var attr = s.el.getAttribute('data-ticks'), every = parseInt(s.el.getAttribute('data-tick-labels'), 10) || 0, marks = 0, asked = parseInt(attr, 10);
    if (attr !== null) {
      marks = asked > 0 ? Math.min(asked, 200) : s.any ? 10 : (s.max - s.min) / s.step <= 100 ? Math.round((s.max - s.min) / s.step) : 0;
    }
    var scale = null, k, pct;
    if (marks > 0) {
      var ticks = slEl('div', 'mat-slider__ticks');
      for (k = 0; k <= marks; k += 1) {
        var tick = slEl('span', 'mat-slider__tick');
        pct = (k / marks) * 100;
        tick.style.setProperty('--mat-slider-at', pct + '%');
        ticks.appendChild(tick);
        s.ticks.push({ el: tick, pct: pct });
      }
      s.rail.insertBefore(ticks, s.thumbs[0]);
      if (every > 0) {
        scale = slEl('div', 'mat-slider__scale');
        for (k = 0; k <= marks; k += every) {
          var name = slEl('span', 'mat-slider__tick-label');
          name.textContent = slFormat(s, slRound(s.min + ((s.max - s.min) * k) / marks, s.decimals));
          name.style.setProperty('--mat-slider-at', ((k / marks) * 100) + '%');
          scale.appendChild(name);
        }
      }
    }
    if (!scale && s.el.getAttribute('data-labels') !== 'none') {
      scale = slEl('div', 'mat-slider__scale');
      var lowest = slEl('span', 'mat-slider__end mat-slider__end--min'), highest = slEl('span', 'mat-slider__end mat-slider__end--max');
      lowest.textContent = slFormat(s, s.min);
      highest.textContent = slFormat(s, s.max);
      scale.appendChild(lowest);
      scale.appendChild(highest);
    }
    if (scale) { scale.setAttribute('aria-hidden', 'true'); s.el.appendChild(scale); }
  }

  function slInit(el) {
    if (el._matSlider) { return; }
    var range = el.classList.contains('mat-slider--range');
    var inputs = $$('input[type=range]', el).slice(0, range ? 2 : 1);
    if (inputs.length < (range ? 2 : 1)) { return; }
    // data-min, data-max and data-step on the wrapper are copied to the inputs, which can carry them as well (and then work without the script)
    ['min', 'max', 'step'].forEach(function (name) {
      var v = el.getAttribute('data-' + name);
      if (v !== null) { inputs.forEach(function (input) { input.setAttribute(name, v); }); }
    });
    var s = { el: el, inputs: inputs, range: range, thumbs: [], bubbles: [], ticks: [], last: range ? 1 : 0 };
    el._matSlider = s;
    slScale(s);
    slNormalize(s);
    // each input of a range slider has a name of its own: "Price from" and "Price to", from the label of the first
    if (range) {
      var name = labelOf(inputs[0]) || inputs[0].name;
      if (!el.hasAttribute('role')) { el.setAttribute('role', 'group'); }
      if (!el.hasAttribute('aria-label') && !el.hasAttribute('aria-labelledby')) { el.setAttribute('aria-label', name); }
      inputs.forEach(function (input, i) {
        if (!input.hasAttribute('aria-label') && !input.hasAttribute('aria-labelledby')) { input.setAttribute('aria-label', name + ' ' + slText(i === 0 ? 'from' : 'to')); }
      });
    }
    s.rail = slEl('div', 'mat-slider__rail');
    s.rail.setAttribute('aria-hidden', 'true');
    var track = slEl('div', 'mat-slider__track');
    track.appendChild(slEl('div', 'mat-slider__fill'));
    s.rail.appendChild(track);
    inputs.forEach(function (input, i) {
      var thumb = slEl('span', 'mat-slider__thumb' + (range ? ' mat-slider__thumb--' + (i === 0 ? 'from' : 'to') : ''));
      var bubble = slEl('span', 'mat-slider__bubble');
      thumb.appendChild(bubble);
      s.rail.appendChild(thumb);
      s.thumbs.push(thumb);
      s.bubbles.push(bubble);
    });
    el.appendChild(s.rail);
    slBuild(s);
    el.classList.add('is-ready');
    slSync(s);
    // a slider in a dialog that is closed has no width: the bubbles are placed again when it has one
    if (typeof ResizeObserver === 'function') { new ResizeObserver(function () { slBubbles(s); }).observe(el); }
  }

  function initSliders(scope) { $$('[data-slider]', scope).forEach(slInit); }

  function slUpdate(el) {
    var s = el && el._matSlider;
    if (!s) { return; }
    slScale(s);
    slNormalize(s);
    slBuild(s);
    slSync(s);
  }

  // whatever changes a value (a screen reader, a script that sets it and sends input) is made right before anybody reads it, hence the capture phase
  doc.addEventListener('input', function (e) {
    var s = e.target && e.target.classList && e.target.classList.contains('mat-slider__input') ? slOf(e.target) : null;
    if (!s) { return; }
    var i = s.inputs.indexOf(e.target), v = slConstrain(s, i, slRead(s, i));
    if (v !== slRead(s, i)) { e.target.value = String(v); }
    s.last = i;
    slSync(s);
  }, true);

  // arrows by one step, Page Up and Page Down by a tenth of the scale, Home and End to the ends; every key sends input and change like the native control
  doc.addEventListener('keydown', function (e) {
    var t = e.target;
    if (e.ctrlKey || e.metaKey || e.altKey || !t.classList || !t.classList.contains('mat-slider__input')) { return; }
    var s = slOf(t);
    if (!s) { return; }
    var i = s.inputs.indexOf(t), v = slRead(s, i), big = Math.max(s.step, (s.max - s.min) / 10), next;
    switch (e.key) {
      case 'ArrowRight': case 'ArrowUp': next = v + s.step; break;
      case 'ArrowLeft': case 'ArrowDown': next = v - s.step; break;
      case 'PageUp': next = v + big; break;
      case 'PageDown': next = v - big; break;
      case 'Home': next = s.min; break;
      case 'End': next = s.max; break;
      default: return;
    }
    e.preventDefault();
    if (slSet(s, i, next)) { t.dispatchEvent(new Event('change', { bubbles: true })); }
  });

  // the bubble and the ring of a thumb follow the focus of its input
  function slFocus(t, on) {
    var s = t && t.classList && t.classList.contains('mat-slider__input') ? slOf(t) : null;
    if (!s) { return; }
    var thumb = s.thumbs[s.inputs.indexOf(t)], key = false;
    if (on) { try { key = t.matches(':focus-visible'); } catch (err) { key = true; } }
    thumb.classList.toggle('is-focus', on);
    thumb.classList.toggle('is-key', key);
  }
  doc.addEventListener('focusin', function (e) { slFocus(e.target, true); });
  doc.addEventListener('focusout', function (e) { slFocus(e.target, false); });

  // a press on the rail takes the nearest thumb (both at one value: the side of the press; on the thumbs the first movement says which), a press on a thumb grabs it
  // where it is, one beside it moves it there; the thumb follows the pointer and change is sent when the pointer lets go
  doc.addEventListener('pointerdown', function (e) {
    if (e.button !== 0 || e.isPrimary === false) { return; }
    var rail = e.target.closest ? e.target.closest('.mat-slider__rail') : null, s = rail && slOf(rail), live = [];
    if (!s) { return; }
    s.inputs.forEach(function (input, i) { if (!input.matches(':disabled')) { live.push(i); } });
    var rect = rail.getBoundingClientRect();
    if (live.length === 0 || rect.width <= 0) { return; }
    // text is not selected and the focus is not taken by a mouse press; a touch keeps its default (the page can still be scrolled up and down)
    if (e.pointerType !== 'touch') { e.preventDefault(); }
    var x = e.clientX - rect.left, reach = s.thumbs[0].getBoundingClientRect().width / 2;
    function centre(i) { return (slPct(s, slRead(s, i)) / 100) * rect.width; }
    var n = live[0], pending = false;
    if (live.length === 2 && slRead(s, 0) === slRead(s, 1)) {
      if (Math.abs(x - centre(0)) <= reach) { pending = true; n = s.last; } else { n = x < centre(0) ? 0 : 1; }
    } else if (live.length === 2) {
      n = Math.abs(x - centre(0)) <= Math.abs(x - centre(1)) ? 0 : 1;
    }
    var d = { n: n, pending: pending, id: e.pointerId, startX: e.clientX, grab: Math.abs(x - centre(n)) <= reach ? x - centre(n) : 0, start: slRead(s, n) };

    function activate(i) {
      d.n = i;
      s.last = i;
      s.thumbs.forEach(function (thumb, k) { thumb.classList.toggle('is-drag', k === i); });
      s.inputs[i].focus({ preventScroll: true });
      slSync(s);
    }
    function drive(ev) {
      if (d.pending) {
        if (Math.abs(ev.clientX - d.startX) < 2) { return; }
        d.pending = false;
        activate(ev.clientX < d.startX ? 0 : 1);
      }
      var r = rail.getBoundingClientRect();
      slSet(s, d.n, s.min + clamp((ev.clientX - r.left - d.grab) / r.width, 0, 1) * (s.max - s.min));
    }
    function move(ev) { if (ev.pointerId === d.id) { drive(ev); } }
    function end(ev) {
      if (ev.pointerId !== d.id) { return; }
      ['pointermove', 'pointerup', 'pointercancel', 'lostpointercapture'].forEach(function (name) { rail.removeEventListener(name, name === 'pointermove' ? move : end); });
      s.thumbs.forEach(function (thumb) { thumb.classList.remove('is-drag'); });
      s.el.classList.remove('is-dragging');
      if (slRead(s, d.n) !== d.start) { s.inputs[d.n].dispatchEvent(new Event('change', { bubbles: true })); }
    }
    activate(n);
    s.el.classList.add('is-dragging');
    try { rail.setPointerCapture(e.pointerId); } catch (err) { /* the pointer is gone: the press is over */ }
    ['pointermove', 'pointerup', 'pointercancel', 'lostpointercapture'].forEach(function (name) { rail.addEventListener(name, name === 'pointermove' ? move : end); });
    drive(e);
  });

  // a reset puts the values of the markup back after the event, so the sliders are drawn again right after it
  doc.addEventListener('reset', function (e) {
    setTimeout(function () {
      $$('[data-slider]').forEach(function (el) {
        var s = el._matSlider;
        if (s && s.inputs[0].form === e.target) { slNormalize(s); slSync(s); }
      });
    }, 0);
  });

  // ------------------------------------------------------------------------------------------------ carousel (LIST-13, matui.lists.css)
  // A carousel is a strip of slides that scrolls and snaps in CSS, so it is a scrollable strip without the script:
  //   <section class="mat-carousel" data-carousel aria-label="..."><div class="mat-carousel__track"><div class="mat-carousel__slide">...</div></div></section>
  // The script names the slides ("2 of 5"), adds a bar below the strip (Previous, a dot per page, Next, and Pause when data-autoplay="ms" is set), keeps the
  // slides that are out of view inert (the Tab key never enters them) and says which page is shown. data-loop: Next on the last page goes to the first.
  // Autoplay stops while the pointer or the focus is inside, after any use of a control, and never starts for a user who asked for less motion.
  var CAR_TEXT = {
    en: { carousel: 'carousel', slide: 'slide', slides: 'Slides', prev: 'Previous', next: 'Next', goTo: 'Show {0}', slideOf: '{0} of {1}', showing: 'Showing {0} of {1}', pages: 'Choose what to show', pause: 'Pause the slide show', play: 'Start the slide show' },
    de: { carousel: 'Karussell', slide: 'Element', slides: 'Elemente', prev: 'Zurück', next: 'Weiter', goTo: '{0} zeigen', slideOf: '{0} von {1}', showing: '{0} von {1} wird gezeigt', pages: 'Anzeige wählen', pause: 'Diashow anhalten', play: 'Diashow starten' }
  };

  function carText(key, a, b) {
    var table = CAR_TEXT[(root.lang || 'en').slice(0, 2).toLowerCase()] || CAR_TEXT.en;
    var s = root.getAttribute('data-car-' + key.toLowerCase()) || table[key] || CAR_TEXT.en[key] || key;
    return s.replace('{0}', function () { return a === undefined ? '' : a; }).replace('{1}', function () { return b === undefined ? '' : b; });
  }
  function reducedMotion() { return window.matchMedia('(prefers-reduced-motion: reduce)').matches; }

  // the distance from one slide to the next, and how many fit in the strip
  function carStep(c) {
    var first = c.slides[0];
    return first ? first.getBoundingClientRect().width + c.gap : 0;
  }
  function carLeft(c, index) { return c.slides[index].offsetLeft - c.slides[0].offsetLeft; }

  function carDots(c) {
    c.dots.textContent = '';
    for (var i = 0; i < c.pages; i += 1) {
      var d = doc.createElement('button');
      d.type = 'button';
      d.className = 'mat-carousel__dot';
      d.setAttribute('data-page', String(i));
      d.setAttribute('aria-label', carText('goTo', i + 1));
      c.dots.appendChild(d);
    }
  }

  // what is shown now: the page, the slides in view (the others are inert), the state of the buttons
  function carUpdate(c) {
    c.frame = 0;
    var t = c.track, step = carStep(c), max = t.scrollWidth - t.clientWidth;
    var atStart = t.scrollLeft <= 2, atEnd = t.scrollLeft >= max - 2;
    var index = step > 0 ? Math.round(t.scrollLeft / step) : 0;
    var page = atEnd ? c.pages - 1 : atStart ? 0 : Math.min(c.pages - 1, Math.floor(index / c.per));
    var box = t.getBoundingClientRect();
    c.slides.forEach(function (s) {
      var r = s.getBoundingClientRect();
      var inView = r.width > 0 && r.right > box.left + r.width / 2 && r.left < box.right - r.width / 2;
      if (inView) { s.removeAttribute('inert'); } else { s.setAttribute('inert', ''); }
      s.classList.toggle('is-active', inView);
    });
    $$('.mat-carousel__dot', c.dots).forEach(function (d, i) {
      if (i === page) { d.setAttribute('aria-current', 'true'); } else { d.removeAttribute('aria-current'); }
    });
    c.prev.setAttribute('aria-disabled', !c.loop && atStart ? 'true' : 'false');
    c.next.setAttribute('aria-disabled', !c.loop && atEnd ? 'true' : 'false');
    if (page !== c.page) {
      c.page = page;
      // a screen reader is told what is shown, unless the show moves by itself
      if (c.ready && !c.playing) { c.live.textContent = carText('showing', page + 1, c.pages); }
    }
  }
  function carSchedule(c) { if (!c.frame) { c.frame = window.requestAnimationFrame(function () { carUpdate(c); }); } }

  function carLayout(c) {
    var t = c.track, step;
    c.gap = parseFloat(getComputedStyle(t).columnGap) || 0;
    step = carStep(c);
    c.per = step > 0 ? Math.max(1, Math.round((t.clientWidth + c.gap) / step)) : 1;
    var pages = Math.max(1, Math.ceil(c.slides.length / c.per));
    if (pages !== c.pages) { c.pages = pages; carDots(c); c.page = -1; }
    c.bar.hidden = pages <= 1 && !c.toggle;
    carUpdate(c);
  }

  function carGo(c, page, auto) {
    var index = Math.min(page * c.per, Math.max(0, c.slides.length - c.per));
    c.track.scrollTo({ left: carLeft(c, index), behavior: reducedMotion() || !c.ready ? 'auto' : 'smooth' });
    if (!auto) { carStop(c); }
  }
  function carMove(c, dir, auto) {
    var page = c.page + dir;
    if (page >= c.pages) { if (!c.loop && !auto) { return; } page = 0; }
    else if (page < 0) { if (!c.loop) { return; } page = c.pages - 1; }
    carGo(c, page, auto);
  }

  // ---- autoplay: the user stops it for good with any control, and it waits while the pointer or the focus is inside
  function carToggleButton(c) {
    if (!c.toggle) { return; }
    c.toggle.setAttribute('aria-label', carText(c.playing ? 'pause' : 'play'));
    c.toggle.innerHTML = icon(c.playing ? 'pause' : 'play');
  }
  function carStart(c) {
    if (c.playing || !c.ms) { return; }
    c.playing = true;
    c.timer = setInterval(function () {
      if (c.hover || c.focus || doc.hidden || c.track.clientWidth === 0) { return; }
      carMove(c, 1, true);
    }, c.ms);
    carToggleButton(c);
  }
  function carStop(c) {
    clearInterval(c.timer);
    c.timer = 0;
    if (c.playing) { c.playing = false; carToggleButton(c); }
  }

  function carInit(root1) {
    if (root1._matCarousel) { return; }
    var track = $('.mat-carousel__track', root1);
    var slides = $$('.mat-carousel__slide', root1);
    if (!track || slides.length === 0) { return; }
    var c = { root: root1, track: track, slides: slides, pages: 0, page: -1, per: 1, gap: 0, playing: false, hover: false, focus: false, ready: false, frame: 0 };
    root1._matCarousel = c;
    c.loop = root1.hasAttribute('data-loop');
    c.ms = parseInt(root1.getAttribute('data-autoplay'), 10) || 0;

    if (!root1.getAttribute('aria-roledescription')) { root1.setAttribute('aria-roledescription', carText('carousel')); }
    if (!root1.getAttribute('role') && root1.tagName !== 'SECTION') { root1.setAttribute('role', 'region'); }
    track.setAttribute('tabindex', '0');
    track.setAttribute('aria-label', carText('slides'));
    slides.forEach(function (s, i) {
      s.setAttribute('role', 'group');
      s.setAttribute('aria-roledescription', carText('slide'));
      if (!s.getAttribute('aria-label') && !s.getAttribute('aria-labelledby')) { s.setAttribute('aria-label', carText('slideOf', i + 1, slides.length)); }
    });

    c.bar = doc.createElement('div');
    c.bar.className = 'mat-carousel__bar';
    function button(cls, label, ic) {
      var b = doc.createElement('button');
      b.type = 'button';
      b.className = 'mat-btn mat-btn--ghost mat-btn--icon ' + cls;
      b.setAttribute('aria-label', label);
      b.innerHTML = icon(ic);
      return b;
    }
    if (c.ms) { c.toggle = button('mat-carousel__toggle', carText('pause'), 'pause'); c.bar.appendChild(c.toggle); }
    c.prev = button('mat-carousel__prev', carText('prev'), 'chevron-left');
    c.dots = doc.createElement('div');
    c.dots.className = 'mat-carousel__dots';
    c.dots.setAttribute('role', 'group');
    c.dots.setAttribute('aria-label', carText('pages'));
    c.next = button('mat-carousel__next', carText('next'), 'chevron-right');
    c.bar.appendChild(c.prev);
    c.bar.appendChild(c.dots);
    c.bar.appendChild(c.next);
    c.live = doc.createElement('span');
    c.live.className = 'mat-sr-only';
    c.live.setAttribute('role', 'status');
    c.live.setAttribute('aria-live', 'polite');
    root1.appendChild(c.bar);
    root1.appendChild(c.live);
    root1.classList.add('is-ready');

    c.bar.addEventListener('click', function (e) {
      var b = e.target.closest('button');
      if (!b) { return; }
      if (b === c.toggle) { if (c.playing) { carStop(c); } else { carStart(c); } return; }
      if (b === c.prev || b === c.next) { if (b.getAttribute('aria-disabled') !== 'true') { carStop(c); carMove(c, b === c.next ? 1 : -1, false); } return; }
      if (b.hasAttribute('data-page')) { carGo(c, parseInt(b.getAttribute('data-page'), 10), false); }
    });
    root1.addEventListener('keydown', function (e) {
      if (e.ctrlKey || e.metaKey || e.altKey || e.shiftKey) { return; }
      var t = e.target;
      // the keys belong to the strip and to the buttons of the bar, not to a field inside a slide
      if (t !== track && !c.bar.contains(t)) { return; }
      var to = e.key === 'ArrowRight' ? 'next' : e.key === 'ArrowLeft' ? 'prev' : e.key === 'Home' ? 'first' : e.key === 'End' ? 'last' : '';
      if (!to) { return; }
      e.preventDefault();
      carStop(c);
      if (to === 'next') { carMove(c, 1, false); } else if (to === 'prev') { carMove(c, -1, false); }
      else { carGo(c, to === 'first' ? 0 : c.pages - 1, false); }
    });
    root1.addEventListener('mouseenter', function () { c.hover = true; });
    root1.addEventListener('mouseleave', function () { c.hover = false; });
    root1.addEventListener('focusin', function () { c.focus = true; });
    root1.addEventListener('focusout', function (e) { if (!e.relatedTarget || !root1.contains(e.relatedTarget)) { c.focus = false; } });
    track.addEventListener('scroll', function () { carSchedule(c); }, { passive: true });
    if (typeof ResizeObserver === 'function') { new ResizeObserver(function () { carLayout(c); }).observe(track); }
    else { window.addEventListener('resize', function () { carLayout(c); }); }

    carLayout(c);
    c.ready = true;
    // a show that moves by itself starts for those who have not asked for less motion; the others can start it with the button
    if (c.ms) { if (reducedMotion()) { carToggleButton(c); } else { carStart(c); } }
  }

  function initCarousels(scope) { $$('[data-carousel]', scope).forEach(carInit); }

  // ------------------------------------------------------------------------------------------------ schedule (LIST-14, matui.lists.css)
  // A schedule shows events one below another and pages through time: Agenda (grouped by day under sticky headings), Day, Week, Month and Year. The author writes the root and
  // gives the events as JSON; the script draws everything else and owns what is inside the root (what is written there is replaced: a good place for a text for readers without script):
  //   <section class="mat-schedule" data-schedule aria-label="Practice" data-view="agenda" data-date="2026-10-12" data-week-numbers
  //            data-events='[{"id":"a1","title":"Check-up","start":"2026-10-12T09:00","end":"2026-10-12T09:30","tone":"ok","location":"Room 2","status":"Confirmed"}]'></section>
  // An event has an id, a title, a start and an end (a local date-time such as 2026-10-12T09:00, or a date for an all-day event: then the end date is the last day), a tone (accent, ok,
  // warn, error, info, plain: always shown by an icon and a text as well, never by colour alone: COLOR-04), a location, a note, a url (then it is a link, otherwise a button) and a status
  // (the text of the pill). The script fires mat:schedule-select (detail: the event; cancelable, so a link can be stopped), mat:schedule-range (detail: from, to, view, date: what is
  // visible now, so that an app can load it; with data-lazy a loading state shows until MatUi.schedule.setEvents is called) and mat:schedule-day (the user chose a day).
  // Paging: the buttons, the arrow keys and PageUp / PageDown (with Shift: a year), a swipe on a touch screen. In the month grid the arrow keys move from day to day (a date grid).
  var SCH_TEXT = {
    en: {
      schedule: 'Schedule', views: 'View', agenda: 'Agenda', day: 'Day', week: 'Week', month: 'Month', year: 'Year', today: 'Today',
      prevDay: 'Previous day', nextDay: 'Next day', prevWeek: 'Previous week', nextWeek: 'Next week', prevMonth: 'Previous month', nextMonth: 'Next month',
      prevYear: 'Previous year', nextYear: 'Next year', prevDays: 'Previous {0} days', nextDays: 'Next {0} days',
      weekNo: 'Week {0}', weekHead: 'Wk', calWeek: 'Calendar week', allDay: 'All day', dayOf: 'Day {0} of {1}', to: 'to', until: 'until',
      oneEvent: '1 event', events: '{0} events', noEvents: 'No events', period: '{0}, {1}', more: '+{0} more', moreLabel: 'Show the day with {0} events',
      nothing: 'Nothing planned', emptyTitle: 'Nothing planned', emptyText: 'There are no events in this period.', loading: 'Loading events',
      jump: 'Choose a month or year', openMonth: 'Open {0}', openDay: 'Open the day', yearHint: 'A darker day has more events. Choose a month to open it.',
      toneAccent: 'Highlighted', toneOk: 'In order', toneWarn: 'Attention', toneError: 'Problem', toneInfo: 'Information'
    },
    de: {
      schedule: 'Terminkalender', views: 'Ansicht', agenda: 'Agenda', day: 'Tag', week: 'Woche', month: 'Monat', year: 'Jahr', today: 'Heute',
      prevDay: 'Vorheriger Tag', nextDay: 'Nächster Tag', prevWeek: 'Vorherige Woche', nextWeek: 'Nächste Woche', prevMonth: 'Vorheriger Monat', nextMonth: 'Nächster Monat',
      prevYear: 'Vorheriges Jahr', nextYear: 'Nächstes Jahr', prevDays: 'Vorherige {0} Tage', nextDays: 'Nächste {0} Tage',
      weekNo: 'KW {0}', weekHead: 'KW', calWeek: 'Kalenderwoche', allDay: 'Ganztägig', dayOf: 'Tag {0} von {1}', to: 'bis', until: 'bis',
      oneEvent: '1 Termin', events: '{0} Termine', noEvents: 'Keine Termine', period: '{0}, {1}', more: '+{0} weitere', moreLabel: 'Den Tag mit {0} Terminen öffnen',
      nothing: 'Nichts geplant', emptyTitle: 'Nichts geplant', emptyText: 'In diesem Zeitraum gibt es keine Termine.', loading: 'Termine werden geladen',
      jump: 'Monat oder Jahr wählen', openMonth: '{0} öffnen', openDay: 'Tag öffnen', yearHint: 'Ein dunklerer Tag hat mehr Termine. Wählen Sie einen Monat, um ihn zu öffnen.',
      toneAccent: 'Hervorgehoben', toneOk: 'In Ordnung', toneWarn: 'Achtung', toneError: 'Problem', toneInfo: 'Information'
    }
  };
  var SCH_VIEWS = ['agenda', 'day', 'week', 'month', 'year'];
  // the icon that tells the tone apart from colour (COLOR-04); a plain event needs none
  var SCH_TONES = { accent: 'star', ok: 'check-circle', warn: 'alert', error: 'x-circle', info: 'info', plain: '' };
  var SCH_TONE_TEXT = { accent: 'toneAccent', ok: 'toneOk', warn: 'toneWarn', error: 'toneError', info: 'toneInfo' };
  var SCH_WIDE = 640;           // from this width (of the schedule, not of the window) the week is a time grid and the month shows its events
  var SCH_SWIPE = 48;           // the distance of a swipe in px
  var schAll = [];
  var schFormatsByLocale = {};
  var schTimer = 0;
  var schOutside = false;

  function schText(c, key, a, b) {
    var table = SCH_TEXT[c.lang] || SCH_TEXT.en;
    var s = root.getAttribute('data-sch-' + key.toLowerCase()) || table[key] || SCH_TEXT.en[key] || key;
    return s.replace('{0}', function () { return a === undefined ? '' : a; }).replace('{1}', function () { return b === undefined ? '' : b; });
  }

  // the language of one schedule: its own data-locale or lang, else the one of the page (a German schedule can stand in an English page)
  function schLocale(el) {
    var holder = el.closest('[lang]');
    var l = el.getAttribute('data-locale') || (holder && holder.getAttribute('lang')) || pkLocale();
    if (/^en$/i.test(l)) { l = 'en-GB'; }
    try { Intl.DateTimeFormat(l); } catch (e) { l = 'en-GB'; }
    return l;
  }
  function schFormats(c) {
    var f = schFormatsByLocale[c.locale];
    if (!f) {
      var l = c.locale;
      f = schFormatsByLocale[c.locale] = {
        dow: new Intl.DateTimeFormat(l, { weekday: 'short' }),
        dowLong: new Intl.DateTimeFormat(l, { weekday: 'long' }),
        dowNarrow: new Intl.DateTimeFormat(l, { weekday: 'narrow' }),
        dayMonth: new Intl.DateTimeFormat(l, { weekday: 'long', day: 'numeric', month: 'long' }),
        dayMonthYear: new Intl.DateTimeFormat(l, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }),
        monthYear: new Intl.DateTimeFormat(l, { month: 'long', year: 'numeric' }),
        monthLong: new Intl.DateTimeFormat(l, { month: 'long' }),
        monthShort: new Intl.DateTimeFormat(l, { month: 'short' }),
        range: new Intl.DateTimeFormat(l, { day: 'numeric', month: 'short', year: 'numeric' }),
        time: new Intl.DateTimeFormat(l, { hour: '2-digit', minute: '2-digit' })
      };
    }
    return f;
  }
  function schTime(c, min) { return schFormats(c).time.format(new Date(2000, 0, 1, Math.floor(min / 60) % 24, min % 60)); }
  function schRangeText(c, a, b) {
    var f = schFormats(c).range;
    return typeof f.formatRange === 'function' ? f.formatRange(a, b) : f.format(a) + ' – ' + f.format(b);
  }

  // the number of the calendar week by ISO 8601: weeks start on Monday and week 1 is the one with the first Thursday of the year
  function isoWeek(d) {
    var t = new Date(d.getFullYear(), d.getMonth(), d.getDate());
    t.setDate(t.getDate() - ((t.getDay() + 6) % 7) + 3);                 // the Thursday of this week
    var jan4 = new Date(t.getFullYear(), 0, 4);
    return 1 + Math.round(((t - jan4) / 864e5 - 3 + ((jan4.getDay() + 6) % 7)) / 7);
  }
  // a week of the grid that starts on any day has the number of the week that holds most of it: the one of its fourth day (a Thursday when weeks start on Monday)
  function schWeekFrom(c, d) { return addDays(d, -((d.getDay() - c.weekStart + 7) % 7)); }
  function schWeekNo(c, from) { return isoWeek(addDays(from, 3)); }
  function schDays(from, n) { var out = []; for (var i = 0; i < n; i += 1) { out.push(addDays(from, i)); } return out; }
  function schSpan(a, b) { return Math.round((b - a) / 864e5); }

  // ---- the clock: the browser's, or the one that data-now pins (a demo, a test, the time of the server)
  function schWhen(s) {
    var m = /^(\d{4}-\d{2}-\d{2})(?:[T ](\d{2}):(\d{2}))?/.exec(String(s === undefined || s === null ? '' : s).trim());
    var d = m && dateOf(m[1]);
    if (!d) { return null; }
    if (m[2] === undefined) { return { d: d, min: null }; }
    return +m[2] < 24 && +m[3] < 60 ? { d: d, min: +m[2] * 60 + +m[3] } : null;
  }
  function schToday(c) { return c.now ? c.now.d : dayStart(new Date()); }
  function schNowMin(c) { var n = new Date(); return c.now ? c.now.min || 0 : n.getHours() * 60 + n.getMinutes(); }

  // ---- the events: normalised once, in the order of the day
  function schEvents(list) {
    var out = [];
    (Array.isArray(list) ? list : []).forEach(function (raw) {
      var s = raw && typeof raw === 'object' ? schWhen(raw.start) : null;
      var title = raw && raw.title !== undefined && raw.title !== null ? String(raw.title) : '';
      if (!s || !title) { return; }
      var e = schWhen(raw.end);
      var allDay = raw.allDay === true || s.min === null;
      var ev = { raw: raw, title: title, tone: SCH_TONES.hasOwnProperty(raw.tone) ? raw.tone : 'plain', sd: s.d, ed: s.d, sm: allDay ? 0 : s.min, em: allDay ? 1440 : s.min, allDay: allDay };
      if (e && e.d >= s.d) {
        if (allDay || e.min === null) { ev.ed = e.d; }
        else if (e.d.getTime() === s.d.getTime()) { ev.em = Math.max(e.min, s.min); }
        else if (e.min === 0 && e.d.getTime() === addDays(s.d, 1).getTime()) { ev.em = 1440; }      // until midnight is still that day
        else { ev.ed = e.d; ev.em = e.min; }
      }
      ev.s = isoOf(ev.sd);
      ev.e = isoOf(ev.ed);
      ev.days = schSpan(ev.sd, ev.ed) + 1;
      ev.span = allDay || ev.days > 1;       // drawn as a bar above the hours
      out.push(ev);
    });
    out.sort(function (a, b) { return a.s < b.s ? -1 : a.s > b.s ? 1 : (a.span ? 0 : 1) - (b.span ? 0 : 1) || a.sm - b.sm || (a.title < b.title ? -1 : a.title > b.title ? 1 : 0); });
    out.forEach(function (ev, i) { ev.i = i; });
    return out;
  }
  function schParse(text) {
    if (!text) { return []; }
    try { return JSON.parse(text); } catch (e) { return []; }
  }
  // the events of one day, all-day and multi-day first, then by time
  function schOnDay(c, iso) {
    var out = [];
    for (var i = 0; i < c.events.length; i += 1) {
      var e = c.events[i];
      if (e.s > iso) { break; }
      if (e.e >= iso) { out.push(e); }
    }
    return out.sort(function (a, b) { return (a.span ? 0 : 1) - (b.span ? 0 : 1) || a.sm - b.sm || a.i - b.i; });
  }
  function schCountIn(c, from, to) {
    var a = isoOf(from), b = isoOf(to), n = 0;
    c.events.forEach(function (e) { if (e.s <= b && e.e >= a) { n += 1; } });
    return n;
  }
  function schCountText(c, n) { return n === 0 ? schText(c, 'noEvents') : n === 1 ? schText(c, 'oneEvent') : schText(c, 'events', n); }
  function schSafeUrl(u) { return typeof u === 'string' && u && !/^\s*(javascript|data|vbscript):/i.test(u) ? u : ''; }

  // ---- periods: what is visible (grid: with the days of the neighbouring months that fill the weeks), what paging stops at, and the step of a page
  function schRange(c, d, view, grid) {
    var a, b;
    if (view === 'day') { a = b = d; }
    else if (view === 'week' || (view === 'agenda' && c.agenda === 'week')) { a = schWeekFrom(c, d); b = addDays(a, 6); }
    else if (view === 'agenda' && c.agenda !== 'month') { a = d; b = addDays(d, c.agenda - 1); }
    else if (view === 'year') { a = new Date(d.getFullYear(), 0, 1); b = new Date(d.getFullYear(), 11, 31); }
    else {
      a = firstOf(d);
      b = new Date(d.getFullYear(), d.getMonth() + 1, 0);
      if (grid && view === 'month') { a = schWeekFrom(c, a); b = addDays(schWeekFrom(c, b), 6); }
    }
    return { from: a, to: b };
  }
  function schStep(c, d, view, dir) {
    if (view === 'day') { return addDays(d, dir); }
    if (view === 'week') { return addDays(d, 7 * dir); }
    if (view === 'month') { return addMonths(d, dir); }
    if (view === 'year') { return addMonths(d, 12 * dir); }
    return c.agenda === 'week' ? addDays(d, 7 * dir) : c.agenda === 'month' ? addMonths(d, dir) : addDays(d, c.agenda * dir);
  }
  function schAllowed(c, d, view) {
    var r = schRange(c, d, view, false);
    return (!c.max || r.from <= c.max) && (!c.min || r.to >= c.min);
  }
  function schUnit(c, view) {
    return view === 'agenda' ? (c.agenda === 'week' ? 'Week' : c.agenda === 'month' ? 'Month' : 'Days') : view === 'day' ? 'Day' : view === 'week' ? 'Week' : view === 'month' ? 'Month' : 'Year';
  }
  function schTitle(c) {
    var f = schFormats(c), v = c.view, r = schRange(c, c.date, v, false);
    if (v === 'year') { return String(c.date.getFullYear()); }
    if (v === 'month' || (v === 'agenda' && c.agenda === 'month')) { return f.monthYear.format(c.date); }
    if (v === 'day') { return f.dayMonthYear.format(c.date); }
    var text = schRangeText(c, r.from, r.to);
    return c.weekNumbers && (v === 'week' || (v === 'agenda' && c.agenda === 'week')) ? schText(c, 'weekNo', schWeekNo(c, r.from)) + ' · ' + text : text;
  }

  // ---- markup of the parts
  function schMark(ev) { return SCH_TONES[ev.tone] ? '<span class="mat-schedule__mark" aria-hidden="true">' + icon(SCH_TONES[ev.tone]) + '</span>' : ''; }
  // what a reader who cannot see the colour or the icon is told: the status of the event, or the name of its tone
  function schToneText(c, ev) {
    var t = ev.raw.status ? String(ev.raw.status) : SCH_TONE_TEXT[ev.tone] ? schText(c, SCH_TONE_TEXT[ev.tone]) : '';
    return t ? '<span class="mat-sr-only">' + esc(t) + '. </span>' : '';
  }
  function schMeta(ev) { return [ev.raw.location, ev.raw.note].filter(function (x) { return x; }).map(String).join(' · '); }
  // the time of an event on one day: all day, day n of m, the start and the end, or only what remains of a long one
  function schWhenText(c, ev, iso) {
    if (ev.allDay) { return ev.days === 1 ? { a: schText(c, 'allDay') } : { a: schText(c, 'dayOf', schSpan(ev.sd, dateOf(iso)) + 1, ev.days) }; }
    if (ev.days > 1) {
      if (iso === ev.s) { return { a: schTime(c, ev.sm) }; }
      return iso === ev.e ? { a: schText(c, 'until') + ' ' + schTime(c, ev.em) } : { a: schText(c, 'allDay') };
    }
    return ev.em > ev.sm ? { a: schTime(c, ev.sm), b: schTime(c, ev.em) } : { a: schTime(c, ev.sm) };
  }
  // an event is a link when it has a url and a button otherwise; the classes say where it is drawn: row, block, chip or bar
  function schOpen(ev, mode, more, attr) {
    var url = schSafeUrl(ev.raw.url);
    var cls = 'mat-schedule__event mat-schedule__event--' + ev.tone + ' mat-schedule__event--' + mode + (more || '');
    return (url ? '<a href="' + esc(url) + '"' : '<button type="button"') + ' class="' + cls + '" data-ev="' + ev.i + '"' + (attr || '') + '>';
  }
  function schClose(ev) { return ev.raw.url && schSafeUrl(ev.raw.url) ? '</a>' : '</button>'; }
  function schRow(c, ev, iso) {
    var w = schWhenText(c, ev, iso), meta = schMeta(ev), status = ev.raw.status ? String(ev.raw.status) : '';
    var pill = status ? '<span class="mat-pill mat-pill--' + ev.tone + '">' + esc(status) + '</span>' : '';
    return '<li class="mat-schedule__item">' + schOpen(ev, 'row', '', '') +
      '<span class="mat-schedule__when"><span class="mat-schedule__from">' + esc(w.a) + '</span>' + (w.b ? '<span class="mat-schedule__to"><span class="mat-sr-only">' + esc(schText(c, 'to')) + ' </span>' + esc(w.b) + '</span>' : '') + '</span>' +
      '<span class="mat-schedule__what"><span class="mat-schedule__title">' + schMark(ev) + (status ? '' : schToneText(c, ev)) + esc(ev.title) + '</span>' + (meta ? '<span class="mat-schedule__meta">' + esc(meta) + '</span>' : '') + '</span>' +
      pill + schClose(ev) + '</li>';
  }
  function schEmpty(c) {
    return '<div class="mat-empty mat-schedule__empty"><span class="mat-empty__icon" aria-hidden="true">' + icon('calendar') + '</span>' +
      '<p class="mat-empty__title">' + esc(c.root.getAttribute('data-empty-title') || schText(c, 'emptyTitle')) + '</p><p>' + esc(c.root.getAttribute('data-empty') || schText(c, 'emptyText')) + '</p></div>';
  }
  function schDayText(c, d) { return (d.getFullYear() === c.date.getFullYear() ? schFormats(c).dayMonth : schFormats(c).dayMonthYear).format(d); }
  function schWeekLabel(c, d) { return '<span class="mat-schedule__kw">' + esc(schText(c, 'weekNo', schWeekNo(c, schWeekFrom(c, d)))) + '</span>'; }

  // ---- Agenda: the days one below another, each under its heading. Week on a phone is the same list with every day (rows).
  function schAgenda(c, r, o) {
    var today = isoOf(schToday(c)), n = schSpan(r.from, r.to) + 1, H = 'h' + c.level, out = '', any = false, lastWeek = '';
    for (var k = 0; k < n; k += 1) {
      var d = addDays(r.from, k), iso = isoOf(d), list = schOnDay(c, iso);
      if (!list.length && !o.rows && !c.emptyDays) { continue; }
      any = any || list.length > 0;
      var id = nextId('mat-sch'), week = isoOf(schWeekFrom(c, d));
      out += '<section class="mat-schedule__day' + (iso === today ? ' is-today' : '') + '" aria-labelledby="' + id + '">' +
        '<' + H + ' class="mat-schedule__dayhead" id="' + id + '"><span>' + esc(schDayText(c, d)) + '</span>' +
        (iso === today ? '<span class="mat-pill mat-pill--accent mat-pill--plain">' + esc(schText(c, 'today')) + '</span>' : '') +
        (c.weekNumbers && week !== lastWeek ? schWeekLabel(c, d) : '') + '</' + H + '>' +
        (list.length ? '<ul class="mat-schedule__items">' + list.map(function (ev) { return schRow(c, ev, iso); }).join('') + '</ul>' : '<p class="mat-schedule__none">' + esc(schText(c, 'nothing')) + '</p>') + '</section>';
      lastWeek = week;
    }
    return any ? '<div class="mat-schedule__list">' + out + '</div>' : schEmpty(c);
  }

  // ---- Day and Week: a time grid. Events that last all day or longer are bars above the hours; the others are blocks, side by side when they overlap.
  function schLanes(list) {
    var cluster = [], ends = [], clusterEnd = -1;
    function close() { cluster.forEach(function (x) { x.lanes = ends.length; }); cluster = []; ends = []; }
    list.forEach(function (x) {
      if (cluster.length && x.s >= clusterEnd) { close(); clusterEnd = -1; }
      var lane = 0;
      while (lane < ends.length && ends[lane] > x.s) { lane += 1; }
      x.lane = lane;
      ends[lane] = x.e;
      clusterEnd = Math.max(clusterEnd, x.e);
      cluster.push(x);
    });
    close();
  }
  function schBlock(c, x, h0) {
    var ev = x.ev, meta = schMeta(ev), when = schWhenText(c, ev, ev.s);
    var label = when.a + (when.b ? ' – ' + when.b : '') + ' · ' + ev.title + (meta ? ' · ' + meta : '');
    return schOpen(ev, 'block', x.e - x.s <= 30 ? ' is-short' : '', ' title="' + esc(label) + '" data-sch-top="' + ((x.s - h0 * 60) / 60).toFixed(4) + '" data-sch-len="' + ((x.e - x.s) / 60).toFixed(4) + '" data-sch-lane="' + x.lane + '" data-sch-lanes="' + x.lanes + '"') +
      '<span class="mat-schedule__title">' + schMark(ev) + schToneText(c, ev) + esc(ev.title) + '</span>' +
      '<span class="mat-schedule__time">' + esc(when.a) + (when.b ? ' – ' + esc(when.b) : '') + '</span>' + (meta ? '<span class="mat-schedule__meta">' + esc(meta) + '</span>' : '') + schClose(ev);
  }
  function schGrid(c, days) {
    var f = schFormats(c), n = days.length, H = 'h' + c.level, today = isoOf(schToday(c)), nowMin = schNowMin(c);
    var first = isoOf(days[0]), last = isoOf(days[n - 1]), h0 = c.dayStart, h1 = c.dayEnd, spans = [], timed = [], k, out = '';
    for (k = 0; k < n; k += 1) { timed.push([]); }
    c.events.forEach(function (ev) {
      if (ev.e < first || ev.s > last) { return; }
      if (ev.span) { spans.push(ev); return; }
      var end = Math.max(ev.em, ev.sm + 30);       // a short event still takes the room of half an hour, so that its neighbours do not cover it
      timed[schSpan(days[0], ev.sd)].push({ ev: ev, s: ev.sm, e: end });
      h0 = Math.min(h0, Math.floor(ev.sm / 60));
      h1 = Math.max(h1, Math.ceil(end / 60));
    });
    h1 = Math.min(24, h1);
    c.h0 = h0;
    // the hour that the grid scrolls to: the hour before the first event, or before now when that is earlier, or the working day
    var firstMin = 1440, todayIn = days.some(function (d) { return isoOf(d) === today; });
    timed.forEach(function (list) { list.forEach(function (x) { firstMin = Math.min(firstMin, x.s); }); });
    c.firstHour = Math.max(h0, Math.floor(Math.min(firstMin < 1440 ? firstMin / 60 - 0.25 : 8, todayIn && nowMin >= h0 * 60 && nowMin < h1 * 60 ? nowMin / 60 - 1.5 : 24)));

    out += '<div class="mat-schedule__grid" data-sch-cols="' + n + '"><div class="mat-schedule__top"><div class="mat-schedule__head">' +
      '<div class="mat-schedule__corner">' + (c.weekNumbers && n > 1 ? '<span aria-hidden="true">' + esc(schText(c, 'weekHead')) + ' ' + schWeekNo(c, days[0]) + '</span><span class="mat-sr-only">' + esc(schText(c, 'weekNo', schWeekNo(c, days[0]))) + '</span>' : '') + '</div>';
    var ids = days.map(function () { return nextId('mat-sch'); });
    days.forEach(function (d, i) {
      var inner = '<span class="mat-schedule__dow">' + esc(f.dow.format(d)) + '</span><span class="mat-schedule__dnum">' + d.getDate() + '</span>';
      out += '<' + H + ' class="mat-schedule__colhead' + (isoOf(d) === today ? ' is-today' : '') + '" id="' + ids[i] + '">' +
        (n > 1 ? '<button type="button" class="mat-schedule__dayopen" data-sch-open="' + isoOf(d) + '" aria-label="' + esc(schText(c, 'openDay') + ': ' + f.dayMonthYear.format(d)) + '"' + (isoOf(d) === today ? ' aria-current="date"' : '') + '>' + inner + '</button>' : inner) + '</' + H + '>';
    });
    out += '</div>';
    if (spans.length) {
      var lanes = [];
      out += '<div class="mat-schedule__band" data-sch-cols="' + n + '"><div class="mat-schedule__bandlabel">' + esc(schText(c, 'allDay')) + '</div>';
      spans.forEach(function (ev) {
        var c1 = Math.max(0, schSpan(days[0], ev.sd)), c2 = Math.min(n - 1, schSpan(days[0], ev.ed)), lane = 0;
        while (lanes[lane] !== undefined && lanes[lane] >= c1) { lane += 1; }
        lanes[lane] = c2;
        var w = schWhenText(c, ev, ev.s);
        out += schOpen(ev, 'bar', (ev.sd < days[0] ? ' is-before' : '') + (ev.ed > days[n - 1] ? ' is-after' : ''), ' data-sch-c1="' + (c1 + 2) + '" data-sch-c2="' + (c2 + 3) + '" data-sch-lane="' + (lane + 1) + '"') +
          '<span class="mat-schedule__title">' + schMark(ev) + schToneText(c, ev) + esc(ev.title) + '</span>' + (!ev.allDay ? '<span class="mat-schedule__time">' + esc(w.a) + '</span>' : '') + schClose(ev);
      });
      out += '</div>';
    }
    out += '</div>';
    out += '<div class="mat-schedule__tbody"><div class="mat-schedule__hours" aria-hidden="true">';
    for (k = h0; k < h1; k += 1) { out += '<div class="mat-schedule__hour"><span>' + esc(schTime(c, k * 60)) + '</span></div>'; }
    out += '</div><div class="mat-schedule__cols" data-sch-hours="' + (h1 - h0) + '" data-sch-cols="' + n + '"><div class="mat-schedule__lines" aria-hidden="true">';
    for (k = h0; k < h1; k += 1) { out += '<div></div>'; }
    out += '</div>';
    days.forEach(function (d, i) {
      var iso = isoOf(d);
      schLanes(timed[i]);
      out += '<div class="mat-schedule__col' + (iso === today ? ' is-today' : '') + '" role="group" aria-labelledby="' + ids[i] + '">' + timed[i].map(function (x) { return schBlock(c, x, h0); }).join('');
      if (iso === today && nowMin >= h0 * 60 && nowMin < h1 * 60) { out += '<div class="mat-schedule__now" aria-hidden="true" data-sch-top="' + ((nowMin - h0 * 60) / 60).toFixed(4) + '"></div>'; }
      out += '</div>';
    });
    return out + '</div></div></div>';
  }

  // ---- Month: a grid of weeks. Wide: the events are in the cells. Narrow: a day has dots, and the chosen day is listed below the grid.
  // In a week row an event that lasts for days is one bar over the cells it covers; every bar keeps its lane, so that the bars of the days line up.
  function schLanes7(c, rowStart) {
    var first = isoOf(rowStart), last = isoOf(addDays(rowStart, 6)), ends = [], place = {};
    c.events.forEach(function (ev) {
      if (!ev.span || ev.s > last || ev.e < first) { return; }
      var c1 = Math.max(0, schSpan(rowStart, ev.sd)), lane = 0;
      while (ends[lane] !== undefined && ends[lane] >= c1) { lane += 1; }
      ends[lane] = Math.min(6, schSpan(rowStart, ev.ed));
      place[ev.i] = lane;
    });
    return place;
  }
  function schChips(c, list, iso, col, rowStart, place) {
    var LINES = 3, lines = [], slots = [], timed = [], hidden = 0, out = '';
    list.forEach(function (ev) { if (ev.span) { slots[place[ev.i]] = ev; } else { timed.push(ev); } });
    for (var lane = 0; lane < slots.length; lane += 1) {
      var ev = slots[lane];
      if (lane >= LINES) { hidden += ev ? 1 : 0; continue; }
      if (!ev || (col > 0 && iso > ev.s)) { lines.push(ev ? 'G' : null); continue; }        // free, or covered by the bar that began in an earlier cell
      var span = Math.min(6, schSpan(rowStart, ev.ed)) - col + 1;
      lines.push('<li>' + schOpen(ev, 'chip', (ev.sd < rowStart ? ' is-before' : '') + (ev.ed > addDays(rowStart, 6) ? ' is-after' : ''), ' data-sch-span="' + span + '" title="' + esc(ev.title + (schMeta(ev) ? ' \u00b7 ' + schMeta(ev) : '')) + '"') +
        schMark(ev) + schToneText(c, ev) + '<span class="mat-schedule__title">' + esc(ev.title) + '</span>' + schClose(ev) + '</li>');
    }
    while (lines.length && lines[lines.length - 1] === null) { lines.pop(); }
    lines.forEach(function (x) { out += x === null || x === 'G' ? '<li class="mat-schedule__gap" aria-hidden="true"></li>' : x; });
    timed.forEach(function (ev) {
      if (lines.length >= LINES) { hidden += 1; return; }
      var when = schWhenText(c, ev, iso);
      lines.push('');
      out += '<li>' + schOpen(ev, 'chip', '', ' title="' + esc(when.a + ' \u00b7 ' + ev.title + (schMeta(ev) ? ' \u00b7 ' + schMeta(ev) : '')) + '"') + schMark(ev) + schToneText(c, ev) +
        '<span class="mat-schedule__time">' + esc(when.a) + '</span><span class="mat-schedule__title">' + esc(ev.title) + '</span>' + schClose(ev) + '</li>';
    });
    if (hidden) { out += '<li><button type="button" class="mat-schedule__more" data-sch-open="' + iso + '" aria-label="' + esc(schText(c, 'moreLabel', list.length)) + '">' + esc(schText(c, 'more', hidden)) + '</button></li>'; }
    return out ? '<ul class="mat-schedule__chips">' + out + '</ul>' : '';
  }
  function schMonth(c, r) {
    var f = schFormats(c), today = isoOf(schToday(c)), sel = isoOf(c.sel || c.date), month = c.date.getMonth(), H = 'h' + c.level;
    var weeks = (schSpan(r.from, r.to) + 1) / 7, out = '', i, w;
    var inGrid = sel >= isoOf(r.from) && sel <= isoOf(r.to);
    var stop = inGrid ? sel : isoOf(firstOf(c.date));
    out += '<div class="mat-schedule__mgrid' + (c.weekNumbers ? ' is-numbered' : '') + '" role="grid" aria-label="' + esc(f.monthYear.format(c.date)) + '"><div class="mat-schedule__row mat-schedule__row--head" role="row">' +
      (c.weekNumbers ? '<div class="mat-schedule__wk" role="columnheader"><span aria-hidden="true">' + esc(schText(c, 'weekHead')) + '</span><span class="mat-sr-only">' + esc(schText(c, 'calWeek')) + '</span></div>' : '');
    for (i = 0; i < 7; i += 1) { var wd = addDays(r.from, i); out += '<div class="mat-schedule__wd" role="columnheader" title="' + esc(f.dowLong.format(wd)) + '">' + esc(f.dow.format(wd)) + '</div>'; }
    out += '</div>';
    for (w = 0; w < weeks; w += 1) {
      var rowStart = addDays(r.from, w * 7), place = c.wide ? schLanes7(c, rowStart) : null;
      out += '<div class="mat-schedule__row" role="row">' + (c.weekNumbers ? '<div class="mat-schedule__wk" role="rowheader"><span aria-hidden="true">' + schWeekNo(c, rowStart) + '</span><span class="mat-sr-only">' + esc(schText(c, 'weekNo', schWeekNo(c, rowStart))) + '</span></div>' : '');
      for (i = 0; i < 7; i += 1) {
        var d = addDays(rowStart, i), iso = isoOf(d), list = schOnDay(c, iso), off = (c.min && d < c.min) || (c.max && d > c.max);
        var label = f.dayMonthYear.format(d) + ', ' + schCountText(c, list.length);
        var cls = 'mat-schedule__cell' + (iso === today ? ' is-today' : '') + (d.getMonth() !== month ? ' is-outside' : '') + (iso === sel ? ' is-selected' : '') + (off ? ' is-off' : '');
        var btn = ' data-sch-day="' + iso + '" tabindex="' + (iso === stop ? '0' : '-1') + '" aria-label="' + esc(label) + '"' + (iso === today ? ' aria-current="date"' : '') + (off ? ' aria-disabled="true"' : '');
        out += '<div class="' + cls + '" role="gridcell"' + (iso === sel ? ' aria-selected="true"' : '') + '>';
        if (c.wide) {
          out += '<button type="button" class="mat-schedule__daynum"' + btn + '>' + d.getDate() + '</button>';
          out += schChips(c, list, iso, i, rowStart, place);
        } else {
          var dots = '';
          list.slice(0, 3).forEach(function (ev) { dots += '<i class="mat-schedule__dot mat-schedule__dot--' + ev.tone + '"></i>'; });
          out += '<button type="button" class="mat-schedule__daybtn"' + btn + '><span class="mat-schedule__dnum">' + d.getDate() + '</span><span class="mat-schedule__dots" aria-hidden="true">' + dots + '</span></button>';
        }
        out += '</div>';
      }
      out += '</div>';
    }
    out += '</div>';
    if (!c.wide) {
      var sd = dateOf(sel), list2 = schOnDay(c, sel), hid = nextId('mat-sch');
      out += '<div class="mat-schedule__daylist"><' + H + ' class="mat-schedule__dayhead" id="' + hid + '"><span>' + esc(schDayText(c, sd)) + '</span><span class="mat-schedule__count">' + esc(schCountText(c, list2.length)) + '</span>' +
        '<button type="button" class="mat-btn mat-btn--ghost mat-btn--sm" data-sch-open="' + sel + '">' + esc(schText(c, 'openDay')) + '</button></' + H + '>' +
        (list2.length ? '<ul class="mat-schedule__items" aria-labelledby="' + hid + '">' + list2.map(function (ev) { return schRow(c, ev, sel); }).join('') + '</ul>' : '<p class="mat-schedule__none">' + esc(schText(c, 'nothing')) + '</p>') + '</div>';
    }
    return out;
  }

  // ---- Year: twelve small months, each day tinted by how many events it has (one, two, three or more); a month opens the month view
  function schYear(c) {
    var f = schFormats(c), y = c.date.getFullYear(), today = isoOf(schToday(c)), H = 'h' + c.level, counts = {}, perMonth = [], m, i, out = '';
    var ya = new Date(y, 0, 1), yb = new Date(y, 11, 31);
    for (m = 0; m < 12; m += 1) { perMonth.push(0); }
    c.events.forEach(function (ev) {
      var a = ev.sd < ya ? ya : ev.sd, b = ev.ed > yb ? yb : ev.ed, d, seen = {};
      for (d = a; d <= b; d = addDays(d, 1)) {
        var key = isoOf(d);
        counts[key] = (counts[key] || 0) + 1;
        if (!seen[d.getMonth()]) { seen[d.getMonth()] = 1; perMonth[d.getMonth()] += 1; }
      }
    });
    out += '<div class="mat-schedule__year">';
    for (m = 0; m < 12; m += 1) {
      var first = new Date(y, m, 1), days = new Date(y, m + 1, 0).getDate(), lead = (first.getDay() - c.weekStart + 7) % 7;
      var name = f.monthYear.format(first), off = (c.min && new Date(y, m + 1, 0) < c.min) || (c.max && first > c.max);
      out += '<div class="mat-schedule__month' + (off ? ' is-off' : '') + '" data-sch-month="' + isoOf(first) + '"><' + H + ' class="mat-schedule__monthhead">' +
        '<button type="button" class="mat-schedule__monthname" data-sch-month="' + isoOf(first) + '" aria-label="' + esc(schText(c, 'openMonth', name) + ', ' + schCountText(c, perMonth[m])) + '"' + (off ? ' aria-disabled="true"' : '') + '>' + esc(f.monthLong.format(first)) + '</button></' + H + '>' +
        '<div class="mat-schedule__mini" aria-hidden="true">';
      for (i = 0; i < 7; i += 1) { out += '<span class="mat-schedule__mwd">' + esc(f.dowNarrow.format(addDays(schWeekFrom(c, first), i))) + '</span>'; }
      for (i = 0; i < lead; i += 1) { out += '<span></span>'; }
      for (i = 1; i <= days; i += 1) {
        var iso = isoOf(new Date(y, m, i)), n = counts[iso] || 0;
        out += '<span class="mat-schedule__d' + (n ? ' is-l' + Math.min(3, n) : '') + (iso === today ? ' is-today' : '') + '">' + i + '</span>';
      }
      out += '</div></div>';
    }
    return out + '</div><p class="mat-schedule__hint">' + esc(schText(c, 'yearHint')) + '</p>';
  }

  // ---- drawing: the body, the bar above it, what is announced, and what an app is told
  // a length, a place or a column that is data is set by the script (no inline style in the markup, so a strict content security policy holds)
  function schPlace(body) {
    var names = ['top', 'len', 'lane', 'lanes', 'hours', 'cols', 'c1', 'c2', 'span'];
    $$('[data-sch-top],[data-sch-len],[data-sch-lane],[data-sch-lanes],[data-sch-hours],[data-sch-cols],[data-sch-c1],[data-sch-c2],[data-sch-span]', body).forEach(function (el) {
      names.forEach(function (n) { if (el.hasAttribute('data-sch-' + n)) { el.style.setProperty('--mat-sch-' + n, el.getAttribute('data-sch-' + n)); } });
    });
  }
  function schScrollTo(c, keep) {
    if (keep >= 0) { c.body.scrollTop = keep; return; }
    var cols = $('.mat-schedule__cols', c.body), top = $('.mat-schedule__top', c.body);
    if (!cols) { return; }
    var hourPx = cols.offsetHeight / (parseFloat(cols.getAttribute('data-sch-hours')) || 1);
    var off = cols.getBoundingClientRect().top - c.body.getBoundingClientRect().top + c.body.scrollTop;
    c.body.scrollTop = Math.max(0, off + Math.max(0, c.firstHour - c.h0) * hourPx - (top ? top.offsetHeight : 0));
  }
  function schBar(c, title) {
    var dir = { prev: -1, next: 1 };
    c.titleText.textContent = title;
    c.body.setAttribute('aria-label', title);
    ['prev', 'next'].forEach(function (k) {
      var unit = schUnit(c, c.view), b = c[k];
      b.setAttribute('aria-label', unit === 'Days' ? schText(c, k + 'Days', c.agenda) : schText(c, k + unit));
      b.setAttribute('aria-disabled', schAllowed(c, schStep(c, c.date, c.view, dir[k]), c.view) ? 'false' : 'true');
    });
    c.today.setAttribute('aria-disabled', (c.min && schToday(c) < c.min) || (c.max && schToday(c) > c.max) ? 'true' : 'false');
    $$('[data-sch-go]', c.views).forEach(function (b) {
      var on = b.getAttribute('data-sch-go') === c.view;
      b.classList.toggle('is-active', on);
      b.setAttribute('aria-pressed', on ? 'true' : 'false');
    });
  }
  function schLoading(c, on) {
    c.loading = on;
    c.root.classList.toggle('is-loading', on);
    c.body.setAttribute('aria-busy', on ? 'true' : 'false');
    c.busy.hidden = !on;
    if (on) { c.live.textContent = schText(c, 'loading'); }
  }
  // an app that loads its events when the period changes listens to this one (the first one is sent as soon as the page is ready)
  function schNotify(c, r, first) {
    var key = isoOf(r.from) + '|' + isoOf(r.to);
    if (key === c.rangeKey) { return; }
    c.rangeKey = key;
    if (c.lazy && !(first && c.events.length)) { schLoading(c, true); }
    var detail = { from: isoOf(r.from), to: isoOf(r.to), view: c.view, date: isoOf(c.date) };
    var send = function () { c.root.dispatchEvent(new CustomEvent('mat:schedule-range', { bubbles: true, detail: detail })); };
    // the first one is sent when the page is ready, and not at all if the user has paged by then (that page has told the app already)
    if (first) { setTimeout(function () { if (c.rangeKey === key) { send(); } }, 0); } else { send(); }
  }

  function schRender(c, o) {
    o = o || {};
    var v = c.view, r = schRange(c, c.date, v, true), per = schRange(c, c.date, v, false);
    var gridView = v === 'day' || (v === 'week' && c.wide);
    var hadFocus = c.body.contains(doc.activeElement), top = c.body.scrollTop;
    var keep = !o.resetScroll && c.drawn === v + (c.wide ? 'w' : 'n') && (gridView || !o.page) ? top : -1;
    c.drawn = v + (c.wide ? 'w' : 'n');
    c.body.innerHTML = v === 'year' ? schYear(c) : v === 'month' ? schMonth(c, r) : v === 'day' ? schGrid(c, [c.date]) : v === 'week' && c.wide ? schGrid(c, schDays(r.from, 7)) : schAgenda(c, r, { rows: v === 'week' });
    schPlace(c.body);
    c.body.classList.toggle('is-scroll', v === 'agenda' || v === 'day' || v === 'week');
    c.root.setAttribute('data-view', v);
    var title = schTitle(c);
    schBar(c, title);
    if (gridView) { schScrollTo(c, keep); } else { c.body.scrollTop = keep >= 0 ? keep : 0; }
    if (o.focusDay) {
      var b = $('[data-sch-day="' + o.focusDay + '"]', c.body);
      if (b) { b.focus({ preventScroll: true }); }
    } else if (hadFocus && !c.body.contains(doc.activeElement)) { c.body.focus({ preventScroll: true }); }
    if (o.dir && !reducedMotion()) {
      c.body.classList.remove('is-from-next', 'is-from-prev');
      void c.body.offsetWidth;
      c.body.classList.add(o.dir > 0 ? 'is-from-next' : 'is-from-prev');
    }
    if (o.announce) { c.live.textContent = schText(c, 'period', title, schCountText(c, schCountIn(c, per.from, per.to))); }
    schNotify(c, r, o.first);
  }

  // ---- moving: to a date (and view), by one page, to today
  function schGo(c, date, view, o) {
    o = o || {};
    schJumpClose(c, false);
    if (view && c.offered.indexOf(view) >= 0) { c.view = view; }
    c.date = limitDate(dayStart(date), c.min, c.max);
    c.sel = o.sel || null;
    schRender(c, o);
  }
  function schPage(c, dir, year) {
    var to = year ? addMonths(c.date, 12 * dir) : schStep(c, c.date, c.view, dir);
    if (!schAllowed(c, to, c.view)) { return false; }
    schGo(c, to, null, { dir: dir, page: true, announce: true });
    return true;
  }
  function schGoToday(c) {
    var t = schToday(c);
    schGo(c, t, null, { dir: t < c.date ? -1 : 1, page: true, announce: true });
  }
  // a day in the month grid: the focus moves with the arrow keys; past the edge of the grid the month turns
  function schFocusDay(c, to) {
    to = limitDate(dayStart(to), c.min, c.max);
    var iso = isoOf(to), b = $('[data-sch-day="' + iso + '"]', c.body);
    if (b) { b.focus(); return; }
    schGo(c, to, null, { dir: to < c.date ? -1 : 1, page: true, announce: true, focusDay: iso });
  }
  function schSelectEvent(c, node, e) {
    var ev = c.events[parseInt(node.getAttribute('data-ev'), 10)];
    if (!ev) { return; }
    if (!c.root.dispatchEvent(new CustomEvent('mat:schedule-select', { bubbles: true, cancelable: true, detail: ev.raw }))) { e.preventDefault(); }
  }
  function schChooseDay(c, iso, open) {
    var d = dateOf(iso);
    if (!d || (c.min && d < c.min) || (c.max && d > c.max)) { return; }
    c.root.dispatchEvent(new CustomEvent('mat:schedule-day', { bubbles: true, detail: { date: iso, view: c.view } }));
    if (open) { schGo(c, d, 'day', { announce: true, resetScroll: true }); return; }
    schGo(c, c.date, null, { sel: d, focusDay: iso });
    c.live.textContent = schText(c, 'period', schDayText(c, d), schCountText(c, schOnDay(c, iso).length));
  }

  // ---- the small panel under the title: a year, and its twelve months
  function schJumpFill(c) {
    var f = schFormats(c), y = c.jumpYear, out = '<div class="mat-schedule__jumphead"><button type="button" class="mat-btn mat-btn--ghost mat-btn--icon" data-sch-year="-1" aria-label="' + esc(schText(c, 'prevYear')) + '">' + icon('chevron-left') + '</button>' +
      '<strong class="mat-schedule__jumpyear" aria-live="polite">' + y + '</strong><button type="button" class="mat-btn mat-btn--ghost mat-btn--icon" data-sch-year="1" aria-label="' + esc(schText(c, 'nextYear')) + '">' + icon('chevron-right') + '</button></div>' +
      '<div class="mat-schedule__jumpmonths">';
    for (var m = 0; m < 12; m += 1) {
      var first = new Date(y, m, 1), off = (c.min && new Date(y, m + 1, 0) < c.min) || (c.max && first > c.max);
      var current = y === c.date.getFullYear() && m === c.date.getMonth();
      out += '<button type="button" class="mat-btn mat-btn--sm ' + (current ? 'is-active mat-btn--secondary' : 'mat-btn--ghost') + '" data-sch-pick="' + m + '"' + (current ? ' aria-current="date"' : '') + (off ? ' aria-disabled="true"' : '') + '>' + esc(f.monthShort.format(first)) + '</button>';
    }
    c.jump.innerHTML = out + '</div>';
  }
  function schJumpOpen(c) {
    c.jumpYear = c.date.getFullYear();
    schJumpFill(c);
    c.jump.hidden = false;
    c.jump.style.setProperty('--mat-sch-left', c.titleBtn.offsetLeft + 'px');      // under the title
    c.titleBtn.setAttribute('aria-expanded', 'true');
    var cur = $('[data-sch-pick][aria-current]', c.jump) || $('[data-sch-pick]', c.jump);
    if (cur) { cur.focus(); }
  }
  function schJumpClose(c, focus) {
    if (c.jump.hidden) { return; }
    c.jump.hidden = true;
    c.titleBtn.setAttribute('aria-expanded', 'false');
    if (focus) { c.titleBtn.focus(); }
  }
  function schJumpPick(c, m) {
    var y = c.jumpYear, last = new Date(y, m + 1, 0).getDate(), to = new Date(y, m, Math.min(c.date.getDate(), last));
    if ((c.min && new Date(y, m + 1, 0) < c.min) || (c.max && new Date(y, m, 1) > c.max)) { return; }
    schJumpClose(c, true);
    schGo(c, to, c.view === 'year' ? 'month' : null, { dir: to < c.date ? -1 : 1, page: true, announce: true });
  }

  // ---- events of the user
  function schClick(c, e) {
    var t = e.target, b;
    if (!t.closest) { return; }
    if ((b = t.closest('.mat-schedule__today'))) { if (b.getAttribute('aria-disabled') !== 'true') { schGoToday(c); } return; }
    if ((b = t.closest('.mat-schedule__prev, .mat-schedule__next'))) { if (b.getAttribute('aria-disabled') !== 'true') { schPage(c, b.classList.contains('mat-schedule__next') ? 1 : -1, false); } return; }
    if ((b = t.closest('[data-sch-go]'))) { schGo(c, c.date, b.getAttribute('data-sch-go'), { announce: true, resetScroll: true }); return; }
    if (t.closest('.mat-schedule__period')) { if (c.jump.hidden) { schJumpOpen(c); } else { schJumpClose(c, true); } return; }
    if ((b = t.closest('[data-sch-year]'))) { c.jumpYear += parseInt(b.getAttribute('data-sch-year'), 10); schJumpFill(c); $('[data-sch-year="' + b.getAttribute('data-sch-year') + '"]', c.jump).focus(); return; }
    if ((b = t.closest('[data-sch-pick]'))) { if (b.getAttribute('aria-disabled') !== 'true') { schJumpPick(c, parseInt(b.getAttribute('data-sch-pick'), 10)); } return; }
    if (!c.body.contains(t)) { return; }
    if ((b = t.closest('[data-ev]'))) { schSelectEvent(c, b, e); return; }
    if ((b = t.closest('[data-sch-open]'))) { schChooseDay(c, b.getAttribute('data-sch-open'), true); return; }
    if ((b = t.closest('[data-sch-day]'))) { if (b.getAttribute('aria-disabled') !== 'true') { schChooseDay(c, b.getAttribute('data-sch-day'), c.wide); } return; }
    if ((b = t.closest('[data-sch-month]'))) {
      var m = dateOf(b.getAttribute('data-sch-month'));
      if (m && !b.classList.contains('is-off') && b.getAttribute('aria-disabled') !== 'true') { schGo(c, m, 'month', { announce: true, resetScroll: true }); }
    }
  }
  function schKey(c, e) {
    if (e.ctrlKey || e.metaKey || e.altKey) { return; }
    var t = e.target, k = e.key;
    if (t.closest('.mat-schedule__jump')) {
      if (k === 'Escape') { e.preventDefault(); e.stopPropagation(); schJumpClose(c, true); }
      return;
    }
    if (k === 'Escape' && !c.jump.hidden) { schJumpClose(c, true); return; }
    if (t.matches && t.matches('input, textarea, select')) { return; }
    // in the month grid the arrow keys move from day to day, and PageUp and PageDown from month to month
    var dayBtn = c.view === 'month' && t.closest ? t.closest('[data-sch-day]') : null;
    if (dayBtn) {
      var d = dateOf(dayBtn.getAttribute('data-sch-day')), to = null;
      if (k === 'ArrowLeft') { to = addDays(d, -1); } else if (k === 'ArrowRight') { to = addDays(d, 1); } else if (k === 'ArrowUp') { to = addDays(d, -7); } else if (k === 'ArrowDown') { to = addDays(d, 7); }
      else if (k === 'Home') { to = schWeekFrom(c, d); } else if (k === 'End') { to = addDays(schWeekFrom(c, d), 6); }
      else if (k === 'PageUp' || k === 'PageDown') { to = addMonths(d, (k === 'PageDown' ? 1 : -1) * (e.shiftKey ? 12 : 1)); }
      if (to) { e.preventDefault(); schFocusDay(c, to); return; }
    }
    var dir = k === 'ArrowRight' || k === 'PageDown' ? 1 : k === 'ArrowLeft' || k === 'PageUp' ? -1 : 0;
    if (!dir) { return; }
    if (t.closest('.mat-schedule__views') && (k === 'ArrowLeft' || k === 'ArrowRight')) { return; }
    e.preventDefault();
    schPage(c, dir, e.shiftKey && (k === 'PageUp' || k === 'PageDown'));
  }

  function schSetEvents(c, list) {
    c.events = schEvents(list);
    schLoading(c, false);
    var per = schRange(c, c.date, c.view, false);
    schRender(c, {});
    c.live.textContent = schText(c, 'period', schTitle(c), schCountText(c, schCountIn(c, per.from, per.to)));
  }

  // the clock moves: the line of the time in the day and week views, and the highlighted day at midnight
  function schTick() {
    schAll = schAll.filter(function (c) { return doc.documentElement.contains(c.root); });
    schAll.forEach(function (c) {
      if (c.now) { return; }
      var key = isoOf(schToday(c));
      if (key !== c.todayKey) { c.todayKey = key; schRender(c, {}); return; }
      var line = $('.mat-schedule__now', c.body);
      if (line) { line.setAttribute('data-sch-top', ((schNowMin(c) - c.h0 * 60) / 60).toFixed(4)); schPlace(c.body); }
    });
    if (!schAll.length) { clearInterval(schTimer); schTimer = 0; }
  }

  function schInit(el) {
    if (el._matSchedule) { return; }
    var c = { root: el, events: [], sel: null, loading: false, rangeKey: '', drawn: '', jumpYear: 0, h0: 0, firstHour: 8 };
    el._matSchedule = c;
    c.locale = schLocale(el);
    c.lang = c.locale.slice(0, 2).toLowerCase();
    c.weekStart = (parseInt(el.getAttribute('data-week-start') || root.getAttribute('data-week-start') || '1', 10) || 0) % 7;
    c.weekNumbers = el.hasAttribute('data-week-numbers');
    c.min = dateOf(el.getAttribute('data-min'));
    c.max = dateOf(el.getAttribute('data-max'));
    var agenda = el.getAttribute('data-agenda') || 'week';
    c.agenda = agenda === 'month' || agenda === 'week' ? agenda : Math.max(1, parseInt(agenda, 10) || 7);
    c.dayStart = clamp(isNaN(parseInt(el.getAttribute('data-day-start'), 10)) ? 7 : parseInt(el.getAttribute('data-day-start'), 10), 0, 23);
    c.dayEnd = clamp(isNaN(parseInt(el.getAttribute('data-day-end'), 10)) ? 20 : parseInt(el.getAttribute('data-day-end'), 10), c.dayStart + 1, 24);
    c.level = clamp(parseInt(el.getAttribute('data-heading-level'), 10) || 3, 1, 6);
    c.lazy = el.hasAttribute('data-lazy');
    c.emptyDays = el.hasAttribute('data-empty-days');
    c.now = schWhen(el.getAttribute('data-now'));
    c.todayKey = isoOf(schToday(c));
    c.offered = (el.getAttribute('data-views') || '').split(/[\s,]+/).filter(function (v) { return SCH_VIEWS.indexOf(v) >= 0; });
    if (!c.offered.length) { c.offered = SCH_VIEWS.slice(); }
    c.view = c.offered.indexOf(el.getAttribute('data-view')) >= 0 ? el.getAttribute('data-view') : c.offered[0];
    c.date = limitDate(dateOf(el.getAttribute('data-date')) || schToday(c), c.min, c.max);
    c.events = schEvents(schParse(el.getAttribute('data-events')));
    c.wide = el.clientWidth >= SCH_WIDE;

    if (!el.getAttribute('aria-label') && !el.getAttribute('aria-labelledby')) { el.setAttribute('aria-label', schText(c, 'schedule')); }
    if (!el.getAttribute('role') && el.tagName !== 'SECTION') { el.setAttribute('role', 'region'); }
    el.innerHTML = '<div class="mat-schedule__bar"><div class="mat-schedule__nav">' +
      '<button type="button" class="mat-btn mat-btn--secondary mat-schedule__today">' + esc(schText(c, 'today')) + '</button>' +
      '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-schedule__prev">' + icon('chevron-left') + '</button>' +
      '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-schedule__next">' + icon('chevron-right') + '</button></div>' +
      '<button type="button" class="mat-schedule__period" aria-haspopup="dialog" aria-expanded="false" title="' + esc(schText(c, 'jump')) + '"><span class="mat-schedule__periodtext"></span>' + icon('chevron-down') + '</button>' +
      '<span class="mat-schedule__busy" hidden><span class="mat-spinner mat-spinner--sm" aria-hidden="true"></span></span>' +
      '<div class="mat-btn-group mat-schedule__views" role="group" aria-label="' + esc(schText(c, 'views')) + '">' +
      c.offered.map(function (v) { return '<button type="button" class="mat-btn mat-btn--secondary" data-sch-go="' + v + '" aria-pressed="false">' + esc(schText(c, v)) + '</button>'; }).join('') + '</div></div>' +
      '<div class="mat-schedule__jump" role="dialog" aria-label="' + esc(schText(c, 'jump')) + '" hidden></div>' +
      '<div class="mat-schedule__body" tabindex="0" role="region" aria-busy="false"></div>' +
      '<span class="mat-sr-only" role="status" aria-live="polite"></span>';
    c.today = $('.mat-schedule__today', el);
    c.prev = $('.mat-schedule__prev', el);
    c.next = $('.mat-schedule__next', el);
    c.titleBtn = $('.mat-schedule__period', el);
    c.titleText = $('.mat-schedule__periodtext', el);
    c.busy = $('.mat-schedule__busy', el);
    c.views = $('.mat-schedule__views', el);
    c.jump = $('.mat-schedule__jump', el);
    c.body = $('.mat-schedule__body', el);
    c.live = $('[role=status]', el);
    el.classList.toggle('is-wide', c.wide);
    el.classList.toggle('is-narrow', !c.wide);

    el.addEventListener('click', function (e) { schClick(c, e); });
    el.addEventListener('keydown', function (e) { schKey(c, e); });
    // the month grid has one tab stop; it moves with the focus
    el.addEventListener('focusin', function (e) {
      var b = e.target.closest && e.target.closest('[data-sch-day]');
      if (b) { $$('[data-sch-day][tabindex="0"]', c.body).forEach(function (x) { x.tabIndex = -1; }); b.tabIndex = 0; }
    });
    // a swipe to the left shows the next period, to the right the one before (a vertical pan is the browser's: touch-action in the CSS)
    c.body.addEventListener('pointerdown', function (e) { c.swipe = e.pointerType !== 'mouse' && e.isPrimary ? { x: e.clientX, y: e.clientY, id: e.pointerId, t: Date.now() } : null; });
    c.body.addEventListener('pointerup', function (e) {
      var s = c.swipe;
      c.swipe = null;
      if (!s || s.id !== e.pointerId || Date.now() - s.t > 900) { return; }
      var dx = e.clientX - s.x, dy = e.clientY - s.y;
      if (Math.abs(dx) >= SCH_SWIPE && Math.abs(dx) > Math.abs(dy) * 1.6) { schPage(c, dx < 0 ? 1 : -1, false); }
    });
    c.body.addEventListener('pointercancel', function () { c.swipe = null; });
    c.body.addEventListener('animationend', function (e) { if (e.target.parentNode === c.body) { c.body.classList.remove('is-from-next', 'is-from-prev'); } });
    // the layout follows the width of the schedule itself, not of the window: it can stand in a column of a page
    if (typeof ResizeObserver === 'function') {
      new ResizeObserver(function () {
        var wide = el.clientWidth >= SCH_WIDE;
        if (wide === c.wide || el.clientWidth === 0) { return; }
        c.wide = wide;
        el.classList.toggle('is-wide', wide);
        el.classList.toggle('is-narrow', !wide);
        schRender(c, {});
      }).observe(el);
    }

    el.classList.add('is-ready', c.wide ? 'is-wide' : 'is-narrow');
    schRender(c, { first: true });
    schAll.push(c);
    if (!schTimer) { schTimer = setInterval(schTick, 30000); }
    if (!schOutside) {
      schOutside = true;
      // a click outside of the panel under the title closes it
      // (the path is read from the event: the button that was pressed in the panel may be gone by now, the panel having drawn itself again)
      doc.addEventListener('click', function (e) {
        var path = e.composedPath ? e.composedPath() : [];
        schAll.forEach(function (x) { if (!x.jump.hidden && path.indexOf(x.jump) < 0 && path.indexOf(x.titleBtn) < 0) { schJumpClose(x, false); } });
      });
    }
  }

  function initSchedules(scope) { $$('[data-schedule]', scope).forEach(schInit); }
  // MatUi.schedule: the events of a schedule, a date (and a view) to show, the loading state, and what is shown now
  var schedule = {
    init: initSchedules,
    setEvents: function (el, events) { var c = el && el._matSchedule; if (c) { schSetEvents(c, events); } },
    go: function (el, date, view) {
      var c = el && el._matSchedule, d = date instanceof Date ? dayStart(date) : dateOf(String(date || '').slice(0, 10));
      if (c && d) { schGo(c, d, view, { announce: true, resetScroll: true }); }
    },
    setLoading: function (el, on) { var c = el && el._matSchedule; if (c) { schLoading(c, on !== false); } },
    get: function (el) {
      var c = el && el._matSchedule;
      if (!c) { return null; }
      var r = schRange(c, c.date, c.view, true);
      return { view: c.view, date: isoOf(c.date), from: isoOf(r.from), to: isoOf(r.to), loading: c.loading, wide: c.wide };
    }
  };

  // ------------------------------------------------------------------------------------------------ rating (FORM-22, matui.forms.css)
  // A value on a short scale that people give to something (stars, hearts or dots), or an average that is shown. To choose, the markup is a group of native radio buttons:
  //   <fieldset class="mat-field mat-fieldset mat-rating" data-rating data-clearable data-show-value><legend class="mat-legend">Overall</legend>
  //     <input class="mat-rating__input" type="radio" id="q-1" name="q" value="1"><label class="mat-rating__label" for="q-1">1 star</label> ... one per value</fieldset>
  // so a form posts it, the keyboard of the browser (arrow keys, one Tab stop) and the screen reader work, and without the script it is a row of radio buttons. The radios say
  // what can be chosen: half stars are 0.5, 1, 1.5 ... (twice the radios), and the highest value is the number of shapes. A radio with the value 0 or empty is "no rating".
  // The script marks the group is-ready (the radios and labels become invisible, they keep the focus) and draws the shapes: the outline of each and, over it, the filled ones up
  // to --mat-rating-fill. The pointer shows the value it is on (a mouse or a pen: the shapes fill up to it, and the text beside them says it), a click chooses it, and with
  // data-clearable a click on the chosen value clears it (the radio "no rating", or no radio at all). The shape is the class mat-rating--heart or --dot (star by default).
  // To show a value: <span class="mat-rating mat-rating--readonly" data-rating data-value="4.5" data-max="5" data-count="128" data-show-value>4.5 out of 5</span>; the content is what is
  // seen without the script, the script draws the shapes (any value, partly filled), gives it role="img" and an aria-label ("Rated 4.5 out of 5, 128 ratings") unless it has one.
  // data-max is the number of shapes (3 to 10, default 5), data-show-value writes the value as text beside the shapes (colour and shape alone do not say it, COLOR-04), data-count
  // adds "(128)". Every change of a choice sends mat:rating-change (bubbles; detail.value, detail.rating) after the native change event. API: MatUi.rating.init / get / set.
  var RT_TEXT = {
    en: { rated: 'Rated {0} out of {1}', ratedCount: 'Rated {0} out of {1}, {2} ratings', ratedOne: 'Rated {0} out of {1}, 1 rating', notRated: 'Not rated' },
    de: { rated: 'Bewertet mit {0} von {1}', ratedCount: 'Bewertet mit {0} von {1}, {2} Bewertungen', ratedOne: 'Bewertet mit {0} von {1}, 1 Bewertung', notRated: 'Nicht bewertet' }
  };
  var RT_ICONS = { heart: 'heart', dot: 'circle' };

  function ratText(key, a, b, c) {
    var table = RT_TEXT[(root.lang || 'en').slice(0, 2).toLowerCase()] || RT_TEXT.en;
    var s = root.getAttribute('data-rt-' + key.toLowerCase()) || table[key] || RT_TEXT.en[key] || key;
    return s.replace('{0}', function () { return a === undefined ? '' : a; }).replace('{1}', function () { return b === undefined ? '' : b; }).replace('{2}', function () { return c === undefined ? '' : c; });
  }
  // a number as the language of the page writes it (4,5 in German), with at most two decimals
  function ratNum(n) {
    var t = Math.round(n * 100) / 100;
    try { return t.toLocaleString(root.lang || 'en', { maximumFractionDigits: 2 }); } catch (e) { return String(t); }
  }
  function ratOf(el) {
    var r = el && el.closest ? el.closest('[data-rating]') : null;
    return r && r._matRating ? r._matRating : null;
  }
  function ratShapes(el, max) {
    var name = 'star', cls;
    for (cls in RT_ICONS) { if (Object.prototype.hasOwnProperty.call(RT_ICONS, cls) && el.classList.contains('mat-rating--' + cls)) { name = RT_ICONS[cls]; } }
    var cell = '<span class="mat-rating__cell">' + icon(name).replace('class="mat-icon"', 'class="mat-icon mat-rating__icon"') + '</span>', cells = '';
    for (var i = 0; i < max; i += 1) { cells += cell; }
    return '<span class="mat-rating__layer mat-rating__layer--empty">' + cells + '</span><span class="mat-rating__layer mat-rating__layer--fill">' + cells + '</span>';
  }
  function ratBody(r, body) {
    var s = doc.createElement('span');
    s.className = 'mat-rating__stars';
    s.setAttribute('aria-hidden', 'true');
    s.innerHTML = ratShapes(r.root, r.max);
    body.appendChild(s);
    r.stars = s;
    if (r.root.hasAttribute('data-show-value')) {
      r.valueEl = doc.createElement('span');
      r.valueEl.className = 'mat-rating__value';
      body.appendChild(r.valueEl);
    }
    var count = parseInt(r.root.getAttribute('data-count'), 10);
    if (count >= 0) {
      var c = doc.createElement('span');
      c.className = 'mat-rating__count';
      c.textContent = '(' + count.toLocaleString(root.lang || 'en') + ')';
      body.appendChild(c);
      r.count = count;
    }
  }

  // the value that is chosen (0 when nothing is, or "no rating"), or for a read-only rating its data-value
  function ratValue(r) {
    if (r.readonly) { var v = parseFloat(r.root.getAttribute('data-value')); return isFinite(v) ? Math.max(0, Math.min(r.max, v)) : 0; }
    var on = r.inputs.filter(function (i) { return i.el.checked; })[0];
    return on ? on.v : 0;
  }
  function ratDisabled(r) { return r.inputs.every(function (i) { return i.el.matches(':disabled'); }); }

  function ratSync(r) {
    var v = ratValue(r), shown = r.preview !== null ? r.preview : v;
    r.root.style.setProperty('--mat-rating-fill', (Math.min(100, shown / r.max * 100)) + '%');
    r.root.classList.toggle('is-previewing', r.preview !== null);
    if (r.valueEl) { r.valueEl.textContent = shown > 0 ? ratNum(shown) : ratText('notRated'); }
    if (r.readonly && r.ownLabel) {
      var text = ratNum(v), max = ratNum(r.max);
      r.root.setAttribute('aria-label', v <= 0 ? ratText('notRated') : r.count === undefined ? ratText('rated', text, max) : r.count === 1 ? ratText('ratedOne', text, max) : ratText('ratedCount', text, max, r.count.toLocaleString(root.lang || 'en')));
    }
  }

  function ratInit(el) {
    var readonly = el.classList.contains('mat-rating--readonly'), r = el._matRating;
    if (r && !readonly) { ratSync(r); return; }
    var max = parseInt(el.getAttribute('data-max'), 10) || 5, inputs = [];
    if (!readonly) {
      $$('input[type=radio]', el).forEach(function (i) {
        var v = parseFloat(i.value);
        inputs.push({ el: i, v: isFinite(v) && v > 0 ? v : 0 });
      });
      var top = inputs.reduce(function (m, i) { return Math.max(m, i.v); }, 0);
      if (!top) { return; }
      max = Math.ceil(top - 1e-9);
    }
    max = Math.max(3, Math.min(10, max));
    r = { root: el, readonly: readonly, max: max, inputs: inputs, preview: null };
    if (readonly) {
      r.ownLabel = el.hasAttribute('data-rating-label') || !el.hasAttribute('aria-label');
      if (r.ownLabel) { el.setAttribute('data-rating-label', ''); }
      el.setAttribute('role', 'img');
      el.textContent = '';
      ratBody(r, el);
    } else {
      r.values = inputs.filter(function (i) { return i.v > 0; }).map(function (i) { return i.v; }).sort(function (a, b) { return a - b; });
      r.none = inputs.filter(function (i) { return i.v === 0; })[0] || null;
      r.clearable = el.hasAttribute('data-clearable');
      var body = doc.createElement('div');
      body.className = 'mat-rating__body';
      ratBody(r, body);
      el.appendChild(body);
      r.stars.addEventListener('pointermove', function (e) { if (e.pointerType !== 'touch') { ratPreview(r, ratAt(r, e.clientX, e.clientY)); } });
      r.stars.addEventListener('pointerleave', function () { ratPreview(r, null); });
      r.stars.addEventListener('click', function (e) { ratPick(r, ratAt(r, e.clientX, e.clientY)); });
      if (typeof ResizeObserver === 'function') { new ResizeObserver(function () { ratFit(r); }).observe(el); }
    }
    el.style.setProperty('--mat-rating-max', String(max));
    el._matRating = r;
    el.classList.add('is-ready');
    ratSync(r);
    ratFit(r);
  }
  // On a touch screen every shape is a target as high as it is wide (MOBILE-04), so a row that has no room for that many of them is set in two rows (is-wrapped, --mat-rating-cols):
  // the row is measured as one row (its cells shrink when it does not fit), and again when the width of the field changes.
  function ratFit(r) {
    if (r.readonly || !r.stars) { return; }
    var el = r.root, width = el.clientWidth, cell = r.stars.querySelector('.mat-rating__cell');
    if (!width || !cell || width === r.fitWidth) { return; }
    el.classList.remove('is-wrapped');
    if (window.matchMedia && window.matchMedia('(pointer: coarse)').matches && cell.getBoundingClientRect().width < parseFloat(getComputedStyle(cell).height) - 0.5) {
      el.style.setProperty('--mat-rating-cols', String(Math.ceil(r.max / 2)));
      el.classList.add('is-wrapped');
    }
    r.fitWidth = el.clientWidth;
  }
  function initRatings(scope) { $$('[data-rating]', scope).forEach(ratInit); }

  // the value under the pointer: the first value that can be chosen at or above its place in the row (the left half of a star is the half value where there is one)
  // (set in two rows, y tells the row: the first half of the height is the first row)
  function ratAt(r, x, y) {
    var box = r.stars.getBoundingClientRect(), raw = 0, i;
    if (box.width && r.root.classList.contains('is-wrapped')) {
      var cols = Math.ceil(r.max / 2), row = box.height && y !== undefined && y - box.top >= box.height / 2 ? 1 : 0;
      raw = row * cols + Math.max(0, Math.min(1, (x - box.left) / box.width)) * cols;
    } else if (box.width) {
      raw = Math.max(0, Math.min(1, (x - box.left) / box.width)) * r.max;
    }
    for (i = 0; i < r.values.length; i += 1) { if (r.values[i] >= raw - 1e-9) { return r.values[i]; } }
    return r.values[r.values.length - 1];
  }
  function ratPreview(r, v) {
    if (v !== null && ratDisabled(r)) { v = null; }
    if (r.preview === v) { return; }
    r.preview = v;
    ratSync(r);
  }
  function ratFire(input) {
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }
  // Choose a value (0 clears): the radio that has it is checked and the browser's own events follow; without a radio for "no rating" the checked one is unchecked.
  function ratChoose(r, v) {
    if (v === ratValue(r)) { return true; }
    if (v === 0) {
      if (r.none) { r.none.el.checked = true; ratFire(r.none.el); return true; }
      var on = r.inputs.filter(function (i) { return i.el.checked; })[0];
      if (on) { on.el.checked = false; ratFire(on.el); }
      return true;
    }
    var hit = r.inputs.filter(function (i) { return Math.abs(i.v - v) < 1e-9; })[0];
    if (!hit) { return false; }
    hit.el.checked = true;
    ratFire(hit.el);
    return true;
  }
  function ratPick(r, v) {
    r.preview = null;
    if (ratDisabled(r) || v === undefined) { ratSync(r); return; }
    if (v === ratValue(r) && r.clearable) { ratChoose(r, 0); }
    else {
      ratChoose(r, v);
      var on = r.inputs.filter(function (i) { return i.el.checked; })[0];
      if (on) { on.el.focus({ preventScroll: true }); }
    }
    ratSync(r);
  }
  function ratGet(el) { var r = ratOf(el); return r ? ratValue(r) : null; }
  function ratSet(el, value) {
    var host = el && el.closest ? el.closest('[data-rating]') : null, r;
    if (host && !host._matRating) { ratInit(host); }
    r = ratOf(el);
    if (!r) { return null; }
    var v = Math.max(0, Math.min(r.max, parseFloat(value) || 0));
    if (r.readonly) { host.setAttribute('data-value', String(v)); ratSync(r); return v; }
    ratChoose(r, v);
    ratSync(r);
    return ratValue(r);
  }

  // every change of the choice (a click on a shape, a key, a script) is told to the page, after the native events
  doc.addEventListener('change', function (e) {
    var r = e.target.matches && e.target.matches('.mat-rating__input') ? ratOf(e.target) : null;
    if (!r || r.readonly) { return; }
    r.preview = null;
    ratSync(r);
    r.root.dispatchEvent(new CustomEvent('mat:rating-change', { bubbles: true, detail: { value: ratValue(r), rating: r.root } }));
  });
  doc.addEventListener('reset', function (e) {
    setTimeout(function () { $$('[data-rating]', e.target).forEach(function (w) { if (w._matRating && !w._matRating.readonly) { ratSync(w._matRating); } }); }, 0);
  }, true);
  // ------------------------------------------------------------------------------------------------ tree view (LIST-17, matui.lists.css)
  // A hierarchy of any depth: folders, categories, areas. The markup is a nested list that works (all open) without the script:
  //   <ul class="mat-tree" data-tree aria-label="Files"><li class="mat-tree__item"><div class="mat-tree__row">icon, .mat-tree__label, .mat-tree__count, .mat-tree__actions</div>
  //     <ul class="mat-tree__group"><li class="mat-tree__item">...</li></ul></li></ul>
  // The script follows the ARIA tree pattern: role tree, treeitem and group; aria-level, aria-setsize and aria-posinset on every item (counted over the items that are shown);
  // aria-expanded on a parent (the CSS reads it); aria-selected (one chosen) or, with data-multiple, aria-checked true, false or mixed; one tab stop (the item that was used last
  // has tabindex 0, the others -1, and so have the buttons in .mat-tree__actions of the other rows). The focus is on the item, not on a control in it.
  // Keys: Down and Up walk the items that are shown, Right opens a parent or goes to its first child, Left closes it or goes to the parent, Home and End, Enter and Space choose
  // (Space ticks the box with data-multiple), * opens all parents of the level, a letter goes to the next item that starts with what was typed. The level of an item is written into
  // --mat-tree-level (the CSS makes the indentation and the guide lines of it). With data-multiple a parent is checked when all of its items are, in between when some are, and
  // checking it checks all of them. Lazy items (data-lazy) fire mat:tree-load when they open and the app answers with MatUi.tree.setChildren(item, html), or with null when it failed.
  // What the user opened or closed is remembered with data-tree-key (per app, tree and item, like the groups of a table); an item with data-tree-fixed stays as drawn.
  // The texts are English and German (TR_TEXT: both must have every key); data-tr-<name> on <html> changes one.
  var TR_KEY = 'mat-tree';
  var TR_TEXT = {
    en: { tree: 'Tree', none: 'No matches', one: '1 match', many: '{0} matches', loading: 'Loading {0}', failed: '{0} could not be loaded', empty: '{0} is empty', selected: '{0} selected' },
    de: { tree: 'Baumansicht', none: 'Keine Treffer', one: '1 Treffer', many: '{0} Treffer', loading: '{0} wird geladen', failed: '{0} konnte nicht geladen werden', empty: '{0} ist leer', selected: '{0} ausgewählt' }
  };

  function trText(key, a) {
    var table = TR_TEXT[(root.lang || 'en').slice(0, 2).toLowerCase()] || TR_TEXT.en;
    var s = root.getAttribute('data-tr-' + key.toLowerCase()) || table[key] || TR_TEXT.en[key] || key;
    return s.replace('{0}', function () { return a === undefined ? '' : a; });
  }

  function trChild(el, cls) {
    for (var c = el.firstElementChild; c; c = c.nextElementSibling) { if (c.classList.contains(cls)) { return c; } }
    return null;
  }
  function trItems(group) {
    return group ? Array.prototype.filter.call(group.children, function (c) { return c.classList.contains('mat-tree__item'); }) : [];
  }
  function trGroup(item) { return trChild(item, 'mat-tree__group'); }
  function trRow(item) { return trChild(item, 'mat-tree__row'); }
  function trParent(item) { var up = item.parentElement; return up ? up.closest('.mat-tree__item') : null; }
  function trTreeOf(el) { var e = el && el.closest ? el.closest('[data-tree]') : null; return e && e._matTree ? e._matTree : null; }
  function trOpen(item) { return item.getAttribute('aria-expanded') === 'true'; }
  function trName(item) {
    var row = trRow(item), l = row && $('.mat-tree__label', row);
    return l ? l.textContent.replace(/\s+/g, ' ').trim() : '';
  }
  function trValue(item) { return item.getAttribute('data-value') || trName(item); }
  // what is remembered of an item: its own id, or the names from the top down
  function trId(item) {
    var own = item.getAttribute('data-tree-id');
    if (own) { return own; }
    var parts = [];
    for (var i = item; i; i = trParent(i)) { parts.unshift(trName(i)); }
    return parts.join('/');
  }
  function trFold(s) { return String(s).normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/\s+/g, ' ').toLowerCase().trim(); }
  function trSay(t, text) { if (t.live) { t.live.textContent = text; } }

  // the items that can be reached with the arrow keys: not hidden, and every parent above them open
  function trVisible(el) {
    var out = [];
    (function walk(group) {
      trItems(group).forEach(function (item) {
        if (item.hidden) { return; }
        out.push(item);
        var sub = trGroup(item);
        if (sub && trOpen(item)) { walk(sub); }
      });
    })(el);
    return out;
  }

  // the place of an item among the items that are shown next to it (a filter hides some)
  function trNumber(group) {
    var shown = trItems(group).filter(function (i) { return !i.hidden; });
    shown.forEach(function (item, n) {
      item.setAttribute('aria-setsize', String(shown.length));
      item.setAttribute('aria-posinset', String(n + 1));
      var sub = trGroup(item);
      if (sub) { trNumber(sub); }
    });
  }

  // the chevron, the box of several, the name of the item for a screen reader (its label and its count, not what is below it)
  function trParts(t, item, row) {
    var label = $('.mat-tree__label', row);
    if (label) {
      if (!label.id) { label.id = nextId('mat-tree-l'); }
      var ids = [label.id];
      $$('.mat-tree__count', row).forEach(function (c) { if (!c.id) { c.id = nextId('mat-tree-c'); } ids.push(c.id); });
      item.setAttribute('aria-labelledby', ids.join(' '));
    }
    var toggle = trChild(row, 'mat-tree__toggle');
    if (!toggle) {
      toggle = doc.createElement('span');
      toggle.className = 'mat-tree__toggle';
      toggle.setAttribute('aria-hidden', 'true');
      toggle.innerHTML = icon('chevron-right');
      row.insertBefore(toggle, row.firstChild);
    }
    if (t.multiple && !trChild(row, 'mat-tree__check')) {
      var box = doc.createElement('span');
      box.className = 'mat-tree__check';
      box.setAttribute('aria-hidden', 'true');
      box.innerHTML = icon('check').replace('class="mat-icon"', 'class="mat-icon mat-tree__check-on"') + icon('minus').replace('class="mat-icon"', 'class="mat-icon mat-tree__check-mixed"');
      row.insertBefore(box, toggle.nextSibling);
    }
  }

  // the items of a group get their roles and their level; a parent is an item with a group of items or with data-lazy
  function trPrepare(t, group, level) {
    trItems(group).forEach(function (item) {
      var row = trRow(item);
      var sub = trGroup(item);
      var kids = trItems(sub);
      item.setAttribute('role', 'treeitem');
      item.setAttribute('aria-level', String(level));
      item.style.setProperty('--mat-tree-level', String(level - 1));
      item.tabIndex = -1;
      if (kids.length && item.hasAttribute('data-lazy')) { item.removeAttribute('data-lazy'); }
      var parent = kids.length > 0 || item.hasAttribute('data-lazy');
      if (sub) { sub.setAttribute('role', 'group'); }
      if (!parent) { item.removeAttribute('aria-expanded'); }
      else if (!item.hasAttribute('aria-expanded')) {
        item.setAttribute('aria-expanded', !item.hasAttribute('data-lazy') && (item.hasAttribute('data-expanded') || t.expandAll) ? 'true' : 'false');
      }
      if (row) { trParts(t, item, row); trControls(item, false); }
      if (kids.length) { trPrepare(t, sub, level + 1); }
    });
  }

  // the buttons in the actions of a row are in the tab order only while their row is the one with the tab stop; a link in the label is reached by Enter on the item
  function trControls(item, on) {
    var row = trRow(item);
    if (!row) { return; }
    $$('a[href], button, input, select, textarea, [tabindex]', row).forEach(function (c) { c.tabIndex = on && c.closest('.mat-tree__actions') ? 0 : -1; });
  }
  function trRove(t, item) {
    if (t.rover && t.rover !== item) { t.rover.tabIndex = -1; trControls(t.rover, false); }
    t.rover = item;
    item.tabIndex = 0;
    trControls(item, true);
  }
  function trFixRoving(t) {
    var shown = trVisible(t.el);
    if (t.rover && shown.indexOf(t.rover) >= 0) { return; }
    var pick = shown.filter(function (i) { return i.getAttribute('aria-selected') === 'true'; })[0] || shown[0];
    if (pick) { trRove(t, pick); } else if (t.rover) { t.rover.tabIndex = -1; trControls(t.rover, false); t.rover = null; }
  }
  function trFocus(t, item) { trRove(t, item); item.focus(); }

  // ---- several: the box of a parent follows what is below it
  function trCheckDown(item, on) {
    item.setAttribute('aria-checked', on ? 'true' : 'false');
    trItems(trGroup(item)).forEach(function (c) { trCheckDown(c, on); });
  }
  function trSyncParent(p) {
    var kids = trItems(trGroup(p));
    if (!kids.length) { return; }
    var on = kids.filter(function (k) { return k.getAttribute('aria-checked') === 'true'; }).length;
    var mixed = kids.some(function (k) { return k.getAttribute('aria-checked') === 'mixed'; });
    p.setAttribute('aria-checked', on === kids.length ? 'true' : on > 0 || mixed ? 'mixed' : 'false');
  }
  function trCheckUp(item) { for (var p = trParent(item); p; p = trParent(p)) { trSyncParent(p); } }

  // the states that the markup says (aria-selected or aria-checked on an item) made complete and consistent
  function trNormalise(t, scope) {
    var items = $$('.mat-tree__item', scope);
    if (!t.multiple) {
      items.forEach(function (i) { if (i.getAttribute('aria-selected') !== 'true') { i.setAttribute('aria-selected', 'false'); } });
      return;
    }
    items.forEach(function (i) { if (i.getAttribute('aria-checked') === 'true' && trItems(trGroup(i)).length) { trCheckDown(i, true); } });
    items.forEach(function (i) { if (!trItems(trGroup(i)).length) { i.setAttribute('aria-checked', i.getAttribute('aria-checked') === 'true' ? 'true' : 'false'); } else if (!i.hasAttribute('aria-checked')) { i.setAttribute('aria-checked', 'false'); } });
    items.slice().reverse().forEach(trSyncParent);
  }

  function trSelected(t) {
    return $$(t.multiple ? '.mat-tree__item[aria-checked="true"]' : '.mat-tree__item[aria-selected="true"]', t.el);
  }

  // choose an item (one), or tick or untick it (several); on: true, false, or undefined (several: the other state)
  function trChoose(item, on) {
    var t = trTreeOf(item);
    if (!t || item.getAttribute('aria-disabled') === 'true') { return; }
    if (t.multiple) {
      trCheckDown(item, on === undefined ? item.getAttribute('aria-checked') !== 'true' : on);
      trCheckUp(item);
    } else {
      var next = on !== false;
      if ((item.getAttribute('aria-selected') === 'true') === next) { return; }
      $$('.mat-tree__item[aria-selected="true"]', t.el).forEach(function (x) { x.setAttribute('aria-selected', 'false'); });
      item.setAttribute('aria-selected', next ? 'true' : 'false');
    }
    var picked = trSelected(t);
    if (t.multiple) { trSay(t, trText('selected', picked.filter(function (i) { return !trGroup(i) || i.hasAttribute('data-lazy'); }).length)); }
    item.dispatchEvent(new CustomEvent('mat:tree-select', { bubbles: true, detail: { tree: t.el, item: item, selected: picked, values: picked.map(trValue) } }));
  }

  // ---- open and close
  function trStore() { try { return JSON.parse(localStorage.getItem(TR_KEY) || '{}') || {}; } catch (e) { return {}; } }
  function trStoreKey(t, item) { return (root.getAttribute('data-app') || '') + ':' + t.key + ':' + trId(item); }
  function trRemember(t, item, open) {
    if (!t.key) { return; }
    var store = trStore();
    store[trStoreKey(t, item)] = open;
    try { localStorage.setItem(TR_KEY, JSON.stringify(store)); } catch (e) { /* ignore */ }
  }
  function trRestore(t) {
    if (!t.key) { return; }
    var store = trStore();
    $$('.mat-tree__item[aria-expanded]', t.el).forEach(function (item) {
      var saved = store[trStoreKey(t, item)];
      if (typeof saved === 'boolean' && !item.hasAttribute('data-lazy') && !item.hasAttribute('data-tree-fixed')) { item.setAttribute('aria-expanded', saved ? 'true' : 'false'); }
    });
  }

  // opts.quiet: no event and nothing remembered (a filter opens what holds a match)
  function trSetOpen(item, open, opts) {
    var t = trTreeOf(item);
    if (!t || !item.hasAttribute('aria-expanded') || trOpen(item) === open) { return; }
    var quiet = opts && opts.quiet;
    item.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (open && item.hasAttribute('data-lazy') && !item._matLoading) { trLoad(t, item); }
    // the focus was below the item that closes: it goes to the item
    if (!open && item !== doc.activeElement && item.contains(doc.activeElement)) { trFocus(t, item); }
    if (!quiet) {
      if (item._matWas !== undefined) { item._matWas = open; }
      trRemember(t, item, open);
      item.dispatchEvent(new CustomEvent('mat:tree-toggle', { bubbles: true, detail: { tree: t.el, item: item, open: open } }));
    }
    trFixRoving(t);
  }
  // what holds the item opens, without an event and without being remembered (it is not a choice of the user)
  function trReveal(item) { for (var p = trParent(item); p; p = trParent(p)) { trSetOpen(p, true, { quiet: true }); } }
  function trSetAll(scope, open) {
    $$('.mat-tree__item[aria-expanded]', scope).forEach(function (i) { if (!(open && i.hasAttribute('data-lazy'))) { trSetOpen(i, open); } });
  }

  // ---- lazy children
  function trLoad(t, item) {
    item._matLoading = true;
    item.classList.add('is-loading');
    item.setAttribute('aria-busy', 'true');
    trSay(t, trText('loading', trName(item)));
    item.dispatchEvent(new CustomEvent('mat:tree-load', { bubbles: true, detail: { tree: t.el, item: item, id: trId(item), value: trValue(item) } }));
  }
  // the answer to mat:tree-load: the markup of the children (li.mat-tree__item); null when it failed (the item closes and can be tried again), an empty text when there are none
  function trSetChildren(item, html) {
    var t = trTreeOf(item);
    if (!t || !item.classList.contains('mat-tree__item')) { return; }
    item._matLoading = false;
    item.classList.remove('is-loading');
    item.removeAttribute('aria-busy');
    if (html === null || html === undefined || html === false) {
      item.setAttribute('aria-expanded', 'false');
      trSay(t, trText('failed', trName(item)));
      trFixRoving(t);
      return;
    }
    var sub = trGroup(item);
    if (!sub) { sub = doc.createElement('ul'); sub.className = 'mat-tree__group'; item.appendChild(sub); }
    sub.innerHTML = String(html);
    item.removeAttribute('data-lazy');
    var kids = trItems(sub);
    if (!kids.length) {
      sub.remove();
      item.removeAttribute('aria-expanded');
      trSay(t, trText('empty', trName(item)));
      trFixRoving(t);
      return;
    }
    sub.setAttribute('role', 'group');
    trPrepare(t, sub, (parseInt(item.getAttribute('aria-level'), 10) || 1) + 1);
    if (t.multiple && item.getAttribute('aria-checked') === 'true') { kids.forEach(function (k) { trCheckDown(k, true); }); }
    trNormalise(t, sub);
    if (t.multiple) { trCheckUp(kids[0]); }
    trNumber(sub);
    $$('[data-tree-filter="' + t.el.id + '"]').forEach(function (f) { if (f.value) { trFilter(f); } });
    trFixRoving(t);
  }

  // ---- filter: a search field hides the items that do not match and keeps the parents of those that do, open
  function trEmpty(el) {
    for (var n = el.nextElementSibling; n && !n.hasAttribute('data-tree'); n = n.nextElementSibling) { if (n.classList.contains('mat-tree__empty')) { return n; } }
    return null;
  }
  function trFilter(field) {
    var el = doc.getElementById(field.getAttribute('data-tree-filter'));
    var t = el && el._matTree;
    if (!t) { return; }
    var text = trFold(field.value);
    var matches = 0;
    var all = $$('.mat-tree__item', el);
    if (!text) {
      all.forEach(function (i) {
        i.hidden = false;
        if (i._matWas !== undefined) { var was = i._matWas; i._matWas = undefined; trSetOpen(i, was, { quiet: true }); }
      });
    } else {
      (function mark(group) {
        var any = false;
        trItems(group).forEach(function (item) {
          var own = trFold(item.getAttribute('data-tree-search') || trName(item)).indexOf(text) >= 0;
          var sub = trGroup(item);
          var below = sub ? mark(sub) : false;
          if (own) { matches += 1; }
          item.hidden = !(own || below);
          if (item.hasAttribute('aria-expanded')) {
            if (item._matWas === undefined) { item._matWas = trOpen(item); }
            trSetOpen(item, below ? true : item._matWas, { quiet: true });
          }
          if (own || below) { any = true; }
        });
        return any;
      })(el);
    }
    trNumber(el);
    trFixRoving(t);
    var empty = trEmpty(el);
    if (empty) {
      if (!empty.textContent.trim()) { empty.textContent = trText('none'); }
      empty.hidden = !text || matches > 0;
    }
    trSay(t, !text ? '' : matches === 0 ? trText('none') : matches === 1 ? trText('one') : trText('many', matches));
    el.dispatchEvent(new CustomEvent('mat:tree-filter', { bubbles: true, detail: { tree: el, text: field.value, shown: matches, total: all.length } }));
  }
  doc.addEventListener('input', function (e) { if (e.target.matches && e.target.matches('[data-tree-filter]')) { trFilter(e.target); } });

  // ---- the tree
  function trInit(el) {
    if (el._matTree) { return; }
    var t = { el: el, multiple: el.hasAttribute('data-multiple'), expandAll: el.hasAttribute('data-expand-all'), key: el.getAttribute('data-tree-key') || '', rover: null, typed: '', typedAt: 0, live: null };
    el._matTree = t;
    el.setAttribute('role', 'tree');
    if (!el.hasAttribute('aria-label') && !el.hasAttribute('aria-labelledby')) { el.setAttribute('aria-label', trText('tree')); }
    t.live = doc.createElement('span');
    t.live.className = 'mat-sr-only';
    t.live.setAttribute('role', 'status');
    t.live.setAttribute('aria-live', 'polite');
    el.parentNode.insertBefore(t.live, el.nextSibling);
    trPrepare(t, el, 1);
    trRestore(t);
    trNormalise(t, el);
    trNumber(el);
    trFixRoving(t);
    el.classList.add('is-ready');
    $$('[data-tree-filter="' + el.id + '"]').forEach(function (f) { if (f.value) { trFilter(f); } });
  }
  function initTrees(scope) {
    if (scope && scope.matches && scope.matches('[data-tree]')) { trInit(scope); }
    $$('[data-tree]', scope).forEach(trInit);
  }

  // a click: the chevron opens, the box ticks, the rest of the row chooses (and, with data-tree-toggle="row", opens); a link in the label is followed as usual
  doc.addEventListener('click', function (e) {
    var target = e.target;
    if (!target.closest) { return; }
    var all = target.closest('[data-tree-set]');
    if (all) {
      var want = all.getAttribute('data-tree-target');
      var scope = (want && $(want)) || $('[data-tree]');
      var mode = all.getAttribute('data-tree-set');
      if (scope) { trSetAll(scope, mode === 'open' ? true : mode === 'closed' ? false : !$$('.mat-tree__item[aria-expanded="true"]', scope).length); }
      return;
    }
    var row = target.closest('.mat-tree__row');
    var item = row && row.parentNode;
    var t = item && item.classList && item.classList.contains('mat-tree__item') ? trTreeOf(item) : null;
    if (!t || target.closest('.mat-tree__actions')) { return; }
    trRove(t, item);
    if (target.closest('.mat-tree__toggle')) { item.focus(); trSetOpen(item, !trOpen(item)); return; }
    if (item.getAttribute('aria-disabled') === 'true') { item.focus(); return; }
    if (target.closest('.mat-tree__check')) { item.focus(); trChoose(item); return; }
    item.focus();
    trChoose(item, t.multiple ? undefined : true);
    if (t.el.getAttribute('data-tree-toggle') === 'row' && item.hasAttribute('aria-expanded')) { trSetOpen(item, !trOpen(item)); }
  });
  doc.addEventListener('dblclick', function (e) {
    var row = e.target.closest && e.target.closest('.mat-tree__row');
    var item = row && row.parentNode;
    var t = item && item.classList && item.classList.contains('mat-tree__item') ? trTreeOf(item) : null;
    if (!t || t.el.getAttribute('data-tree-toggle') === 'row' || e.target.closest('.mat-tree__actions, .mat-tree__toggle, .mat-tree__check')) { return; }
    trSetOpen(item, !trOpen(item));
  });
  doc.addEventListener('focusin', function (e) {
    var item = e.target.closest && e.target.closest('.mat-tree__item');
    var t = item && trTreeOf(item);
    if (t) { trRove(t, item); }
  });

  // the keys of the tree pattern; they work only while the focus is on an item (a button in its actions has its own keys)
  doc.addEventListener('keydown', function (e) {
    var item = e.target;
    if (!item.classList || !item.classList.contains('mat-tree__item') || e.altKey || e.ctrlKey || e.metaKey) { return; }
    var t = trTreeOf(item);
    if (!t) { return; }
    var shown = trVisible(t.el);
    var at = shown.indexOf(item);
    var rtl = getComputedStyle(t.el).direction === 'rtl';
    var key = e.key === 'ArrowRight' ? (rtl ? 'ArrowLeft' : 'ArrowRight') : e.key === 'ArrowLeft' ? (rtl ? 'ArrowRight' : 'ArrowLeft') : e.key;
    var go = null;
    // a key that is not a letter ends the word that was being typed
    if (key.length > 1) { t.typed = ''; }
    if (key === 'ArrowDown') { go = shown[at + 1]; }
    else if (key === 'ArrowUp') { go = shown[at - 1]; }
    else if (key === 'Home') { go = shown[0]; }
    else if (key === 'End') { go = shown[shown.length - 1]; }
    else if (key === 'ArrowRight') {
      if (item.hasAttribute('aria-expanded') && !trOpen(item)) { trSetOpen(item, true); } else if (trOpen(item)) { go = trItems(trGroup(item)).filter(function (c) { return !c.hidden; })[0]; }
    } else if (key === 'ArrowLeft') {
      if (trOpen(item)) { trSetOpen(item, false); } else { go = trParent(item); }
    } else if (key === '*') {
      trItems(item.parentElement).forEach(function (s) { if (!(s.hasAttribute('data-lazy'))) { trSetOpen(s, true); } });
    } else if (key === 'Enter' || (key === ' ' && !t.typed)) {
      if (item.getAttribute('aria-disabled') !== 'true') {
        var link = $('.mat-tree__label[href]', trRow(item) || item);
        trChoose(item, t.multiple ? undefined : true);
        if (link && key === 'Enter') { link.click(); }
        if (t.el.getAttribute('data-tree-toggle') === 'row' && item.hasAttribute('aria-expanded')) { trSetOpen(item, !trOpen(item)); }
      }
    } else if (key.length === 1 && key !== ' ' || (key === ' ' && t.typed)) {
      // the first letters: what is typed within 700 ms is one word; the search starts after this item, or at it while the word grows
      var now = Date.now();
      t.typed = (now - t.typedAt < 700 ? t.typed : '') + key;
      t.typedAt = now;
      setTimeout(function () { if (Date.now() - t.typedAt >= 690) { t.typed = ''; } }, 700);
      var word = trFold(t.typed);
      for (var n = 0; n < shown.length; n += 1) {
        var cand = shown[(at + (word.length > 1 ? 0 : 1) + n) % shown.length];
        if (trFold(trName(cand)).indexOf(word) === 0) { go = cand; break; }
      }
    } else { return; }
    e.preventDefault();
    if (go) { trFocus(t, go); }
  });

  // MatUi.tree: set up trees added later, open and close, choose, the children of a lazy item, every parent at once, and what is chosen now
  var tree = {
    init: initTrees,
    expand: function (item) { if (item && trTreeOf(item)) { trReveal(item); trSetOpen(item, true); } },
    collapse: function (item) { if (item && trTreeOf(item)) { trSetOpen(item, false); } },
    select: function (item, on) { if (item && trTreeOf(item)) { if (on !== false) { trReveal(item); } trChoose(item, on); } },
    setChildren: trSetChildren,
    setAll: function (el, open) { if (el && el._matTree) { trSetAll(el, open !== false); } },
    get: function (el) { var t = el && el._matTree; return t ? { items: trSelected(t), values: trSelected(t).map(trValue) } : null; }
  };

  // ------------------------------------------------------------------------------------------------ file upload (FORM-21, matui.forms.css)
  // <div class="mat-upload" data-upload><input class="mat-upload__input" type="file" id="" name="" multiple accept=".pdf"></div> is the native file field
  // without the script, and it stays the thing that is validated and posted (required, accept, multiple work). The script hides it and draws a drop zone (files
  // dragged onto it, or a click, Enter or Space on its button, which is the one Tab stop), the list of the chosen files (name, size, a thumbnail for an image, a
  // remove button named by the file) and a line for what was refused and why. Every file that arrives (from the dialog, a drop, a paste or MatUi.upload.add) is
  // checked against accept, data-max-size and data-max-files; a refused file is named with the reason in words, the others are still added. The files of the
  // real input are kept in step with the list (a DataTransfer), so the form posts exactly what the list shows. Without `multiple` a new file replaces the old one.
  // data-paste adds the images of the clipboard (only while the focus is in the control). An app that uploads by itself draws progress and failures with
  // MatUi.upload.setProgress / setError. Events on the wrapper: mat:upload-change (detail.files, added, removed, source) and mat:upload-refused (detail.refused).
  var UP_TEXT = {
    en: {
      dropOne: 'Drop a file here or choose one', dropMany: 'Drop files here or choose them', tapOne: 'Tap to choose a file', tapMany: 'Tap to choose files', dropping: 'Drop to add',
      upToFiles: 'up to {0} files', upToSize: 'up to {0}', upToSizeEach: 'up to {0} each', pasteImage: 'you can also paste an image',
      typeImages: 'images', typeAudio: 'audio', typeVideo: 'video', typeText: 'text',
      files: 'Chosen files', remove: 'Remove {0}', added: '{0} added', addedMany: '{0} files added', removed: '{0} removed',
      refusedType: '{0}: this type of file is not accepted. Allowed: {1}.', refusedSize: '{0}: the file is too large ({1}). The limit is {2}.',
      refusedCount: '{0}: not added. At most {1} files are allowed.', refusedOne: '{0}: not added. Only one file is allowed.',
      refusedDuplicate: '{0}: this file has already been added.', refusedFolder: '{0}: a folder cannot be uploaded. Choose the files in it instead.',
      refusedPaste: 'Only images can be pasted here.',
      percent: '{0} %', done: 'Uploaded', failed: 'Upload failed', pastedName: 'image'
    },
    de: {
      dropOne: 'Datei hierher ziehen oder auswählen', dropMany: 'Dateien hierher ziehen oder auswählen', tapOne: 'Tippen, um eine Datei auszuwählen', tapMany: 'Tippen, um Dateien auszuwählen', dropping: 'Zum Hinzufügen loslassen',
      upToFiles: 'bis zu {0} Dateien', upToSize: 'bis zu {0}', upToSizeEach: 'je bis zu {0}', pasteImage: 'ein Bild lässt sich auch einfügen',
      typeImages: 'Bilder', typeAudio: 'Audio', typeVideo: 'Video', typeText: 'Text',
      files: 'Ausgewählte Dateien', remove: '{0} entfernen', added: '{0} hinzugefügt', addedMany: '{0} Dateien hinzugefügt', removed: '{0} entfernt',
      refusedType: '{0}: Dieser Dateityp wird nicht angenommen. Erlaubt: {1}.', refusedSize: '{0}: Die Datei ist zu groß ({1}). Das Limit ist {2}.',
      refusedCount: '{0}: nicht hinzugefügt. Es sind höchstens {1} Dateien erlaubt.', refusedOne: '{0}: nicht hinzugefügt. Es ist nur eine Datei erlaubt.',
      refusedDuplicate: '{0}: Diese Datei wurde schon hinzugefügt.', refusedFolder: '{0}: Ein Ordner kann nicht hochgeladen werden. Die Dateien darin lassen sich einzeln auswählen.',
      refusedPaste: 'Hier lassen sich nur Bilder einfügen.',
      percent: '{0} %', done: 'Hochgeladen', failed: 'Hochladen fehlgeschlagen', pastedName: 'bild'
    }
  };

  function upText(key, a, b, c) {
    var table = UP_TEXT[(root.lang || 'en').slice(0, 2).toLowerCase()] || UP_TEXT.en;
    var s = root.getAttribute('data-up-' + key.toLowerCase()) || table[key] || UP_TEXT.en[key] || key;
    var args = [a, b, c];
    return s.replace(/\{(\d)\}/g, function (m, i) { var v = args[+i]; return v === undefined ? '' : v; });
  }

  // "10MB", "512 KB", "1.5 GB" or a number of bytes (1 KB is 1024 bytes); 0 when there is no limit or it cannot be read
  function upSizeOf(text) {
    var m = /^\s*(\d+(?:[.,]\d+)?)\s*(b|kb|k|mb|m|gb|g)?\s*$/i.exec(text || '');
    if (!m) { return 0; }
    var unit = (m[2] || 'b').toLowerCase().charAt(0);
    return Math.round(parseFloat(m[1].replace(',', '.')) * (unit === 'g' ? 1073741824 : unit === 'm' ? 1048576 : unit === 'k' ? 1024 : 1));
  }
  function upFormatSize(bytes) {
    var units = ['B', 'KB', 'MB', 'GB'], n = bytes, i = 0, text;
    while (n >= 1024 && i < units.length - 1) { n /= 1024; i += 1; }
    try { text = n.toLocaleString(root.lang || 'en', { maximumFractionDigits: i === 0 ? 0 : 1, useGrouping: false }); } catch (err) { text = String(Math.round(n * 10) / 10); }
    return text + ' ' + units[i];
  }

  // accept as the browser reads it: ".pdf" (the end of the name), a kind such as image followed by a slash and a star (every type of that kind), "application/pdf" (one type)
  var UP_KIND = /^([a-z]+)\/\*$/;
  var UP_KIND_TEXT = { image: 'typeImages', audio: 'typeAudio', video: 'typeVideo', text: 'typeText' };
  function upAcceptList(input) { return (input.getAttribute('accept') || '').split(',').map(function (s) { return s.trim().toLowerCase(); }).filter(Boolean); }
  function upAccepts(list, file) {
    if (!list.length) { return true; }
    var name = file.name.toLowerCase(), type = (file.type || '').toLowerCase();
    return list.some(function (t) {
      var kind = UP_KIND.exec(t);
      if (t.charAt(0) === '.') { return name.slice(-t.length) === t; }
      if (kind) { return type.indexOf(kind[1] + '/') === 0; }
      return type === t;
    });
  }
  // the accepted types as people say them: "PDF, PNG, images"
  function upTypeLabel(list) {
    var out = [];
    list.forEach(function (t) {
      var s, kind = UP_KIND.exec(t);
      if (t.charAt(0) === '.') { s = t.slice(1).toUpperCase(); }
      else if (kind) { s = UP_KIND_TEXT[kind[1]] ? upText(UP_KIND_TEXT[kind[1]]) : kind[1]; }
      else { s = (t.split('/')[1] || t).split('+')[0].replace(/^x-/, '').toUpperCase(); }
      if (s && out.indexOf(s) < 0) { out.push(s); }
    });
    return out.join(', ');
  }
  function upIconFor(file) {
    var n = file.name.toLowerCase(), t = (file.type || '').toLowerCase();
    if (t.indexOf('image/') === 0) { return 'image'; }
    if (t === 'application/pdf' || t.indexOf('text/') === 0 || /\.(pdf|txt|md|doc|docx|odt|rtf)$/.test(n)) { return 'file-text'; }
    if (/\.(zip|tar|gz|tgz|7z|rar)$/.test(n)) { return 'archive'; }
    return 'file';
  }
  function upSupported() {
    try { return typeof DataTransfer === 'function' && !!new DataTransfer().files; } catch (err) { return false; }
  }
  function upTouch() { return !!(window.matchMedia && window.matchMedia('(pointer: coarse)').matches); }

  // what the wrapper is told: the limits of the real input and of the data attributes (read when they are needed, so that a change of one is seen)
  function upConf(u) {
    var multiple = u.input.multiple, max = parseInt(u.wrap.getAttribute('data-max-files'), 10);
    return { multiple: multiple, room: multiple ? (max > 0 ? max : Infinity) : 1, max: max > 0 ? max : 0, maxSize: upSizeOf(u.wrap.getAttribute('data-max-size')), accept: upAcceptList(u.input) };
  }

  function upOf(ref) {
    var el = typeof ref === 'string' ? (doc.getElementById(ref) || $(ref)) : ref;
    var wrap = el && el.closest ? el.closest('.mat-upload') : null;
    return wrap && wrap._matUpload ? wrap._matUpload : null;
  }
  function upSay(u, text) {
    u.live.textContent = '';
    if (text) { u.live.textContent = text; }
  }

  // The words in the zone: what to do, and the limits (so that the person knows them before choosing, not only when a file is refused).
  function upPaintZone(u) {
    var c = upConf(u), parts = [], types = upTypeLabel(c.accept);
    u.title.textContent = u.wrap.classList.contains('is-drag') ? upText('dropping') : upText((upTouch() ? 'tap' : 'drop') + (c.multiple ? 'Many' : 'One'));
    if (types) { parts.push(types); }
    if (c.multiple && c.max) { parts.push(upText('upToFiles', c.max)); }
    if (c.maxSize) { parts.push(upText(c.multiple ? 'upToSizeEach' : 'upToSize', upFormatSize(c.maxSize))); }
    if (u.wrap.hasAttribute('data-paste')) { parts.push(upText('pasteImage')); }
    u.limits.textContent = parts.length ? parts.join(' · ').replace(/^./, function (ch) { return ch.toUpperCase(); }) : '';
    u.limits.hidden = !parts.length;
  }

  // the state of the real input is the one of the control: disabled, and the red mark that showError() puts on it
  function upState(u) {
    var off = u.input.disabled;
    u.wrap.classList.toggle('is-disabled', off);
    u.wrap.classList.toggle('is-invalid', u.input.getAttribute('aria-invalid') === 'true');
    u.browse.disabled = off;
    u.entries.forEach(function (e) { e.remove.disabled = off; });
    upPaintZone(u);
  }

  // the real input holds exactly the chosen files (a form posts what the list shows)
  function upSync(u) {
    var dt = new DataTransfer();
    u.entries.forEach(function (e) { dt.items.add(e.file); });
    u.input.files = dt.files;
    u.list.hidden = u.entries.length === 0;
  }

  function upPaintEntry(e) {
    var state = e.error ? 'error' : e.done ? 'done' : e.progress !== null ? 'uploading' : '';
    e.el.classList.toggle('is-uploading', state === 'uploading');
    e.el.classList.toggle('is-done', state === 'done');
    e.el.classList.toggle('is-error', state === 'error');
    e.meta.textContent = upFormatSize(e.file.size) + (state === 'uploading' ? ' · ' + upText('percent', Math.round(e.progress)) : '');
    if (state === 'done') { e.meta.insertAdjacentHTML('beforeend', ' · <span class="mat-upload__state">' + icon('check-circle') + esc(upText('done')) + '</span>'); }
    e.bar.hidden = state !== 'uploading';
    if (state === 'uploading') {
      e.bar.setAttribute('aria-valuenow', String(Math.round(e.progress)));
      e.fill.style.setProperty('--mat-up-progress', e.progress + '%');
    }
    e.problem.hidden = state !== 'error';
    e.problem.innerHTML = state === 'error' ? icon('alert') + '<span>' + esc(e.error) + '</span>' : '';
    if (state === 'error') { e.remove.setAttribute('aria-describedby', e.problem.id); } else { e.remove.removeAttribute('aria-describedby'); }
  }

  function upEntry(u, file) {
    var e = { file: file, url: '', progress: null, done: false, error: '' };
    var id = nextId('mat-up-file');
    if (u.wrap.getAttribute('data-preview') !== 'none' && /^image\/(png|jpe?g|gif|webp|avif|bmp|svg\+xml)$/i.test(file.type) && window.URL && URL.createObjectURL) { e.url = URL.createObjectURL(file); }
    e.el = doc.createElement('li');
    e.el.className = 'mat-upload__item';
    e.thumb = doc.createElement('span');
    e.thumb.className = 'mat-upload__thumb';
    e.thumb.setAttribute('aria-hidden', 'true');
    var fallback = function () { e.thumb.textContent = ''; e.thumb.innerHTML = icon(upIconFor(file)); };
    if (e.url) {
      var img = doc.createElement('img');
      img.alt = '';
      img.src = e.url;
      img.addEventListener('error', fallback);
      e.thumb.appendChild(img);
    } else { fallback(); }
    var body = doc.createElement('span');
    body.className = 'mat-upload__body';
    var name = doc.createElement('span');
    name.className = 'mat-upload__name';
    name.textContent = file.name;
    e.meta = doc.createElement('span');
    e.meta.className = 'mat-upload__meta';
    e.bar = doc.createElement('span');
    e.bar.className = 'mat-upload__bar';
    e.bar.setAttribute('role', 'progressbar');
    e.bar.setAttribute('aria-label', file.name);
    e.bar.setAttribute('aria-valuemin', '0');
    e.bar.setAttribute('aria-valuemax', '100');
    e.bar.hidden = true;
    e.fill = doc.createElement('span');
    e.fill.className = 'mat-upload__fill';
    e.bar.appendChild(e.fill);
    e.problem = doc.createElement('span');
    e.problem.className = 'mat-upload__problem';
    e.problem.id = id + '-problem';
    e.problem.hidden = true;
    body.appendChild(name);
    body.appendChild(e.meta);
    body.appendChild(e.bar);
    body.appendChild(e.problem);
    e.remove = doc.createElement('button');
    e.remove.type = 'button';
    e.remove.className = 'mat-btn mat-btn--ghost mat-btn--icon mat-upload__remove';
    e.remove.setAttribute('aria-label', upText('remove', file.name));
    e.remove.title = upText('remove', file.name);
    e.remove.innerHTML = icon('x');
    e.remove.disabled = u.input.disabled;
    e.el.appendChild(e.thumb);
    e.el.appendChild(body);
    e.el.appendChild(e.remove);
    e.el._matUploadEntry = e;
    u.list.appendChild(e.el);
    u.entries.push(e);
    upPaintEntry(e);
    return e;
  }
  function upDrop(u, e) {
    if (e.url) { URL.revokeObjectURL(e.url); e.url = ''; }
    e.el.remove();
    u.entries.splice(u.entries.indexOf(e), 1);
  }

  // what the person is told about the files that were refused: a list at the field, a live region, in words with an icon (never only a colour)
  function upIssues(u, messages) {
    u.status.textContent = '';
    if (!messages.length) { return; }
    var ul = doc.createElement('ul');
    ul.className = 'mat-upload__issues';
    messages.forEach(function (m) {
      var li = doc.createElement('li');
      li.innerHTML = icon('alert') + '<span></span>';
      li.lastChild.textContent = m;
      ul.appendChild(li);
    });
    u.status.appendChild(ul);
  }

  function upFileList(u) { return u.entries.map(function (e) { return e.file; }); }
  function upChanged(u, source, added, removed) {
    u.wrap.dispatchEvent(new CustomEvent('mat:upload-change', { bubbles: true, detail: { files: upFileList(u), added: added, removed: removed, source: source } }));
  }
  // a change that did not come from the dialog is told to the form like the browser would: input and change on the real input (the control ignores them)
  function upTell(u) {
    u.busy = true;
    try {
      u.input.dispatchEvent(new Event('input', { bubbles: true }));
      u.input.dispatchEvent(new Event('change', { bubbles: true }));
    } finally { u.busy = false; }
  }

  // Files arrive (source: picker, drop, paste, api). Each is checked in this order: folder, type, size, already added, room. What is refused is named; the rest is added.
  // Without `multiple` the first file replaces the one that was there. folders: the indexes of the files that are folders (a drop).
  function upAdd(u, files, source, folders) {
    var c = upConf(u), refused = [], accepted = [], removed = [], added = [], types = upTypeLabel(c.accept), have = upFileList(u);
    function refuse(file, reason, message) { refused.push({ file: file, name: file.name, reason: reason, message: message }); }
    function same(a, b) { return a.name === b.name && a.size === b.size && a.lastModified === b.lastModified; }
    files.forEach(function (file, i) {
      if (folders && folders.indexOf(i) >= 0) { refuse(file, 'folder', upText('refusedFolder', file.name)); }
      else if (!upAccepts(c.accept, file)) { refuse(file, 'type', upText('refusedType', file.name, types)); }
      else if (c.maxSize && file.size > c.maxSize) { refuse(file, 'size', upText('refusedSize', file.name, upFormatSize(file.size), upFormatSize(c.maxSize))); }
      else if (have.concat(accepted).some(function (o) { return same(o, file); })) { refuse(file, 'duplicate', upText('refusedDuplicate', file.name)); }
      else { accepted.push(file); }
    });
    if (c.room === 1) {
      accepted.slice(1).forEach(function (f) { refuse(f, 'count', upText('refusedOne', f.name)); });
      accepted = accepted.slice(0, 1);
      if (accepted.length) { removed = u.entries.slice(); }
    } else {
      var free = Math.max(0, c.room - u.entries.length);
      accepted.slice(free).forEach(function (f) { refuse(f, 'count', upText('refusedCount', f.name, c.room)); });
      accepted = accepted.slice(0, free);
    }
    var removedFiles = removed.map(function (e) { return e.file; });
    removed.forEach(function (e) { upDrop(u, e); });
    accepted.forEach(function (f) { added.push(upEntry(u, f).file); });
    upSync(u);
    upIssues(u, refused.map(function (r) { return r.message; }));
    if (added.length) { upSay(u, added.length === 1 ? upText('added', added[0].name) : upText('addedMany', added.length)); }
    if (added.length || removedFiles.length) {
      upChanged(u, source, added, removedFiles);
      if (source !== 'picker') { upTell(u); }
    }
    if (refused.length) { u.wrap.dispatchEvent(new CustomEvent('mat:upload-refused', { bubbles: true, detail: { refused: refused, source: source } })); }
    return { added: added, removed: removedFiles, refused: refused };
  }

  function upEntryOf(u, ref) {
    if (typeof ref === 'number') { return u.entries[ref] || null; }
    for (var i = 0; i < u.entries.length; i += 1) {
      if (u.entries[i].file === ref || u.entries[i].file.name === ref) { return u.entries[i]; }
    }
    return null;
  }
  function upRemoveEntry(u, e, source) {
    var at = u.entries.indexOf(e), name = e.file.name;
    upDrop(u, e);
    upSync(u);
    upIssues(u, []);
    upSay(u, upText('removed', name));
    upChanged(u, source, [], [e.file]);
    upTell(u);
    return at;
  }
  function upClear(u, source) {
    var removed = upFileList(u);
    u.entries.slice().forEach(function (e) { upDrop(u, e); });
    upSync(u);
    upIssues(u, []);
    upSay(u, '');
    if (removed.length) { upChanged(u, source, [], removed); }
    return removed.length;
  }

  function upDragState(u, on) {
    u.wrap.classList.toggle('is-drag', on);
    upPaintZone(u);
  }

  function upPasted(file) {
    var ext = (/^image\/([a-z0-9]+)/i.exec(file.type) || [])[1] || 'png', d = new Date();
    var two = function (n) { return (n < 10 ? '0' : '') + n; };
    ext = ext === 'jpeg' ? 'jpg' : ext;
    var stamp = d.getFullYear() + two(d.getMonth() + 1) + two(d.getDate()) + '-' + two(d.getHours()) + two(d.getMinutes()) + two(d.getSeconds());
    return new File([file], upText('pastedName') + '-' + stamp + '.' + ext, { type: file.type, lastModified: file.lastModified });
  }

  function upInit(wrap) {
    var old = wrap._matUpload;
    if (old) { upState(old); return; }
    var input = $('.mat-upload__input', wrap);
    if (!input || input.type !== 'file' || !upSupported()) { return; }
    var u = { wrap: wrap, input: input, entries: [], busy: false, drag: 0 };
    wrap._matUpload = u;
    if (!input.id) { input.id = nextId('mat-up'); }

    u.zone = doc.createElement('div');
    u.zone.className = 'mat-upload__zone';
    u.zone.innerHTML = icon('upload');
    u.browse = doc.createElement('button');
    u.browse.type = 'button';
    u.browse.className = 'mat-upload__browse';
    u.title = doc.createElement('span');
    u.title.className = 'mat-upload__title';
    u.title.id = input.id + '-title';
    u.browse.appendChild(u.title);
    u.limits = doc.createElement('span');
    u.limits.className = 'mat-upload__limits';
    u.limits.id = input.id + '-limits';
    u.zone.appendChild(u.browse);
    u.zone.appendChild(u.limits);
    // the button is named by the label of the field and by what it says, and told the limits and the hint of the field
    var lab = $('label[for="' + input.id + '"]');
    if (lab) { if (!lab.id) { lab.id = input.id + '-label'; } u.browse.setAttribute('aria-labelledby', lab.id + ' ' + u.title.id); }
    else if (input.getAttribute('aria-label')) { u.browse.setAttribute('aria-label', input.getAttribute('aria-label')); }
    u.browse.setAttribute('aria-describedby', (u.limits.id + ' ' + (input.getAttribute('aria-describedby') || '')).trim());

    u.list = doc.createElement('ul');
    u.list.className = 'mat-upload__list';
    u.list.setAttribute('aria-label', upText('files'));
    u.list.hidden = true;
    u.status = doc.createElement('div');
    u.status.className = 'mat-upload__status';
    u.status.setAttribute('role', 'status');
    u.status.setAttribute('aria-live', 'polite');
    u.live = doc.createElement('span');
    u.live.className = 'mat-sr-only';
    u.live.setAttribute('role', 'status');
    u.live.setAttribute('aria-live', 'polite');
    wrap.appendChild(u.zone);
    wrap.appendChild(u.list);
    wrap.appendChild(u.status);
    wrap.appendChild(u.live);

    // the real input is not offered to the Tab key or a screen reader (the button is); whoever focuses it (a message of the form) lands on the button
    input.tabIndex = -1;
    input.setAttribute('aria-hidden', 'true');
    wrap.classList.add('is-ready');
    upState(u);
    if (typeof MutationObserver === 'function') {
      new MutationObserver(function () { upState(u); }).observe(input, { attributes: true, attributeFilter: ['disabled', 'accept', 'multiple', 'aria-invalid'] });
      new MutationObserver(function () { upState(u); }).observe(wrap, { attributes: true, attributeFilter: ['data-max-size', 'data-max-files', 'data-paste'] });
    }
    // files that the field holds when the page is drawn (the browser restores them after a reload) are the list
    if (input.files && input.files.length) { upAdd(u, Array.prototype.slice.call(input.files), 'picker'); }
  }
  function initUploads(scope) { $$('.mat-upload[data-upload]', scope).forEach(upInit); }

  // ---- the events of the upload
  doc.addEventListener('click', function (e) {
    var t = e.target;
    if (!t.closest) { return; }
    var browse = t.closest('.mat-upload__browse'), remove = t.closest('.mat-upload__remove'), u = upOf(browse || remove);
    if (!u || u.input.disabled) { return; }
    if (browse) { u.input.click(); return; }
    var item = remove.closest('.mat-upload__item'), entry = item && item._matUploadEntry;
    if (!entry) { return; }
    // the focus goes to the button of the next file, else of the one before, else back to the zone: it never falls on the page
    var at = upRemoveEntry(u, entry, 'remove'), next = u.entries[at] || u.entries[at - 1];
    (next ? next.remove : u.browse).focus();
  });
  // a file chosen in the dialog: the person's choice is added to the list, and the field is put back to the whole list before anybody else hears of the change
  doc.addEventListener('change', function (e) {
    var t = e.target, u = t && t.classList && t.classList.contains('mat-upload__input') ? upOf(t) : null;
    if (!u || u.busy) { return; }
    var picked = Array.prototype.slice.call(t.files || []), done = picked.length ? upAdd(u, picked, 'picker') : null;
    // an empty field is a dialog that was cancelled (some browsers then clear the field): the list stays, and so does the field
    if (!picked.length) { upSync(u); }
    if (!done || (!done.added.length && !done.removed.length)) { e.stopImmediatePropagation(); }
  }, true);
  doc.addEventListener('focusin', function (e) {
    var t = e.target, u = t.classList && t.classList.contains('mat-upload__input') ? upOf(t) : null;
    if (u && !u.input.disabled) { u.browse.focus(); }
  });

  function upHasFiles(e) { var types = e.dataTransfer && e.dataTransfer.types; return !!types && Array.prototype.indexOf.call(types, 'Files') >= 0; }
  doc.addEventListener('dragenter', function (e) {
    var u = upOf(e.target);
    if (!u || !upHasFiles(e)) { return; }
    e.preventDefault();
    if (u.input.disabled) { return; }
    u.drag += 1;
    upDragState(u, true);
  });
  doc.addEventListener('dragover', function (e) {
    var u = upOf(e.target);
    if (!u || !upHasFiles(e)) { return; }
    e.preventDefault();
    e.dataTransfer.dropEffect = u.input.disabled ? 'none' : 'copy';
  });
  doc.addEventListener('dragleave', function (e) {
    var u = upOf(e.target);
    if (!u || u.drag === 0) { return; }
    u.drag -= 1;
    if (u.drag === 0) { upDragState(u, false); }
  });
  doc.addEventListener('drop', function (e) {
    $$('.mat-upload.is-drag').forEach(function (w) { if (w._matUpload) { w._matUpload.drag = 0; upDragState(w._matUpload, false); } });
    var u = upOf(e.target);
    if (!u || !upHasFiles(e)) { return; }
    e.preventDefault();
    if (u.input.disabled) { return; }
    var files = Array.prototype.slice.call(e.dataTransfer.files || []), folders = [], items = Array.prototype.filter.call(e.dataTransfer.items || [], function (it) { return it.kind === 'file'; });
    items.forEach(function (it, i) {
      var entry = it.webkitGetAsEntry ? it.webkitGetAsEntry() : null;
      if (entry && entry.isDirectory) { folders.push(i); }
    });
    if (files.length) { upAdd(u, files, 'drop', folders); }
  });
  doc.addEventListener('dragend', function () { $$('.mat-upload.is-drag').forEach(function (w) { if (w._matUpload) { w._matUpload.drag = 0; upDragState(w._matUpload, false); } }); });

  // an image on the clipboard (a screenshot) is added while the focus is in the control; text and everything else is left to the browser
  doc.addEventListener('paste', function (e) {
    var u = upOf(e.target);
    if (!u || !u.wrap.hasAttribute('data-paste') || u.input.disabled) { return; }
    var data = e.clipboardData, files = data && data.files ? Array.prototype.slice.call(data.files) : [];
    if (!files.length) { return; }
    e.preventDefault();
    var images = files.filter(function (f) { return /^image\//i.test(f.type); });
    if (images.length) { upAdd(u, images.map(upPasted), 'paste'); }
    else { upIssues(u, [upText('refusedPaste')]); }
  });
  doc.addEventListener('reset', function (e) {
    setTimeout(function () { $$('.mat-upload', e.target).forEach(function (w) { if (w._matUpload) { upClear(w._matUpload, 'reset'); } }); }, 0);
  }, true);

  // ---- for an app: the files of a control, and what it draws for an upload it does by itself
  function upFiles(ref) { var u = upOf(ref); return u ? upFileList(u) : []; }
  function upAddFiles(ref, files) {
    var u = upOf(ref);
    return u ? upAdd(u, Array.prototype.slice.call(files || []), 'api') : { added: [], removed: [], refused: [] };
  }
  function upRemoveFile(ref, which) {
    var u = upOf(ref), e = u && upEntryOf(u, which);
    if (e) { upRemoveEntry(u, e, 'api'); }
    return !!e;
  }
  function upClearFiles(ref) { var u = upOf(ref); if (u && upClear(u, 'api')) { upTell(u); } }
  // percent 0 to 100 draws a bar (100 is "Uploaded"); null or undefined takes the progress, the mark and the error of that file away (a new try)
  function upSetProgress(ref, which, percent) {
    var u = upOf(ref), e = u && upEntryOf(u, which);
    if (!e) { return false; }
    var p = parseFloat(percent);
    e.error = '';
    e.progress = isFinite(p) ? clamp(p, 0, 100) : null;
    e.done = e.progress !== null && e.progress >= 100;
    if (e.done) { e.progress = null; upSay(u, e.file.name + ': ' + upText('done')); }
    upPaintEntry(e);
    return true;
  }
  // a file that the server did not take: its message (the default is "Upload failed") in words at the file; an empty message takes the failure away
  function upSetError(ref, which, message) {
    var u = upOf(ref), e = u && upEntryOf(u, which);
    if (!e) { return false; }
    e.error = message === undefined || message === null ? upText('failed') : String(message);
    e.progress = null;
    e.done = false;
    upPaintEntry(e);
    if (e.error) { upSay(u, e.file.name + ': ' + e.error); }
    return true;
  }

  // ------------------------------------------------------------------------------------------------------- init
  // a meter is filled by data: <div class="mat-meter__fill" data-value="63"> (no inline style in the markup)
  function fillMeters(scope) {
    $$(".mat-meter__fill[data-value]", scope).forEach(function (m) { m.style.setProperty("--mat-meter", Math.max(0, Math.min(100, parseFloat(m.getAttribute("data-value")) || 0)) + "%"); });
  }

  // a colour a user chose is data (COLOR-10): data-colour="#rrggbb" (or #rgb, any case) on a tag, a dot, an icon or an option of a picker is written into --mat-swatch, which the CSS draws as a
  // dot or as the colour of the icon, and the element is marked is-coloured. Anything else is ignored: no dot, the colour of the text. Markup that is added later needs MatUi.refresh(scope).
  var MARK_COLOUR = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i;
  function markColour(el) {
    var m = MARK_COLOUR.exec(el.getAttribute('data-colour') || '');
    if (!m) { el.classList.remove('is-coloured'); el.style.setProperty('--mat-swatch', 'initial'); return; }
    var h = m[1].toLowerCase();
    if (h.length === 3) { h = h.charAt(0) + h.charAt(0) + h.charAt(1) + h.charAt(1) + h.charAt(2) + h.charAt(2); }
    el.style.setProperty('--mat-swatch', '#' + h);
    el.classList.add('is-coloured');
  }
  function applyColours(scope) {
    if (scope && scope.matches && scope.matches('[data-colour]')) { markColour(scope); }
    $$('[data-colour]', scope).forEach(markColour);
    // what has lost its data-colour is neutral again
    $$('.is-coloured:not([data-colour])', scope).forEach(function (el) { el.classList.remove('is-coloured'); el.style.removeProperty('--mat-swatch'); });
  }

  function init() {
    scheduleSpy();
    initForms();
    applyColours();
    initPickers();
    initOtps();
    initCalendars();
    initCarousels();
    initSchedules();
    initItemLists();
    initRatings();
    initTrees();
    initUploads();
    // the saved state of the submenus is applied without animating them
    root.classList.add('mat-nav-init');
    restoreBranches();
    restoreGroups();
    window.requestAnimationFrame(function () { window.requestAnimationFrame(function () { root.classList.remove('mat-nav-init'); }); });
    fillMeters();
    applyTheme(currentTheme(), false);
    syncScheme();
    refreshDependents();
    var custom = root.getAttribute('data-custom-accent');
    if (custom) { customAccent(custom); }
    $$('[data-toast]').forEach(function (el) { toast(el.getAttribute('data-toast'), { tone: el.getAttribute('data-tone') || undefined }); el.remove(); });
    $$('[data-view-key]').forEach(function (b) {
      var saved = null;
      try { saved = localStorage.getItem('mat-view-' + b.getAttribute('data-view-key')); } catch (e) { /* ignore */ }
      if (saved && b.getAttribute('data-view-switch') === saved) { setView(b); }
    });
    $$('dialog[data-autoopen]').forEach(function (d) { openDialog(d); });
    if (!doc.querySelector('.mat-backdrop') && app()) {
      var bd = doc.createElement('div'); bd.className = 'mat-backdrop'; bd.setAttribute('data-nav-close', ''); app().appendChild(bd);
    }
  }

  if (doc.readyState === 'loading') { doc.addEventListener('DOMContentLoaded', init); } else { init(); }

  window.MatUi = {
    version: '0.2.0',
    dialog: { open: openDialog, close: closeDialog },
    confirm: confirmDialog,
    alert: alertDialog,
    prompt: promptDialog,
    toast: toast,
    theme: { get: currentTheme, set: function (t) { applyTheme(t); } },
    scheme: { get: currentScheme, set: function (name) { applyScheme(name); } },
    accent: { set: setAccent, custom: customAccent },
    scrollspy: { update: updateSpy },
    nav: { set: setNavSet, branch: function (key, open) { var b = $('[data-nav-branch="' + key + '"]'); if (b) { setBranch(b, open !== false, true); } } },
    picker: { init: initPickers, close: closePicker, setSlots: setSlots, slotsFailed: slotsFailed, set: pickerSet, addOption: pickerAddOption, suggest: pickerSuggest },
    slider: { init: initSliders, update: slUpdate },
    otp: { init: initOtps, clear: otpClear, focus: otpFocusFirst, setInvalid: otpSetInvalid, value: otpValue },
    calendar: CALENDAR_API,
    carousel: { init: initCarousels, go: function (el, page) { var c = el && el._matCarousel; if (c) { carGo(c, page, false); } } },
    schedule: schedule,
    groups: { set: function (group, open) { setGroup(group, open !== false, true); }, setAll: function (scope, open) { setAllGroups(scope, open !== false); }, restore: restoreGroups },
    itemlist: { init: initItemLists, close: function () { ilCloseAll(); }, open: function (item, side) { if (item && item._matSlide) { ilOpen(item, side === 'start' ? 'start' : 'end'); } } },
    rating: { init: initRatings, get: ratGet, set: ratSet },
    tree: tree,
    upload: { init: initUploads, files: upFiles, add: upAddFiles, remove: upRemoveFile, clear: upClearFiles, setProgress: upSetProgress, setError: upSetError },
    refresh: function (scope) { refreshDependents(scope); fillMeters(scope); initForms(scope); applyColours(scope); initPickers(scope); initOtps(scope); initCalendars(scope); initCarousels(scope); initSchedules(scope); initItemLists(scope); initRatings(scope); initTrees(scope); initUploads(scope); restoreGroups(scope); }
  };
})();
