/**
 * SwarmCore.Chat.Components browser helpers.
 *
 *   window.chatComponents = {
 *     autosize(el), scrollToBottom(el, afterLayout), cancelPendingBottomScroll(el),
 *     isNearBottom(el, px), copyText(text), focus(el),
 *     enhanceCodeBlocks(container), observeScroll(el, dotNetRef), unobserveScroll(el)
 *   }
 *
 * Attribute-driven textarea behavior (same contract as Talavine's sm-textarea-autosize.js):
 *   data-sm-autosize="true"       grow/shrink with content up to max-height
 *   data-sm-enter-submits="true"  plain Enter never inserts a newline (the component's keydown
 *                                 handler decides what Enter does); Shift+Enter inserts one;
 *                                 an Enter that commits IME composition is left alone
 *   data-chat-slash-open="true"   ArrowUp/ArrowDown/Tab are reserved for the slash popup
 *
 * Safe to load more than once (script tag and/or dynamic import): the first load wins.
 */
(function (global) {
    'use strict';

    if (global.chatComponents) return;

    const selector = 'textarea[data-sm-autosize="true"]';
    const enterSubmitsSelector = 'textarea[data-sm-enter-submits="true"]';
    const slashOpenSelector = 'textarea[data-chat-slash-open="true"]';
    const scheduled = new WeakSet();

    function asElement(target) {
        return typeof target === 'string' ? document.getElementById(target) : target;
    }

    function positiveNumber(value) {
        const parsed = Number.parseFloat(value);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
    }

    function computedPixels(value) {
        if (typeof value !== 'string' || !value.trim().endsWith('px')) return null;
        return positiveNumber(value);
    }

    function autosize(target, explicitMaxPx) {
        const textarea = asElement(target);
        if (!(textarea instanceof global.HTMLTextAreaElement) || !textarea.isConnected) return false;

        textarea.style.height = '0px';
        const style = global.getComputedStyle(textarea);
        const minimum = positiveNumber(style.minHeight) ?? 0;
        const cap = Math.max(minimum,
            positiveNumber(explicitMaxPx)
            ?? positiveNumber(textarea.dataset.smAutosizeMax)
            ?? computedPixels(style.maxHeight)
            ?? Math.min(480, (positiveNumber(global.innerHeight) ?? 880) * 0.45));
        const content = textarea.scrollHeight;
        textarea.style.height = Math.ceil(Math.max(minimum, Math.min(content, cap))) + 'px';
        textarea.style.overflowY = content > cap ? 'auto' : 'hidden';
        return true;
    }

    function schedule(target) {
        const textarea = asElement(target);
        if (!textarea || scheduled.has(textarea)) return;
        scheduled.add(textarea);
        // Blazor applies bound values a frame after the input event; measure on the second frame.
        global.requestAnimationFrame(() => global.requestAnimationFrame(() => {
            scheduled.delete(textarea);
            autosize(textarea);
        }));
    }

    function resizeAll(root) {
        const scope = root && typeof root.querySelectorAll === 'function' ? root : document;
        if (scope.matches?.(selector)) schedule(scope);
        scope.querySelectorAll(selector).forEach(schedule);
    }

    function isSubmitEnter(event) {
        return event.key === 'Enter' && !event.shiftKey && !event.isComposing && event.keyCode !== 229;
    }

    function installAutosizeListeners() {
        // Talavine's host script already provides identical delegated behavior.
        if (global.smTextareaAutosize) return;

        const prototype = global.HTMLTextAreaElement?.prototype;
        const descriptor = prototype && Object.getOwnPropertyDescriptor(prototype, 'value');
        if (descriptor?.get && descriptor?.set && descriptor.configurable !== false
            && !Object.prototype.hasOwnProperty.call(prototype, '__chatAutosizeValueHook')) {
            Object.defineProperty(prototype, 'value', {
                configurable: true,
                enumerable: descriptor.enumerable,
                get: descriptor.get,
                set: function (value) {
                    descriptor.set.call(this, value);
                    if (this.matches?.(selector)) schedule(this);
                }
            });
            Object.defineProperty(prototype, '__chatAutosizeValueHook', { configurable: true, value: true });
        }

        const onInput = event => { if (event.target?.matches?.(selector)) schedule(event.target); };
        document.addEventListener('input', onInput, true);
        document.addEventListener('focusin', onInput, true);
        global.addEventListener('resize', () => resizeAll(document));
        new global.MutationObserver(records => {
            for (const record of records) {
                for (const node of record.addedNodes) {
                    if (node.nodeType === 1) resizeAll(node);
                }
            }
        }).observe(document.documentElement, { childList: true, subtree: true });
        resizeAll(document);
    }

    function installKeyListener() {
        document.addEventListener('keydown', event => {
            const target = event.target;
            if (!target?.matches) return;
            if (isSubmitEnter(event) && target.matches(enterSubmitsSelector)) {
                event.preventDefault();
            } else if (target.matches(slashOpenSelector)
                && (event.key === 'ArrowUp' || event.key === 'ArrowDown' || event.key === 'Tab')
                && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey && !event.isComposing) {
                event.preventDefault();
            }
        }, true);
    }

    // ---- scrolling -------------------------------------------------------

    const pendingBottomScrolls = new WeakMap();

    function cancelPendingBottomScroll(el) {
        if (el) pendingBottomScrolls.get(el)?.(false);
    }

    function isNearBottom(target, px) {
        const el = asElement(target);
        if (!el) return true;
        const threshold = Number.isFinite(px) ? px : 80;
        return el.scrollHeight - el.scrollTop - el.clientHeight <= threshold;
    }

    function scrollToBottom(target, afterLayout) {
        const el = asElement(target);
        if (!el) return undefined;
        cancelPendingBottomScroll(el);
        el.scrollTop = el.scrollHeight;
        if (!afterLayout) return undefined;

        // Restored history and the autosized composer finish layout over the next frames.
        // Cancel on user interaction, not on scrollTop changes caused by layout itself.
        return new Promise(resolve => {
            const events = ['wheel', 'touchstart', 'pointerdown', 'keydown'];
            let frame;
            let settled = false;
            const cancel = () => finish(false);
            const finish = scroll => {
                if (settled) return;
                settled = true;
                cancelAnimationFrame(frame);
                events.forEach(name => el.removeEventListener(name, cancel));
                if (pendingBottomScrolls.get(el) === finish) pendingBottomScrolls.delete(el);
                if (scroll && el.isConnected) el.scrollTop = el.scrollHeight;
                resolve();
            };
            pendingBottomScrolls.set(el, finish);
            events.forEach(name => el.addEventListener(name, cancel, { passive: true }));
            frame = requestAnimationFrame(() => { frame = requestAnimationFrame(() => finish(true)); });
        });
    }

    const scrollObservers = new WeakMap();

    function observeScroll(target, dotNetRef) {
        const el = asElement(target);
        if (!el || !dotNetRef) return;
        unobserveScroll(el);
        let last = true;
        let queued = false;
        const handler = () => {
            if (queued) return;
            queued = true;
            global.requestAnimationFrame(() => {
                queued = false;
                const near = isNearBottom(el, 80);
                if (near === last) return;
                last = near;
                dotNetRef.invokeMethodAsync('OnScrollState', near).catch(() => { });
            });
        };
        el.addEventListener('scroll', handler, { passive: true });
        scrollObservers.set(el, handler);
    }

    function unobserveScroll(target) {
        const el = asElement(target);
        const handler = el && scrollObservers.get(el);
        if (handler) {
            el.removeEventListener('scroll', handler);
            scrollObservers.delete(el);
        }
    }

    // ---- clipboard / code blocks / focus --------------------------------

    async function copyText(text) {
        try {
            await navigator.clipboard.writeText(text || '');
            return true;
        } catch (e) {
            try {
                const ta = document.createElement('textarea');
                ta.value = text || '';
                ta.style.position = 'fixed';
                ta.style.opacity = '0';
                document.body.appendChild(ta);
                ta.focus();
                ta.select();
                const ok = document.execCommand('copy');
                document.body.removeChild(ta);
                return ok;
            } catch (_) {
                return false;
            }
        }
    }

    function enhanceCodeBlocks(container) {
        const root = asElement(container);
        if (!root || typeof root.querySelectorAll !== 'function') return;
        root.querySelectorAll('pre.chat-code').forEach(pre => {
            if (pre.querySelector(':scope > .chat-code__copy')) return;
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'chat-code__copy';
            button.textContent = 'Copy';
            button.setAttribute('aria-label', 'Copy code');
            button.addEventListener('click', async () => {
                const code = pre.querySelector('code');
                const ok = await copyText((code ?? pre).textContent);
                button.textContent = ok ? 'Copied' : 'Failed';
                global.setTimeout(() => { button.textContent = 'Copy'; }, 1500);
            });
            pre.appendChild(button);
        });
    }

    function focus(target) {
        const el = asElement(target);
        if (!el || typeof el.focus !== 'function') return false;
        try {
            el.focus();
            if (el instanceof global.HTMLTextAreaElement) schedule(el);
            return true;
        } catch (_) {
            return false;
        }
    }

    // Dictation: the browser's speech recognition where the page has it (Chrome, Edge), writing into the
    // target field as the person speaks; otherwise the host starts the operating system's own dictation.
    const dictation = (function () {
        const sessions = new Map();
        function supported() { return !!(global.SpeechRecognition || global.webkitSpeechRecognition); }
        function insert(target, text) {
            if (!text) return;
            const before = target.value || '';
            const spacer = before.length > 0 && !/\s$/.test(before) ? ' ' : '';
            const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(target), 'value').set;
            setter.call(target, before + spacer + text);
            target.dispatchEvent(new Event('input', { bubbles: true }));
            target.dispatchEvent(new Event('change', { bubbles: true }));
        }
        function focusTarget(selector) {
            const target = document.querySelector(selector);
            if (!target) return false;
            target.focus();
            try { const end = (target.value || '').length; target.setSelectionRange(end, end); } catch (_) { }
            return true;
        }
        function start(selector, dotNetRef) {
            const target = document.querySelector(selector);
            if (!target || !supported()) return false;
            const Ctor = global.SpeechRecognition || global.webkitSpeechRecognition;
            const recognition = new Ctor();
            recognition.lang = navigator.language || 'en-US';
            recognition.continuous = true;
            recognition.interimResults = false;
            recognition.onresult = function (e) {
                for (let i = e.resultIndex; i < e.results.length; i++)
                    if (e.results[i].isFinal) insert(target, e.results[i][0].transcript.trim());
            };
            recognition.onerror = function (e) { dotNetRef.invokeMethodAsync('OnDictationError', e.error || 'error'); };
            recognition.onend = function () { sessions.delete(selector); dotNetRef.invokeMethodAsync('OnDictationEnded'); };
            sessions.set(selector, recognition);
            target.focus();
            recognition.start();
            return true;
        }
        function stop(selector) {
            const recognition = sessions.get(selector);
            if (recognition) { try { recognition.stop(); } catch (_) { } }
        }
        return { supported, start, stop, focusTarget };
    })();

    global.chatComponents = {
        revealSlashSelection: list => {
            const selected = list?.querySelector('[aria-selected="true"]');
            if (!selected) return;
            const row = selected.getBoundingClientRect();
            const viewport = list.getBoundingClientRect();
            if (row.top < viewport.top) list.scrollTop -= viewport.top - row.top;
            else if (row.bottom > viewport.bottom) list.scrollTop += row.bottom - viewport.bottom;
        },
        dictation,
        autosize,
        scrollToBottom,
        cancelPendingBottomScroll,
        isNearBottom,
        copyText,
        focus,
        enhanceCodeBlocks,
        observeScroll,
        unobserveScroll,
        isSubmitEnter,
        showDialog: dialog => { if (dialog && !dialog.open) dialog.showModal(); },
        closeDialog: dialog => dialog?.close()
    };

    function initialize() {
        installAutosizeListeners();
        installKeyListener();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initialize, { once: true });
    } else {
        initialize();
    }
})(window);
