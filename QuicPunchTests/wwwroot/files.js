let driveStatus=null, networkStatus=null, driveBusy=false;
document.addEventListener('DOMContentLoaded',()=>{
  document.getElementById('openOutboxBtn').addEventListener('click',openOutbox);
  document.getElementById('openCacheBtn').addEventListener('click',openCache);
  document.getElementById('mountAllBtn').addEventListener('click',mountAll);
  const input = document.getElementById('driveFileInput') || document.getElementById('fileInput');
  const drop = document.getElementById('fileDrop');
  drop.addEventListener('click',()=>input.click());
  input.addEventListener('change',()=>publishFiles([...input.files]));
  drop.addEventListener('dragover',e=>{e.preventDefault();drop.classList.add('drag');});
  drop.addEventListener('dragleave',()=>drop.classList.remove('drag'));
  drop.addEventListener('drop',e=>{e.preventDefault();drop.classList.remove('drag');publishFiles([...e.dataTransfer.files]);});
  refreshDrive();
  setInterval(refreshDrive,1500);
});
async function refreshDrive(){
  if(driveBusy)return;
  driveBusy=true;
  try{
    [driveStatus,networkStatus]=await Promise.all([QP.api('/api/files/status'),QP.api('/api/status')]);
    renderDrive();
  }catch(e){}finally{driveBusy=false;}
}
function renderDrive(){
  const mountedCount = (driveStatus?.sessions?.length || 0);
  QP.setText('driveBadge',`${mountedCount} mounted`);
  QP.setText('outboxPath',driveStatus?.outboxPath||'Temporary outbox');
  QP.setText('cachePath',driveStatus?.cachePath||'Temporary cache');
  renderPublished();
  renderRemote();
  renderCached();
  renderPeers();
}
function rowBase(name,meta){
  const row=QP.el('div','drive-row');
  const icon=QP.el('div','file-icon','◇');
  const copy=QP.el('div','peer-copy');
  copy.append(QP.el('div','peer-name',name || 'Unnamed file'),QP.el('div','peer-meta',meta));
  const actions=QP.el('div','row-actions');
  row.append(icon,copy,actions);
  return{row,actions};
}
function renderPublished(){
  const host=document.getElementById('publishedFiles');
  QP.clear(host);
  const files=driveStatus?.published||[];
  if(!files.length){
    host.appendChild(QP.el('div','empty','Your Outbox is empty.'));
    return;
  }
  files.forEach(f=>{
    const name = f.name || f.Name || 'Unnamed file';
    const size = f.size ?? f.Size ?? 0;
    const fileId = f.id || f.Id;
    const {row,actions}=rowBase(name,`${QP.fmtBytes(size)} · offered virtually`);
    actions.append(QP.button('Remove','ghost',()=>removePublished(fileId)));
    host.appendChild(row);
  });
}
function renderRemote(){
  const host=document.getElementById('remoteFiles');
  QP.clear(host);
  const files=driveStatus?.remote||[];
  const transfers=new Map((driveStatus?.transfers||[]).map(t=>[`${t.peerId || t.PeerId}:${t.fileId || t.FileId}`,t]));
  const mountedCount = (driveStatus?.sessions?.length || 0);
  if(!files.length){
    if(mountedCount > 0){
      host.appendChild(QP.el('div','empty','No remote files offered by mounted peers.'));
    }else{
      host.appendChild(QP.el('div','empty','Mount a trusted peer to see its virtual shelf.'));
    }
    return;
  }
  files.forEach(f=>{
    const name = f.name || f.Name || 'Unnamed file';
    const peerName = f.peerName || f.PeerName || 'Peer';
    const size = f.size ?? f.Size ?? 0;
    const fileId = f.id || f.Id;
    const peerId = f.peerId || f.PeerId;
    const transfer=transfers.get(`${peerId}:${fileId}`);
    const received = transfer ? (transfer.received ?? transfer.Received ?? 0) : 0;
    const expected = transfer ? (transfer.expected ?? transfer.Expected ?? transfer.expectedSize ?? size) : size;
    const pct=transfer && expected > 0 ? Math.min(100,Math.round(received*100/expected)):0;
    const state=f.cached?'materialized':transfer?`fetching ${pct}%`:'virtual';
    const {row,actions}=rowBase(name,`${peerName} · ${QP.fmtBytes(size)} · ${state}`);
    if(f.cached)actions.append(QP.button('Download','green',()=>downloadFile(peerId,fileId)));
    else{
      const btn=QP.button(transfer?`${pct}%`:'Materialize','accent',()=>materialize(peerId,fileId));
      btn.disabled=!!transfer;
      actions.append(btn);
    }
    host.appendChild(row);
  });
}
function renderCached(){
  const host=document.getElementById('cachedFiles');
  QP.clear(host);
  const files=driveStatus?.cached||[];
  if(!files.length){
    host.appendChild(QP.el('div','empty','Nothing has been materialized yet.'));
    return;
  }
  files.forEach(f=>{
    const name = f.name || f.Name || 'Unnamed file';
    const peerName = f.peerName || f.PeerName || 'Peer';
    const size = f.size ?? f.Size ?? 0;
    const fileId = f.id || f.Id;
    const peerId = f.peerId || f.PeerId;
    const {row,actions}=rowBase(name,`${peerName} · ${QP.fmtBytes(size)}`);
    actions.append(QP.button('Download','green',()=>downloadFile(peerId,fileId)));
    host.appendChild(row);
  });
}
function renderPeers(){
  const host=document.getElementById('drivePeers');
  QP.clear(host);
  const mounted=new Set((driveStatus?.sessions||[]).map(s=>s.peerId || s.PeerId));
  const peers=QP.trusted(networkStatus||{});
  if(!peers.length){
    host.appendChild(QP.el('div','empty','No trusted peers available.'));
    return;
  }
  peers.forEach(peer=>{
    const row=QP.el('div','peer-row');
    row.appendChild(QP.el('div','avatar',QP.initials(peer.name)));
    const copy=QP.el('div','peer-copy');
    copy.append(QP.el('div','peer-name',peer.name),QP.el('div','peer-meta',QP.peerMeta(peer)));
    const actions=QP.el('div','row-actions');
    const isMounted=mounted.has(peer.id);
    const btn=QP.button(isMounted?'Mounted':'Mount',isMounted?'green':'accent',()=>mountPeer(peer.id));
    btn.disabled=isMounted;
    actions.append(btn);
    row.append(copy,actions);
    host.appendChild(row);
  });
}
async function publishFiles(files){
  if(!files.length)return;
  const state=document.getElementById('uploadState');
  for(const file of files){
    if(file.size>2*1024*1024*1024){QP.toast(`${file.name} is larger than 2 GB`);continue;}
    state.textContent=`Copying ${file.name} to the temporary Outbox…`;
    try{
      await QP.api('/api/files/publish',{
        method:'POST',
        headers:{'Content-Type':'application/octet-stream','X-File-Name-B64':utf8Base64(file.name)},
        body:file
      });
      QP.toast(`${file.name} is now offered`);
    }catch(e){
      QP.toast(e.message);
    }
  }
  state.textContent='';
  document.getElementById('fileInput').value='';
  refreshDrive();
}
function utf8Base64(value){
  const bytes=new TextEncoder().encode(value);
  let bin='';
  for(const byte of bytes)bin+=String.fromCharCode(byte);
  return btoa(bin);
}
async function removePublished(fileId){
  try{
    await QP.api('/api/files/remove',{
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body:JSON.stringify({fileId})
    });
  }catch(e){
    QP.toast(e.message);
  }
  refreshDrive();
}
async function mountPeer(peerId){
  try{
    await QP.api('/api/connect-peer',{
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body:JSON.stringify({peerId,protocol:'files'})
    });
    QP.toast('Mounting virtual shelf…');
  }catch(e){
    QP.toast(e.message);
  }
  setTimeout(refreshDrive,700);
}
async function mountAll(){
  for(const peer of QP.trusted(networkStatus||{})){
    if(!(driveStatus?.sessions||[]).some(s=>(s.peerId || s.PeerId)===peer.id))
      await mountPeer(peer.id);
  }
  refreshDrive();
}
async function materialize(peerId,fileId){
  try{
    QP.toast('Fetching file from peer…');
    await QP.api('/api/files/materialize',{
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body:JSON.stringify({peerId,fileId})
    });
    QP.toast('File verified and materialized');
    downloadFile(peerId,fileId);
  }catch(e){
    QP.toast(e.message);
  }
  refreshDrive();
}
function downloadFile(peerId,fileId){
  location.href=`/api/files/download?peerId=${encodeURIComponent(peerId)}&fileId=${encodeURIComponent(fileId)}`;
}
async function openOutbox(){
  try{
    await QP.api('/api/files/open-outbox',{
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body:'{}'
    });
  }catch(e){
    QP.toast(e.message);
  }
}
async function openCache(){
  try{
    await QP.api('/api/files/open-cache',{
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body:'{}'
    });
  }catch(e){
    QP.toast(e.message);
  }
}
