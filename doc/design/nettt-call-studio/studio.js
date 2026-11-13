'use strict';
// Design-only state. No capture, network requests, production storage or API calls.
(() => {
  const $ = selector => document.querySelector(selector);
  const form = $('#settings');
  const profiles = {
    conversation: {name:'Conversación', icon:'◉', help:'Voz primero. Vídeo equilibrado.', summary:'Conversa con naturalidad.', values:{resolution:'1280x720',fps:30,bitrate:4,playout:30,playback:'latest',preset:'balanced',adaptation:'fps',vbv:50,gop:1000}},
    game: {name:'Juego',icon:'⌁',help:'Respuesta rápida. Movimiento fluido.',summary:'Prioriza la respuesta y conserva la fluidez.', values:{resolution:'1920x1080',fps:60,bitrate:12,playout:15,playback:'latest',preset:'speed',adaptation:'fps',vbv:25,gop:500}},
    cinema: {name:'Cine',icon:'▻',help:'Detalle y continuidad. Audio sincronizado.',summary:'Reserva tiempo para reproducir el contenido con continuidad.',values:{resolution:'2560x1440',fps:24,bitrate:18,playout:150,playback:'synced',preset:'quality',adaptation:'detail',vbv:150,gop:2000}},
    presentation: {name:'Presentación',icon:'▤',help:'Texto legible. Menor uso de red.',summary:'Conserva la nitidez del texto al adaptar el vídeo.',values:{resolution:'1920x1080',fps:30,bitrate:6,playout:60,playback:'latest',preset:'balanced',adaptation:'detail',vbv:100,gop:1000}}
  };
  const base = {codec:'h264',backend:'ffmpeg',encoder:'auto',rateControl:'cbr',quality:23,memory:128};
  const make = id => ({...base,...profiles[id].values,profile:id});
  let draft = {screen:make('game'),camera:make('conversation'),voice:{bitrate:96}};
  let applied = structuredClone(draft), track = 'screen', tab = 'video';
  const trackNames = {screen:'Mi pantalla',camera:'Mi cámara',voice:'Mi micrófono'};
  const numeric = new Set(['fps','bitrate','playout','quality','memory','gop','vbv']);
  const video = () => draft[track === 'voice' ? 'screen' : track];
  const dirty = () => JSON.stringify(draft) !== JSON.stringify(applied);
  function status(message) { $('#status').textContent = message; }
  function setTab(next, focus=false) {
    tab = next;
    document.querySelectorAll('[data-tab]').forEach(button => {
      const selected = button.dataset.tab === tab;
      button.setAttribute('aria-selected',String(selected)); button.tabIndex = selected ? 0 : -1;
      $(`#panel-${button.dataset.tab}`).hidden = !selected;
      if (selected && focus) button.focus();
    });
  }
  function render() {
    const value = video();
    for (const [key,item] of Object.entries(value)) if (form.elements[key]) form.elements[key].value = String(item);
    form.elements.voiceBitrate.value = draft.voice.bitrate;
    document.querySelectorAll('[data-profile]').forEach(button => {button.setAttribute('aria-pressed',String(value.profile === button.dataset.profile));button.disabled = track === 'voice';});
    document.querySelectorAll('[data-track]').forEach(button => button.setAttribute('aria-pressed',String(track === button.dataset.track)));
    $('#trackTitle').textContent = trackNames[track];
    $('#trackEyebrow').textContent = track === 'voice' ? 'CONFIGURACIÓN INDEPENDIENTE · VOZ' : 'CONFIGURACIÓN DE ENVÍO';
    for (const id of ['video','latency']) $(`#tab-${id}`).disabled = track === 'voice';
    $('#profileLabel').textContent = track === 'voice' ? 'La voz no hereda el perfil de vídeo' : value.profile === 'custom' ? 'Personalizado' : 'Perfil de esta pista';
    const web = false;
    for (const key of ['encoder','preset','vbv','rateControl']) form.elements[key].disabled = web;
    form.elements.quality.disabled = web || value.rateControl !== 'vbr';
    $('#engineNote').textContent = 'En producción, implementación y preset se enumeran por encoder probado (NVENC, AMF, x264, x265, VP9 o SVT-AV1). CQ es ilustrativo; no se trasladan escalas entre códecs.';
    $('#codecNote').textContent = value.codec === 'hevc' ? 'Escenario de ejemplo: un receptor no acepta HEVC. Se conserva tu elección y se muestra el motivo; no se cambia de códec en silencio.' : 'Compatibilidad de ejemplo: el perfil exacto y el primer fotograma deben verificarse por receptor.';
    $('#draftBadge').textContent = dirty() ? 'Cambios pendientes' : 'Sin cambios';
    $('#apply').disabled = $('#reset').disabled = !dirty();
    const name = profiles[value.profile]?.name || 'Personalizado';
    $('#summaryTitle').textContent = track === 'voice' ? 'Micrófono · Opus' : `${name} · ${trackNames[track].toLowerCase()}`;
    $('#summaryText').textContent = track === 'voice' ? `Voz independiente · ${draft.voice.bitrate} kbps. Los objetivos de vídeo inferiores corresponden a tu pantalla.` : profiles[value.profile]?.summary || 'Ajustes manuales a partir de un perfil.';
    $('#resolutionStat').textContent = value.resolution.replace('x',' × ');
    $('#fpsStat').textContent = `${value.fps} FPS`;
    $('#bitrateStat').textContent = `${value.bitrate} Mbps`;
    $('#playoutStat').textContent = `${value.playout} ms`;
    $('#upload').textContent = `${value.bitrate * 2} Mbps`;
    $('#bufferValue').textContent = `${value.playout} ms`;
    const frames = Math.ceil(value.playout * value.fps / 1000);
    const [width,height] = value.resolution.split('x').map(Number);
    const mib = width * height * 4 * frames / (1024*1024);
    $('#bufferExplanation').textContent = `≈ ${frames} fotogramas a ${value.fps} FPS · ${mib.toFixed(1)} MiB si fueran superficies RGBA. Es una estimación de reserva; no incluye decoder, GPU, audio ni transporte.`;
    $('#bufferViz').replaceChildren(...Array.from({length:20},(_,i) => {const el=document.createElement('i');el.className=i<Math.min(frames,20)?'':'off';return el;}));
    $('#latencyNote').textContent = mib > value.memory ? 'La reserva estimada supera el límite de memoria. Reduce resolución o buffer, o aumenta el límite antes de aplicar.' : value.playback === 'synced' ? 'El audio del contenido guía el reloj de presentación. Esta reserva añade espera; el micrófono conserva su propia política de baja latencia.' : 'Los fotogramas fuera de plazo se descartan. Un buffer de 0 ms no elimina el retardo de captura, codificación, red o pantalla.';
    $('#pendingNote').textContent = value.fps > 60 ? '120/144 FPS requieren una prueba sostenida de captura, codificación y recepción.' : 'Los objetivos no son mediciones de rendimiento.';
    $('#compatibility').replaceChildren(...['Ana · escritorio','Leo · móvil'].map((name,index) => {
      const row=document.createElement('div');row.className='receiver';
      const label=document.createElement('div');label.textContent=name;
      const detail=document.createElement('small');detail.textContent='Receptor simulado · sin prueba de reproducción';label.append(detail);
      const result=document.createElement('span');result.className='result';
      result.textContent='Perfil aceptado (ejemplo)';
      if(index && value.codec==='hevc'){result.classList.add('warn');result.textContent='No compatible · vídeo omitido';}
      else if(index && (value.fps>60 || value.codec==='av1')){result.classList.add('warn');result.textContent='Pendiente de comprobar';}
      row.append(label,result);return row;
    }));
    const previous=applied[track === 'voice'?'screen':track];
    const rows = track === 'voice' ? [['Bitrate de voz',`${draft.voice.bitrate} kbps`,`${applied.voice.bitrate} kbps`]] : [['Resolución',value.resolution,previous.resolution],['FPS',value.fps,previous.fps],['Códec',value.codec,previous.codec],['Buffer de reproducción',`${value.playout} ms`,`${previous.playout} ms`],['VBV',web?'No aplicable':`${value.vbv} ms`,`${previous.vbv} ms`]];
    $('#comparison').replaceChildren(...rows.map(values => {const tr=document.createElement('tr');for(const text of values){const td=document.createElement('td');td.textContent=String(text);tr.append(td);}return tr;}));
  }
  for (const [id,profile] of Object.entries(profiles)) {
    const button=document.createElement('button');button.className='profile';button.dataset.profile=id;
    button.innerHTML=`<span class="profile-icon" aria-hidden="true">${profile.icon}</span><b>${profile.name}</b><small>${profile.help}</small>`;
    button.onclick=()=>{if(track==='voice')return;draft[track]={...draft[track],...profile.values,profile:id};render();status(`Perfil ${profile.name} preparado para ${trackNames[track].toLowerCase()}. No cambia el micrófono ni la otra pista.`);};
    $('#profiles').append(button);
  }
  for(const [id,name] of Object.entries(trackNames)){
    const button=document.createElement('button');button.className='track';button.dataset.track=id;
    button.innerHTML=`<span aria-hidden="true">${{screen:'▣',camera:'◈',voice:'♩'}[id]}</span>${name}`;
    button.onclick=()=>{track=id;if(id==='voice')setTab('audio');else if(tab==='audio')setTab('video');render();};$('#tracks').append(button);
  }
  form.addEventListener('submit',event=>event.preventDefault());
  form.addEventListener('input',event=>{
    const {name,value}=event.target;if(!name)return;
    if(name==='voiceBitrate')draft.voice.bitrate=value === '' ? NaN : Number(value);
    else if(Object.hasOwn(video(),name)){video()[name]=numeric.has(name)?(value === '' ? NaN : Number(value)):value;video().profile='custom';}
    render();
  });
  document.querySelectorAll('[data-tab]').forEach(button=>{
    button.onclick=()=>setTab(button.dataset.tab);
    button.onkeydown=event=>{
      const tabs=[...document.querySelectorAll('[data-tab]:not(:disabled)')];
      const index=tabs.indexOf(button);let next;
      if(event.key==='ArrowRight')next=(index+1)%tabs.length;
      if(event.key==='ArrowLeft')next=(index+tabs.length-1)%tabs.length;
      if(event.key==='Home')next=0;if(event.key==='End')next=tabs.length-1;
      if(next!==undefined){event.preventDefault();setTab(tabs[next].dataset.tab,true);}
    };
  });
  function validate(state){
    if(!state || !Number.isFinite(state.voice?.bitrate) || state.voice.bitrate<8 || state.voice.bitrate>300 || !Number.isInteger(state.voice.bitrate))return 'El bitrate de voz debe ser un entero entre 8 y 300 kbps.';
    const enums={resolution:['1280x720','1920x1080','2560x1440','3840x2160'],fps:[24,30,60,120,144],codec:['h264','hevc','vp9','av1'],backend:['ffmpeg'],encoder:['auto','gpu','cpu'],rateControl:['cbr','vbr'],preset:['speed','balanced','quality'],adaptation:['fps','detail','manual'],playback:['latest','synced'],profile:[...Object.keys(profiles),'custom']};
    const ranges={bitrate:[.5,100,.5],playout:[0,500,5],memory:[16,512,16],quality:[0,51,1],gop:[250,5000,50],vbv:[16,250,1]};
    for(const key of ['screen','camera']){
      const v=state[key];if(!v)return 'El perfil está incompleto.';
      for(const [field,choices] of Object.entries(enums))if(!choices.includes(v[field]))return `Valor no admitido: ${field}.`;
      for(const [field,[min,max,step]] of Object.entries(ranges))if(!Number.isFinite(v[field])||v[field]<min||v[field]>max||Math.abs((v[field]-min)/step-Math.round((v[field]-min)/step))>1e-7)return `Revisa ${field}: de ${min} a ${max}, en pasos de ${step}.`;
      const [w,h]=v.resolution.split('x').map(Number);
      if(w*h*4*Math.ceil(v.playout*v.fps/1000)/1048576>v.memory)return `La reserva estimada de ${trackNames[key].toLowerCase()} supera su límite de memoria.`;
    }
    return null;
  }
  $('#apply').onclick=()=>{
    const error=validate(draft);if(error){status(error);return;}
    if($('#failNext').checked){$('#failNext').checked=false;status('Fallo simulado del codificador. Se conserva la configuración aplicada anterior y tu borrador para reintentar.');return;}
    applied=structuredClone(draft);render();status('Configuración aplicada únicamente a la demo. El motor real necesitará validar, negociar y confirmar el primer fotograma.');
  };
  $('#reset').onclick=()=>{draft=structuredClone(applied);render();status('Borrador descartado. Se han recuperado los valores aplicados en la demo.');};
  const storageKey='nettt-call-studio-design-v1';
  $('#save').onclick=()=>{const error=validate(draft);if(error){status(error);return;}try{localStorage.setItem(storageKey,JSON.stringify({schemaVersion:1,prototype:true,tracks:draft}));status('Perfil de diseño guardado en este navegador.');}catch{status('El navegador no permite guardar el perfil local. Puedes exportar JSON.');}};
  $('#load').onclick=()=>{try{const saved=JSON.parse(localStorage.getItem(storageKey));if(!saved){status('Todavía no hay un perfil local guardado.');return;}const error=saved.schemaVersion===1?validate(saved.tracks):'Versión de perfil no admitida.';if(error){status(error);return;}draft=structuredClone(saved.tracks);render();status('Perfil local recuperado como borrador.');}catch{status('No se pudo leer el perfil local. Se mantiene tu borrador.');}};
  $('#export').onclick=()=>{const error=validate(draft);if(error){status(error);return;}const blob=new Blob([JSON.stringify({schemaVersion:1,prototype:true,units:{videoBitrate:'Mbps',voiceBitrate:'kbps',time:'ms',memory:'MiB'},tracks:draft},null,2)],{type:'application/json'});const url=URL.createObjectURL(blob);const link=document.createElement('a');link.href=url;link.download='nettt-perfil-diseno.json';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);status('Perfil JSON exportado. Es un contrato de diseño, no una configuración importable en producción.');};
  render();
})();
