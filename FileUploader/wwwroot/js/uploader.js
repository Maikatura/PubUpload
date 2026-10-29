const STORAGE_KEY = 'fileuploader.uploads.v1';
const MAX_HISTORY = 100;
const MAX_PARALLEL = 3;

let root = null;
let config = { maxBytes: 100 * 1024 * 1024, allowed: null };
let picker = null;
let nextId = 1;
const queue = [];
let renderScheduled = false;

function readHistory() {
    try {
        const parsed = JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]');
        return Array.isArray(parsed) ? parsed : [];
    } catch {
        return [];
    }
}

function writeHistory(list) {
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(list.slice(0, MAX_HISTORY)));
    } catch {
    }
}

function activeHistory() {
    const now = Date.now();
    const list = readHistory()
        .filter(item => item && item.deleteToken && (!item.expiresAt || new Date(item.expiresAt).getTime() > now))
        .sort((a, b) => new Date(b.uploadedAt || 0) - new Date(a.uploadedAt || 0));
    return list;
}

function addToHistory(item) {
    const list = readHistory().filter(entry => entry.deleteToken !== item.deleteToken);
    list.unshift(item);
    writeHistory(list);
}

function removeFromHistory(deleteToken) {
    writeHistory(readHistory().filter(entry => entry.deleteToken !== deleteToken));
}

writeHistory(activeHistory());

function tryMount() {
    const current = document.getElementById('uploader-root');
    if (current === root) return;
    root = current;
    if (!root) return;

    config.maxBytes = Number(root.dataset.maxBytes) || config.maxBytes;
    const allowed = (root.dataset.allowed || '').split(',').map(s => s.trim().toLowerCase()).filter(Boolean);
    config.allowed = allowed.length ? new Set(allowed) : null;

    if (!picker) {
        picker = document.createElement('input');
        picker.type = 'file';
        picker.multiple = true;
        picker.hidden = true;
        picker.addEventListener('change', () => {
            if (picker.files && picker.files.length) enqueue(Array.from(picker.files));
            picker.value = '';
        });
        document.body.appendChild(picker);
    }
    if (config.allowed) picker.accept = Array.from(config.allowed).join(',');

    const zone = root.querySelector('#drop-zone');
    zone.addEventListener('click', () => picker.click());
    zone.addEventListener('keydown', e => {
        if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            picker.click();
        }
    });

    for (const item of queue) item.el = null;
    renderQueue();
    renderHistory();
    updateStatus();
}

new MutationObserver(tryMount).observe(document.documentElement, { childList: true, subtree: true });
tryMount();

let dragDepth = 0;

function hasFiles(e) {
    return e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');
}

function setDragging(on) {
    const zone = root && root.querySelector('#drop-zone');
    if (zone) zone.classList.toggle('is-dragging', on);
}

window.addEventListener('dragenter', e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    dragDepth++;
    if (root && document.contains(root)) setDragging(true);
});

window.addEventListener('dragleave', e => {
    if (!hasFiles(e)) return;
    dragDepth = Math.max(0, dragDepth - 1);
    if (dragDepth === 0) setDragging(false);
});

window.addEventListener('dragover', e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = root && document.contains(root) ? 'copy' : 'none';
});

window.addEventListener('drop', e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    dragDepth = 0;
    setDragging(false);
    if (!root || !document.contains(root)) return;
    const files = Array.from(e.dataTransfer.files || []);
    if (files.length) enqueue(files);
});

window.addEventListener('paste', e => {
    if (!root || !document.contains(root)) return;
    const t = e.target;
    const tag = (t && t.tagName) || '';
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || (t && t.isContentEditable)) return;

    const files = [];
    for (const item of (e.clipboardData && e.clipboardData.items) || []) {
        if (item.kind !== 'file') continue;
        const file = item.getAsFile();
        if (!file) continue;
        if (/^image\.\w+$/i.test(file.name)) {
            const ext = file.name.slice(file.name.lastIndexOf('.'));
            const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
            files.push(new File([file], `paste-${stamp}${ext}`, { type: file.type }));
        } else {
            files.push(file);
        }
    }
    if (files.length) {
        e.preventDefault();
        enqueue(files);
    }
});

window.addEventListener('beforeunload', e => {
    if (queue.some(i => i.status === 'uploading' || i.status === 'queued')) {
        e.preventDefault();
        e.returnValue = '';
    }
});

function extensionOf(name) {
    const i = name.lastIndexOf('.');
    return i >= 0 ? name.slice(i).toLowerCase() : '';
}

function precheck(file) {
    if (file.size === 0) return 'Empty file';
    if (file.size > config.maxBytes) return `Too large (max ${formatSize(config.maxBytes)})`;
    const ext = extensionOf(file.name);
    if (config.allowed && !config.allowed.has(ext)) return ext ? `${ext} files are not allowed` : 'File type not allowed';
    return null;
}

function enqueue(files) {
    const batch = [];
    for (const file of files) {
        const error = precheck(file);
        const item = { id: nextId++, file, status: error ? 'error' : 'queued', loaded: 0, total: file.size, xhr: null, error, el: null };
        queue.push(item);
        batch.push(item);
    }
    batch.autoCopy = batch.filter(i => i.status === 'queued').length === 1;
    for (const item of batch) item.batch = batch;
    pump();
    renderQueue();
    updateStatus();
}

function pump() {
    let running = queue.filter(i => i.status === 'uploading').length;
    for (const item of queue) {
        if (running >= MAX_PARALLEL) break;
        if (item.status !== 'queued') continue;
        start(item);
        running++;
    }
}

function start(item) {
    item.status = 'uploading';
    item.loaded = 0;
    item.error = null;
    item.startedAt = performance.now();

    const xhr = new XMLHttpRequest();
    item.xhr = xhr;
    xhr.open('POST', '/api/upload');
    xhr.responseType = 'json';

    xhr.upload.onprogress = e => {
        if (e.lengthComputable) {
            item.loaded = e.loaded;
            item.total = e.total;
            scheduleRender();
        }
    };

    xhr.onload = () => {
        const body = xhr.response || {};
        if (xhr.status >= 200 && xhr.status < 300 && body.success) {
            finish(item, 'done');
            onUploaded(item, body);
        } else {
            finish(item, 'error', body.error || (xhr.status === 413 ? 'File too large' : `Upload failed (${xhr.status})`));
        }
    };
    xhr.onerror = () => finish(item, 'error', 'Network error, check your connection');
    xhr.onabort = () => finish(item, 'canceled');

    const form = new FormData();
    form.append('file', item.file);
    xhr.send(form);
    scheduleRender();
}

function finish(item, status, error) {
    item.status = status;
    item.error = error || null;
    item.xhr = null;
    pump();
    renderQueue();
    updateStatus();

    if (status === 'done') {
        setTimeout(() => {
            const idx = queue.indexOf(item);
            if (idx >= 0 && item.status === 'done') {
                queue.splice(idx, 1);
                renderQueue();
            }
        }, 900);
    }
}

function onUploaded(item, body) {
    const entry = {
        fileName: item.file.name,
        shortCode: body.shortCode,
        deleteToken: body.deleteToken,
        fileExtension: body.fileExtension,
        fileSize: body.fileSize,
        url: body.url,
        deletionURL: body.deletionURL,
        uploadedAt: body.uploadedAt ? new Date(body.uploadedAt).toISOString() : new Date().toISOString(),
        expiresAt: body.expiresAt ? new Date(body.expiresAt).toISOString() : null
    };
    addToHistory(entry);
    renderHistory(entry.deleteToken);

    if (item.batch && item.batch.autoCopy) {
        copyText(entry.url, true).then(ok => toast(ok ? 'Uploaded, link copied' : `Uploaded ${entry.fileName}`));
    } else if (item.batch && item.batch.every(i => i.status !== 'queued' && i.status !== 'uploading')) {
        const ok = item.batch.filter(i => i.status === 'done').length;
        toast(`Uploaded ${ok} of ${item.batch.length} files`);
    }
}

function cancel(item) {
    if (item.xhr) item.xhr.abort();
    else if (item.status === 'queued') finish(item, 'canceled');
}

function retry(item) {
    if (precheck(item.file)) return;
    item.status = 'queued';
    pump();
    renderQueue();
    updateStatus();
}

function dismiss(item) {
    const idx = queue.indexOf(item);
    if (idx >= 0) queue.splice(idx, 1);
    renderQueue();
    updateStatus();
}

function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
}

function button(label, className, onClick, title) {
    const b = el('button', className, label);
    b.type = 'button';
    if (title) b.title = title;
    b.addEventListener('click', e => {
        e.stopPropagation();
        onClick(e);
    });
    return b;
}

function scheduleRender() {
    if (renderScheduled) return;
    renderScheduled = true;
    requestAnimationFrame(() => {
        renderScheduled = false;
        for (const item of queue) updateQueueRow(item);
        updateStatus();
    });
}

function renderQueue() {
    const host = root && root.querySelector('#upload-queue');
    if (!host) return;

    if (!queue.length) {
        host.replaceChildren();
        return;
    }

    let list = host.querySelector('.upload-list');
    if (!list) {
        const section = el('div', 'section-container');
        const header = el('div', 'section-header');
        header.append(el('h2', 'section-title', 'Uploading'));
        section.append(header);
        list = el('div', 'upload-list');
        section.append(list);
        host.replaceChildren(section);
    }

    const wanted = queue.map(item => {
        if (!item.el) item.el = createQueueRow(item);
        updateQueueRow(item);
        return item.el;
    });
    list.replaceChildren(...wanted);
}

function createQueueRow(item) {
    const row = el('div', 'file-row upload-item');
    const meta = el('div', 'file-meta-side');
    meta.append(el('span', 'file-type-tag', fileLabel(extensionOf(item.file.name))));
    const name = el('span', 'file-name', item.file.name);
    name.title = item.file.name;
    meta.append(name);

    const info = el('span', 'file-size-info');
    meta.append(info);

    const controls = el('div', 'file-controls-side');
    const bar = el('div', 'progress-bar upload-item-bar');
    const fill = el('div', 'progress-fill');
    bar.append(fill);
    controls.append(bar);
    const actions = el('div', 'upload-item-actions');
    controls.append(actions);

    row.append(meta, controls);
    row._parts = { info, fill, bar, actions, lastStatus: null };
    return row;
}

function updateQueueRow(item) {
    const row = item.el;
    if (!row) return;
    const p = row._parts;

    row.classList.toggle('is-error', item.status === 'error');
    row.classList.toggle('is-done', item.status === 'done');
    row.classList.toggle('is-canceled', item.status === 'canceled');

    const pct = item.total ? Math.min(100, Math.round((item.loaded / item.total) * 100)) : 0;

    switch (item.status) {
        case 'queued':
            p.info.textContent = `${formatSize(item.file.size)} · waiting`;
            p.fill.style.width = '0%';
            break;
        case 'uploading': {
            const secs = (performance.now() - item.startedAt) / 1000;
            const speed = secs > 0.5 ? item.loaded / secs : 0;
            p.info.textContent = pct >= 100
                ? `${formatSize(item.file.size)} · processing…`
                : `${pct}% · ${formatSize(item.loaded)} / ${formatSize(item.total)}${speed ? ` · ${formatSize(speed)}/s` : ''}`;
            p.fill.style.width = `${pct}%`;
            break;
        }
        case 'done':
            p.info.textContent = `${formatSize(item.file.size)} · done`;
            p.fill.style.width = '100%';
            break;
        case 'canceled':
            p.info.textContent = 'canceled';
            break;
        case 'error':
            p.info.textContent = item.error || 'failed';
            p.info.title = item.error || '';
            break;
    }

    p.bar.hidden = item.status === 'error' || item.status === 'canceled';

    if (p.lastStatus !== item.status) {
        p.lastStatus = item.status;
        const actions = [];
        if (item.status === 'queued' || item.status === 'uploading') {
            actions.push(button('Cancel', 'action-trigger', () => cancel(item)));
        } else if (item.status === 'error' || item.status === 'canceled') {
            if (!precheck(item.file)) actions.push(button('Retry', 'action-trigger', () => retry(item)));
            actions.push(button('✕', 'action-trigger', () => dismiss(item), 'Dismiss'));
        }
        p.actions.replaceChildren(...actions);
    }
}

function updateStatus() {
    const zone = root && root.querySelector('#drop-zone');
    const status = root && root.querySelector('#drop-status');
    if (!zone || !status) return;

    const active = queue.filter(i => i.status === 'uploading' || i.status === 'queued');
    zone.classList.toggle('is-uploading', active.length > 0);

    if (!active.length) {
        status.textContent = '';
        return;
    }
    const total = active.reduce((s, i) => s + i.total, 0);
    const loaded = active.reduce((s, i) => s + i.loaded, 0);
    const pct = total ? Math.round((loaded / total) * 100) : 0;
    status.textContent = `Uploading ${active.length} file${active.length === 1 ? '' : 's'} · ${pct}% · drop more to add them`;
}

function renderHistory(highlightToken) {
    const host = root && root.querySelector('#upload-history');
    if (!host) return;

    const items = activeHistory();
    if (!items.length) {
        host.replaceChildren();
        return;
    }

    const section = el('div', 'section-container');
    const header = el('div', 'section-header');
    header.append(el('h2', 'section-title', `Your Files (${items.length})`));
    header.append(button('Forget list', 'btn-action', () => {
        if (!confirm('Remove the list of your uploads from this browser?\n\nThe files themselves stay online until they expire. Without this list you will not be able to delete them.')) return;
        writeHistory([]);
        renderHistory();
    }, 'Clears this browser-only list. Files stay online until they expire.'));
    section.append(header);

    for (const item of items) {
        section.append(createHistoryRow(item, item.deleteToken === highlightToken));
    }
    host.replaceChildren(section);
}

function createHistoryRow(item, highlight) {
    const url = item.url || `${location.origin}/${item.shortCode}`;
    const row = el('div', highlight ? 'file-row is-new' : 'file-row');
    if (highlight) setTimeout(() => row.classList.remove('is-new'), 1200);

    const meta = el('div', 'file-meta-side');
    meta.append(el('span', 'file-type-tag', fileLabel(item.fileExtension || extensionOf(item.fileName || ''))));
    const name = el('span', 'file-name', item.fileName || item.shortCode);
    name.title = item.fileName || '';
    meta.append(name);
    meta.append(el('span', 'file-size-info', `${formatSize(item.fileSize || 0)} · ${timeLeft(item.expiresAt)}`));

    const controls = el('div', 'file-controls-side');
    const box = el('input', 'url-box');
    box.type = 'text';
    box.readOnly = true;
    box.value = url;
    box.title = 'Click to copy';
    box.addEventListener('click', async () => {
        box.select();
        toast((await copyText(url)) ? 'Link copied' : 'Press Ctrl+C to copy');
    });

    const view = el('a', 'action-trigger', 'View');
    view.href = url;
    view.target = '_blank';
    view.rel = 'noopener noreferrer';

    const del = button('Delete', 'action-trigger danger', () => deleteUpload(item, row, del));

    controls.append(box, view, del);
    row.append(meta, controls);
    return row;
}

async function deleteUpload(item, row, btn) {
    if (!confirm(`Permanently delete "${item.fileName || item.shortCode}"?\n\nThis cannot be undone.`)) return;

    btn.disabled = true;
    btn.textContent = 'Deleting…';
    try {
        const res = await fetch(`/api/delete/${encodeURIComponent(item.deleteToken)}`, { method: 'POST' });
        if (res.ok || res.status === 404) {
            removeFromHistory(item.deleteToken);
            row.classList.add('is-removing');
            setTimeout(() => renderHistory(), 180);
            toast('File deleted');
        } else {
            throw new Error(String(res.status));
        }
    } catch {
        btn.disabled = false;
        btn.textContent = 'Delete';
        toast('Could not delete the file, try again');
    }
}

async function copyText(text, quiet) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        if (quiet) return false;
        try {
            const ta = el('textarea');
            ta.value = text;
            ta.setAttribute('readonly', '');
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.append(ta);
            ta.select();
            const ok = document.execCommand('copy');
            ta.remove();
            return ok;
        } catch {
            return false;
        }
    }
}

let toastHost = null;
function toast(message) {
    if (!toastHost || !document.body.contains(toastHost)) {
        toastHost = el('div', 'toast-stack');
        toastHost.setAttribute('role', 'status');
        toastHost.setAttribute('aria-live', 'polite');
        document.body.append(toastHost);
    }
    const t = el('div', 'status-toast', message);
    toastHost.append(t);
    while (toastHost.children.length > 3) toastHost.firstChild.remove();
    setTimeout(() => {
        t.classList.add('is-leaving');
        setTimeout(() => t.remove(), 250);
    }, 2600);
}

function formatSize(bytes) {
    const units = ['B', 'KB', 'MB', 'GB'];
    let n = bytes;
    let i = 0;
    while (n >= 1024 && i < units.length - 1) {
        n /= 1024;
        i++;
    }
    return `${n < 10 && i > 0 ? n.toFixed(1) : Math.round(n)} ${units[i]}`;
}

function timeLeft(expiresAt) {
    if (!expiresAt) return '';
    const ms = new Date(expiresAt).getTime() - Date.now();
    const days = Math.floor(ms / 86400000);
    if (days >= 1) return `${days}d left`;
    const hours = Math.max(0, Math.floor(ms / 3600000));
    return `${hours}h left`;
}

function fileLabel(ext) {
    switch ((ext || '').toLowerCase()) {
        case '.jpg': case '.jpeg': case '.png': case '.gif': case '.webp': case '.svg':
        case '.avif': case '.ico': case '.bmp': case '.tiff':
            return 'IMG';
        case '.mp4': case '.avi': case '.mkv': case '.mov': case '.webm': case '.wmv':
        case '.flv': case '.m4v': case '.mpg': case '.mpeg':
            return 'VID';
        case '.mp3': case '.wav': case '.flac': case '.aac': case '.ogg': case '.m4a':
        case '.wma': case '.opus':
            return 'AUD';
        case '.pdf':
            return 'PDF';
        case '.txt': case '.md': case '.log':
            return 'TXT';
        case '.unitypackage':
            return 'PKG';
        default:
            return 'FILE';
    }
}
