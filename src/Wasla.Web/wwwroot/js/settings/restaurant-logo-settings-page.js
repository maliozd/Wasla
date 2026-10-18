/**
 * Account settings: previews the chosen restaurant logo before it is saved and refuses, with the page's
 * localized messages, a missing file or one the server would refuse for its type or size. The server checks
 * the content again on save; this only saves a round trip. The input is deliberately not `required`: the
 * browser's own bubble would speak the browser's language, and the field would be announced as invalid
 * before anyone touched it.
 */
(function () {
    'use strict';

    var form = document.getElementById('restaurantLogoUploadForm');
    var input = document.getElementById('restaurantLogoFile');
    var error = document.getElementById('restaurantLogoClientError');
    var mark = document.querySelector('.wasla-restaurant-logo-settings [data-restaurant-logo-mark]');
    var caption = document.querySelector('.wasla-restaurant-logo-settings [data-logo-preview-caption]');
    if (!form || !input || !error || !mark) return;

    var maxBytes = parseInt(form.dataset.maxBytes || '0', 10);
    var acceptedTypes = (form.dataset.acceptedTypes || '').split(',');
    var initialMarkup = mark.innerHTML;
    var initialClass = mark.className;
    var initialCaption = caption ? caption.textContent : '';
    var previewUrl = null;

    function setError(message) {
        error.textContent = message || '';
        error.hidden = !message;
        input.classList.toggle('is-invalid', !!message);
        input.setAttribute('aria-invalid', message ? 'true' : 'false');
    }

    function restorePreview() {
        if (previewUrl) {
            URL.revokeObjectURL(previewUrl);
            previewUrl = null;
        }
        mark.innerHTML = initialMarkup;
        mark.className = initialClass;
        if (caption) caption.textContent = initialCaption;
    }

    function validate(file) {
        if (!file) return '';
        if (acceptedTypes.indexOf(file.type) < 0) return form.dataset.messageUnsupported;
        if (maxBytes > 0 && file.size > maxBytes) return form.dataset.messageTooLarge;
        return '';
    }

    function showPreview(file) {
        restorePreview();
        previewUrl = URL.createObjectURL(file);
        var img = document.createElement('img');
        img.className = 'wasla-restaurant-logo-frame__image';
        img.alt = form.dataset.previewAlt || '';
        img.src = previewUrl;
        // A file the browser cannot draw is no logo: keep the current state and let the server explain on save.
        img.addEventListener('error', restorePreview, { once: true });
        mark.innerHTML = '';
        mark.appendChild(img);
        mark.classList.add('wasla-restaurant-logo-frame--logo');
        if (caption) caption.textContent = form.dataset.messagePreview || '';
    }

    input.addEventListener('change', function () {
        var file = input.files && input.files[0];
        var message = validate(file);
        setError(message);
        if (!file || message) {
            restorePreview();
            return;
        }
        showPreview(file);
    });

    form.addEventListener('submit', function (event) {
        var file = input.files && input.files[0];
        var message = file ? validate(file) : form.dataset.messageRequired;
        if (message) {
            event.preventDefault();
            setError(message);
            input.focus();
        }
    });

    window.addEventListener('pagehide', function () {
        if (previewUrl) URL.revokeObjectURL(previewUrl);
    });
})();
