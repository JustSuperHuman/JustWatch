// Presentation-only highlighting for xterm 5.3. Text, ANSI state and links stay intact.
(() => {
    const rules = [
        ['url', /\b(?:https?:\/\/|wss?:\/\/|localhost:)\S+/gi, '#8FC9FF'],
        ['timestamp', /\b(?:\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?|\d{2}:\d{2}:\d{2}(?:[.,]\d+)?)\b/g, '#8FA2B8'],
        ['error', /\b(?:ERROR|ERR|FATAL|FAIL(?:ED|URE)?|EXCEPTION|PANIC)\b/gi, '#F3939F'],
        ['warning', /\b(?:WARN(?:ING)?|DEPRECATED)\b/gi, '#E8C77C'],
        ['success', /\b(?:SUCCESS(?:FUL(?:LY)?)?|READY|PASS(?:ED)?|LISTENING|COMPILED)\b/gi, '#89E5B6'],
        ['info', /\b(?:INFO|INFORMATION)\b/gi, '#9EBFFE'],
        ['debug', /\b(?:DEBUG|TRACE|VERBOSE)\b/gi, '#B2A5D3'],
        ['method', /\b(?:GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\b/g, '#C6AEF1'],
        ['path', /(?:\b[A-Za-z]:[\\/]|\.{0,2}\/)?[\w@.\-]+(?:[\\/][\w@.\-]+)*\.(?:[cm]?[jt]sx?|cs|json|ya?ml|py|go|rs|html|css|sql|vue|svelte)(?::\d+(?::\d+)?|\(\d+(?:,\d+)?\))?/gi, '#BEC4F1'],
        ['duration', /\b\d+(?:\.\d+)?\s?(?:ms|µs|us|ns|s|sec|seconds|KB|MB|GB|KiB|MiB|GiB)\b/g, '#D8B48A'],
        ['key', /"[^"\r\n]{1,64}"(?=\s*:)/g, '#B4B9F7']
    ];

    function tokens(text) {
        const found = [];
        for (const [kind, regex, color] of rules) {
            regex.lastIndex = 0;
            for (const match of text.matchAll(regex)) {
                let value = match[0];
                if (kind === 'url') value = value.replace(/[.,;!?)\]}]+$/, '');
                const start = match.index, end = start + value.length;
                if (!value || found.some(t => start < t.end && end > t.start)) continue;
                found.push({ start, end, color, kind });
                if (found.length >= 128) return found.sort((a, b) => a.start - b.start);
            }
        }
        return found.sort((a, b) => a.start - b.start);
    }

    function attach(term) {
        let entries = [], frame = 0, disposed = false;
        const release = entry => { entry.decorations.forEach(d => d.dispose()); entry.marker.dispose(); };
        function clear() { entries.forEach(release); entries = []; }
        function refresh() {
            frame = 0;
            if (disposed) return;
            const buffer = term.buffer.active;
            if (buffer.type !== 'normal') { clear(); return; }
            const first = buffer.viewportY;
            const last = Math.min(buffer.length, first + Math.min(term.rows, 200));
            const plans = new Map();
            let row = first;
            // Include a wrapped prefix so URLs/timestamps retain their meaning at the viewport edge.
            while (row > Math.max(0, first - 8) && buffer.getLine(row)?.isWrapped) row--;
            while (row < last) {
                const cells = [];
                let text = '', wrappedRows = 0;
                do {
                    const line = buffer.getLine(row);
                    if (!line) break;
                    for (let x = 0; x < line.length; x++) {
                        const cell = line.getCell(x);
                        if (!cell || cell.getWidth() === 0) continue;
                        const chars = cell.getChars() || ' ';
                        cells.push({ row, x, width: cell.getWidth(), start: text.length, end: text.length + chars.length,
                            plain: cell.isFgDefault() && cell.isBgDefault() && !cell.isInverse() && !cell.isInvisible() });
                        text += chars;
                    }
                    row++; wrappedRows++;
                } while (row < buffer.length && buffer.getLine(row)?.isWrapped && wrappedRows < 16);
                const matches = tokens(text);
                let tokenIndex = 0;
                for (const cell of cells) {
                    if (cell.row < first || cell.row >= last || !cell.plain) continue;
                    while (tokenIndex < matches.length && matches[tokenIndex].end <= cell.start) tokenIndex++;
                    const token = matches[tokenIndex];
                    if (!token || token.start >= cell.end) continue;
                    const runs = plans.get(cell.row) || [];
                    const previous = runs[runs.length - 1];
                    if (previous && previous.x + previous.width === cell.x && previous.kind === token.kind)
                        previous.width += cell.width;
                    else runs.push({ x: cell.x, width: cell.width, color: token.color, kind: token.kind });
                    plans.set(cell.row, runs);
                }
            }
            const current = new Map();
            for (const entry of entries) {
                if (entry.marker.isDisposed || !plans.has(entry.marker.line)) release(entry);
                else current.set(entry.marker.line, entry);
            }
            entries = [];
            let count = 0;
            for (const [line, runs] of plans) {
                const signature = JSON.stringify(runs);
                const previous = current.get(line);
                if (count + runs.length > 800) { if (previous) release(previous); continue; }
                if (previous?.signature === signature) { entries.push(previous); count += runs.length; continue; }
                if (previous) release(previous);
                const marker = term.registerMarker(line - (buffer.baseY + buffer.cursorY));
                if (!marker) continue;
                const decorations = [];
                for (const run of runs) {
                    const decoration = term.registerDecoration({ marker, x: run.x, width: run.width,
                        foregroundColor: run.color, layer: 'bottom' });
                    if (decoration) {
                        decoration.onRender(element => {
                            element.style.pointerEvents = 'none';
                            element.dataset.logToken = run.kind;
                        });
                        decorations.push(decoration);
                    }
                }
                entries.push({ marker, decorations, signature }); count += runs.length;
            }
        }
        function schedule() {
            if (!disposed && !frame) frame = requestAnimationFrame(refresh);
        }
        const listeners = [term.onRender(schedule), term.onScroll(schedule), term.onResize(() => { clear(); schedule(); }),
            term.buffer.onBufferChange(() => { clear(); schedule(); })];
        schedule();
        return { refresh, schedule, dispose() {
            disposed = true; if (frame) cancelAnimationFrame(frame);
            listeners.forEach(l => l.dispose()); clear();
        }};
    }
    window.JustWatchLogHighlighter = { attach, tokens };
})();
