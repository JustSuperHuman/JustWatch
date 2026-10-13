'use strict';
const $=id=>document.getElementById(id);
const paths={trash:'M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7m4-7v7',layers:'M12 3 3 8l9 5 9-5-9-5ZM3 12l9 5 9-5M3 16l9 5 9-5',grid:'M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z',activity:'M3 12h4l3-8 4 16 3-8h4',folder:'M3 7V5h6l2 2h10v13H3V7Z',plus:'M12 5v14M5 12h14',sliders:'M4 7h5m5 0h6M4 17h10m5 0h1M9 4v6m5 4v6',terminal:'m5 6 6 6-6 6m8 0h6',arrow:'M7 17 17 7M7 7h10v10',chevron:'m9 5 7 7-7 7',monitor:'M3 4h18v13H3zM8 21h8m-4-4v4',refresh:'M20 7v5h-5M4 17v-5h5M5.4 7a8 8 0 0 1 13-2L20 8M4 16l1.6 3a8 8 0 0 0 13-2',search:'M21 21l-5-5M18 10a8 8 0 1 1-16 0 8 8 0 0 1 16 0',list:'M8 5h13M8 12h13M8 19h13M3 5h.1M3 12h.1M3 19h.1',play:'m8 4 12 8-12 8V4Z',stop:'M6 6h12v12H6z',close:'m6 6 12 12M6 18 18 6',sparkle:'m12 3 2.7 6.3L21 12l-6.3 2.7L12 21l-2.7-6.3L3 12l6.3-2.7L12 3Z',check:'m5 12 4 4L19 6'};
function icon(name){const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 24 24');svg.setAttribute('aria-hidden','true');const path=document.createElementNS(svg.namespaceURI,'path');path.setAttribute('d',paths[name]||paths.folder);svg.append(path);return svg}
document.querySelectorAll('[data-icon]').forEach(n=>n.classList.contains('brand-mark')?n.replaceChildren(icon(n.dataset.icon)):n.replaceWith(icon(n.dataset.icon)));
function el(tag,text,cls){const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n}
function button(text,fn,cls,ic){const b=el('button',undefined,cls);b.type='button';if(ic)b.append(icon(ic));if(text)b.append(document.createTextNode(text));b.onclick=fn;return b}
let data={projects:[],folders:[],scanning:false},filter='all',folder=null,loaded=false,loading=false,mutation=false,epoch=0,logTarget=null,logLoading=false,toastTimer,cardSignature='',folderSignature='';
const selections=new Map(),busy=new Set(),failedIcons=new Set(),pendingOpens=new Map();
let deleteTarget=null,deleting=false,deleteReview=0;
const hasIcon=p=>Boolean(p.iconUrl)&&!failedIcons.has(p.iconUrl);
function displayName(p){return p.name.replace(/^@([^/]+)\//,'$1 / ').replace(/[-_]+/g,' ').replace(/\b[a-z]/g,c=>c.toUpperCase())}
const active=s=>['running','starting','stopping','recoveringwithagent'].includes(s.status);
const projectActive=p=>p.scripts.some(active);
function notify(text){$('toast').textContent=text;clearTimeout(toastTimer);toastTimer=setTimeout(()=>$('toast').textContent='',7000)}
async function api(path,method='GET',body,timeout=45000){const response=await fetch('/api/projects'+path,{method,headers:body?{'Content-Type':'application/json'}:{},body:body?JSON.stringify(body):method==='POST'?'':undefined,signal:AbortSignal.timeout(timeout)});let result;try{result=await response.json()}catch{throw Error('JustWatch returned an unreadable response. Please try again.')}if(!response.ok)throw Error(result.error||'Unable to reach JustWatch.');return result}
function selectScript(p){return p.scripts.find(s=>s.name===selections.get(p.id))||p.scripts.find(active)||p.scripts[0]}
function setFilter(value,path=null){filter=value;folder=path;cardSignature='';render()}
function folderLabel(path){return path.replace(/[\\/]+$/,'').split(/[\\/]/).pop()||path}
function contains(path,root){path=path.toLowerCase();root=root.toLowerCase().replace(/[\\/]+$/,'');return path===root||path.startsWith(root+'\\')||path.startsWith(root+'/')}
function showFolders(){$('folderError').textContent='';$('folderDialog').showModal();$('folderPath').focus();renderSavedFolders()}
function renderSavedFolders(){const rows=data.folders.map(f=>{const row=el('div',undefined,'saved-folder');const path=el('span',f.path,'folder-name');path.title=f.path;const remove=button('Remove',()=>removeFolder(f.path),'remove');remove.setAttribute('aria-label','Remove watch folder '+f.path);remove.disabled=mutation;row.append(icon('folder'),path,remove);return row});$('savedFolders').replaceChildren(...rows)}
async function removeFolder(path){epoch++;mutation=true;renderSavedFolders();try{data=await api('/folders','DELETE',{path});if(folder===path)setFilter('all');render();notify('Folder removed. Existing services are still available in Service settings.')}catch(e){$('folderError').textContent=e.message}finally{mutation=false;renderSavedFolders()}}
function render(){
 handlePendingOpens();
 const total=data.projects.length,running=data.projects.filter(projectActive).length;
 $('allCount').textContent=total;$('runningCount').textContent=running;
 $('allProjects').classList.toggle('active',filter==='all');$('runningProjects').classList.toggle('active',filter==='running');$('allProjects').setAttribute('aria-current',filter==='all'?'page':'false');$('runningProjects').setAttribute('aria-current',filter==='running'?'page':'false');
 $('pageTitle').textContent=filter==='running'?'Running':folder?folderLabel(folder):'Applications';$('folderHint').hidden=data.folders.length>0;
 const fs=JSON.stringify([data.folders,folder]);if(fs!==folderSignature){folderSignature=fs;$('folders').replaceChildren(...data.folders.map(f=>{const b=button('',()=>setFilter('folder',f.path),'nav-item'+(folder===f.path?' active':''),'folder');b.title=f.path;b.append(el('span',folderLabel(f.path),'folder-name'),el('span',f.error?'!':f.projectCount,'nav-count'));b.setAttribute('aria-label',f.path);return b}));if($('folderDialog').open)renderSavedFolders()}
 $('rescan').disabled=data.scanning||!loaded||!data.folders.length;$('rescan').querySelector('svg').classList.toggle('spin',data.scanning);
 const scanIcon=icon(data.scanning?'refresh':'check');if(data.scanning)scanIcon.classList.add('spin');
 let scanText=data.scanning?`Scanning ${Number(data.visitedDirectories).toLocaleString()} folders…`:data.completedAt?'Updated '+new Date(data.completedAt).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'}):'Ready to discover';
 $('scanState').replaceChildren(scanIcon,document.createTextNode(scanText));
 const warnings=data.folders.filter(f=>f.error).map(f=>`${f.path}: ${f.error}`);const skipped=data.folders.reduce((sum,f)=>sum+f.skipped,0);if(skipped)warnings.push(`${skipped} unreadable folders or invalid package files skipped.`);if(data.error)warnings.push(data.error);$('notice').textContent=warnings.join(' ');$('notice').hidden=!warnings.length;
 let projects=data.projects.filter(p=>(filter!=='running'||projectActive(p))&&(!folder||contains(p.directory,folder)));
 const query=$('search').value.trim().toLowerCase();if(query)projects=projects.filter(p=>[p.name,displayName(p),p.directory,p.framework,p.packageManager,...p.scripts.map(s=>s.name)].some(v=>v.toLowerCase().includes(query)));
 const sort=$('sort').value;projects.sort((a,b)=>Number(hasIcon(b))-Number(hasIcon(a))||(sort==='running'?Number(projectActive(b))-Number(projectActive(a)):sort==='framework'?a.framework.localeCompare(b.framework):0)||displayName(a).localeCompare(displayName(b))||a.directory.localeCompare(b.directory));
 $('visibleCount').textContent=projects.length;$('collectionLabel').textContent=query?'Search results':filter==='running'?'Running applications':folder?'Applications in '+folderLabel(folder):'All applications';
 const sig=JSON.stringify([projects,[...selections],[...busy],[...failedIcons],filter,folder,query,loaded,data.scanning&&total===0,data.folders.length]);if(sig===cardSignature)return;cardSignature=sig;
 const focus=document.activeElement?.dataset.focus;
 $('projects').replaceChildren(...projects.map(projectCard));
 if(!projects.length){const box=el('div',undefined,'empty'),symbol=el('div',undefined,'empty-icon');symbol.append(icon(query?'search':filter==='running'?'play':'folder'));box.append(symbol);
  const title=!loaded?'Connecting to your workspace…':query?'No matching applications':filter==='running'?'Nothing running':!data.folders.length?'Your apps belong here':data.scanning?'Finding your web apps…':'No web services found';
  const hint=query?'Try another name, framework, or folder path.':filter==='running'?'Start an application from your library to see it here.':!data.folders.length?'Add a watch folder to discover your local web apps and launch them in one click.':data.scanning?'JustWatch is checking your folders for web server scripts.':'This folder has no detected web server scripts. Add another folder or rescan after adding a web app.';
  box.append(el('h2',title),el('p',hint));if(loaded)box.append(button(query?'Clear search':filter==='running'?'Browse applications':'Add folder',query?()=>{$('search').value='';render()}:filter==='running'?()=>setFilter('all'):showFolders,'btn primary',query?'search':filter==='running'?'grid':'plus'));$('projects').append(box)}
 if(focus){const target=[...document.querySelectorAll('[data-focus]')].find(n=>n.dataset.focus===focus);target?.focus({preventScroll:true})}
}
function projectCard(p){
 const script=selectScript(p),isActive=script&&active(script),pending=busy.has(p.id),card=el('article',undefined,'project-card');card.dataset.projectId=p.id;card.setAttribute('aria-label',p.name);
 const top=el('div',undefined,'card-top'),appIcon=el('div',displayName(p)[0]||'A','app-icon'+(hasIcon(p)?' has-icon':''));appIcon.style.setProperty('--hue',String([...p.name].reduce((h,c)=>(h+c.charCodeAt(0)*17)%360,225)));
 if(hasIcon(p)){const img=el('img');img.src=p.iconUrl;img.alt='';img.loading='lazy';img.onerror=()=>{failedIcons.add(p.iconUrl);render()};appIcon.append(img)}
 const identity=el('div',undefined,'card-identity'),title=el('h2',displayName(p));title.title=p.name;
 const meta=el('div',undefined,'card-meta');meta.append(el('span',p.framework));
 const status=script?.status||'stopped';const label=pending?'Updating':status==='running'?'Running':status==='crashed'?'Failed':status==='stopping'?'Stopping':isActive?'Starting':null;
 if(label){const pill=el('span',undefined,'status-pill '+status);pill.append(el('span',undefined,'status-dot'),document.createTextNode(label));meta.append(el('span','·','meta-dot'),pill)}
 identity.append(title,meta);top.append(appIcon,identity);
 const dir=el('p',p.directory,'project-dir');dir.title=p.directory;
 const command=el('div',undefined,'script-picker');
 if(script){const select=el('select');select.setAttribute('aria-label','Dev script for '+p.name);select.dataset.focus='script-'+p.id;for(const s of p.scripts){const option=el('option',s.name+(active(s)?' • running':''));option.value=s.name;select.append(option)}select.value=script.name;select.title=p.packageManager+' run '+script.name+' — '+script.command;select.disabled=pending;select.onchange=()=>{selections.set(p.id,select.value);render()};command.append(select)}
 const footer=el('div',undefined,'card-footer'),secondary=el('div',undefined,'card-secondary');const logs=button('',()=>openLogs(p,script),'icon-button','terminal');logs.disabled=!script;logs.title='View logs';logs.setAttribute('aria-label','View logs for '+p.name);logs.dataset.focus='logs-'+p.id;secondary.append(logs);const remove=button('',()=>showDelete(p),'icon-button delete-button','trash');remove.title='Delete project from disk';remove.setAttribute('aria-label','Delete '+p.name+' from disk');remove.dataset.focus='delete-'+p.id;remove.disabled=pending;secondary.append(remove);
 if(script?.url){const link=el('a',undefined,'open-button');link.append(document.createTextNode('Open'),icon('arrow'));link.href=script.url;link.target='_blank';link.rel='noopener noreferrer';link.title='Open '+script.url;link.setAttribute('aria-label','Open '+p.name);secondary.append(link)}
 const control=button(pending?'Wait':isActive?'Stop':'Start',()=>act(p,script,isActive?'stop':'start'),'start-button'+(isActive?' stop':''),isActive?'stop':'play');control.disabled=!script||pending;control.setAttribute('aria-label',(isActive?'Stop ':'Start ')+p.name);control.dataset.focus='control-'+p.id;const launch=el('div',undefined,'launch-control');launch.append(control);
 if(!isActive&&!pending){const menu=el('details',undefined,'launch-menu'),summary=el('summary');summary.title='More launch options';summary.setAttribute('aria-label','Launch options for '+p.name);summary.append(icon('chevron'));const options=el('div',undefined,'launch-options');options.append(button('Start & Open',()=>{menu.open=false;startAndOpen(p,script)},'','arrow'));menu.append(summary,options);launch.append(menu)}else control.style.borderRadius='6px';
 footer.append(command,secondary,launch);
 card.append(top,dir,footer);return card;
}
function closePendingWindow(item){try{if(item.window&&!item.window.closed&&item.window.location.href==='about:blank')item.window.close()}catch{}}
function startAndOpen(p,script){
 if(!script||busy.has(p.id))return;
 const popup=window.open('about:blank','_blank');
 if(popup){popup.opener=null;popup.document.title='Starting '+displayName(p);const note=popup.document.createElement('p');note.textContent='Starting '+displayName(p)+'… This tab will open the app when its URL is ready.';note.style.cssText='font:15px system-ui;margin:48px;color:#777';popup.document.body.append(note)}
 else notify('Your browser blocked the new tab. Use Open when the server is ready.');
 pendingOpens.set(p.id,{script:script.name,window:popup,expires:Date.now()+120000});
 act(p,script,'start');
}
function handlePendingOpens(){
 for(const [id,item] of pendingOpens){
  if(busy.has(id))continue;
  const s=data.projects.find(p=>p.id===id)?.scripts.find(s=>s.name===item.script);
  if(s?.url){try{const url=new URL(s.url);if(['http:','https:'].includes(url.protocol)&&item.window&&!item.window.closed)item.window.location.replace(url.href)}catch{}pendingOpens.delete(id)}
  else if(s?.status==='crashed'||Date.now()>item.expires){closePendingWindow(item);pendingOpens.delete(id);notify(s?.status==='crashed'?'The server could not start. Check its logs for details.':'No web URL was reported yet. Check the logs, or use Open when it appears.')}
 }
}
async function showDelete(p){
 if(deleting)return;const review=++deleteReview;
 deleteTarget=null;$('deleteTitle').textContent='Delete '+displayName(p)+'?';$('deletePath').textContent=p.directory;$('deleteName').textContent=p.name;$('deleteConfirmation').value='';$('deleteError').textContent='';$('deleteImpact').textContent='Checking the folder and its managed services…';$('confirmDelete').disabled=true;$('deleteDialog').showModal();
 try{const preview=await api('/'+p.id+'/delete-preview');if(!$('deleteDialog').open||review!==deleteReview)return;deleteTarget={id:p.id,...preview};$('confirmDelete').disabled=$('deleteConfirmation').value!==preview.name;$('deletePath').textContent=preview.directory;$('deleteImpact').textContent=(preview.projects.length>1?preview.projects.length+' discovered apps are inside this folder. ':'')+(preview.services.length?preview.services.length+' managed service'+(preview.services.length===1?'':'s')+' will be stopped and removed.':'All files in this folder will be permanently removed.');$('deleteConfirmation').focus()}catch(e){if(review===deleteReview){$('deleteError').textContent=e.message;$('deleteImpact').textContent=''}}
}
async function act(p,script,action){if(!script||busy.has(p.id))return;if(action==='stop'&&pendingOpens.has(p.id)){closePendingWindow(pendingOpens.get(p.id));pendingOpens.delete(p.id)}epoch++;selections.set(p.id,script.name);busy.add(p.id);render();try{const result=await api('/'+p.id+'/'+action,'POST',{script:script.name});data=result;const s=result.projects.find(x=>x.id===p.id)?.scripts.find(x=>x.name===script.name);notify(s?.status==='crashed'?`${p.name} could not start. Open Logs for details.`:action==='start'?`${p.name} is starting.`:`${p.name} stopped.`)}catch(e){const item=pendingOpens.get(p.id);if(item){closePendingWindow(item);pendingOpens.delete(p.id)}notify(e.message)}finally{busy.delete(p.id);render();refresh()}}
async function refresh(){if(loading||mutation||busy.size)return;loading=true;const requestEpoch=epoch;try{const result=await api('');if(requestEpoch!==epoch)return;data=result;loaded=true;$('healthLabel').textContent='Connected to JustWatch';$('healthDot').style.background='';render()}catch(e){$('healthLabel').textContent='JustWatch is offline';$('healthDot').style.background='#c28a77';$('notice').textContent=e.message+' Retrying automatically.';$('notice').hidden=false}finally{loading=false}}
async function openLogs(p,script){if(!script)return;logTarget={id:p.id,script:script.name};$('logTitle').textContent=p.name;$('logSubtitle').textContent=`${p.packageManager} run ${script.name} · Output refreshes automatically`;$('logs').textContent='Loading…';$('logDialog').showModal();await refreshLogs(true)}
async function refreshLogs(first=false){if(!logTarget||!$('logDialog').open||logLoading)return;const target=logTarget;logLoading=true;try{const result=await api('/'+target.id+'/logs?script='+encodeURIComponent(target.script));if(logTarget!==target)return;const logs=$('logs'),atEnd=logs.scrollHeight-logs.scrollTop-logs.clientHeight<40;logs.textContent=result.output||'Waiting for output…';if(first||atEnd)logs.scrollTop=logs.scrollHeight}catch(e){if(logTarget===target)$('logs').textContent=e.message}finally{logLoading=false}}
$('folderForm').onsubmit=async e=>{e.preventDefault();if(mutation)return;epoch++;mutation=true;$('saveFolder').disabled=true;$('folderError').textContent='';try{data=await api('/folders','POST',{path:$('folderPath').value.trim()});$('folderPath').value='';loaded=true;render();$('folderDialog').close();notify('Watch folder added. Discovering your projects…')}catch(e){$('folderError').textContent=e.message}finally{mutation=false;$('saveFolder').disabled=false;renderSavedFolders();refresh()}};
$('addFolder').onclick=$('manageFolders').onclick=showFolders;$('closeFolders').onclick=()=>$('folderDialog').close();$('closeLogs').onclick=()=>$('logDialog').close();$('logDialog').onclose=()=>{logTarget=null};
$('allProjects').onclick=()=>setFilter('all');$('runningProjects').onclick=()=>setFilter('running');$('search').oninput=render;$('sort').onchange=render;
$('rescan').onclick=async()=>{$('rescan').disabled=true;try{await api('/scan','POST');data.scanning=true;render();refresh()}catch(e){notify(e.message);$('rescan').disabled=false}};
function layout(list){$('content').classList.toggle('list-view',list);$('gridView').classList.toggle('active',!list);$('listView').classList.toggle('active',list);$('gridView').setAttribute('aria-pressed',String(!list));$('listView').setAttribute('aria-pressed',String(list))}
$('gridView').onclick=()=>layout(false);$('listView').onclick=()=>layout(true);
document.addEventListener('keydown',e=>{if(e.key==='/'&&!['INPUT','TEXTAREA','SELECT'].includes(document.activeElement.tagName)&&!document.querySelector('dialog[open]')){e.preventDefault();$('search').focus()}});
render();refresh();setInterval(()=>{if(!document.hidden||pendingOpens.size){refresh();refreshLogs()}},2000);document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh()});

$('deleteConfirmation').oninput=()=>{$('confirmDelete').disabled=deleting||!deleteTarget||$('deleteConfirmation').value!==deleteTarget.name};
$('closeDelete').onclick=$('cancelDelete').onclick=()=>{if(!deleting)$('deleteDialog').close()};
$('deleteDialog').onclose=()=>{deleteTarget=null;deleteReview++};$('deleteDialog').oncancel=e=>{if(deleting)e.preventDefault()};
$('deleteForm').onsubmit=async e=>{
 e.preventDefault();if(!deleteTarget||deleting||$('deleteConfirmation').value!==deleteTarget.name)return;
 const target=deleteTarget;epoch++;deleting=true;mutation=true;busy.add(target.id);$('confirmDelete').disabled=$('cancelDelete').disabled=$('closeDelete').disabled=$('deleteConfirmation').disabled=true;$('deleteError').textContent='';$('deleteProgress').hidden=false;render();
 try{data=await api('/'+target.id,'DELETE',{token:target.token,confirmation:$('deleteConfirmation').value},300000);for(const [id,item]of pendingOpens){if(!data.projects.some(p=>p.id===id)){closePendingWindow(item);pendingOpens.delete(id)}}$('deleteDialog').close();notify(target.name+' was deleted from disk.')}catch(e){$('deleteError').textContent=e.message+' Close this dialog and review the project before trying again.';deleteTarget=null}finally{deleting=false;mutation=false;busy.delete(target.id);$('cancelDelete').disabled=$('closeDelete').disabled=$('deleteConfirmation').disabled=false;$('deleteProgress').hidden=true;render();refresh()}
};

document.addEventListener('click',e=>document.querySelectorAll('.launch-menu[open]').forEach(menu=>{if(!menu.contains(e.target))menu.open=false}));
document.addEventListener('keydown',e=>{if(e.key==='Escape')document.querySelectorAll('.launch-menu[open]').forEach(menu=>menu.open=false)});
