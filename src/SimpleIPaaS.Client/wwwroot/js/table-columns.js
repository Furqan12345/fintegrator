// Manual, drag-to-resize columns for the transaction table.
// Auto-fit always measures the horizontal scroll container (not the table, which
// grows to its columns), so shrinking the window, expanding the sidebar or leaving
// fullscreen re-fits the columns instead of keeping the old, wider widths.
// Once the user resizes a column, their saved widths win; any spare space is given
// to Message at display time only, so the pinned Action column never stretches.

const MIN_WIDTH = 88;
const MAX_WIDTH = 1400;
// Fixed-width columns: a full GUID with its select box, the status badge, the timestamp and the two
// action buttons. The Action column is always last and never receives spare width.
const FIXED = { transaction: 314, status: 132, runAt: 140, action: 190 };
const FLEX = { 1: 0.34, 2: 0.30, 3: 0.36 };
const ACTION_INDEX = 6;
const MESSAGE_INDEX = 3;

// Per-table state, so bound handlers always work against the live DOM even after
// Blazor re-renders the table. byId also lets init/dispose retire a replaced table.
const state = new WeakMap();
const byId = new Map();

function loadWidths(storageKey) {
    try {
        const raw = localStorage.getItem(storageKey);
        if (!raw) return null;
        const parsed = JSON.parse(raw);
        return Array.isArray(parsed) && parsed.every(value => value === null || typeof value === 'number')
            ? parsed
            : null;
    } catch {
        return null;
    }
}

function saveWidths(storageKey, widths) {
    try {
        localStorage.setItem(storageKey, JSON.stringify(widths));
    } catch {
        // Storage unavailable (private mode) — resizing still works for this session.
    }
}

function clearWidths(storageKey) {
    try {
        localStorage.removeItem(storageKey);
    } catch {
        // ignore
    }
}

function scrollContainer(table) {
    return table.closest('.tx-table-scroll') ?? table.parentElement;
}

// Columns the responsive layout has dropped are display:none on their header and
// cells. They must not take a share of the width or appear in sums.
function visibleColumns(table, columnCount) {
    const headers = [...table.querySelectorAll('thead th')];
    const visible = [];
    // The CSS container query hides Identifier at <= 1000px. Check the same
    // breakpoint here as well, because the query can settle a frame after init.
    const narrow = (scrollContainer(table)?.clientWidth ?? Infinity) <= 1000;
    for (let index = 0; index < columnCount; index++) {
        const header = headers[index];
        if (index === 2 && narrow) continue;
        if (!header || getComputedStyle(header).display !== 'none') visible.push(index);
    }
    return visible;
}

function layoutKey(table, columnCount) {
    return visibleColumns(table, columnCount).join(',');
}

function availableWidth(live) {
    return Math.max(0, live.scroll?.clientWidth ?? 0);
}

// Auto-fit from the space the scroll container actually offers. Every flexible
// column is clamped to MIN_WIDTH; if fixed + minimums do not fit, the widths sum
// to that real minimum and the scroll container shows a clean horizontal scrollbar.
function autoFitWidths(table, columnCount, available) {
    const widths = new Array(columnCount).fill(null);
    const visible = visibleColumns(table, columnCount);
    if (visible.length === 0) return widths;

    const isVisible = index => visible.includes(index);
    let used = 0;
    for (const [index, width] of [[0, FIXED.transaction], [4, FIXED.status], [5, FIXED.runAt], [ACTION_INDEX, FIXED.action]]) {
        if (index < columnCount && isVisible(index)) {
            widths[index] = width;
            used += width;
        }
    }

    const flexible = visible.filter(index => Object.prototype.hasOwnProperty.call(FLEX, index));
    if (flexible.length === 0) {
        widths[visible[visible.length - 1]] = Math.max(MIN_WIDTH, available - used);
        return widths;
    }

    if (available < used + flexible.length * MIN_WIDTH) {
        flexible.forEach(index => { widths[index] = MIN_WIDTH; });
        return widths;
    }

    let remaining = available - used;
    let pending = flexible.slice();
    while (pending.length > 0) {
        const weightSum = pending.reduce((sum, index) => sum + FLEX[index], 0);
        const tooNarrow = pending.find(index => remaining * FLEX[index] / weightSum < MIN_WIDTH);
        if (tooNarrow !== undefined) {
            widths[tooNarrow] = MIN_WIDTH;
            remaining -= MIN_WIDTH;
            pending = pending.filter(index => index !== tooNarrow);
            continue;
        }

        let assigned = 0;
        pending.forEach((index, position) => {
            const width = position === pending.length - 1
                ? Math.max(MIN_WIDTH, remaining - assigned)
                : Math.round(remaining * FLEX[index] / weightSum);
            widths[index] = width;
            assigned += width;
        });
        break;
    }

    return widths;
}

// Older saved layouts could leave any column below the drag minimum, or the Action
// column below the width its buttons need. Clamp when restoring.
function normalizeWidths(widths, columnCount) {
    return widths.map((value, index) => {
        if (index === ACTION_INDEX) {
            return Math.max(FIXED.action, typeof value === 'number' ? value : FIXED.action);
        }
        return typeof value === 'number' ? Math.max(MIN_WIDTH, value) : value;
    });
}

function applyWidths(cols, widths, visible) {
    cols.forEach((col, index) => {
        if (visible && !visible.includes(index)) {
            col.style.width = '';
            return;
        }
        const width = widths[index];
        col.style.width = typeof width === 'number' ? `${width}px` : '';
    });
}

function displayWidths(live) {
    const columnCount = live.cols.length;
    const available = availableWidth(live);
    if (!live.customized) {
        return autoFitWidths(live.table, columnCount, available);
    }

    const auto = autoFitWidths(live.table, columnCount, available);
    const visible = visibleColumns(live.table, columnCount);
    const widths = live.widths.slice();
    for (const index of visible) {
        const current = widths[index];
        widths[index] = typeof current === 'number'
            ? Math.max(index === ACTION_INDEX ? FIXED.action : MIN_WIDTH, current)
            : (typeof auto[index] === 'number' ? auto[index] : MIN_WIDTH);
    }

    const visibleSum = visible.reduce((sum, index) => sum + (widths[index] ?? 0), 0);
    if (visibleSum < available) {
        const fill = visible.includes(MESSAGE_INDEX)
            ? MESSAGE_INDEX
            : [...visible].reverse().find(index => Object.prototype.hasOwnProperty.call(FLEX, index));
        if (fill !== undefined) widths[fill] += available - visibleSum;
    }

    return widths;
}

function updateOverflow(live) {
    if (!live || !live.table.isConnected) return;
    const scroll = live.scroll;
    const wrap = scroll?.parentElement;
    if (!wrap) return;

    const max = scroll.scrollWidth - scroll.clientWidth;
    const moreRight = max > 1 && scroll.scrollLeft < max - 1;
    const moreLeft = scroll.scrollLeft > 1;
    wrap.classList.toggle('is-overflowing', moreRight);
    wrap.classList.toggle('is-scrolled-left', moreLeft);
}

function applyLive(live) {
    if (!live || !live.table.isConnected) return;

    const widths = displayWidths(live);
    live.displayWidths = widths;
    const visible = visibleColumns(live.table, live.cols.length);
    applyWidths(live.cols, widths, visible);

    const total = visible.reduce((sum, index) =>
        sum + (typeof widths[index] === 'number' ? widths[index] : 0), 0);
    const tableWidth = Math.max(total, MIN_WIDTH);
    live.table.style.width = `${tableWidth}px`;
    live.table.style.minWidth = `${tableWidth}px`;

    const actionWidth = typeof widths[ACTION_INDEX] === 'number' ? widths[ACTION_INDEX] : FIXED.action;
    live.scroll?.parentElement?.style.setProperty('--tx-action-shadow-right', `${actionWidth + 1}px`);
    updateOverflow(live);
}

function columnIndex(handle) {
    const th = handle.parentElement;
    return [...th.parentElement.children].indexOf(th);
}

const EDGE_GRAB = 9; // px around a column border that starts a drag

function resolveDragTarget(event, table) {
    const handle = event.target.closest('.col-resizer');
    if (handle) return { handle, index: columnIndex(handle) };

    const header = event.target.closest('thead th');
    if (!header) return null;

    const headers = [...table.querySelectorAll('thead th')];
    const index = headers.indexOf(header);
    if (index < 0) return null;

    const rect = header.getBoundingClientRect();
    const resizable = th => th.querySelector('.col-resizer');
    if (resizable(header) && rect.right - event.clientX <= EDGE_GRAB) {
        return { handle: resizable(header), index };
    }
    if (index > 0 && resizable(headers[index - 1]) && event.clientX - rect.left <= EDGE_GRAB) {
        return { handle: resizable(headers[index - 1]), index: index - 1 };
    }
    return null;
}

function handlePointerDown(event) {
    const table = event.target.closest('table');
    if (!table) return;

    const live = state.get(table);
    if (!live || live.cols.length === 0) return;

    const target = resolveDragTarget(event, table);
    if (!target) return;

    const index = target.index;
    if (index < 0 || index >= live.cols.length - 1) return;

    event.preventDefault();
    const handle = target.handle;
    handle.classList.add('is-active');

    const startX = event.clientX;
    const startWidth = typeof live.displayWidths?.[index] === 'number'
        ? live.displayWidths[index]
        : Math.round(table.querySelectorAll('thead th')[index].getBoundingClientRect().width);
    let active = startWidth;

    document.body.classList.add('col-resizing');

    const onMove = moveEvent => {
        const next = Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, Math.round(startWidth + (moveEvent.clientX - startX))));
        if (next === active) return;
        active = next;
        const current = state.get(table);
        if (!current || !current.cols[index]) return;
        current.widths[index] = next;
        current.customized = true;
        applyLive(current);
    };

    const onUp = () => {
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onUp);
        document.body.classList.remove('col-resizing');
        handle.classList.remove('is-active');
        const current = state.get(table);
        if (current && current.customized) {
            current.displayWidths = displayWidths(current);
            saveWidths(current.storageKey, current.widths);
        }
    };

    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
}

function handleDoubleClick(event) {
    const table = event.target.closest('table');
    if (!table) return;
    const live = state.get(table);
    if (!live) return;

    const target = resolveDragTarget(event, table);
    if (target && target.index >= 0 && target.index < live.cols.length - 1) {
        const index = target.index;
        const fit = autoFitWidths(table, live.cols.length, availableWidth(live));
        live.widths[index] = fit[index];
        live.customized = true;
        applyLive(live);
        saveWidths(live.storageKey, live.widths);
        return;
    }

    if (event.target.closest('thead th')) {
        live.customized = false;
        clearWidths(live.storageKey);
        applyLive(live);
    }
}

function bindLive(live, tableId) {
    if (live.bound) return;

    live.pointerDownHandler = handlePointerDown;
    live.doubleClickHandler = handleDoubleClick;
    live.scrollHandler = () => {
        const current = state.get(live.table);
        if (current) updateOverflow(current);
    };

    live.table.addEventListener('pointerdown', live.pointerDownHandler);
    live.table.addEventListener('dblclick', live.doubleClickHandler);
    live.scroll.addEventListener('scroll', live.scrollHandler, { passive: true });

    if (typeof ResizeObserver !== 'undefined') {
        live.resizeObserver = new ResizeObserver(() => {
            const current = state.get(live.table);
            if (!current) return;
            applyLive(current);
        });
        live.resizeObserver.observe(live.scroll);
    }

    live.bound = true;
}

function disposeState(live) {
    live.resizeObserver?.disconnect();
    live.table.removeEventListener('pointerdown', live.pointerDownHandler);
    live.table.removeEventListener('dblclick', live.doubleClickHandler);
    live.scroll.removeEventListener('scroll', live.scrollHandler);
    state.delete(live.table);
    if (byId.get(live.tableId) === live) byId.delete(live.tableId);
}

export function init(tableId, storageKey) {
    const table = document.getElementById(tableId);
    if (!table) return;

    const headers = [...table.querySelectorAll('thead th')];
    if (headers.length === 0) return;

    const scroll = scrollContainer(table);
    if (!scroll) return;

    const previous = byId.get(tableId);
    if (previous && previous.table !== table) disposeState(previous);

    let colgroup = table.querySelector('colgroup.tx-colgroup');
    if (!colgroup) {
        colgroup = document.createElement('colgroup');
        colgroup.className = 'tx-colgroup';
        for (let i = 0; i < headers.length; i++) colgroup.appendChild(document.createElement('col'));
        table.insertBefore(colgroup, table.firstChild);
    }
    while (colgroup.children.length < headers.length) colgroup.appendChild(document.createElement('col'));
    while (colgroup.children.length > headers.length) colgroup.removeChild(colgroup.lastElementChild);

    const cols = [...colgroup.children];
    let live = state.get(table);

    if (!live) {
        const stored = loadWidths(storageKey);
        live = {
            tableId,
            table,
            scroll,
            cols,
            storageKey,
            customized: stored !== null,
            visibleKey: layoutKey(table, cols.length),
            widths: stored && stored.length === cols.length
                ? normalizeWidths(stored.slice(), cols.length)
                : autoFitWidths(table, cols.length, scroll.clientWidth),
            displayWidths: null,
            bound: false,
            resizeObserver: null,
            pointerDownHandler: null,
            doubleClickHandler: null,
            scrollHandler: null
        };
        state.set(table, live);
        byId.set(tableId, live);
        bindLive(live, tableId);
    } else {
        live.cols = cols;
        live.scroll = scroll;
    }

    // A container query change can drop or restore Identifier without a resize.
    const key = layoutKey(table, live.cols.length);
    if (!live.customized && key !== live.visibleKey) {
        live.visibleKey = key;
        live.widths = autoFitWidths(table, live.cols.length, availableWidth(live));
    }

    applyLive(live);

    // Container queries can settle just after the first synchronous init; re-apply
    // once so a column that has just been dropped is excluded from the width sums.
    requestAnimationFrame(() => {
        const current = state.get(table);
        if (!current) return;
        current.visibleKey = layoutKey(table, current.cols.length);
        applyLive(current);
    });
}

export function dispose(tableId) {
    const live = byId.get(tableId);
    if (live) disposeState(live);
}
