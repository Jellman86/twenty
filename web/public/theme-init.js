// Applies the saved colour theme before the app renders, so there is no flash of the wrong theme.
// Kept as a same-origin file because the Content-Security-Policy forbids inline scripts.
(function () {
  var pref = null;
  try { pref = localStorage.getItem('twenty-theme'); } catch (e) { /* storage blocked */ }
  var light = pref === 'light' || (pref !== 'dark' && window.matchMedia('(prefers-color-scheme: light)').matches);
  document.documentElement.dataset.theme = light ? 'light' : 'dark';
  var meta = document.querySelector('meta[name=theme-color]');
  if (meta) meta.setAttribute('content', light ? '#f1f2f4' : '#111214');
})();
