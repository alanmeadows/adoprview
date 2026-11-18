'use strict';

// The URL is only a first-paint hint, supplied from native settings at launch and
// refreshed after a confirmed save. There is no second preference store.
window.AppTheme = Object.freeze({
  supported: ['light', 'dark'],
  effective(value) { return this.supported.includes(value) ? value : 'light'; },
  apply(value) { document.documentElement.dataset.theme = this.effective(value); },
  remember(value) {
    const url = new URL(location.href);
    url.searchParams.set('theme', this.effective(value));
    history.replaceState(null, '', url);
  },
});
window.AppTheme.apply(new URLSearchParams(location.search).get('theme'));
