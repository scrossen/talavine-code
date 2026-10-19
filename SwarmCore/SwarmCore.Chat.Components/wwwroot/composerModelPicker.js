const instances = new WeakMap();
export function initialize(root) {
    const trigger = root.querySelector('button');
    const panel = root.querySelector('[popover]');
    const place = () => {
        if (!panel.matches(':popover-open')) return;
        const anchor = trigger.getBoundingClientRect();
        const gap = 8;
        const zoom = panel.currentCSSZoom || 1;
        panel.style.maxHeight = `${Math.max(80, window.innerHeight - gap * 2) / zoom}px`;
        const bounds = panel.getBoundingClientRect();
        panel.style.left = `${Math.max(gap, Math.min(anchor.right - bounds.width, window.innerWidth - bounds.width - gap)) / zoom}px`;
        panel.style.top = `${Math.max(gap, anchor.top - bounds.height - gap) / zoom}px`;
    };
    const toggle = () => {
        trigger.setAttribute('aria-expanded', String(panel.matches(':popover-open')));
        place();
    };
    trigger.setAttribute('aria-expanded', 'false');
    trigger.setAttribute('aria-controls', panel.id);
    const observer = new ResizeObserver(place);
    observer.observe(panel);
    panel.addEventListener('toggle', toggle);
    window.addEventListener('resize', place);
    window.addEventListener('scroll', place, true);
    instances.set(root, () => {
        observer.disconnect();
        panel.removeEventListener('toggle', toggle);
        window.removeEventListener('resize', place);
        window.removeEventListener('scroll', place, true);
    });
}
export function dispose(root) { instances.get(root)?.(); instances.delete(root); }
