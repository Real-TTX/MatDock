/* MatUi boot 0.9.2: include it in <head> as a plain (blocking) script, before the stylesheet is painted.
   It applies the saved theme ("system" = no attribute), the saved colour scheme and the saved accent preset, so the page does not flash in the wrong colours.
   A server-side preference (the user's account) can write data-theme / data-scheme / data-accent on <html> itself; this only fills in what is missing. */
(function () {
  try {
    var root = document.documentElement;
    var theme = localStorage.getItem('mat-theme');
    if (!root.hasAttribute('data-theme') && (theme === 'light' || theme === 'dark')) { root.setAttribute('data-theme', theme); }
    var scheme = localStorage.getItem('mat-scheme');
    if (!root.hasAttribute('data-scheme') && (scheme === 'paper' || scheme === 'contrast' || scheme === 'oled')) { root.setAttribute('data-scheme', scheme); }
    var accent = localStorage.getItem('mat-accent');
    if (!root.hasAttribute('data-accent') && accent) { root.setAttribute('data-accent', accent); }
  } catch (e) { /* storage blocked: the system theme stays */ }
})();
