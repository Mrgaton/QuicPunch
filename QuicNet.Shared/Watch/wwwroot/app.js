const csrf=document.querySelector('meta[name="quicwatch-csrf"]').content;
const $=id=>document.getElementById(id);
const player=$('player');
let state=null, latestPlayback=null, applying=false, toastTimer=null, seekTimer=null;
let peerRenderKey='';
let roomActionPending=false;
let mediaActionPending=false, refreshPromise=null, playbackTimer=null;
let statusRevision=0, tokenActionPending=false;
let closingRoomId=null;
const selectedPeers=new Set();
async function api(path,body){
  const response=await fetch(`/watch${path}`,{method:'POST',headers:{'Content-Type':'application/json','X-QuicWatch-Key':csrf},body:JSON.stringify(body||{})});
  const data=await response.json().catch(error=>{window.QuicNetDiagnostics?.report(error,'Respuesta de Ver juntos');throw error;});
  if(!response.ok || (data.success===false&&!data.cancelled)) throw new Error(data.error||'No se pudo completar la acción.');
  return data;
}

function toast(message,error=false){
  if(error)window.QuicNetDiagnostics?.report(new Error(message),'Ver juntos');
  const el=$('toast');clearTimeout(toastTimer);
  el.setAttribute('role',error?'alert':'status');el.setAttribute('aria-live',error?'assertive':'polite');
  el.replaceChildren();const text=document.createElement('span');text.textContent=message;el.append(text);
  el.className='toast show'+(error?' error':'');
  if(error){
    const previous=document.activeElement,close=document.createElement('button');
    close.type='button';close.textContent='Cerrar';close.setAttribute('aria-label','Cerrar aviso');
    close.onclick=()=>{const focused=document.activeElement===close;el.className='toast';if(focused&&previous?.isConnected)previous.focus();};
    el.append(close);
  }else toastTimer=setTimeout(()=>el.className='toast',5000);
}

function fmtBytes(value){
  if(!value)return '0 B'; const units=['B','KB','MB','GB','TB']; const i=Math.min(units.length-1,Math.floor(Math.log(value)/Math.log(1024)));
  return `${(value/Math.pow(1024,i)).toFixed(i>1?1:0)} ${units[i]}`;
}

function render(next){
  statusRevision++;
  state=next; const room=next.room;
  if($('closeRoomDialog').open&&room?.roomId!==closingRoomId)$('closeRoomDialog').close();
  $('localName').textContent=next.local.name||'Este dispositivo';
  $('myToken').textContent=next.local.token||'El token estará disponible cuando la red esté lista.';
  $('liveDot').classList.add('live'); $('connectionText').textContent='Listo para compartir';
  $('retryConnection').hidden=true;
  $('roomTitle').textContent=room?room.name:'Todo empieza con un buen plan';
  $('roleBadge').textContent=room?(room.isOwner?'Anfitrión':room.myRole==='Controller'?'Puedes controlar':'Espectador'):'Sin sala';
  $('pickMedia').disabled=!room?.isOwner||mediaActionPending||next.canPickMedia===false;
  $('pickMedia').textContent=mediaActionPending?'Preparando vídeo…':room?.media?'Cambiar vídeo':'Elegir vídeo';
  $('pickMedia').setAttribute('aria-busy',String(mediaActionPending));
  $('pathInput').disabled=!room?.isOwner||mediaActionPending;
  $('pathForm').querySelector('button').disabled=!room?.isOwner||mediaActionPending;
  $('pathDetails').hidden=next.canSelectPath===false;
  $('roomName').disabled=!!room||roomActionPending;
  $('roomForm').hidden=!!room;
  $('setupTitle').textContent=room?'Tu sala está lista':'Prepara tu sala';
  $('mediaHelp').textContent=mediaActionPending?'Preparando una copia temporal del original. En Android, un vídeo grande puede tardar unos minutos.':!room?'Crea una sala para elegir un vídeo de tu dispositivo.':!room.isOwner?'El anfitrión elige el vídeo y decide quién puede controlar la reproducción.':next.canPickMedia===false?'Abre el vídeo por su ruta en este dispositivo.':'MP4 y WebM suelen ser compatibles. El vídeo se comparte sin recodificar.';
  $('peopleHelp').textContent=room?(room.isOwner?'Invita a tus contactos y elige quién controla el vídeo.':'Estas son las personas conectadas. El anfitrión gestiona los permisos.'):'Selecciona contactos para invitarlos al crear la sala.';
  $('emptyTitle').textContent=!room?'Hoy, la última fila es vuestra.':room.isOwner?'La sala está lista. Falta la película.':'El plan está a punto de empezar.';
  $('emptyDescription').textContent=!room?'Prepara una sala, invita a tu gente y comparte un vídeo en su calidad original.':room.isOwner?'Elige un vídeo de tu dispositivo. Después, invita a quien quieras y dale al play.':'El anfitrión está eligiendo el vídeo. Aparecerá aquí cuando esté preparado.';
  $('startWatching').hidden=!!room&&!room.isOwner;
  $('startWatching').disabled=roomActionPending||mediaActionPending;
  $('startWatching').textContent=mediaActionPending?'Preparando vídeo…':room?'Elegir un vídeo':'Preparar mi sala ↗';
  $('emptyHint').textContent=room?'Podéis seguir hablando durante la película.':'Tú eliges quién entra y quién controla el vídeo.';
  $('stepRoom').classList.toggle('complete',!!room);
  $('stepPeople').classList.toggle('complete',!!room?.participants?.some(p=>p.peerId!==next.local.peerId));
  $('stepMedia').classList.toggle('complete',!!room?.media);
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
    row.innerHTML=`<label class="peer-check-label"><input class="peer-check" type="checkbox" data-id="${peer.peerId}" ${checked} ${disabled}></label><div class="peer-name"><strong>${escapeHtml(peer.name||'Participante')}</strong><small>${peer.inRoom?'En la sala':peer.connected?'Conectado':'Sin conectar'}</small></div><div class="peer-actions">${action}</div>`;
    row.querySelector('.peer-check').setAttribute('aria-label', `Incluir a ${peer.name || 'participante'} en la sala`);
    row.querySelector('select')?.setAttribute('aria-label', `Permisos de ${peer.name || 'participante'}`);
    $('peerList').appendChild(row);
  }
  document.querySelectorAll('.connect').forEach(b=>b.onclick=()=>run(()=>api('/api/peer/connect',{peerId:b.dataset.id}),'Dispositivo conectado.'));
  document.querySelectorAll('.invite').forEach(b=>b.onclick=()=>run(()=>api('/api/room/invite',{peerId:b.dataset.id}),'Invitación enviada.'));
  document.querySelectorAll('.role-select').forEach(s=>s.onchange=async()=>{
    const wanted=s.value, confirmed=state.peers.find(p=>p.peerId===s.dataset.id)?.role||'Viewer';
    s.disabled=true;
    try{await api('/api/permission',{peerId:s.dataset.id,role:wanted});toast('Permiso actualizado.');await refresh(true);}
    catch(error){s.value=confirmed;toast(error.message,true);}
    finally{s.disabled=false;s.value=state.peers.find(p=>p.peerId===s.dataset.id)?.role||confirmed;}
  });
  document.querySelectorAll('.peer-check').forEach(input=>input.onchange=()=>{
    if(input.checked)selectedPeers.add(input.dataset.id);else selectedPeers.delete(input.dataset.id);
    peerRenderKey=JSON.stringify([room?.isOwner,!!room,peers.map(p=>[p.peerId,p.name,p.connected,p.inRoom,p.role]),[...selectedPeers]]);
  });
  }

  const media=room?.media;
  $('playerShell').classList.toggle('has-media',!!media);
  $('fullscreenBtn').disabled=!media;
  $('playbackStatus').textContent=media?(room.isOwner||room.myRole==='Controller'?'Puedes controlar la reproducción':'El anfitrión controla la reproducción'):'Todo a vuestro ritmo';
  if(media){
    $('emptyPlayer').classList.add('hidden'); $('mediaName').textContent=media.name; $('mediaMeta').textContent=`${fmtBytes(media.size)} · original sin recodificar`;
    const wanted=`${location.origin}/watch/media/${media.mediaId}`;
    if(player.src!==wanted){latestPlayback=null;clearTimeout(playbackTimer);$('resumePlayback').hidden=true;setCacheProgress(0);player.src=wanted; player.load();}
  }else{
    $('emptyPlayer').classList.remove('hidden'); $('mediaName').textContent='Tu próximo momento compartido'; $('mediaMeta').textContent=room?'Esperando un vídeo.':'Elige un vídeo después de crear la sala.';
    if(player.src){player.removeAttribute('src');player.load();}
    latestPlayback=null;
    clearTimeout(playbackTimer);$('resumePlayback').hidden=true;
    setCacheProgress(0);$('cacheText').textContent='Calidad original · Sin recodificar';
  }
  if(media&&room?.playback){
    const next=room.playback;
    if(latestPlayback?.roomId===next.roomId&&latestPlayback?.mediaId===next.mediaId&&latestPlayback.revision>next.revision)return;
    const changed=!latestPlayback||latestPlayback.roomId!==next.roomId||latestPlayback.mediaId!==next.mediaId||latestPlayback.revision!==next.revision;
    latestPlayback=next;
    if(changed)schedulePlayback(next);
  }
}

function escapeHtml(text){const div=document.createElement('div');div.textContent=text;return div.innerHTML;}
function refresh(afterAction=false){
  // An action needs a snapshot requested after it completed. Ordinary polls share
  // the current request; they must never replace a newer socket/action snapshot.
  if(afterAction===true)statusRevision++;
  if(refreshPromise)return afterAction===true?refreshPromise.then(()=>refresh()):refreshPromise;
  const revision=statusRevision;
  refreshPromise=(async()=>{
    try{
      const r=await fetch('/watch/api/status',{signal:AbortSignal.timeout(10000)});
      if(!r.ok)throw new Error('No se puede consultar la sala.');
      const next=await r.json();
      if(revision===statusRevision)render(next);
    }catch(error){
      window.QuicNetDiagnostics?.report(error,'Estado de Ver juntos');
      if(revision===statusRevision){$('connectionText').textContent='Reconectando…';$('liveDot').classList.remove('live');$('retryConnection').hidden=false;}
    }finally{refreshPromise=null;}
  })();
  return refreshPromise;
}
async function run(action,success){try{const result=await action();if(result?.cancelled)return false;if(result?.local)render(result);if(success)toast(success);await refresh(true);return true;}catch(e){window.QuicNetDiagnostics?.report(e,'Acción de Ver juntos');toast(e.message,true);applyPlayback(latestPlayback);return false;}}

function expectedTime(snapshot,now=Date.now()){
  if(!snapshot||snapshot.paused)return Math.max(0,snapshot?.positionSeconds||0);
  return Math.max(0,snapshot.positionSeconds+Math.max(0,now-snapshot.issuedAtUnixMs)/1000*snapshot.playbackRate);
}
function schedulePlayback(snapshot){
  clearTimeout(playbackTimer);
  const delay=Math.max(0,Math.min(3000,snapshot.applyAtUnixMs-Date.now()));
  playbackTimer=setTimeout(()=>{if(latestPlayback?.revision===snapshot.revision&&latestPlayback?.roomId===snapshot.roomId&&latestPlayback?.mediaId===snapshot.mediaId&&state?.room?.roomId===snapshot.roomId&&state?.room?.media?.mediaId===snapshot.mediaId)applyPlayback(snapshot);},delay);
}
async function applyPlayback(snapshot){
  if(!snapshot||snapshot.roomId!==state?.room?.roomId||snapshot.mediaId!==state?.room?.media?.mediaId)return;
  if(player.readyState<1)return; // loadedmetadata applies the latest revision.
  applying=true;
  try{
    const target=expectedTime(snapshot);
    if(Number.isFinite(player.duration)) player.currentTime=Math.min(target,Math.max(0,player.duration-.05)); else player.currentTime=target;
    player.playbackRate=snapshot.playbackRate||1;
    if(snapshot.paused){player.pause();$('resumePlayback').hidden=true;}
    else try{await player.play();$('resumePlayback').hidden=true;}catch(error){if(snapshot.mediaId===state?.room?.media?.mediaId)$('resumePlayback').hidden=false;window.QuicNetDiagnostics?.report(error,'Reproducir vídeo','warn');}
  }finally{setTimeout(()=>applying=false,120);}
}
async function sendPlayback(paused){
  if(applying||!state?.room?.media)return;
  if(!state.room.isOwner&&state.room.myRole!=='Controller'){applyPlayback(latestPlayback);return;}
  await run(()=>api('/api/playback',{paused,positionSeconds:player.currentTime||0,playbackRate:player.playbackRate||1}));
}

player.addEventListener('play',()=>sendPlayback(false));
player.addEventListener('pause',()=>sendPlayback(true));
player.addEventListener('seeking',()=>{if(applying)return;clearTimeout(seekTimer);seekTimer=setTimeout(()=>sendPlayback(player.paused),180);});
player.addEventListener('ratechange',()=>{if(!applying)sendPlayback(player.paused);});
player.addEventListener('loadedmetadata',()=>{if(latestPlayback)schedulePlayback(latestPlayback);});
player.addEventListener('waiting',()=>{if(state?.room?.media)$('playbackStatus').textContent='Cargando vídeo…';});
player.addEventListener('playing',()=>{$('playbackStatus').textContent='Reproducción sincronizada';});
$('resumePlayback').onclick=()=>applyPlayback(latestPlayback);
$('retryConnection').onclick=refresh;
$('startWatching').onclick=()=>{
  if(state?.room?.isOwner){if(state.canPickMedia===false){$('pathDetails').open=true;$('pathInput').focus();}else $('pickMedia').click();}
  else{$('roomName').scrollIntoView({block:'center'});$('roomName').focus();$('roomName').select();}
};

$('tokenForm').onsubmit=async e=>{
  e.preventDefault();
  const input=$('tokenInput'),form=$('tokenForm'),button=form.querySelector('button');
  const token=input.value.trim();
  if(!token||tokenActionPending)return;
  tokenActionPending=true;button.disabled=true;input.readOnly=true;
  form.setAttribute('aria-busy','true');button.textContent='Conectando…';
  try{if(await run(()=>api('/api/token/connect',{token}),'Dispositivo conectado.'))input.value='';}
  finally{tokenActionPending=false;button.disabled=false;input.readOnly=false;form.setAttribute('aria-busy','false');button.textContent='Conectar';}
};
$('roomForm').onsubmit=async e=>{
  e.preventDefault();
  if(roomActionPending||state?.room)return;
  if(!$('roomName').value.trim()){toast('Escribe un nombre para la sala.',true);$('roomName').focus();return;}
  const peerIds=[...selectedPeers];roomActionPending=true;$('createRoom').disabled=true;
  try{const ok=await run(()=>api('/api/room/create',{name:$('roomName').value.trim(),peerIds}),'Sala creada.');if(ok){selectedPeers.clear();$('roomTitle').focus();}}
  finally{roomActionPending=false;if(state)render(state);}
};
async function leaveRoom(){
  if(roomActionPending||!state?.room)return;
  const owner=state.room.isOwner;roomActionPending=true;$('leaveRoom').disabled=true;
  let left=false;
  try{left=await run(()=>api('/api/room/leave'),owner?'Sala cerrada.':'Has salido de la sala.');}
  finally{roomActionPending=false;if(state)render(state);if(left&&!state?.room){$('roomName').focus();$('roomName').select();}}
}
$('leaveRoom').onclick=()=>{
  if(roomActionPending||!state?.room)return;
  const others=state.room.participants?.some(p=>p.peerId!==state.local.peerId)||state.peers?.some(p=>p.inRoom);
  if(state.room.isOwner&&others){closingRoomId=state.room.roomId;$('closeRoomDialog').showModal();}
  else leaveRoom();
};
$('cancelCloseRoom').onclick=()=>$('closeRoomDialog').close();
$('confirmCloseRoom').onclick=()=>{const roomId=closingRoomId;$('closeRoomDialog').close();if(state?.room?.roomId===roomId)leaveRoom();};
async function selectMedia(action){
  if(mediaActionPending||!state?.room?.isOwner)return;
  mediaActionPending=true;render(state);
  try{await run(action,'Vídeo preparado.');}finally{mediaActionPending=false;if(state)render(state);}
}
$('pickMedia').onclick=()=>selectMedia(()=>api('/api/media/pick',{}));
$('pathForm').onsubmit=e=>{e.preventDefault();selectMedia(()=>api('/api/media/select-path',{path:$('pathInput').value}));};
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
document.addEventListener('visibilitychange',()=>{if(!document.hidden){refresh();if(latestPlayback)schedulePlayback(latestPlayback);}});
function setCacheProgress(value){const p=Number.isFinite(value)?Math.max(0,Math.min(100,value)):0;$('cacheBar').style.width=`${p}%`;$('cacheProgress').setAttribute('aria-valuenow',String(Math.round(p)));}

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
  ws.onmessage=e=>{
    try{
      const message=JSON.parse(e.data);
      if(message.type==='status')render(message.data);
      else if(message.type==='playback'){
        const next=message.data;
        if(next.roomId!==state?.room?.roomId||next.mediaId!==state?.room?.media?.mediaId){refresh(true);return;}
        if(latestPlayback&&next.revision<latestPlayback.revision)return;
        latestPlayback=next;schedulePlayback(next);
      }else if(message.type==='media')refresh(true);
      else if(message.type==='cache'&&message.data.mediaId===state?.room?.media?.mediaId){const p=message.data.total>0?Math.min(100,message.data.cached/message.data.total*100):0;setCacheProgress(p);$('cacheText').textContent=`${p.toFixed(0)}% en la caché local`;}
      else if(message.type==='error')toast(message.data.message,true);
    }catch(error){window.QuicNetDiagnostics?.report(error,'Actualización de la sala');}
  };
  ws.onclose=event=>{window.QuicNetDiagnostics?.setTransport(null);if(event.code!==1000)window.QuicNetDiagnostics?.report(new Error('Conexión cerrada: '+event.code),'WebSocket de Ver juntos','warn');setTimeout(connectSocket,1400);};
}
refresh();connectSocket();
const initialPeerId=new URLSearchParams(location.search).get('peer');
if(initialPeerId)run(()=>api('/api/peer/connect',{peerId:initialPeerId}));
