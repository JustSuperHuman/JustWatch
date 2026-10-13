namespace JustWatch.TrayApp;

public static class TerminalHtml
{
    public static string Generate(string[] terminalIds)
    {
        var idList = string.Join(",", terminalIds.Select(id => $"\"{id}\""));

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8" />
                <style>
                    * { margin: 0; padding: 0; box-sizing: border-box; }
                    html, body {
                        width: 100%; height: 100%;
                        overflow: hidden; background: #10151c;
                        font-family: 'Segoe UI', sans-serif;
                    }
                    #terminals { position: relative; width: 100%; height: 100%; }
                    .terminal-container {
                        position: absolute; top: 0; left: 0; right: 0; bottom: 0;
                        visibility: hidden;
                    }
                    .terminal-container.active { visibility: visible; z-index: 1; }
                    .terminal-container .xterm { width: 100% !important; height: 100% !important; }
                    .terminal-container .xterm-viewport { width: 100% !important; }
                    #loading { color: #666; font-family: 'Cascadia Code', monospace; font-size: 14px; padding: 20px; position: absolute; top: 0; left: 0; z-index: 10; }
                    #loading.hidden { display: none; }
                    #error-msg { color: #f14c4c; font-family: 'Cascadia Code', monospace; font-size: 13px; padding: 20px; white-space: pre-wrap; position: absolute; top: 0; left: 0; z-index: 10; }
                    #error-msg.hidden { display: none; }

                    /* 3-column agent panel */
                    #agent-panel {
                        position: absolute; top: 0; left: 0; right: 0; bottom: 0;
                        display: flex; flex-direction: column;
                        visibility: hidden; background: #10151c;
                    }
                    #agent-panel.active { visibility: visible; z-index: 2; }

                    #agent-columns {
                        flex: 1; display: flex; gap: 1px;
                        background: #2a3847; overflow: hidden;
                    }
                    .agent-col {
                        flex: 1; display: flex; flex-direction: column;
                        background: #10151c; min-width: 0;
                    }
                    .agent-header {
                        display: flex; align-items: center; justify-content: space-between;
                        padding: 6px 10px; font-size: 12px; font-weight: 600;
                        color: #fff; flex-shrink: 0;
                    }
                    .agent-header .dot {
                        width: 8px; height: 8px; border-radius: 50%;
                        display: inline-block; margin-right: 6px;
                    }
                    .agent-header .label { display: flex; align-items: center; }
                    .agent-header .model-tag {
                        font-size: 10px; font-weight: 400; color: #999;
                        margin-left: 6px;
                    }
                    .stop-btn {
                        background: transparent; border: 1px solid #666; color: #999;
                        border-radius: 4px; padding: 2px 8px; font-size: 11px;
                        cursor: pointer; transition: all 0.15s;
                    }
                    .stop-btn:hover { background: #f14c4c; border-color: #f14c4c; color: #fff; }
                    .stop-btn:disabled { opacity: 0.3; cursor: default; }
                    .stop-btn:disabled:hover { background: transparent; border-color: #666; color: #999; }

                    .col-orch .agent-header { background: #1a3a5c; }
                    .col-opus .agent-header { background: #3a1a5c; }
                    .col-sonnet .agent-header { background: #1a4a3a; }

                    .agent-messages {
                        flex: 1; overflow-y: auto; padding: 8px;
                        font-family: 'Cascadia Code', 'Consolas', monospace;
                        font-size: 12px; line-height: 1.6; color: #ccc;
                        user-select: text; cursor: text;
                    }

                    .agent-messages .msg {
                        padding: 4px 0; white-space: pre-wrap; word-break: break-word;
                    }
                    .agent-messages .msg.info { color: #0dbc79; }
                    .agent-messages .msg.warn { color: #e5e510; }
                    .agent-messages .msg.error { color: #f14c4c; }
                    .agent-messages .msg.streaming { border-left: 2px solid #007acc; padding-left: 8px; }
                    .agent-messages .msg.user-echo {
                        color: #999; font-style: italic; border-bottom: 1px solid #333;
                        padding-bottom: 6px; margin-bottom: 4px;
                    }
                    .agent-empty {
                        color: #444; text-align: center; padding: 20px;
                        font-size: 12px; font-family: 'Segoe UI', sans-serif;
                    }

                    /* Task list */
                    #task-list {
                        border-bottom: 1px solid #2a3847;
                        max-height: 180px; overflow-y: auto;
                        font-family: 'Segoe UI', sans-serif;
                    }
                    #task-list:empty { display: none; }
                    .task-row {
                        display: flex; align-items: center; gap: 6px;
                        padding: 3px 10px; font-size: 11px; color: #bbb;
                    }
                    .task-dot {
                        width: 7px; height: 7px; border-radius: 50%; flex-shrink: 0;
                    }
                    .task-dot.pending { background: #555; }
                    .task-dot.queued { background: #e5e510; }
                    .task-dot.running { background: #007acc; animation: pulse 1.2s ease-in-out infinite; }
                    .task-dot.completed { background: #0dbc79; }
                    .task-dot.failed { background: #f14c4c; }
                    .task-dot.cancelled { background: #555; opacity: 0.4; }
                    @keyframes pulse { 0%,100% { opacity:1; } 50% { opacity:0.4; } }
                    .task-name { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                    .task-channel {
                        font-size: 9px; color: #666; background: #2a2a2a;
                        padding: 1px 5px; border-radius: 3px;
                    }
                    .task-stop-btn {
                        background: none; border: 1px solid #555; color: #888;
                        border-radius: 3px; padding: 0 4px; font-size: 9px;
                        cursor: pointer; line-height: 14px;
                    }
                    .task-stop-btn:hover { border-color: #f14c4c; color: #f14c4c; }
                    .task-row.done .task-name { color: #666; }
                    .task-row.done .task-stop-btn { display: none; }

                    #agent-input-area {
                        display: flex; gap: 8px; padding: 10px 12px;
                        background: #19232f; border-top: 1px solid #2a3847; flex-shrink: 0;
                    }
                    #agent-input {
                        flex: 1; background: #10151c; color: #ccc;
                        border: 1px solid #2a3847; border-radius: 6px;
                        padding: 8px 12px; font-size: 13px;
                        font-family: 'Segoe UI', sans-serif;
                        resize: none; outline: none;
                        min-height: 38px; max-height: 100px;
                    }
                    #agent-input:focus { border-color: #007acc; }
                    #agent-input::placeholder { color: #555; }
                    #agent-send {
                        background: #007acc; color: #fff; border: none;
                        border-radius: 6px; padding: 0 18px; font-size: 13px;
                        cursor: pointer; font-weight: 600;
                    }
                    #agent-send:hover { background: #0098ff; }
                    #agent-send:disabled { background: #444; cursor: not-allowed; }

                    /* User Conversation panel */
                    #user-convo {
                        flex-shrink: 0;
                        max-height: 180px; min-height: 60px;
                        overflow-y: auto;
                        border-top: 1px solid #2a3847;
                        background: #1a1a2e;
                        padding: 0;
                    }
                    #convo-header {
                        display: flex; align-items: center; justify-content: space-between;
                        padding: 4px 10px; font-size: 11px; font-weight: 600;
                        color: #e5e5e5; background: #16213e;
                        position: sticky; top: 0; z-index: 1;
                    }
                    #convo-header .dot { width: 7px; height: 7px; border-radius: 50%; background: #fbbc05; display: inline-block; margin-right: 5px; }
                    #convo-messages { padding: 6px 10px; user-select: text; cursor: text; }
                    .convo-msg {
                        padding: 4px 0; font-size: 13px; line-height: 1.5;
                        color: #ddd; font-family: 'Segoe UI', sans-serif;
                        border-bottom: 1px solid #1e2a4a;
                        white-space: pre-wrap; word-break: break-word;
                    }
                    .convo-msg:last-child { border-bottom: none; }
                    .convo-msg .convo-time {
                        font-size: 10px; color: #556; margin-right: 6px;
                    }
                    .convo-msg a {
                        color: #4285f4; text-decoration: none;
                    }
                    .convo-msg a:hover { text-decoration: underline; }
                    .convo-msg.convo-video {
                        color: #0dbc79;
                    }
                    .convo-msg.convo-error {
                        color: #f14c4c;
                    }
                    .convo-msg.convo-user {
                        color: #888; font-style: italic;
                    }

                    {{EmulatorStyles}}
                    {{ToastStyles}}
                </style>
            </head>
            <body>
                <div id="loading">Loading terminal...</div>
                <div id="error-msg" class="hidden"><span id="error-details"></span><button onclick="location.reload()">Reload terminal</button></div>
                <div id="terminals"></div>

                <div id="agent-panel">
                    <div id="agent-columns">
                        <div class="agent-col col-orch" id="col-orch">
                            <div class="agent-header">
                                <span class="label"><span class="dot" style="background:#4285f4"></span>Orchestrator</span>
                                <button class="stop-btn" onclick="stopAgent('orch')" disabled>Stop All</button>
                            </div>
                            <div id="task-list"></div>
                            <div class="agent-messages" id="msgs-orch">
                                <div class="agent-empty">Coordinates tasks, builds, tests, and recordings</div>
                            </div>
                        </div>
                        <div class="agent-col col-opus" id="col-opus">
                            <div class="agent-header">
                                <span class="label"><span class="dot" style="background:#d670d6"></span>Opus Agent<span class="model-tag">claude-opus</span></span>
                                <button class="stop-btn" onclick="stopAgent('opus')" disabled>Stop</button>
                            </div>
                            <div class="agent-messages" id="msgs-opus">
                                <div class="agent-empty">Executes complex tasks and test automation</div>
                            </div>
                        </div>
                        <div class="agent-col col-sonnet" id="col-sonnet">
                            <div class="agent-header">
                                <span class="label"><span class="dot" style="background:#0dbc79"></span>Sonnet Agent<span class="model-tag">claude-sonnet</span></span>
                                <button class="stop-btn" onclick="stopAgent('sonnet')" disabled>Stop</button>
                            </div>
                            <div class="agent-messages" id="msgs-sonnet">
                                <div class="agent-empty">Handles test planning and orchestration decisions</div>
                            </div>
                        </div>
                    </div>
                    <div id="user-convo">
                        <div id="convo-header">
                            <span><span class="dot"></span>User Conversation</span>
                        </div>
                        <div id="convo-messages">
                            <div class="convo-msg" style="color:#556">Waiting for requests...</div>
                        </div>
                    </div>
                    <div id="agent-input-area">
                        <textarea id="agent-input"
                            placeholder="Ask anything, or say 'test the web app' to run automated tests..."
                            rows="1"></textarea>
                        <button id="agent-send">Send</button>
                    </div>
                </div>

                {{EmulatorPanelHtml}}

                <div id="toast-container"></div>

                <style>{{TerminalAssets.Styles}}</style>
                <script>{{TerminalAssets.Xterm}}
                </script>
                <script>{{TerminalAssets.Fit}}
                </script>
                <script>{{TerminalAssets.WebLinks}}
                </script>
                <script>{{TerminalAssets.LogHighlighter}}
                </script>
                <script>
                    let loadError = false;
                    function showError(msg) {
                        loadError = true;
                        document.getElementById('error-msg').classList.remove('hidden');
                        document.getElementById('error-details').textContent += msg + '\n';
                        document.getElementById('loading').classList.add('hidden');
                    }

                    const terminals = {};
                    const terminalIds = [{{idList}}];
                    const terminalIdleTailMs = 2 * 60 * 1000;
                    let terminalIdleTailTimer = null;
                    let initialized = false;

                    // Per-column streaming state
                    const streamEls = { orch: null, opus: null, sonnet: null };

                    const theme = {
                        background:'#10151c',foreground:'#ced8e4',cursor:'#89f0c5',
                        selectionBackground:'#304b58',selectionForeground:'#ffffff',
                        black:'#000000',red:'#cd3131',green:'#0dbc79',yellow:'#e5e510',
                        blue:'#2472c8',magenta:'#bc3fbc',cyan:'#11a8cd',white:'#e5e5e5',
                        brightBlack:'#666666',brightRed:'#f14c4c',brightGreen:'#23d18b',
                        brightYellow:'#f5f543',brightBlue:'#3b8eea',brightMagenta:'#d670d6',
                        brightCyan:'#29b8db',brightWhite:'#e5e5e5'
                    };

                    function resolveAddon(g) {
                        if (!g) return null;
                        if (typeof g === 'function') return g;
                        for (var k of Object.keys(g)) if (typeof g[k] === 'function') return g[k];
                        return null;
                    }

                    function createTerminal(id, setActive) {
                        const c = document.createElement('div');
                        c.id = 'terminal-' + id;
                        c.className = 'terminal-container' + (setActive ? ' active' : '');
                        document.getElementById('terminals').appendChild(c);
                        const t = new Terminal({ theme, allowProposedApi:true, fontSize:13, lineHeight:1.25, fontFamily:"'Cascadia Code','Consolas',monospace", cursorBlink:true, scrollback:10000, convertEol:true });
                        const fa = resolveAddon(window.FitAddon); const fit = fa ? new fa() : null;
                        if (fit) t.loadAddon(fit);
                        try { const wl = resolveAddon(window.WebLinksAddon); if (wl) t.loadAddon(new wl()); } catch(e){}
                        t.open(c);
                        if (setActive && fit) try { fit.fit(); } catch(e){}
                        t.onData(d => { try { window.chrome.webview.postMessage({type:'input',id,data:d}); } catch(e){} });
                        c.addEventListener('wheel', scheduleTerminalIdleTail, { passive: true });
                        c.addEventListener('pointerdown', scheduleTerminalIdleTail);
                        c.addEventListener('touchstart', scheduleTerminalIdleTail, { passive: true });
                        c.addEventListener('keydown', scheduleTerminalIdleTail);
                        const highlighter = JustWatchLogHighlighter.attach(t);
                        terminals[id] = { term:t, fitAddon:fit, container:c, highlighter };
                    }

                    function syncServiceTerminals(ids) {
                        for (const id of Object.keys(terminals)) {
                            if (!ids.includes(id) && !id.startsWith('__')) {
                                terminals[id].highlighter.dispose(); terminals[id].term.dispose(); terminals[id].container.remove(); delete terminals[id];
                            }
                        }
                        ids.forEach(id => { if (!terminals[id]) createTerminal(id, false); });
                    }
                    function writeToTerminal(id, data) { if (terminals[id]) terminals[id].term.write(data); }

                    function scrollTerminalToBottom(id) {
                        const entry = terminals[id];
                        if (!entry) return;
                        try {
                            if (entry.term && typeof entry.term.scrollToBottom === 'function') entry.term.scrollToBottom();
                        } catch(e) {}
                    }

                    function scrollAllTerminalsToBottom() {
                        Object.keys(terminals).forEach(scrollTerminalToBottom);
                    }

                    function scheduleTerminalIdleTail() {
                        if (terminalIdleTailTimer) clearTimeout(terminalIdleTailTimer);
                        terminalIdleTailTimer = setTimeout(() => {
                            terminalIdleTailTimer = null;
                            scrollAllTerminalsToBottom();
                        }, terminalIdleTailMs);
                    }

                    function switchTerminal(id) {
                        Object.keys(terminals).forEach(k => terminals[k].container.classList.remove('active'));
                        document.getElementById('agent-panel').classList.remove('active');
                        document.getElementById('emulator-panel').classList.remove('active');
                        if (terminals[id]) {
                            terminals[id].container.classList.add('active');
                            setTimeout(() => { try { terminals[id].fitAddon.fit(); } catch(e){} }, 50);
                        }
                    }

                    function showChat() {
                        Object.keys(terminals).forEach(k => terminals[k].container.classList.remove('active'));
                        document.getElementById('emulator-panel').classList.remove('active');
                        document.getElementById('agent-panel').classList.add('active');
                        document.getElementById('agent-input').focus();
                    }

                    function clearTerminal(id) { if (terminals[id]) terminals[id].term.clear(); }

                    function fitAll() {
                        Object.keys(terminals).forEach(k => {
                            if (terminals[k].container.classList.contains('active'))
                                try { terminals[k].fitAddon.fit(); } catch(e){}
                        });
                    }

                    // --- Agent column functions ---

                    function getColMsgs(ch) { return document.getElementById('msgs-' + ch); }

                    function clearEmpty(ch) {
                        const m = getColMsgs(ch);
                        const e = m.querySelector('.agent-empty');
                        if (e) e.remove();
                    }

                    function addColMsg(ch, text, cls) {
                        clearEmpty(ch);
                        const m = getColMsgs(ch);
                        const el = document.createElement('div');
                        el.className = 'msg' + (cls ? ' ' + cls : '');
                        el.textContent = text;
                        m.appendChild(el);
                        m.scrollTop = m.scrollHeight;
                        return el;
                    }

                    function appendAgentChunk(ch, text) {
                        clearEmpty(ch);
                        if (!streamEls[ch] || !streamEls[ch].classList.contains('streaming')) {
                            streamEls[ch] = addColMsg(ch, '', 'streaming');
                        }
                        streamEls[ch].textContent += text;
                        getColMsgs(ch).scrollTop = getColMsgs(ch).scrollHeight;
                    }

                    function endAgentStream(ch) {
                        if (streamEls[ch]) {
                            streamEls[ch].classList.remove('streaming');
                            streamEls[ch] = null;
                        }
                        setStopEnabled(ch, false);
                    }

                    function endAllStreams() {
                        ['orch','opus','sonnet'].forEach(ch => endAgentStream(ch));
                        document.getElementById('agent-send').disabled = false;
                        document.getElementById('agent-input').disabled = false;
                        document.getElementById('agent-input').focus();
                    }

                    function addOrchInfo(text) { addColMsg('orch', text, 'info'); }
                    function addOrchWarn(text) { addColMsg('orch', text, 'warn'); }
                    function addOrchError(text) { addColMsg('orch', text, 'error'); }
                    function addUserEcho(text) { addColMsg('orch', '> ' + text, 'user-echo'); }

                    function setStopEnabled(ch, enabled) {
                        const btn = document.querySelector('#col-' + ch + ' .stop-btn');
                        if (btn) btn.disabled = !enabled;
                    }

                    function stopAgent(ch) {
                        try { window.chrome.webview.postMessage({ type: 'stop', channel: ch }); } catch(e){}
                        setStopEnabled(ch, false);
                    }

                    // --- Task list ---
                    function upsertTask(id, name, type, status, channel) {
                        var row = document.getElementById('task-' + id);
                        if (!row) {
                            row = document.createElement('div');
                            row.id = 'task-' + id;
                            row.className = 'task-row';
                            row.innerHTML =
                                '<span class="task-dot pending"></span>' +
                                '<span class="task-name"></span>' +
                                '<span class="task-channel"></span>' +
                                '<button class="task-stop-btn" onclick="stopTask(\'' + id + '\')">✕</button>';
                            document.getElementById('task-list').appendChild(row);
                        }
                        row.querySelector('.task-dot').className = 'task-dot ' + status;
                        row.querySelector('.task-name').textContent = name;
                        row.querySelector('.task-channel').textContent = channel;
                        var done = (status === 'completed' || status === 'failed' || status === 'cancelled');
                        row.classList.toggle('done', done);
                        // Enable orch stop when any task is running
                        if (status === 'running') setStopEnabled('orch', true);
                    }

                    function stopTask(id) {
                        try { window.chrome.webview.postMessage({ type: 'stop-task', taskId: id }); } catch(e){}
                    }

                    // --- User Conversation ---
                    function addConvoMessage(text, cls) {
                        const msgs = document.getElementById('convo-messages');
                        // Remove placeholder
                        const placeholder = msgs.querySelector('[style]');
                        if (placeholder && placeholder.textContent.includes('Waiting')) placeholder.remove();

                        const el = document.createElement('div');
                        el.className = 'convo-msg' + (cls ? ' convo-' + cls : '');
                        const time = new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'});
                        el.innerHTML = '<span class="convo-time">' + time + '</span>' + escapeHtml(text);
                        msgs.appendChild(el);

                        const convo = document.getElementById('user-convo');
                        convo.scrollTop = convo.scrollHeight;
                    }

                    function addConvoVideo(text, filePath) {
                        const msgs = document.getElementById('convo-messages');
                        const placeholder = msgs.querySelector('[style]');
                        if (placeholder && placeholder.textContent.includes('Waiting')) placeholder.remove();

                        const el = document.createElement('div');
                        el.className = 'convo-msg convo-video';
                        const time = new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'});
                        el.innerHTML = '<span class="convo-time">' + time + '</span>' +
                            escapeHtml(text) + ' <a href="#" onclick="openFile(\'' +
                            filePath.replace(/\\/g, '\\\\').replace(/'/g, "\\'") +
                            '\'); return false;">Open video</a>';
                        msgs.appendChild(el);

                        document.getElementById('user-convo').scrollTop =
                            document.getElementById('user-convo').scrollHeight;
                    }

                    function openFile(path) {
                        try { window.chrome.webview.postMessage({ type: 'open-file', path: path }); } catch(e){}
                    }

                    function escapeHtml(text) {
                        const d = document.createElement('div');
                        d.textContent = text;
                        return d.innerHTML;
                    }

                    // --- Input ---

                    document.getElementById('agent-send').addEventListener('click', sendAgent);
                    document.getElementById('agent-input').addEventListener('keydown', e => {
                        if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendAgent(); }
                        setTimeout(() => {
                            const ta = e.target;
                            ta.style.height = 'auto';
                            ta.style.height = Math.min(ta.scrollHeight, 100) + 'px';
                        }, 0);
                    });

                    function sendAgent() {
                        const input = document.getElementById('agent-input');
                        const msg = input.value.trim();
                        if (!msg) return;
                        input.value = ''; input.style.height = 'auto';
                        addUserEcho(msg);
                        addConvoMessage(msg, 'user');
                        try { window.chrome.webview.postMessage({ type: 'chat', message: msg }); } catch(e){}
                        // Re-enable after brief delay so user can send more messages
                        setTimeout(() => {
                            document.getElementById('agent-send').disabled = false;
                            document.getElementById('agent-input').disabled = false;
                            document.getElementById('agent-input').focus();
                        }, 500);
                    }

                    {{EmulatorScript}}

                    {{ToastScript}}

                    // --- Terminal init ---

                    try {
                        if (typeof Terminal === 'undefined') showError('xterm.js failed to load');
                        else if (typeof FitAddon === 'undefined') showError('FitAddon failed to load');
                        else {
                            terminalIds.forEach((id, i) => createTerminal(id, i === 0));
                            initialized = true;
                            document.getElementById('loading').classList.add('hidden');
                        }
                    } catch(e) { showError('Init error: ' + e.message); }

                    window.addEventListener('resize', () => fitAll());
                    if (typeof ResizeObserver !== 'undefined')
                        new ResizeObserver(() => fitAll()).observe(document.getElementById('terminals'));

                    setTimeout(() => {
                        fitAll();
                        try { window.chrome.webview.postMessage({type:'ready',initialized,error:loadError}); } catch(e){}
                    }, 300);
                </script>
            </body>
            </html>
            """.Replace("</style>", JustWatch.Core.Services.WebUiStyles.Scrollbars + "</style>");
    }

    // Notification toasts — bottom-right overlay shown on every tab. Fed by
    // MainWindow from NotificationCenter events. Kept as plain raw strings so
    // the CSS/JS braces stay literal; they are spliced into Generate()'s template.

    private const string ToastStyles = """
        #toast-container {
            position: fixed; right: 14px; bottom: 14px; z-index: 1000;
            display: flex; flex-direction: column; gap: 8px; pointer-events: none;
            font-family: 'Segoe UI', sans-serif;
        }
        .toast {
            background: #19232f; border: 1px solid #2a3847; border-left: 3px solid #007acc;
            border-radius: 6px; padding: 8px 12px; min-width: 220px; max-width: 340px;
            box-shadow: 0 4px 12px rgba(0,0,0,0.5); font-size: 12px; color: #ddd;
            pointer-events: auto; cursor: pointer; animation: toast-in 0.2s ease;
        }
        .toast.success { border-left-color: #0dbc79; }
        .toast.warn { border-left-color: #e5e510; }
        .toast.error { border-left-color: #f14c4c; }
        .toast .toast-title { font-weight: 600; color: #eee; }
        .toast .toast-msg { color: #aaa; margin-top: 2px; word-break: break-word; }
        .toast.fade { opacity: 0; transform: translateX(10px); transition: opacity .3s, transform .3s; }
        @keyframes toast-in { from { opacity: 0; transform: translateX(10px); } to { opacity: 1; transform: none; } }
        """;

    private const string ToastScript = """
        function showToast(level, title, message) {
            const c = document.getElementById('toast-container');
            const el = document.createElement('div');
            el.className = 'toast ' + (level || 'info');
            const t = document.createElement('div');
            t.className = 'toast-title'; t.textContent = title || '';
            el.appendChild(t);
            if (message) {
                const m = document.createElement('div');
                m.className = 'toast-msg'; m.textContent = message;
                el.appendChild(m);
            }
            el.onclick = () => el.remove();
            c.appendChild(el);
            while (c.children.length > 5) c.removeChild(c.firstChild);
            setTimeout(() => { el.classList.add('fade'); setTimeout(() => el.remove(), 350); },
                level === 'error' ? 10000 : 6000);
        }
        """;

    // Emulator management screen. Kept as plain (non-interpolated) raw strings so the
    // CSS/JS braces stay literal; they are spliced into Generate()'s template above.

    private const string EmulatorStyles = """
        #emulator-panel {
            position: absolute; top: 0; left: 0; right: 0; bottom: 0;
            visibility: hidden; background: #10151c; overflow-y: auto;
            font-family: 'Segoe UI', sans-serif; color: #ccc;
        }
        #emulator-panel.active { visibility: visible; z-index: 2; }
        .emu-toolbar {
            display: flex; align-items: center; gap: 10px; flex-wrap: wrap;
            padding: 10px 14px; background: #19232f;
            border-bottom: 1px solid #2a3847; position: sticky; top: 0; z-index: 1;
        }
        .emu-toolbar .emu-title { font-weight: 600; color: #eee; font-size: 14px; }
        #emu-status { color: #e5c07b; }
        .emu-host { margin-left: auto; display: flex; gap: 14px; font-size: 12px; color: #999; }
        .emu-host b { color: #ddd; font-weight: 600; }
        .emu-mem { margin-top: 2px; }
        .emu-mem-bar { display: flex; height: 12px; border-radius: 6px; overflow: hidden; background: #10151c; border: 1px solid #333; }
        .emu-mem-bar > span { display: block; height: 100%; transition: width 0.4s ease; }
        .emu-mem-bar .seg-app { background: linear-gradient(90deg, #0dbc79, #23d18b); }
        .emu-mem-bar .seg-used { background: #4a6fa5; }
        .emu-mem-legend { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 6px; font-size: 10px; color: #aaa; }
        .emu-mem-legend b { color: #eee; font-weight: 600; }
        .emu-mem-legend .sw { display: inline-block; width: 9px; height: 9px; border-radius: 2px; margin-right: 5px; vertical-align: middle; }
        .emu-mem-legend .sw.app { background: #0dbc79; }
        .emu-mem-legend .sw.used { background: #4a6fa5; }
        .emu-mem-legend .sw.free { background: #2a2a2a; border: 1px solid #444; }
        .emu-btn {
            background: #0e639c; color: #fff; border: none; border-radius: 5px;
            padding: 6px 12px; font-size: 12px; cursor: pointer; font-family: inherit;
        }
        .emu-btn:hover { background: #1177bb; }
        .emu-btn.secondary { background: #3a3d41; }
        .emu-btn.secondary:hover { background: #4a4d51; }
        .emu-btn.danger { background: #5a1d1d; }
        .emu-btn.danger:hover { background: #7a2626; }
        .emu-grid {
            display: grid; grid-template-columns: repeat(auto-fill, minmax(330px, 1fr));
            gap: 14px; padding: 14px;
        }
        .emu-card {
            background: #19232f; border: 1px solid #2a3847; border-radius: 8px;
            padding: 14px; display: flex; flex-direction: column; gap: 10px;
        }
        .emu-card.running { border-color: #2c5a45; }
        .emu-card-head { display: flex; align-items: center; gap: 8px; }
        .emu-dot { width: 9px; height: 9px; border-radius: 50%; background: #666; flex-shrink: 0; }
        .emu-dot.on { background: #0dbc79; box-shadow: 0 0 6px #0dbc79; }
        .emu-name { font-weight: 600; color: #eee; font-size: 14px; }
        .emu-badge {
            font-size: 9px; text-transform: uppercase; letter-spacing: 0.5px;
            padding: 1px 6px; border-radius: 3px; background: #3a3d41; color: #bbb;
        }
        .emu-badge.core { background: #4b3a1a; color: #f5c451; }
        .emu-state { margin-left: auto; font-size: 11px; color: #999; }
        .emu-stats { display: flex; gap: 14px; font-size: 11px; color: #bbb; }
        .emu-stat { display: flex; flex-direction: column; gap: 3px; flex: 1; }
        .emu-stat .lbl { color: #777; font-size: 10px; text-transform: uppercase; }
        .emu-stat .val { color: #ddd; font-size: 13px; font-weight: 600; }
        .emu-bar { height: 4px; background: #10151c; border-radius: 2px; overflow: hidden; }
        .emu-bar > span { display: block; height: 100%; background: #0dbc79; }
        .emu-bar > span.warn { background: #e5e510; }
        .emu-bar > span.hot { background: #f14c4c; }
        .emu-tune { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
        .emu-field { display: flex; flex-direction: column; gap: 3px; }
        .emu-field.wide { grid-column: 1 / 3; }
        .emu-field label { font-size: 10px; color: #888; text-transform: uppercase; }
        .emu-field input, .emu-field select {
            background: #10151c; color: #ddd; border: 1px solid #2a3847;
            border-radius: 4px; padding: 5px 7px; font-size: 12px; font-family: inherit; outline: none;
        }
        .emu-field input:focus, .emu-field select:focus { border-color: #007acc; }
        .emu-actions { display: flex; flex-wrap: wrap; gap: 6px; }
        .emu-note { font-size: 10px; color: #777; }
        .emu-empty { padding: 40px; text-align: center; color: #666; font-size: 13px; }
        .emu-error { margin: 10px 14px 0; padding: 8px 12px; background: #3a1a1a; color: #f1a0a0; border-radius: 6px; font-size: 12px; }
        """;

    private const string EmulatorPanelHtml = """
        <div id="emulator-panel">
            <div class="emu-toolbar">
                <span class="emu-title">MuMu Player</span>
                <button class="emu-btn secondary" onclick="emuAction('refresh')">Refresh</button>
                <button class="emu-btn" onclick="emuAction('start-all')">Start All Emulators</button>
                <button class="emu-btn secondary" onclick="emuAction('restart-all')">Restart Running</button>
                <button class="emu-btn" onclick="emuAction('scorched-update')">Scorched Update</button>
                <button class="emu-btn" onclick="emuAction('clone')">Clone Core</button>
                <span id="emu-status"></span>
                <span class="emu-host" id="emu-host"></span>
            </div>
            <div id="emu-error" class="emu-error" style="display:none"></div>
            <div id="emu-grid" class="emu-grid">
                <div class="emu-empty">Loading emulators…</div>
            </div>
        </div>
        """;

    private const string EmulatorScript = """
        let emuData = null;

        function showEmulators() {
            Object.keys(terminals).forEach(k => terminals[k].container.classList.remove('active'));
            document.getElementById('agent-panel').classList.remove('active');
            document.getElementById('emulator-panel').classList.add('active');
        }

        function emuPost(msg) { try { window.chrome.webview.postMessage(msg); } catch (e) {} }
        function emuStatus(t) { const el = document.getElementById('emu-status'); if (el) el.textContent = t || ''; }

        function emuAction(action, id) {
            if (action === 'delete' && id && !confirm('Delete emulator clone "' + id + '"? This cannot be undone.')) return;
            const labels = { launch:'Launching…', stop:'Stopping…', 'restart-app':'Restarting app…',
                'start-all':'Starting all emulators…', 'restart-all':'Restarting running emulators…',
                'scorched-update':'Starting emulators and installing latest app…',
                clone:'Cloning…', delete:'Deleting…', refresh:'Refreshing…' };
            emuStatus(labels[action] || 'Working…');
            emuPost({ type: 'emu', action: action, id: id || null });
        }

        function emuSaveConfig(id) {
            const card = document.getElementById('emu-card-' + cssId(id));
            if (!card) return;
            const ram = parseInt(card.querySelector('.emu-ram').value, 10);
            const cores = parseInt(card.querySelector('.emu-cores').value, 10);
            const gpu = card.querySelector('.emu-gpu').value;
            emuStatus('Saving tuning…');
            emuPost({ type: 'emu', action: 'save-config', id: id,
                ram: isNaN(ram) ? null : ram, cores: isNaN(cores) ? null : cores, gpu: gpu });
        }

        function renderEmulatorError(msg) {
            const err = document.getElementById('emu-error');
            err.style.display = 'block';
            err.textContent = msg;
            emuStatus('');
        }

        function cssId(s) { return String(s).replace(/[^a-zA-Z0-9_-]/g, '_'); }
        function jsStr(s) { return String(s).replace(/\\/g, '\\\\').replace(/'/g, "\\'"); }
        function fmtGb(mb) { if (!mb) return '0 GB'; return (mb / 1024).toFixed(1) + ' GB'; }
        function fmtMem(mb) { if (mb == null) return '—'; return mb < 1024 ? Math.round(mb) + ' MB' : (mb / 1024).toFixed(1) + ' GB'; }
        function emuBarClass(pct) { return pct >= 85 ? 'hot' : (pct >= 60 ? 'warn' : ''); }

        function gpuOptions(sel) {
            return ['vk','dx']
                .map(m => '<option value="' + m + '"' + (m === sel ? ' selected' : '') + '>' + (m === 'vk' ? 'Vulkan' : 'DirectX') + '</option>').join('');
        }

        // Per-emulator GUEST memory: how much the JustGains app itself uses (PSS) vs
        // other Android system usage, against this emulator's own RAM (not the host's).
        function emuCardMemHtml(e) {
            const total = e.guestTotalRamMb || 0;
            if (!total) return e.isRunning ? '<div class="emu-note">App / guest memory unavailable.</div>' : '';
            const used = (e.guestUsedRamMb != null) ? e.guestUsedRamMb : 0;
            const app = e.appMemoryMb || 0;
            const other = Math.max(0, used - app);
            const pct = v => total > 0 ? Math.min(100, (v / total) * 100) : 0;
            const appShare = total > 0 ? ((app / total) * 100).toFixed(1) : '0';
            const appLabel = (e.appMemoryMb == null)
                ? 'App <b>not running</b>'
                : 'App <b>' + fmtMem(app) + '</b> (' + appShare + '%)';
            return '<div class="emu-mem">' +
                    '<div class="emu-mem-bar">' +
                      '<span class="seg-app" style="width:' + pct(app) + '%" title="JustGains app: ' + fmtMem(app) + '"></span>' +
                      '<span class="seg-used" style="width:' + pct(other) + '%" title="Other (Android system): ' + fmtMem(other) + '"></span>' +
                    '</div>' +
                    '<div class="emu-mem-legend">' +
                      '<span><i class="sw app"></i>' + appLabel + '</span>' +
                      '<span><i class="sw used"></i>Used <b>' + fmtMem(used) + '</b></span>' +
                      '<span><i class="sw free"></i>Total <b>' + fmtMem(total) + '</b></span>' +
                    '</div>' +
                  '</div>';
        }

        function renderEmulators(data) {
            emuData = data;
            emuStatus('');
            const host = data.host || {};
            const usedMb = (host.usedRamMb != null) ? host.usedRamMb : Math.max(0, (host.totalRamMb || 0) - (host.availableRamMb || 0));
            document.getElementById('emu-host').innerHTML =
                'Cores <b>' + (host.logicalCores || '?') + '</b> &middot; RAM <b>' + fmtGb(usedMb) + ' / ' + fmtGb(host.totalRamMb) + '</b>';

            const err = document.getElementById('emu-error');
            if (data.error) { err.style.display = 'block'; err.textContent = data.error; }
            else { err.style.display = 'none'; }

            const grid = document.getElementById('emu-grid');
            // Don't clobber tuning inputs the user is actively editing — skip this cycle.
            const ae = document.activeElement;
            if (ae && grid.contains(ae) && (ae.tagName === 'INPUT' || ae.tagName === 'SELECT')) return;

            const list = data.emulators || [];
            if (!list.length) { grid.innerHTML = '<div class="emu-empty">No managed MuMu instances found.</div>'; return; }
            grid.innerHTML = list.map(emuCardHtml).join('');
        }

        function emuCardHtml(e) {
            const cid = cssId(e.id);
            const cpu = (e.cpuPercent == null) ? null : e.cpuPercent;
            const stats = e.isRunning ? (
                '<div class="emu-stats">' +
                  '<div class="emu-stat"><span class="lbl">CPU</span><span class="val">' +
                    (cpu == null ? '—' : cpu + '%') + '</span>' +
                    '<div class="emu-bar"><span class="' + emuBarClass(cpu || 0) + '" style="width:' + (cpu || 0) + '%"></span></div></div>' +
                  '<div class="emu-stat"><span class="lbl">Host RAM</span><span class="val">' +
                    (e.memoryMb == null ? '—' : fmtMem(e.memoryMb)) + '</span></div>' +
                  '<div class="emu-stat"><span class="lbl">PID</span><span class="val">' + (e.pid || '—') + '</span></div>' +
                '</div>'
            ) : '<div class="emu-note">Stopped — launch to see live stats.</div>';

            const controls =
                '<div class="emu-actions">' +
                  (e.isRunning
                    ? '<button class="emu-btn danger" onclick="emuAction(\'stop\',\'' + jsStr(e.id) + '\')">Stop</button>' +
                      '<button class="emu-btn secondary" onclick="emuAction(\'restart-app\',\'' + jsStr(e.id) + '\')">Restart App</button>'
                    : '<button class="emu-btn" onclick="emuAction(\'launch\',\'' + jsStr(e.id) + '\')">Launch</button>') +
                  (e.isClone ? '<button class="emu-btn danger" onclick="emuAction(\'delete\',\'' + jsStr(e.id) + '\')">Delete</button>' : '') +
                '</div>';

            return '<div class="emu-card' + (e.isRunning ? ' running' : '') + '" id="emu-card-' + cid + '">' +
              '<div class="emu-card-head">' +
                '<span class="emu-dot' + (e.isRunning ? ' on' : '') + '"></span>' +
                '<span class="emu-name">' + escapeHtml(e.name) + '</span>' +
                '<span class="emu-badge ' + (e.isCore ? 'core' : '') + '">' + (e.isCore ? 'Core' : 'Clone') + '</span>' +
                '<span class="emu-state">' + (e.isRunning ? escapeHtml(e.serial || 'starting / connecting') : 'stopped') + '</span>' +
              '</div>' +
              stats +
              emuCardMemHtml(e) +
              '<div class="emu-tune">' +
                '<div class="emu-field"><label>RAM (MB)</label><input class="emu-ram" type="number" min="768" step="256" value="' + (e.ramSizeMb || '') + '"></div>' +
                '<div class="emu-field"><label>CPU cores</label><input class="emu-cores" type="number" min="1" step="1" value="' + (e.cores || '') + '"></div>' +
                '<div class="emu-field wide"><label>Renderer</label><select class="emu-gpu">' + gpuOptions(e.gpuMode) + '</select></div>' +
              '</div>' +
              '<div class="emu-actions"><button class="emu-btn" onclick="emuSaveConfig(\'' + jsStr(e.id) + '\')">Save tuning</button></div>' +
              controls +
              '<div class="emu-note">Tuning applies on next MuMu restart.</div>' +
            '</div>';
        }
        """;
}
