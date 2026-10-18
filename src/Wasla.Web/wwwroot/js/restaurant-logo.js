/**
 * The restaurant logo beside the restaurant name (Live Screen header, account settings preview).
 * A logo that cannot be loaded is removed and the storefront icon in the same mark is shown again,
 * so the header never shows a broken image. Runs once per page; nothing here reacts to polling.
 */
(function () {
    'use strict';

    function showFallback(img) {
        var mark = img.closest('[data-restaurant-logo-mark]');
        img.remove();
        if (!mark) return;
        mark.classList.remove('wasla-live-display-identity__mark--logo', 'wasla-restaurant-logo-frame--logo');
        var icon = mark.querySelector('[data-restaurant-logo-fallback]');
        if (icon) icon.hidden = false;
    }

    function watch(img) {
        if (!img || img.dataset.restaurantLogoWatched === 'true') return;
        img.dataset.restaurantLogoWatched = 'true';
        // The image may have failed before this script ran.
        if (img.complete && img.naturalWidth === 0) {
            showFallback(img);
            return;
        }
        img.addEventListener('error', function () { showFallback(img); }, { once: true });
    }

    document.querySelectorAll('img[data-restaurant-logo]').forEach(watch);
})();
