const csrf=document.querySelector('meta[name="quicwatch-csrf"]').content;
const $=id=>document.getElementById(id);
const player=$('player');
let state=null, latestPlayback=null, applying=false, toastTimer=null, seekTimer=null;
let peerRenderKey='';
let roomActionPending=false;
const selectedPeers=new Set();
async function api(path,body){
  const response=await fetch(`/watch${path}`,{method:'POST',headers:{'Content-Type':'application/json','X-QuicWatch-Key':csrf},body:JSON.stringify(body||{})});
  const data=await response.json().catch(error=>{window.QuicNetDiagnostics?.report(error,'Respuesta de Ver juntos');throw error;});
  if(!response.ok || data.success===false) throw new Error(data.error||'No se pudo completar la acción.');
  return data;
}

function toast(message,error=false){
  if(error)window.QuicNetDiagnostics?.report(new Error(message),'Ver juntos');
  const el=$('toast'); el.textContent=message; el.className='toast show'+(error?' error':'');
  clearTimeout(toastTimer); toastTimer=setTimeout(()=>el.className='toast',3200);
}

function fmtBytes(value){
  if(!value)return '0 B'; const units=['B','KB','MB','GB','TB']; const i=Math.min(units.length-1,Math.floor(Math.log(value)/Math.log(1024)));
  return `${(value/Math.pow(1024,i)).toFixed(i>1?1:0)} ${units[i]}`;
}

function render(next){
  state=next; const room=next.room;
  $('localName').textContent=next.local.name||'Este dispositivo';
  $('myToken').textContent=next.local.token||'El token estará disponible cuando la red esté lista.';
  $('liveDot').classList.add('live'); $('connectionText').textContent='QuicWatch listo';
  $('roomTitle').textContent=room?room.name:'Todavía no hay una sala';
  $('roleBadge').textContent=room?(room.isOwner?'Propietario':room.myRole==='Controller'?'Puede controlar':'Espectador'):'Sin sala';
  $('pickMedia').disabled=!room?.isOwner;
  $('pathInput').disabled=!room?.isOwner;
  $('pathForm').querySelector('button').disabled=!room?.isOwner;
  $('copyToken').disabled=!next.local.token;
  $('createRoom').disabled=!!room||roomActionPending;
  $('leaveRoom').hidden=!room;
  $('leaveRoom').disabled=roomActionPending;
  $('leaveRoom').textContent=room?.isOwner?'Cerrar sala':'Salir de la sala';

  const peers=next.peers||[]; $('peerCount').textContent=String(peers.filter(p=>p.connected).length);
  const nextPeerKey=JSON.stringify([room?.isOwner,!!room,peers.map(p=>[p.peerId,p.name,p.connected,p.inRoom,p.role]),[...selectedPeers]]);
  if(nextPeerKey!==peerRenderKey){
  peerRenderKey=nextPeerKey;
  $('peerList').innerHTML=peers.length?'':'<p class="muted">Aún no hay nadie conectado.</p>';
  for(const peer of peers){
    const row=document.createElement('div'); row.className='peer';
    const checked=(room?peer.inRoom:selectedPeers.has(peer.peerId))?'checked':''; const disabled=room?'disabled':'';
    let action=!peer.connected?`<button class="secondary connect" data-id="${peer.peerId}">Conectar</button>`:room?.isOwner&&!peer.inRoom?`<button class="secondary invite" data-id="${peer.peerId}">Invitar</button>`:'';
    if(room?.isOwner&&peer.inRoom) action=`<select class="role-select" data-id="${peer.peerId}"><option value="Viewer" ${peer.role==='Viewer'?'selected':''}>Espectador</option><option value="Controller" ${peer.role==='Controller'?'selected':''}>Control</option></select>`;
    row.innerHTML=`<input class="peer-check" type="checkbox" data-id="${peer.peerId}" ${checked} ${disabled}><div class="peer-name"><strong>${escapeHtml(peer.name||'Participante')}</strong><small>${peer.connected?'Conectado':'Disponible'}</small></div><div class="peer-actions">${action}</div>`;
    row.querySelector('.peer-check').setAttribute('aria-label', `Incluir a ${peer.name || 'participante'} en la sala`);
    row.querySelector('select')?.setAttribute('aria-label', `Permisos de ${peer.name || 'participante'}`);
    $('peerList').appendChild(row);
  }
  document.querySelectorAll('.connect').forEach(b=>b.onclick=()=>run(()=>api('/api/peer/connect',{peerId:b.dataset.id}),'Dispositivo conectado.'));
  document.querySelectorAll('.invite').forEach(b=>b.onclick=()=>run(()=>api('/api/room/invite',{peerId:b.dataset.id}),'Invitación enviada.'));
  document.querySelectorAll('.role-select').forEach(s=>s.onchange=()=>run(()=>api('/api/permission',{peerId:s.dataset.id,role:s.value}),'Permiso actualizado.'));
  document.querySelectorAll('.peer-check').forEach(input=>input.onchange=()=>{
    if(input.checked)selectedPeers.add(input.dataset.id);else selectedPeers.delete(input.dataset.id);
    peerRenderKey=JSON.stringify([room?.isOwner,!!room,peers.map(p=>[p.peerId,p.name,p.connected,p.inRoom,p.role]),[...selectedPeers]]);
  });
  }

  const media=room?.media;
  if(media){
    $('emptyPlayer').classList.add('hidden'); $('mediaName').textContent=media.name; $('mediaMeta').textContent=`${fmtBytes(media.size)} · original sin recodificar`;
    const wanted=`${location.origin}/watch/media/${media.mediaId}`;
    if(player.src!==wanted){player.src=wanted; player.load();}
  }else{
    $('emptyPlayer').classList.remove('hidden'); $('mediaName').textContent='Sin contenido'; $('mediaMeta').textContent='Esperando un vídeo';
    if(player.src){player.removeAttribute('src');player.load();}
    latestPlayback=null;
    $('cacheBar').style.width='0%';$('cacheText').textContent='Directo desde el origen';
  }
  if(media&&room?.playback){
    const next=room.playback;
    const changed=!latestPlayback||latestPlayback.roomId!==next.roomId||latestPlayback.mediaId!==next.mediaId||latestPlayback.revision!==next.revision;
    latestPlayback=next;
    if(changed)schedulePlayback(next);
  }
}

function escapeHtml(text){const div=document.createElement('div');div.textContent=text;return div.innerHTML;}
async function refresh(){try{const r=await fetch('/watch/api/status');render(await r.json());}catch(error){window.QuicNetDiagnostics?.report(error,'Estado de Ver juntos');$('connectionText').textContent='Reconectando…';$('liveDot').classList.remove('live');}}
async function run(action,success){try{const result=await action();if(result?.cancelled)return false;if(success)toast(success);await refresh();return true;}catch(e){window.QuicNetDiagnostics?.report(e,'Acción de Ver juntos');toast(e.message,true);applyPlayback(latestPlayback);return false;}}

function expectedTime(snapshot,now=Date.now()){
  if(!snapshot||snapshot.paused)return Math.max(0,snapshot?.positionSeconds||0);
  return Math.max(0,snapshot.positionSeconds+Math.max(0,now-snapshot.issuedAtUnixMs)/1000*snapshot.playbackRate);
}
function schedulePlayback(snapshot){
  const delay=Math.max(0,Math.min(3000,snapshot.applyAtUnixMs-Date.now()));
  setTimeout(()=>{if(latestPlayback?.revision===snapshot.revision&&latestPlayback?.roomId===snapshot.roomId&&latestPlayback?.mediaId===snapshot.mediaId&&state?.room?.roomId===snapshot.roomId&&state?.room?.media?.mediaId===snapshot.mediaId)applyPlayback(snapshot);},delay);
}
async function applyPlayback(snapshot){
  if(!snapshot||!state?.room?.media)return;
  applying=true;
  try{
    const target=expectedTime(snapshot);
    if(Number.isFinite(player.duration)) player.currentTime=Math.min(target,Math.max(0,player.duration-.05)); else player.currentTime=target;
    player.playbackRate=snapshot.playbackRate||1;
    if(snapshot.paused)player.pause();else await player.play().catch(error=>window.QuicNetDiagnostics?.report(error,'Reproducir vídeo','warn'));
  }finally{setTimeout(()=>applying=false,120);}
}
async function sendPlayback(paused){
  if(applying||!state?.room?.media)return;
  await run(()=>api('/api/playback',{paused,positionSeconds:player.currentTime||0,playbackRate:player.playbackRate||1}));
}

player.addEventListener('play',()=>sendPlayback(false));
player.addEventListener('pause',()=>sendPlayback(true));
player.addEventListener('seeking',()=>{if(applying)return;clearTimeout(seekTimer);seekTimer=setTimeout(()=>sendPlayback(player.paused),180);});
player.addEventListener('ratechange',()=>{if(!applying)sendPlayback(player.paused);});

$('tokenForm').onsubmit=e=>{e.preventDefault();const token=$('tokenInput').value.trim();if(token)run(()=>api('/api/token/connect',{token}),'Dispositivo conectado.').then(ok=>{if(ok)$('tokenInput').value='';});};
$('createRoom').onclick=async()=>{
  if(roomActionPending||state?.room)return;
  if(!$('roomName').value.trim()){$('roomName').reportValidity();return;}
  const peerIds=[...selectedPeers];roomActionPending=true;$('createRoom').disabled=true;
  try{const ok=await run(()=>api('/api/room/create',{name:$('roomName').value,peerIds}),'Sala creada.');if(ok)selectedPeers.clear();}
  finally{roomActionPending=false;if(state)render(state);}
};
$('leaveRoom').onclick=async()=>{
  if(roomActionPending||!state?.room)return;
  const owner=state.room.isOwner;roomActionPending=true;$('leaveRoom').disabled=true;
  try{await run(()=>api('/api/room/leave'),owner?'Sala cerrada.':'Has salido de la sala.');}
  finally{roomActionPending=false;if(state)render(state);}
};
$('pickMedia').onclick=()=>run(()=>api('/api/media/pick',{}),'Vídeo preparado.');
$('pathForm').onsubmit=e=>{e.preventDefault();run(()=>api('/api/media/select-path',{path:$('pathInput').value}),'Vídeo preparado.');};
$('copyToken').onclick=async()=>{if(!state?.local?.token)return;try{await navigator.clipboard.writeText(state.local.token);toast('Invitación copiada.');}catch{toast('No se pudo copiar. Abre el código y cópialo manualmente.',true);$('myToken').parentElement.open=true;}};
player.addEventListener('error',()=>{if(player.getAttribute('src'))toast('No se puede reproducir este vídeo. Comprueba la conexión y que el formato sea compatible.',true);});
$('fullscreenBtn').onclick=async()=>{
  try{
    if(document.fullscreenElement) await document.exitFullscreen();
    else await $('playerShell').requestFullscreen();
  }catch{toast('No se pudo activar la pantalla completa.',true);}
};
document.addEventListener('fullscreenchange',()=>{
  $('fullscreenBtn').textContent=document.fullscreenElement?'Salir de pantalla completa':'⛶ Pantalla completa';
});

setInterval(()=>{
  if(!latestPlayback||latestPlayback.paused||player.paused||applying)return;
  const drift=expectedTime(latestPlayback)-player.currentTime;
  if(Math.abs(drift)>.8){applying=true;player.currentTime=expectedTime(latestPlayback);setTimeout(()=>applying=false,100);}
  else{
    const corrected=Math.max(.25,Math.min(4,(latestPlayback.playbackRate||1)+(drift>.15?.03:drift<-.15?-.03:0)));
    if(player.playbackRate!==corrected){applying=true;player.playbackRate=corrected;setTimeout(()=>applying=false,100);}
  }
},2000);
setInterval(refresh,4000);

function connectSocket(){
  const ws=new WebSocket(`${location.protocol==='https:'?'wss':'ws'}://${location.host}/watch/ws?csrf=${encodeURIComponent(csrf)}`);
  ws.onopen=()=>window.QuicNetDiagnostics?.setTransport(entry=>{if(ws.readyState!==WebSocket.OPEN||ws.bufferedAmount>256*1024)return false;ws.send(JSON.stringify({type:'client_log',data:entry}));return true;});
  ws.onerror=()=>window.QuicNetDiagnostics?.report(new Error('Error de conexión.'),'WebSocket de Ver juntos');
  ws.onmessage=e=>{const message=JSON.parse(e.data);if(message.type==='status')render(message.data);else if(message.type==='playback'){latestPlayback=message.data;schedulePlayback(message.data);}else if(message.type==='media')refresh();else if(message.type==='cache'){const p=Math.min(100,message.data.cached/message.data.total*100);$('cacheBar').style.width=`${p}%`;$('cacheText').textContent=`${p.toFixed(0)}% disponible localmente`;}else if(message.type==='error')toast(message.data.message,true);};
  ws.onclose=event=>{window.QuicNetDiagnostics?.setTransport(null);if(event.code!==1000)window.QuicNetDiagnostics?.report(new Error('Conexión cerrada: '+event.code),'WebSocket de Ver juntos','warn');setTimeout(connectSocket,1400);};
}
refresh();connectSocket();
const initialPeerId=new URLSearchParams(location.search).get('peer');
if(initialPeerId)run(()=>api('/api/peer/connect',{peerId:initialPeerId}));
