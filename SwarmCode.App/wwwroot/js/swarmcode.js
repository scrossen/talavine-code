// SwarmCode interop helpers.
window.swarmcode = window.swarmcode || {};

(function (sc) {
    // The resolved theme is always on the root: the shared chat cards switch their fixed
    // accent colors on data-theme, so "system" follows the OS preference live.
    var systemDark = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;
    var followSystem = false;
    function applySystemTheme() {
        if (!followSystem) return;
        document.documentElement.setAttribute("data-theme", systemDark && systemDark.matches ? "dark" : "light");
    }
    if (systemDark) {
        if (systemDark.addEventListener) systemDark.addEventListener("change", applySystemTheme);
        else if (systemDark.addListener) systemDark.addListener(applySystemTheme);
    }

    sc.setTheme = function (theme) {
        var root = document.documentElement;
        followSystem = !(theme === "light" || theme === "dark");
        if (followSystem) applySystemTheme();
        else root.setAttribute("data-theme", theme);
    };

    sc.zoom = function (value) {
        var v = Number(value);
        if (!isFinite(v) || v <= 0) v = 1;
        document.documentElement.style.setProperty("--sc-zoom", String(v));
    };

    var shortcutHandler = null;
    sc.registerShortcuts = function (dotnetRef) {
        sc.unregisterShortcuts();
        shortcutHandler = function (e) {
            if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return;
            var action = null;
            if (e.key === "n" || e.key === "N") action = "new-session";
            else if (e.key === ",") action = "settings";
            if (!action) return;
            e.preventDefault();
            dotnetRef.invokeMethodAsync("OnShortcut", action);
        };
        document.addEventListener("keydown", shortcutHandler);
    };
    sc.unregisterShortcuts = function () {
        if (shortcutHandler) document.removeEventListener("keydown", shortcutHandler);
        shortcutHandler = null;
    };

    // Self-improvement idle clock: any key, pointer or wheel input counts as the person using SwarmCode.
    // Reported at most once every 3 seconds so streaming output and typing do not flood .NET.
    var activityHandler = null;
    var activityEvents = ["keydown", "pointerdown", "wheel", "touchstart"];
    sc.registerActivity = function (dotnetRef) {
        sc.unregisterActivity();
        var last = 0;
        activityHandler = function () {
            var now = Date.now();
            if (now - last < 3000) return;
            last = now;
            try { dotnetRef.invokeMethodAsync("OnUserActivity"); } catch (e) { /* page is going away */ }
        };
        activityEvents.forEach(function (name) { document.addEventListener(name, activityHandler, { passive: true, capture: true }); });
    };
    sc.unregisterActivity = function () {
        if (activityHandler) activityEvents.forEach(function (name) { document.removeEventListener(name, activityHandler, { capture: true }); });
        activityHandler = null;
    };
})(window.swarmcode);

(function (sc) {
    // Blazor's propagation boundary does not cancel a native <dialog>'s Escape
    // action. Consume only an open provider menu's Escape before that default;
    // let the delegated Blazor handler close the menu and restore its focus.
    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        const picker = event.target instanceof Element ? event.target.closest('.provider-picker') : null;
        if (picker?.querySelector('.provider-picker__toggle[aria-expanded="true"]')) event.preventDefault();
    }, true);

    sc.openDialog = element => { if (element && !element.open) element.showModal(); };
    sc.closeDialog = element => element?.close();
    sc.pickFiles = id => document.getElementById(id)?.click();
    sc.attachFiles = function (element, inputId, reference) {
        sc.detachFiles(element);
        const surface = element.closest('.sess-main') || element;
        const input = document.getElementById(inputId);
        const active = () => !input.disabled && element.isConnected;
        const add = files => {
            if (!active() || !files.length) return;
            const transfer = new DataTransfer();
            for (const file of files) transfer.items.add(file);
            input.files = transfer.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        };
        const isFiles = event => Array.from(event.dataTransfer?.types || []).includes('Files');
        const over = event => {
            if (!isFiles(event)) return;
            event.preventDefault();
            event.dataTransfer.dropEffect = active() ? 'copy' : 'none';
            element.classList.add('sess-attachment-intake--drag');
        };
        const leave = event => { if (!surface.contains(event.relatedTarget)) element.classList.remove('sess-attachment-intake--drag'); };
        const drop = event => {
            if (!isFiles(event)) return;
            event.preventDefault();
            element.classList.remove('sess-attachment-intake--drag');
            add(Array.from(event.dataTransfer.files));
        };
        const paste = event => {
            if (!element.contains(event.target) || !event.target.matches('.chat-composer__input')) return;
            const files = Array.from(event.clipboardData?.files || []);
            if (files.length) { event.preventDefault(); add(files); }
            else if (!event.clipboardData?.getData('text/plain')) {
                reference.invokeMethodAsync('PasteLocalFilesAsync').catch(() => {});
            }
        };
        const preventNavigation = event => { if (isFiles(event)) event.preventDefault(); };
        surface.addEventListener('dragover', over);
        surface.addEventListener('dragleave', leave);
        surface.addEventListener('drop', drop);
        surface.addEventListener('paste', paste);
        document.addEventListener('drop', preventNavigation);
        element._detachAttachments = () => {
            surface.removeEventListener('dragover', over);
            surface.removeEventListener('dragleave', leave);
            surface.removeEventListener('drop', drop);
            surface.removeEventListener('paste', paste);
            document.removeEventListener('drop', preventNavigation);
        };
    };
    // The element is gone when the page reloaded before the component was disposed.
    sc.detachFiles = element => { if (!element) return; element._detachAttachments?.(); delete element._detachAttachments; };
})(window.swarmcode);

// File references in the transcript: an inline code span such as `src/Foo.cs:12` inside a chat message opens in
// the person's editor (IdeBridgeHost.OpenFileReference). The host checks the path against the session's
// repository; this only spots candidates. Spans inside code blocks, and clicks that end a text selection, are ignored.
(function (sc) {
    const pattern = /^(?:[A-Za-z]:[\\/])?[^\s:*?"<>|]*[^\s:*?"<>|/\\.]\.[A-Za-z][A-Za-z0-9_+-]{0,11}(?:(?::|#)L?\d{1,7}(?:-L?\d{1,7})?(?::\d{1,5})?)?$/;
    const candidate = target => {
        const code = target instanceof Element ? target.closest('code') : null;
        if (!code || code.closest('pre') || !code.closest('.chat-msg')) return null;
        const text = (code.textContent || '').trim();
        return text.length <= 1024 && !text.includes('://') && pattern.test(text) ? { code, text } : null;
    };
    let handlers = null;
    sc.registerFileReferences = function (dotnetRef) {
        sc.unregisterFileReferences();
        const over = event => {
            const found = candidate(event.target);
            if (found && !found.code.classList.contains('sc-file-ref')) {
                found.code.classList.add('sc-file-ref');
                found.code.title = 'Open ' + found.text + ' in your editor';
            }
        };
        const click = event => {
            if (event.button !== 0) return;
            const found = candidate(event.target);
            if (!found) return;
            const selection = window.getSelection ? window.getSelection() : null;
            if (selection && !selection.isCollapsed) return;
            event.preventDefault();
            dotnetRef.invokeMethodAsync('OpenFileReference', found.text);
        };
        document.addEventListener('mouseover', over);
        document.addEventListener('click', click);
        handlers = { over, click };
    };
    sc.unregisterFileReferences = function () {
        if (!handlers) return;
        document.removeEventListener('mouseover', handlers.over);
        document.removeEventListener('click', handlers.click);
        handlers = null;
    };
})(window.swarmcode);

// PDF preview handoff. The bytes go to pdf-preview.html by postMessage, not in its URL: Chromium (WebView2)
// rejects URLs over 2 MB, so a URL fragment silently showed an empty frame for any PDF above ~1.5 MB.
// The frame takes the first copy it receives and announces when it is listening, so a copy posted before
// its script loaded is resent on that announcement. Same-window frames only: each side checks event.source.
(function (sc) {
    sc.showPdfPreview = function (frame, base64) {
        if (!frame) return;
        const post = () => frame.contentWindow && frame.contentWindow.postMessage({ type: 'swarmcode-pdf', data: base64 }, '*');
        const onReady = event => {
            if (event.source !== frame.contentWindow || !event.data || event.data.type !== 'swarmcode-pdf-ready') return;
            window.removeEventListener('message', onReady);
            post();
        };
        window.addEventListener('message', onReady);
        setTimeout(() => window.removeEventListener('message', onReady), 30000);
        post();
    };
})(window.swarmcode);
