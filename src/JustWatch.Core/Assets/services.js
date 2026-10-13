
'use strict';
const $=id=>document.getElementById(id);let catalog=[],revision='',dirty=false,editing=-1,statuses={},saving=false,loaded=false;
const textFields=['id','name','description','project','group','command','workingDirectory','url','stopCommand'];
const boolFields=['autoStart','showTrayDot','autoRestart','agentRecovery','reclaimPort','reloadAndroidOnRestart'];
const jsonFields=['args','stopArgs','restartOutputPatterns','environment'];
function message(text){$('message').textContent=text}
async function api(path,options){const r=await fetch(path,options);const data=await r.json();if(!r.ok)throw Error(data.error||`HTTP ${r.status}`);return data}
function mark(){dirty=true;render()}
function el(tag,text,cls){const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n}
function button(text,fn,cls){const b=el('button',text,cls);b.type='button';b.disabled=saving;b.onclick=fn;return b}
function serviceLink(url,label,cls){const a=el('a',label,cls);a.href=url;a.target='_blank';a.rel='noopener noreferrer';a.title=url;return a}
function mode(p){return p.hideFromTray?'hidden':p.showInTray?'always':'running'}
function setMode(p,value){p.hideFromTray=value==='hidden';p.showInTray=value==='always'}
function traySelect(p){const select=el('select');select.setAttribute('aria-label',p.name+' tray visibility');for(const [value,text] of [['always','Always'],['running','While running'],['hidden','Hidden']]){const option=el('option',text);option.value=value;select.append(option)}select.value=mode(p);select.disabled=saving;select.onchange=()=>{setMode(p,select.value);mark()};return select}
function toggle(p,key,label){const wrapper=el('label'),input=el('input');input.type='checkbox';input.checked=!!p[key];input.disabled=saving||(key==='showTrayDot'&&p.hideFromTray);input.setAttribute('aria-label',p.name+' '+label);input.onchange=()=>{if(key==='showTrayDot'&&input.checked&&catalog.filter(x=>x.showTrayDot).length>=9){input.checked=false;message('Choose at most nine tray icon dots.');return}p[key]=input.checked;mark()};wrapper.append(input,el('span',label));return wrapper}
function render(){
 const focused=document.activeElement, focusService=focused?.closest('[data-service]')?.dataset.service,focusLabel=focused?.getAttribute('aria-label');
 $('add').disabled=saving||!loaded;$('reload').disabled=saving;$('reload').textContent=dirty?'Discard changes':'Reload';$('save').disabled=saving||!dirty;
 $('saveState').textContent=saving?'Saving changes…':dirty?'You have unsaved changes':'All changes saved';
 $('saveHint').textContent=dirty?'Save to apply your service and tray preferences.':'Tray preferences apply without restarting services.';
 document.querySelector('.save-bar').classList.toggle('unsaved',dirty);
 $('projects').replaceChildren(...[...new Set(catalog.map(p=>p.project?.trim()||'General'))].map(name=>{const option=el('option');option.value=name;return option}));
 $('list').replaceChildren();$('preview').replaceChildren();
 $('totalCount').textContent=catalog.length;$('runningCount').textContent=catalog.filter(p=>statuses[p.id]?.running).length;$('pinnedCount').textContent=catalog.filter(p=>!p.hideFromTray&&p.showInTray).length;
 const dots=catalog.filter(p=>p.showTrayDot&&!p.hideFromTray);
 dots.forEach(p=>{const d=el('span',undefined,'dot');d.style.background=statuses[p.id]?.running?p.trayColor:'#647084';d.title=p.name+(statuses[p.id]?.running?' · Running':' · Stopped');$('preview').append(d)});
 if(!dots.length)$('preview').append(el('span','Default JustWatch icon','muted'));
 const query=$('search').value.trim().toLowerCase(),filter=$('filter').value;
 const projects=new Map();let visible=0;
 catalog.forEach((p,i)=>{const key=p.project?.trim()||'General';const existing=[...projects.keys()].find(k=>k.toLowerCase()===key.toLowerCase())||key;if(!projects.has(existing))projects.set(existing,[]);projects.get(existing).push({p,i})});
 for(const [project,items] of projects){
  const matching=items.filter(({p})=>(!query||[p.name,p.id,p.project,p.description,p.group].join(' ').toLowerCase().includes(query))&&(filter==='all'||filter==='running'&&statuses[p.id]?.running||filter==='pinned'&&mode(p)==='always'||filter==='hidden'&&p.hideFromTray));
  if(!matching.length)continue;visible+=matching.length;
  const section=el('section',undefined,'project-section');const heading=el('div',undefined,'project-divider');
  heading.append(el('h2',project),el('span',`${items.filter(({p})=>statuses[p.id]?.running).length} running · ${items.length} services`,'muted'));
  section.append(heading);$('list').append(section);
  matching.forEach(({p,i})=>{
   const position=items.findIndex(item=>item.i===i);
   const card=el('article',undefined,'card'),row=el('div',undefined,'row'),dot=el('span',undefined,'dot'),info=el('div',undefined,'info'),state=statuses[p.id];
   card.dataset.service=p.id;card.setAttribute('aria-label',project+' · '+p.name);
   const url=p.url?.trim()||(p.port?`http://localhost:${p.port}`:null);
   dot.style.background=state?.running?p.trayColor:state?.status==='crashed'?'#ef8282':'#647084';
   const name=el('strong');name.append(url?serviceLink(url,p.name,'service-link'):el('span',p.name));info.append(name);
   if(p.group)info.append(el('span',p.group,'badge'));
   const description=el('p',p.description||`${p.command} ${(p.args||[]).join(' ')}`);description.title=description.textContent;info.append(description);
   info.append(el('div',`${p.id}${p.port?' · Port '+p.port:''}${state?.pid?' · PID '+state.pid:''}`,'muted'));
   const actions=el('div',undefined,'actions');
   if(url)actions.append(serviceLink(url,'Open ↗','open-link'));
   const active=state&&['running','starting','recoveringwithagent'].includes(state.status);
   const control=button(active?'Stop':'Start',()=>act(p,active?'stop':'start'));control.disabled=dirty||saving||!state||state.status==='stopping';actions.append(control);
   const restart=button('Restart',()=>act(p,'restart'));restart.disabled=dirty||saving||!state||state.status==='stopping';actions.append(restart);
   actions.append(button('Logs',()=>logs(p)),button('Edit service',()=>edit(i)));
   if(dirty)for(const control of [actions.children[url?1:0],restart])control.title='Save or discard changes before controlling a service.';
   row.append(dot,info,el('span',state?.status||'New','status '+(state?.status||'')),actions);card.append(row);
   const preferences=el('div',undefined,'preferences'),label=el('label','Tray visibility');label.append(traySelect(p));
   preferences.append(label,toggle(p,'autoStart','Auto-start'),toggle(p,'showTrayDot','Icon dot'));
   const more=el('div',undefined,'more');
   const up=button('↑',()=>{const other=items[position-1].i;[catalog[other],catalog[i]]=[catalog[i],catalog[other]];mark()});up.title='Move up in project';up.setAttribute('aria-label','Move '+p.name+' up');up.disabled=saving||position===0;
   const down=button('↓',()=>{const other=items[position+1].i;[catalog[other],catalog[i]]=[catalog[i],catalog[other]];mark()});down.title='Move down in project';down.setAttribute('aria-label','Move '+p.name+' down');down.disabled=saving||position===items.length-1;
   more.append(up,down,button('Remove',()=>{if(confirm(`Remove ${p.name}? Saving will stop it and remove its configuration.`)){catalog.splice(i,1);mark()}},'danger'));
   preferences.append(more);card.append(preferences);section.append(card);
  });
 }
 $('count').textContent=`${visible} of ${catalog.length} services`;
 if(!visible)$('list').append(el('div',catalog.length?'No matching services. Try another search or filter.':'No services yet. Add your first service to get started.','empty'));
 if(focusService&&focusLabel){const card=[...document.querySelectorAll('[data-service]')].find(c=>c.dataset.service===focusService);[...card?.querySelectorAll('[aria-label]')||[]].find(c=>c.getAttribute('aria-label')===focusLabel)?.focus({preventScroll:true})}
}
async function load(){try{if(dirty&&!confirm('Discard unsaved service changes?'))return;const data=await api('/api/services/configuration');catalog=data.services;revision=data.revision;dirty=false;loaded=true;$('save').disabled=true;$('workspaceName').textContent=data.projectName;$('projectPath').textContent=data.projectPath;document.title=`Settings · ${data.projectName}`;message('');await refresh();render()}catch(e){message(e.message)}}
async function refresh(){try{const data=await api('/api/services?scope=all');statuses=Object.fromEntries(data.services.map(s=>[s.id,s]));$('connection').textContent='JustWatch connected';if(!$('editor').open&&!saving&&!document.querySelector('.card:focus-within'))render()}catch(e){$('connection').textContent='Connection lost';message(`Unable to refresh services: ${e.message}`)}}
async function act(p,action){try{saving=true;render();await api(`/api/services/${encodeURIComponent(p.id)}/${action}`,{method:'POST',body:''});await refresh();message(`${p.name}: ${statuses[p.id]?.status||'updated'}`)}catch(e){message(e.message)}finally{saving=false;render()}}
async function logs(p){$('logTitle').textContent=p.name+' logs';$('logs').textContent='Loading…';$('logDialog').showModal();try{const r=await fetch(`/api/services/${encodeURIComponent(p.id)}/logs?lines=200`);if(!r.ok)throw Error(`HTTP ${r.status}`);$('logs').textContent=await r.text()}catch(e){$('logs').textContent=e.message}}
function edit(i){editing=i;const p=i<0?{id:'',name:'',command:'bun',args:['run','dev'],autoStart:false,autoRestart:true,agentRecovery:false,reclaimPort:false,showInTray:false,showTrayDot:false,trayColor:'#34a853',crashThreshold:5,crashWindowSeconds:30,restartOutputPatterns:['EMFILE:','EADDRINUSE'],environment:{},stopArgs:[]}:catalog[i];$('editorTitle').textContent=i<0?'Add service':'Edit '+p.name;textFields.forEach(k=>$(k).value=p[k]||'');boolFields.forEach(k=>$(k).checked=!!p[k]);$('trayMode').value=mode(p);jsonFields.forEach(k=>$(k).value=JSON.stringify(p[k],null,k==='environment'?2:0));$('port').value=p.port||'';$('trayColor').value=p.trayColor;$('crashThreshold').value=p.crashThreshold;$('crashWindowSeconds').value=p.crashWindowSeconds;$('editorError').textContent='';$('editor').showModal()}
$('form').onsubmit=e=>{e.preventDefault();try{const p=editing<0?{}:{...catalog[editing]};textFields.forEach(k=>p[k]=$(k).value.trim());boolFields.forEach(k=>p[k]=$(k).checked);setMode(p,$('trayMode').value);jsonFields.forEach(k=>{try{p[k]=JSON.parse($(k).value)}catch{throw Error(k+' must be valid JSON.')}if(k==='environment'){if(!p[k]||Array.isArray(p[k])||typeof p[k]!=='object'||Object.values(p[k]).some(v=>typeof v!=='string'))throw Error('Environment must be an object of string values.')}else if(!Array.isArray(p[k])||p[k].some(v=>typeof v!=='string'))throw Error(k+' must be an array of strings.')});p.port=$('port').value?Number($('port').value):null;p.trayColor=$('trayColor').value;p.crashThreshold=Number($('crashThreshold').value);p.crashWindowSeconds=Number($('crashWindowSeconds').value);if(catalog.some((x,j)=>j!==editing&&x.id===p.id))throw Error('This service ID is already in use.');if(p.showTrayDot&&catalog.filter((x,j)=>j!==editing&&x.showTrayDot).length>=9)throw Error('Choose at most nine tray dots.');if(editing<0)catalog.push(p);else catalog[editing]=p;$('editor').close();mark()}catch(err){$('editorError').textContent=err.message}};
$('save').onclick=async()=>{if(saving||!dirty)return;try{saving=true;$('save').disabled=true;render();message('Saving service configuration…');const data=await api('/api/services/configuration',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({revision,services:catalog})});catalog=data.services;revision=data.revision;dirty=false;message('Saved. Service controls and tray indicators are up to date.');await refresh()}catch(e){message(e.message)}finally{saving=false;$('save').disabled=!dirty;render()}};
$('search').oninput=render;$('filter').onchange=render;$('add').onclick=()=>edit(-1);$('reload').onclick=load;$('cancel').onclick=()=>$('editor').close();$('closeLogs').onclick=()=>$('logDialog').close();window.addEventListener('beforeunload',e=>{if(dirty||$('editor').open){e.preventDefault();e.returnValue=''}});load();setInterval(refresh,4000);
