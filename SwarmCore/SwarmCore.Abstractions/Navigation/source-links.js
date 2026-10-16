// Source coordinates still require host-side parsing and profile authorization.
(function () {
    const assembly = typeof document !== 'undefined' ? document.currentScript?.dataset.assembly : null;
    function isSource(value) {
        try {
            const url = new URL(value);
            return !url.username && !url.password &&
                ((url.protocol === 'https:' && !url.port &&
                    ['talavine.com', 'www.talavine.com', 'swarmmarshal.com', 'www.swarmmarshal.com'].includes(url.hostname) &&
                    url.pathname.startsWith('/sources/')) ||
                    (url.protocol === 'talavine:' && ['source', 'message', 'email', 'document', 'note', 'task', 'calendar', 'contact', 'matter', 'memory', 'fact'].includes(url.hostname)));
        } catch { return false; }
    }
    if (typeof module !== 'undefined') module.exports = { isSource };
    if (!assembly) return;
    document.addEventListener('click', async function (event) {
        if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const anchor = event.target instanceof Element ? event.target.closest('a[href]') : null;
        const href = anchor?.getAttribute('href');
        if (!isSource(href)) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        try {
            await DotNet.invokeMethodAsync(assembly, 'OpenTalavineSourceLink', href);
        } catch {
            const error = document.createElement('p');
            error.setAttribute('role', 'alert');
            error.textContent = 'Talavine could not open this source. Keep the original profile open and retry the link.';
            anchor.insertAdjacentElement('afterend', error);
        }
    }, true);
})();
