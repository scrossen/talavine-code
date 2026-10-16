import { getDocument, GlobalWorkerOptions } from '../vendor/pdfjs/pdf.mjs';

GlobalWorkerOptions.workerSrc = new URL('../vendor/pdfjs/pdf.worker.mjs', import.meta.url).href;
const pageHost = document.getElementById('page');
const status = document.getElementById('status');
const previous = document.getElementById('previous');
const next = document.getElementById('next');
const zoom = document.getElementById('zoom');
let documentTask, pdf, pageNumber = 1, generation = 0, rendering;

async function renderPage() {
    if (!pdf) return;
    const revision = ++generation;
    rendering?.cancel();
    status.hidden = false;
    status.textContent = 'Rendering page…';
    try {
        const page = await pdf.getPage(pageNumber);
        if (revision !== generation) return;
        const base = page.getViewport({ scale: 1 });
        const scale = zoom.value === 'fit' ? Math.max(.1, (pageHost.clientWidth - 24) / base.width) : Number(zoom.value);
        const viewport = page.getViewport({ scale });
        // Bound the canvas allocation even for unusual page sizes or high-DPI displays.
        const pixelRatio = Math.min(devicePixelRatio || 1, 2, Math.sqrt(8_000_000 / (viewport.width * viewport.height)));
        const canvas = document.createElement('canvas');
        canvas.width = Math.ceil(viewport.width * pixelRatio);
        canvas.height = Math.ceil(viewport.height * pixelRatio);
        canvas.style.width = `${viewport.width}px`;
        canvas.style.height = `${viewport.height}px`;
        canvas.setAttribute('role', 'img');
        canvas.setAttribute('aria-label', `PDF page ${pageNumber} of ${pdf.numPages}`);
        rendering = page.render({ canvasContext: canvas.getContext('2d'), viewport,
            transform: pixelRatio === 1 ? null : [pixelRatio, 0, 0, pixelRatio, 0, 0] });
        await rendering.promise;
        if (revision !== generation) return;
        pageHost.replaceChildren(canvas);
        document.getElementById('pages').textContent = `Page ${pageNumber} of ${pdf.numPages}`;
        previous.disabled = pageNumber <= 1;
        next.disabled = pageNumber >= pdf.numPages;
        status.hidden = true;
    } catch (error) {
        if (revision !== generation || error.name === 'RenderingCancelledException') return;
        status.textContent = 'Could not preview this PDF. Use Open in default app.';
    }
}

previous.addEventListener('click', () => { if (pageNumber > 1) { --pageNumber; renderPage(); } });
next.addEventListener('click', () => { if (pdf && pageNumber < pdf.numPages) { ++pageNumber; renderPage(); } });
zoom.addEventListener('change', renderPage);
let resizeTimer;
window.addEventListener('resize', () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(() => { if (zoom.value === 'fit') renderPage(); }, 150); });
window.addEventListener('pagehide', () => { ++generation; rendering?.cancel(); documentTask?.destroy(); });

try {
    const encoded = location.hash.slice(1);
    if (!encoded || encoded.length > 28_000_000) throw new Error('Invalid PDF preview');
    const data = Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
    documentTask = getDocument({ data, isEvalSupported: false,
        cMapUrl: new URL('../vendor/pdfjs/cmaps/', import.meta.url).href, cMapPacked: true,
        standardFontDataUrl: new URL('../vendor/pdfjs/standard_fonts/', import.meta.url).href });
    pdf = await documentTask.promise;
    await renderPage();
} catch {
    status.textContent = 'Could not open this PDF. Use Open in default app.';
}
