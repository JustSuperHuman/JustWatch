const streamEls = { orch: null, opus: null, sonnet: null };

function getColMsgs(ch) { return document.getElementById('msgs-' + ch); }
function clearEmpty(ch) {
    const e = getColMsgs(ch).querySelector('.agent-empty');
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
    if (!streamEls[ch] || !streamEls[ch].classList.contains('streaming'))
        streamEls[ch] = addColMsg(ch, '', 'streaming');
    streamEls[ch].textContent += text;
    getColMsgs(ch).scrollTop = getColMsgs(ch).scrollHeight;
}
function endAgentStream(ch) {
    if (streamEls[ch]) { streamEls[ch].classList.remove('streaming'); streamEls[ch] = null; }
    setStopEnabled(ch, false);
}
function endAllStreams() {
    ['orch','opus','sonnet'].forEach(endAgentStream);
    document.getElementById('agent-send').disabled = false;
    document.getElementById('agent-input').disabled = false;
}
function addOrchInfo(t)  { addColMsg('orch', t, 'info'); }
function addOrchWarn(t)  { addColMsg('orch', t, 'warn'); }
function addOrchError(t) { addColMsg('orch', t, 'error'); }
function addUserEcho(t)  { addColMsg('orch', '> ' + t, 'user-echo'); }

function setStopEnabled(ch, enabled) {
    const btn = document.querySelector('#col-' + ch + ' .stop-btn');
    if (btn) btn.disabled = !enabled;
}

function upsertTask(id, name, type, status, channel) {
    let row = document.getElementById('task-' + id);
    if (!row) {
        row = document.createElement('div');
        row.id = 'task-' + id;
        row.className = 'task-row';
        row.innerHTML =
            '<span class="task-dot pending"></span>' +
            '<span class="task-name"></span>' +
            '<span class="task-channel"></span>' +
            '<button class="task-stop-btn">✕</button>';
        row.querySelector('.task-stop-btn').addEventListener('click', () => stopTask(id));
        document.getElementById('task-list').appendChild(row);
    }
    row.querySelector('.task-dot').className = 'task-dot ' + status;
    row.querySelector('.task-name').textContent = name;
    row.querySelector('.task-channel').textContent = channel;
    const done = (status === 'completed' || status === 'failed' || status === 'cancelled');
    row.classList.toggle('done', done);
    if (status === 'running') setStopEnabled('orch', true);
}

function addConvoMessage(text, cls) {
    const msgs = document.getElementById('convo-messages');
    const ph = msgs.querySelector('.convo-placeholder');
    if (ph) ph.remove();
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
    const ph = msgs.querySelector('.convo-placeholder');
    if (ph) ph.remove();
    const el = document.createElement('div');
    el.className = 'convo-msg convo-video';
    const time = new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'});
    const link = document.createElement('a');
    link.href = '/video?path=' + encodeURIComponent(filePath);
    link.target = '_blank';
    link.textContent = 'Open video';
    el.innerHTML = '<span class="convo-time">' + time + '</span>' + escapeHtml(text) + ' ';
    el.appendChild(link);
    msgs.appendChild(el);
    document.getElementById('user-convo').scrollTop = document.getElementById('user-convo').scrollHeight;
}
function escapeHtml(t) { const d = document.createElement('div'); d.textContent = t; return d.innerHTML; }
function escapeAttr(t) { return escapeHtml(t).replace(/"/g, '&quot;'); }

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

// --- Event handling (mirrors MainWindow) ---
function onChunk(channel, text) {
    if (channel === 'orch') {
        let level = 'info', msg = text;
        if (msg.startsWith('[info]'))       { level = 'info';  msg = msg.slice(6); }
        else if (msg.startsWith('[warn]'))  { level = 'warn';  msg = msg.slice(6); }
        else if (msg.startsWith('[error]')) { level = 'error'; msg = msg.slice(7); }
        else { appendAgentChunk('orch', text); return; }
        if (level === 'warn') addOrchWarn(msg);
        else if (level === 'error') addOrchError(msg);
        else addOrchInfo(msg);
    } else {
        appendAgentChunk(channel, text);
    }
}
function handleEvent(ev) {
    switch (ev.t) {
        case 'chunk':  onChunk(ev.channel, ev.text); break;
        case 'status':
            if (ev.status === 'start') setStopEnabled(ev.channel, true);
            else if (ev.status === 'end') endAgentStream(ev.channel);
            break;
        case 'task':   upsertTask(ev.id, ev.name, ev.type, ev.status, ev.channel); break;
        case 'convo':
            if (ev.ctype === 'video' && ev.filePath) addConvoVideo(ev.text, ev.filePath);
            else addConvoMessage(ev.text, ev.ctype);
            break;
        case 'user':   addUserEcho(ev.text); addConvoMessage(ev.text, 'user'); break;
        case 'notify': showToast(ev.level, ev.title, ev.message); break;
        case 'done':   endAllStreams(); break;
    }
}

// --- Outbound ---
function post(path, body) {
    return fetch(path, { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(body) });
}
function stopAgent(ch) { post('/stop', { channel: ch }); setStopEnabled(ch, false); }
function stopTask(id) { post('/stop-task', { taskId: id }); }
function sendAgent() {
    const input = document.getElementById('agent-input');
    const msg = input.value.trim();
    if (!msg) return;
    input.value = ''; input.style.height = 'auto';
    post('/chat', { message: msg });
}
document.getElementById('agent-send').addEventListener('click', sendAgent);
document.getElementById('agent-input').addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendAgent(); }
    setTimeout(() => { const ta = e.target; ta.style.height = 'auto'; ta.style.height = Math.min(ta.scrollHeight, 100) + 'px'; }, 0);
});

// --- Emulator management ---
let emuTimer = null;
let emuLoading = false;
const emuDrafts = new Map();

function switchView(view) {
    if (location.hash !== '#' + view) history.replaceState(null, '', '#' + view);
    const labels = {
        agent: ['Agent workspace','BUILD WITH YOUR AGENTS','Plan, build, and test. Follow each agent’s progress in one place.','Live activity'],
        emulators: ['Emulators','YOUR ANDROID LAB','Manage your MuMu devices, monitor resources, and tune each instance.','MuMu Player'],
        commands: ['Commands','YOUR PROJECT TOOLKIT','Find a package script and run it in a terminal on this computer.','Local execution']
    };
    const meta=labels[view];
    document.getElementById('view-title').textContent=meta[0];
    document.getElementById('view-breadcrumb').textContent=meta[0];
    document.getElementById('view-eyebrow').textContent=meta[1];
    document.getElementById('view-description').textContent=meta[2];
    document.getElementById('view-tag').textContent=meta[3];
    document.title=meta[0]+' · JustWatch';
    document.querySelectorAll('.ws-nav-link').forEach(link=>{
        if(link.dataset.page===view)link.setAttribute('aria-current','page');
        else link.removeAttribute('aria-current');
    });
    document.getElementById('agent-panel').classList.toggle('hidden', view !== 'agent');
    document.getElementById('emulator-panel').classList.toggle('hidden', view !== 'emulators');
    document.getElementById('commands-panel').classList.toggle('hidden', view !== 'commands');
    if (view === 'emulators') {
        loadEmulators();
        if (!emuTimer) emuTimer = setInterval(loadEmulators, 2000);
    } else if (emuTimer) {
        clearInterval(emuTimer); emuTimer = null;
    }
    if (view === 'commands') loadCommands(false);
}

async function loadEmulators() {
    if (emuLoading) return;
    emuLoading = true;
    try {
        const r = await fetch('/api/android/dashboard');
        if (!r.ok) throw new Error('HTTP ' + r.status);
        renderEmulators(await r.json());
    } catch (e) {
        renderEmulatorError('Failed to load: ' + e.message);
    } finally { emuLoading = false; }
}

function emuStatus(t) { const el = document.getElementById('emu-status'); if (el) el.textContent = t || ''; }
function renderEmulatorError(msg) { const err = document.getElementById('emu-error'); err.style.display = 'block'; err.textContent = msg; emuStatus(''); }
function cssId(s) { return String(s).replace(/[^a-zA-Z0-9_-]/g, '_'); }
function jsStr(s) { return String(s).replace(/\\/g, '\\\\').replace(/'/g, "\\'"); }
function fmtGb(mb) { if (!mb) return '0 GB'; return (mb / 1024).toFixed(1) + ' GB'; }
function fmtMem(mb) { if (mb == null) return '—'; return mb < 1024 ? Math.round(mb) + ' MB' : (mb / 1024).toFixed(1) + ' GB'; }
function emuBarClass(p) { return p >= 85 ? 'hot' : (p >= 60 ? 'warn' : ''); }
function emuIdleStatHtml(e) {
    if (e.idleSeconds != null && e.idleSeconds < 60)
        return '<div class="emu-stat"><span class="lbl">Idle</span><span class="val in-use">In use</span></div>';
    if (e.idleTime)
        return '<div class="emu-stat"><span class="lbl">Idle</span><span class="val">' + escapeHtml(e.idleTime) + '</span></div>';
    return '';
}
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
    emuStatus('');
    const host = data.host || {};
    const usedMb = (host.usedRamMb != null) ? host.usedRamMb : Math.max(0, (host.totalRamMb || 0) - (host.availableRamMb || 0));
    document.getElementById('emu-host').innerHTML =
        'Cores <b>' + (host.logicalCores || '?') + '</b> &middot; RAM <b>' + fmtGb(usedMb) + ' / ' + fmtGb(host.totalRamMb) + '</b>';
    const err = document.getElementById('emu-error');
    if (data.error) { err.style.display = 'block'; err.textContent = data.error; }
    else { err.style.display = 'none'; }
    const grid = document.getElementById('emu-grid');
    const ae = document.activeElement;
    if (ae && grid.contains(ae) && (ae.tagName === 'INPUT' || ae.tagName === 'SELECT')) return;
    const list = data.emulators || [];
    document.getElementById('emu-total').textContent=list.length;
    document.getElementById('emu-running').textContent=list.filter(e=>e.isRunning).length;
    document.getElementById('emu-stopped').textContent=list.filter(e=>!e.isRunning).length;
    if (!list.length) { grid.innerHTML = '<div class="emu-empty">No managed MuMu instances found.</div>'; return; }
    const expanded=[...grid.querySelectorAll('.emu-tuning[open]')].map(el=>el.closest('.emu-card').id);
    grid.innerHTML = list.map(emuCardHtml).join('');
    for(const id of expanded){const card=document.getElementById(id);if(card)card.querySelector('.emu-tuning').open=true;}
    grid.querySelectorAll('.emu-tuning input,.emu-tuning select').forEach(input=>input.addEventListener('input',()=>{
        const card=input.closest('.emu-card');
        emuDrafts.set(card.id,{ram:card.querySelector('.emu-ram').value,cores:card.querySelector('.emu-cores').value,gpu:card.querySelector('.emu-gpu').value});
    }));
    for(const [id,draft] of emuDrafts){const card=document.getElementById(id);if(!card)continue;card.querySelector('.emu-ram').value=draft.ram;card.querySelector('.emu-cores').value=draft.cores;card.querySelector('.emu-gpu').value=draft.gpu;}
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
          emuIdleStatHtml(e) +
        '</div>'
    ) : '<div class="emu-note">Stopped — launch to see live stats.</div>';

    const controls =
        '<div class="emu-actions">' +
          (e.isRunning
            ? '<button class="emu-btn danger" onclick="emuStop(\'' + jsStr(e.id) + '\')">Stop</button>' +
              '<button class="emu-btn secondary" onclick="emuRestartApp(\'' + jsStr(e.id) + '\')">Restart App</button>'
            : '<button class="emu-btn" onclick="emuLaunch(\'' + jsStr(e.id) + '\')">Launch</button>') +
          (e.isClone ? '<button class="emu-btn danger" onclick="emuDelete(\'' + jsStr(e.id) + '\')">Delete</button>' : '') +
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
      controls +
      '<details class="emu-tuning"><summary>Performance tuning</summary><div class="emu-tune">' +
        '<div class="emu-field"><label>RAM (MB)<input aria-label="RAM in megabytes" class="emu-ram" type="number" min="768" step="256" value="' + (e.ramSizeMb || '') + '"></label></div>' +
        '<div class="emu-field"><label>CPU cores<input aria-label="CPU cores" class="emu-cores" type="number" min="1" step="1" value="' + (e.cores || '') + '"></label></div>' +
        '<div class="emu-field wide"><label>Renderer<select aria-label="Renderer" class="emu-gpu">' + gpuOptions(e.gpuMode) + '</select></label></div>' +
      '</div>' +
      '<div class="emu-actions"><button class="emu-btn" onclick="emuSaveConfig(\'' + jsStr(e.id) + '\')">Save tuning</button></div>' +
      '<div class="emu-note">Tuning applies on next MuMu restart.</div></details>' +
    '</div>';
}

async function emuApi(method, path, label) {
    emuStatus(label || 'Working…');
    try {
        const r = await fetch(path, { method: method });
        if (!r.ok) { const j = await r.json().catch(() => ({})); throw new Error(j.error || ('HTTP ' + r.status)); }
        await loadEmulators();
    } catch (e) { renderEmulatorError(e.message); }
}
function emuLaunch(id) { emuApi('POST', '/api/android/emulators/' + encodeURIComponent(id) + '/launch', 'Launching…'); }
function emuStop(id) { emuApi('POST', '/api/android/emulators/' + encodeURIComponent(id) + '/stop', 'Stopping…'); }
function emuRestartApp(id) { emuApi('POST', '/api/android/emulators/' + encodeURIComponent(id) + '/restart-app', 'Restarting app…'); }
function emuDelete(id) { if (!confirm('Delete emulator clone "' + id + '"? This cannot be undone.')) return; emuApi('DELETE', '/api/android/emulators/' + encodeURIComponent(id), 'Deleting…'); }
function emuClone() { emuApi('POST', '/api/android/emulators/clone', 'Cloning…'); }
function emuStartAll() { emuApi('POST', '/api/android/emulators/start-all', 'Starting all emulators…'); }
function emuRestartAll() { emuApi('POST', '/api/android/emulators/restart-all', 'Restarting running emulators…'); }
function emuScorchedUpdate() { emuApi('POST', '/api/android/emulators/scorched-update', 'Starting emulators and installing latest app…'); }

// --- Commands ---
let commandsLoading = false;
let commandsLoaded = false;
let commandData = {packages:[]};
document.getElementById('command-search').addEventListener('input',()=>renderCommands(commandData));

function cmdStatus(t) { const el = document.getElementById('cmd-status'); if (el) el.textContent = t || ''; }

async function loadCommands(rescan) {
    if (commandsLoading || (commandsLoaded && !rescan)) return;
    commandsLoading = true;
    cmdStatus(rescan ? 'Refreshing…' : 'Loading…');
    try {
        const r = await fetch('/api/commands' + (rescan ? '?rescan=1' : ''));
        if (!r.ok) {
            const j = await r.json().catch(() => ({}));
            throw new Error(j.error || ('HTTP ' + r.status));
        }
        renderCommands(await r.json());
        commandsLoaded = true;
        cmdStatus('');
    } catch (e) {
        cmdStatus('');
        document.getElementById('cmd-list').innerHTML='<div class="cmd-empty">Unable to load commands. Use Refresh to try again.</div>';
        showToast('error', 'Commands failed', e.message);
    } finally { commandsLoading = false; }
}

function renderCommands(data) {
    const list = document.getElementById('cmd-list');
    commandData=data;
    const query=document.getElementById('command-search').value.trim().toLowerCase();
    const all=data.packages||[];
    const packages=all.map(pkg=>{
        const packageMatch=[pkg.displayName,pkg.relativeDir,pkg.description].join(' ').toLowerCase().includes(query);
        return {...pkg,scripts:(pkg.scripts||[]).filter(script=>!query||packageMatch||[script.name,script.description,script.command].join(' ').toLowerCase().includes(query))};
    }).filter(pkg=>pkg.scripts.length);
    const count=packages.reduce((sum,pkg)=>sum+pkg.scripts.length,0);
    document.getElementById('command-count').textContent=count+' commands · '+packages.length+' packages';
    if (!packages.length) {
        list.innerHTML = '<div class="cmd-empty">'+(query?'No commands match your search. Try a script or package name.':'No package scripts found.')+'</div>';
        return;
    }
    list.innerHTML = commandRootGroups(packages).map(commandRootHtml).join('');
}

function commandRootGroups(packages) {
    const map = new Map();
    for (const pkg of packages) {
        const root = pkg.root || '';
        if (!map.has(root)) map.set(root, []);
        map.get(root).push(pkg);
    }
    return Array.from(map.entries()).map(([root, pkgs]) => ({
        root,
        packages: pkgs.sort((a, b) => String(a.relativeDir || '').localeCompare(String(b.relativeDir || '')))
    }));
}

function commandRootHtml(group) {
    return '<div class="cmd-root-title">' + escapeHtml(rootLeaf(group.root)) + '</div>' +
        group.packages.map(commandPackageHtml).join('');
}

function rootLeaf(path) {
    const value = String(path || '').replace(/[\\\\/]+$/, '');
    if (!value) return path || 'Root';
    const parts = value.split(/[\\\\/]+/);
    return parts[parts.length - 1] || path || 'Root';
}

function commandPackageHtml(pkg) {
    return '<div class="cmd-package">' +
        '<div class="cmd-package-head">' +
          '<span class="cmd-package-name">' + escapeHtml(pkg.displayName) + '</span>' +
          (pkg.relativeDir && pkg.relativeDir !== '.' ? '<span class="cmd-package-dir">' + escapeHtml(pkg.relativeDir) + '</span>' : '') +
        '</div>' +
        (pkg.description ? '<div class="cmd-package-desc">' + escapeHtml(pkg.description) + '</div>' : '') +
        '<div class="cmd-scripts">' + (pkg.scripts || []).map(s => commandButtonHtml(pkg, s)).join('') + '</div>' +
      '</div>';
}

function commandButtonHtml(pkg, script) {
    const tip = (script.description ? script.description + '\n' : '') + script.command;
    return '<button class="cmd-btn" aria-label="Run ' + escapeAttr(script.name+' in '+pkg.displayName) + '" title="' + escapeAttr(tip) + '" onclick="runCommand(\'' +
        jsStr(pkg.dir) + '\',\'' + jsStr(script.name) + '\')"><span class="cmd-identity"><span class="cmd-script-name">' + escapeHtml(script.name) +
        '</span><span class="cmd-script-detail">' + escapeHtml(script.description || script.command) + '</span></span><span class="cmd-run">Run ↗</span></button>';
}

async function runCommand(dir, script) {
    try {
        const r = await post('/api/commands/run', { dir: dir, script: script });
        if (!r.ok) {
            const j = await r.json().catch(() => ({}));
            throw new Error(j.error || ('HTTP ' + r.status));
        }
        showToast('success', 'Command started', 'Opened a terminal on the host machine.');
    } catch (e) {
        showToast('error', 'Command failed', e.message);
    }
}

async function emuSaveConfig(id) {
    const card = document.getElementById('emu-card-' + cssId(id));
    if (!card) return;
    const ram = parseInt(card.querySelector('.emu-ram').value, 10);
    const cores = parseInt(card.querySelector('.emu-cores').value, 10);
    const gpu = card.querySelector('.emu-gpu').value;
    emuStatus('Saving tuning…');
    try {
        const r = await fetch('/api/android/emulators/' + encodeURIComponent(id) + '/config', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ramSizeMb: isNaN(ram) ? null : ram, cores: isNaN(cores) ? null : cores, gpuMode: gpu })
        });
        if (!r.ok) { const j = await r.json().catch(() => ({})); throw new Error(j.error || ('HTTP ' + r.status)); }
        emuDrafts.delete('emu-card-'+cssId(id));
        await loadEmulators();
        showToast('success','Tuning saved','Changes apply on the next MuMu restart.');
    } catch (e) { renderEmulatorError(e.message); }
}

// --- SSE connection ---
const conn = document.getElementById('conn');
function setConn(state, txt) { conn.className = 'ws-connection ' + state; conn.querySelector('.txt').textContent = txt; }
let es;
function connect() {
    es = new EventSource('/events');
    es.onopen = () => setConn('online', 'Connected');
    es.onmessage = e => { try { handleEvent(JSON.parse(e.data)); } catch (err) {} };
    es.onerror = () => { setConn('offline', 'Reconnecting…'); };
}
document.querySelectorAll('.ws-nav-link').forEach(link=>{
    if(['agent','emulators','commands'].includes(link.dataset.page))link.addEventListener('click',e=>{
        if(e.ctrlKey||e.metaKey||e.shiftKey||e.altKey||e.button!==0)return;
        e.preventDefault();location.hash=link.dataset.page;
    });
});
const initialView = location.hash.slice(1);
switchView(['agent','emulators','commands'].includes(initialView)?initialView:'agent');
window.addEventListener('hashchange', () => { const view=location.hash.slice(1); if (['agent','emulators','commands'].includes(view)) switchView(view); });
connect();
window.addEventListener('beforeunload', () => { if (es) es.close(); });
