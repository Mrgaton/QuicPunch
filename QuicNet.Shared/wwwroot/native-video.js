// Control plane only. Compressed media and decoded pixels never cross this bridge.
globalThis.QuicNetNativeVideo = (() => {
  const streams = new Map(), geometry = new Map(), updates = new Map(), sending = new Set();
  let snapshot = {local:{available:false,decoders:[]},peers:[],streams:[]}, scheduled=false, pendingRefresh=null;
  function schedule() { if(scheduled)return;scheduled=true;requestAnimationFrame(()=>{scheduled=false;layout();}); }
  async function sendLayout(peer){
    if(sending.has(peer))return;sending.add(peer);
    try{
      while(updates.has(peer)){
        const value=updates.get(peer);updates.delete(peer);
        try{await QP.api('/api/voice/native-video/layout',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(value)});}
        catch(error){geometry.delete(peer);const entry=streams.get(peer);if(entry){entry.loadingEl.textContent='Visor nativo: '+error.message;entry.loadingEl.classList.remove('hidden');}break;}
      }
    }finally{sending.delete(peer);}
  }
  function queueLayout(value){updates.set(value.peerId,value);void sendLayout(value.peerId);}
  function fitRect(box, width, height) {
    if (!(width > 0 && height > 0)) return box;
    const scale = Math.min(box.width / width, box.height / height);
    const w = width * scale, h = height * scale;
    return new DOMRect(box.x + (box.width - w) / 2, box.y + (box.height - h) / 2, w, h);
  }
  function fitEntry(box, entry) {
    const monitor = entry.remoteInput?.monitor;
    return fitRect(box, entry.width * (monitor?.width ?? 1), entry.height * (monitor?.height ?? 1));
  }
  function frameGeometry(entry, box) {
    const fitted = fitEntry(box, entry);
    const clip = entry.viewer?.frameRect(box, fitted) || fitted;
    const monitor = entry.remoteInput?.monitor;
    if (!monitor) return { rect: clip, clip };
    const width = clip.width / monitor.width, height = clip.height / monitor.height;
    return { rect: new DOMRect(clip.x - width * monitor.x, clip.y - height * monitor.y, width, height), clip };
  }
  function layout() {
    const covered=!!document.querySelector('.modal-backdrop:not(.hidden),dialog[open],.voice-person-menu[open]');
    const fullscreen = document.fullscreenElement || document.webkitFullscreenElement || document.querySelector('.is-tile-fullscreen');
    for(const [peer,entry] of streams){
      const box=entry.nativeTarget.getBoundingClientRect();
      const { rect, clip } = frameGeometry(entry, box);
      let left = Math.max(0, box.left, clip.left), top = Math.max(0, box.top, clip.top);
      let right = Math.min(innerWidth, box.right, clip.right), bottom = Math.min(innerHeight, box.bottom, clip.bottom);
      for (let parent = entry.nativeTarget.parentElement; parent; parent = parent.parentElement) {
        const style = getComputedStyle(parent), bounds = parent.getBoundingClientRect();
        if (/hidden|auto|scroll|clip/.test(style.overflowX)) { left = Math.max(left, bounds.left); right = Math.min(right, bounds.right); }
        if (/hidden|auto|scroll|clip/.test(style.overflowY)) { top = Math.max(top, bounds.top); bottom = Math.min(bottom, bounds.bottom); }
        // The fixed tile (or browser top layer) escapes the call grid's scroll clip.
        if (parent === fullscreen) break;
      }
      if (entry.cardEl === fullscreen && entry.headerEl && getComputedStyle(entry.headerEl).visibility === 'visible')
        top = Math.max(top, entry.headerEl.getBoundingClientRect().bottom);
      const volume = entry.headerEl?.querySelector('.screen-tile-vol-group[open] .screen-tile-volume-panel');
      if (volume && getComputedStyle(volume).visibility === 'visible')
        top = Math.max(top, volume.getBoundingClientRect().bottom);
      const visible = !document.hidden && !covered && (!fullscreen || fullscreen.contains(entry.nativeTarget)) &&
        entry.nativeTarget.getClientRects().length > 0 && right - left > 1 && bottom - top > 1;
      // Round edges in viewport coordinates, then subtract their common origin.
      const x = Math.round(rect.left), y = Math.round(rect.top);
      const value = {
        peerId: peer, x, y, width: Math.round(rect.right) - x, height: Math.round(rect.bottom) - y,
        visible, scale: devicePixelRatio || 1, zoom: entry.viewer?.zoom ?? 1,
        clipX: Math.max(0, Math.round(left) - x), clipY: Math.max(0, Math.round(top) - y),
        clipWidth: Math.max(0, Math.round(right) - Math.round(left)),
        clipHeight: Math.max(0, Math.round(bottom) - Math.round(top))
      };
      const signature=JSON.stringify(value);if(geometry.get(peer)===signature)continue;
      geometry.set(peer,signature);
      queueLayout(value);
    }
  }
  function state(data){
    const entry=streams.get(data?.peerId);if(!entry)return;
    if(data.width>0&&data.height>0&&(entry.width!==data.width||entry.height!==data.height)){entry.width=data.width;entry.height=data.height;schedule();}
    entry.loadingEl.textContent=data.error||'Esperando el primer fotograma nativo…';
    entry.loadingEl.classList.toggle('hidden',!data.error&&data.presented>0);
    clearTimeout(entry.trafficTimer);
    entry.nativeStats = data;
    const renderStats = (fps, bitrate) => {
      const codec = ({1:'H.264',3:'VP9',4:'AV1',5:'H.265'})[data.codecId || entry.codecId] || 'JPEG';
      const rate = bitrate > 0
        ? (bitrate >= 1e6 ? (bitrate / 1e6).toLocaleString('es', { maximumFractionDigits: 2 }) + ' Mb/s' : Math.round(bitrate / 1000) + ' kb/s')
        : '— Mb/s';
      entry.codecBadge.textContent = [entry.isLocal ? 'Envío' : 'Recepción', codec,
        data.width > 0 && data.height > 0 ? data.width + '×' + data.height : null,
        fps > 0 ? Math.round(fps) + ' FPS' : '— FPS', rate].filter(Boolean).join(' · ');
      entry.codecBadge.title = entry.codecBadge.textContent;
    };
    renderStats(data.timing?.presentedFps ?? data.fps, data.bitrate);
    if (data.timing && !data.error) {
      const t = data.timing;
      entry.codecBadge.title = `${entry.codecBadge.textContent}\n${Math.round(data.fps)} FPS recibidos · ${Math.round(t.decodedFps)} FPS decodificados · ${Math.round(t.presentedFps)} FPS presentados\nCola de recepción p95: ${t.receiveQueue.p95Ms.toFixed(1)} ms · Decodificación p95: ${t.decode.p95Ms.toFixed(1)} ms`;
    }
    if (data.error) {
      entry.codecBadge.textContent = data.error;
      entry.codecBadge.title = data.error;
      entry.cardEl.classList.add("show-stream-info");
      schedule();
    } else entry.trafficTimer = setTimeout(() => renderStats(0, 0), 3000);
    entry.decoderStatus=data.mode;
  }
  function status(item){if(!item||item.status==='unknown')return ['unknown','Pendiente','·'];if(item.hardware===true)return ['hardware','Hardware','★'];if(item.software===true)return ['software','Software','✓'];return ['unsupported','No compatible','×'];}
  function renderMatrix(){
    const body=document.getElementById('nativeDecoderMatrix');if(!body)return;body.replaceChildren();
    const rows=[{name:'Este dispositivo',capabilities:snapshot.local},...snapshot.peers.map(peer=>({name:peer.name||document.querySelector(`[data-stream-peer-id="${peer.peerId}"] .screen-tile-name`)?.textContent||peer.peerId.slice(0,8),capabilities:peer.capabilities}))];
    for(const row of rows){const tr=document.createElement('tr');const label=document.createElement('th');label.scope='row';label.dataset.label='Participante';label.textContent=row.name;tr.append(label);
      for(const id of [1,5,3,4]){const item=row.capabilities?.decoders?.find(c=>c.codecId===id);const cell=document.createElement('td');cell.dataset.label=({1:'H.264',5:'HEVC',3:'VP9',4:'AV1'})[id];const [kind,label,icon]=status(item);const badge=document.createElement('span');badge.className='native-capability';badge.dataset.status=kind;badge.setAttribute('aria-label',label);badge.append(document.createTextNode(label));const mark=document.createElement('span');mark.className='native-capability-icon';mark.textContent=icon;mark.setAttribute('aria-hidden','true');badge.append(mark);cell.append(badge);cell.title=item?`GPU: ${item.hardware===true?'sí':item.hardware===false?'no':'pendiente'} · CPU: ${item.software===true?'sí':item.software===false?'no':'pendiente'}. ${item.reason} Prueba: ${item.testWidth}×${item.testHeight}. ${item.component||''}`:'Este participante todavía no ha enviado una prueba nativa.';tr.append(cell);}body.append(tr);}
  }
  async function refresh(force=false){
    if(pendingRefresh)return pendingRefresh;
    pendingRefresh=QP.api('/api/voice/native-video'+(force?'?refresh=1':'')).then(data=>{snapshot=data;for(const current of data.streams||[])state(current);renderMatrix();return data;}).finally(()=>pendingRefresh=null);
    return pendingRefresh;
  }
  async function applyPlayback(){
    const value={bufferMs:Number(document.getElementById('nativePlaybackBuffer')?.value||0),maxQueueFrames:Number(document.getElementById('nativePlaybackFrames')?.value||4),decoder:document.getElementById('screenDecoderAccelerationSelect')?.value||'auto'};
    await QP.api('/api/voice/native-video/playback',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(value)});
    try{localStorage.setItem('qp_native_playback',JSON.stringify(value));}catch{}
  }
  function initPlayback(){
    let saved;try{saved=JSON.parse(localStorage.getItem('qp_native_playback'));}catch{}
    for(const [id,key] of [['nativePlaybackBuffer','bufferMs'],['nativePlaybackFrames','maxQueueFrames'],['screenDecoderAccelerationSelect','decoder']]){
      const field=document.getElementById(id);if(!field)continue;if(saved&&saved[key]!=null)field.value=String(saved[key]);
      field.addEventListener('change',()=>{const preset=document.getElementById('nativePlaybackPreset');if(preset)preset.value='custom';applyPlayback().catch(e=>QP.toast(e.message,'error'));});
    }
    document.getElementById('nativePlaybackPreset')?.addEventListener('change',event=>{if(event.target.value==='custom')return;document.getElementById('nativePlaybackBuffer').value=event.target.value;document.getElementById('nativePlaybackFrames').value=event.target.value==='0'?'4':'16';applyPlayback().catch(e=>QP.toast(e.message,'error'));});
    if(saved)applyPlayback().catch(()=>{});
  }
  if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',initPlayback,{once:true});else initPlayback();
  const observer=new ResizeObserver(schedule);
  function attach(entry, target) {
    entry.nativeTarget = target;
    streams.set(entry.peerId, entry);
    observer.observe(target);
    if (entry.headerEl) observer.observe(entry.headerEl);
    schedule();
  }
  function detach(peer) {
    const entry = streams.get(peer);
    if (!entry) return;
    observer.unobserve(entry.nativeTarget);
    clearTimeout(entry.trafficTimer);
    if (entry.headerEl) observer.unobserve(entry.headerEl);
    streams.delete(peer);
    geometry.delete(peer);
    queueLayout({peerId: peer, x: 0, y: 0, width: 1, height: 1, visible: false});
  }
  addEventListener('resize',schedule);addEventListener('scroll',schedule,true);document.addEventListener('visibilitychange',schedule);document.addEventListener('fullscreenchange',schedule);
  document.addEventListener('webkitfullscreenchange',schedule);
  document.addEventListener('focusin',schedule);
  document.addEventListener('focusout',schedule);
  new MutationObserver(changes=>{if(changes.some(c=>c.type==='childList'||c.target.matches?.('.modal-backdrop,.spa-view,dialog,body,.screen-tile,.screen-share-grid,.voice-person-menu,.screen-tile-vol-group')))schedule();}).observe(document.documentElement,{subtree:true,childList:true,attributes:true,attributeFilter:['class','open','hidden']});
  QP.on('native_video_state',state);
  QP.on('native_video_capabilities',caps=>{snapshot.local=caps;renderMatrix();});
  QP.on('screen_codec_capabilities',()=>{void refresh().catch(()=>{});});
  return {attach,detach,fitRect,fitEntry,frameGeometry,refresh,schedule,applyPlayback,get snapshot(){return snapshot;}};
})();
