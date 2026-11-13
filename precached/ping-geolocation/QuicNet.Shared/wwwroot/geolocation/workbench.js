import {create} from './map.js';
const $=id=>document.getElementById(id),client=crypto.randomUUID(),api='/api/geolocation/workbench/';
const lifetime=new AbortController();
let map,view,timer,busy=false,disposed=false,opened=false,lastRun;
const defaults={ip:'',offline:false,family:'ipv4',rounds:5,batchSize:32,samples:4,timeoutMs:700,maxHops:28,traceCount:4,maximumTraceRoutes:16,packetsPerSecond:20,seconds:300,resolutionKm:50,parallelism:6,timingSlackMs:1,maxCells:20000,propagationKmPerMs:299.792458,enableTunnelAssessment:true,enableTopology:true,enablePathMtu:false,enableTcp:false,maximumProbes:4000,traceRepetitions:3};
function configurationSummary(){const value=settings();$('networkOptions').disabled=value.offline;$('counters').textContent=value.offline?'Solo catálogo local':`Hasta ${value.seconds} s · ${value.maximumProbes.toLocaleString('es')} sondeos`;}
function fields(settings){for(const input of $('fields').querySelectorAll('[name]')){if(input.type==='checkbox')input.checked=settings[input.name];else input.value=settings[input.name]??'';}configurationSummary();}
function settings(){const result={};for(const input of $('fields').querySelectorAll('[name]'))result[input.name]=input.type==='checkbox'?input.checked:input.type==='number'?Number(input.value):input.value;return result;}
function failure(error){$('error').textContent=error.message||String(error);$('error').hidden=false;}
function renderLocalities(value){
 $('localityAssessment').hidden=$('localityDetails').hidden=!value;if(!value)return;
 $('localityEvidence').textContent=view?.analysis?.conditional?'Hipótesis basada en coordenadas declaradas por operadores':value.evidence;
 const labels={'published-only':'Solo catálogo de IP','locality-candidates':'Localidades orientativas','regional-estimate':'Precisión regional','disagreement':'Fuentes discrepantes','conflicting':'Sin estimación válida','network-area':'Pistas de infraestructura','insufficient-data':'Sin señales suficientes'};
 $('localityStatus').textContent=labels[value.status]||value.status;$('localityStatus').dataset.status=value.status;
 const metric=(label,text)=>{const item=document.createElement('div'),name=document.createElement('span'),number=document.createElement('strong');name.textContent=label;number.textContent=text;item.append(name,number);return item;};
 $('localityMetrics').replaceChildren(...(value.fit?[
  metric('Emplazamientos independientes',String(value.fit.independentSites)),
  metric('Retardo de acceso estimado',value.fit.accessDelayMs==null?'Sin estimar':`${value.fit.accessDelayMs.toFixed(1)} ms`),
  metric('Sensibilidad entre modelos',`${Math.round(value.modelSpreadKm).toLocaleString('es')} km`),
  metric('Geometría de referencias',value.fit.diverseGeometry?'Varias direcciones':'Cobertura limitada')]:[]));
 const published=value.status==='published-only';
 $('localityContext').textContent=published?'Localidades próximas al punto publicado para la IP. Su proximidad no confirma el municipio del dispositivo.':value.status==='network-area'?'Estas localidades están cerca de referencias de red; no identifican dónde está la persona.':value.candidates.length?'Alternativas próximas al ajuste de latencias. No se elige automáticamente la ciudad más grande.':'No hay localidades que se puedan proponer con las señales disponibles.';
 $('localityCandidates').replaceChildren(...value.candidates.map(candidate=>{
  const place=candidate.place,row=document.createElement('li'),copy=document.createElement('div'),name=document.createElement('strong'),detail=document.createElement('span'),basis=document.createElement('small'),button=document.createElement('button');
  name.textContent=place.displayName;detail.textContent=[place.region,place.countryCode].filter(Boolean).join(' · ');
  basis.textContent=candidate.basis==='network-infrastructure'?'Próxima a infraestructura':`${candidate.distanceKm<1?'< 1':Math.round(candidate.distanceKm).toLocaleString('es')} km ${published?'de la referencia de IP':'del ajuste'}`;
  copy.append(name,detail,basis);button.type='button';button.textContent='Ver mapa';button.setAttribute('aria-label',`Ver ${place.displayName} en el mapa`);button.addEventListener('click',()=>map?.locate(place.position,Math.max(5,Math.min(50,value.modelSpreadKm||10))));row.append(copy,button);return row;
 }));
 $('localityPublished').textContent=value.publishedPlace?`Referencia publicada: ${value.publishedPlace.displayName} · ${value.publishedPlace.region} · ${value.publishedPlace.countryCode}`:'';
 $('localityLimitations').replaceChildren(...value.limitations.map(text=>{const item=document.createElement('li');item.textContent=text;return item;}));
}
function renderAnalysis(value){
 $('analysisStatus').textContent=value?.status||'Listo para empezar';
 $('measurementCount').textContent=`${value?.probes??0} sondeos · ${value?.references??0} referencias`;
 $('conditional').hidden=!value?.conditional;
 const evidence=value?.evidence;
 $('alternatives').hidden=$('network').hidden=!evidence;if(!evidence)return;
 $('hierarchyStage').textContent=value.stage;$('tunnelStatus').textContent=value.tunnel;
 const levels={'country':'País','regional-area':'Región','local-area':'Área local'};
 $('hypotheses').replaceChildren(...evidence.hierarchy.hypotheses.map(h=>{
  const row=document.createElement('li'),copy=document.createElement('div'),name=document.createElement('strong'),detail=document.createElement('small'),button=document.createElement('button');
  name.textContent=h.level==='country'?(new Intl.DisplayNames(['es'],{type:'region'}).of(h.countryCode)||h.label):h.label;detail.textContent=`${levels[h.level]||'Zona'} · ${h.independentSites} emplazamientos`;
  copy.append(name,detail);button.type='button';button.textContent='Ver mapa';button.setAttribute('aria-label',`Ver hipótesis ${h.label} en el mapa`);button.addEventListener('click',()=>map?.locate(h.position,h.searchRadiusKm));row.append(copy,button);return row;
 }));
 if(!evidence.hierarchy.hypotheses.length){const row=document.createElement('li');row.textContent='Faltan mediciones para proponer alternativas.';$('hypotheses').append(row);}
 const routing=evidence.routeLocation;
 $('routeComparison').hidden=!routing;
 $('routeZones').replaceChildren(...(routing?.zones||[]).slice(0,12).map(zone=>{
  const row=document.createElement('li'),copy=document.createElement('div'),name=document.createElement('strong'),button=document.createElement('button');
  name.textContent=`${zone.label} · ${zone.countryCode}`;copy.append(name);
  for(const reason of zone.reasons){const detail=document.createElement('small');detail.textContent=reason;copy.append(detail);}
  button.type='button';button.textContent='Ver zona';button.addEventListener('click',()=>map?.locate(zone.position,300));row.append(copy,button);return row;
 }));
 $('routeQuality').textContent=routing?`${routing.usableRoutes} rutas utilizables · ${routing.contrasts.length} contrastes compartidos. La región compatible se conserva.`:'';
 $('networkSummary').textContent=`${value.tunnel} · ${view.settings.enableTopology?`${evidence.topology.distinctRoutes} rutas comparadas · ${evidence.topology.sharedRouters.length} routers compartidos`:'Comparación de rutas desactivada'}`;
 $('fingerprintStatus').textContent=value.fingerprints;
 $('mixedRoutes').hidden=evidence.tunnel.mixedRoutes!==true;
 $('routes').replaceChildren(...evidence.tunnel.routes.map(route=>{
  const row=document.createElement('tr');for(const text of [route.destination,(route.interfaceName||'Sin determinar')+(route.error?' · consulta incompleta':''),route.sourceAddress||'—',route.mtu??'—']){const cell=document.createElement('td');cell.textContent=text;row.append(cell);}return row;
 }));
 const mtuLabels={'bounded':'Intervalo medido','at-least-probed-size':'Al menos el tamaño probado','control-unreachable':'Sin respuesta de control','indeterminate-filter-or-loss':'Indeterminado: filtrado o pérdida','unsupported-portable-ipv6-df':'Medición IPv6 no disponible','interrupted':'Medición interrumpida','probe-budget':'Límite de sondeos alcanzado'};
 const measurements=[...evidence.tunnel.pathMtu.map(m=>`${m.destination} · Path MTU: ${mtuLabels[m.status]||'Sin determinar'} · inferior: ${m.lowerBoundBytes??'sin determinar'} · superior: ${m.upperBoundBytes??'sin determinar'} bytes`),...evidence.supplemental.map(m=>`${m.landmarkId} · ${m.protocol.toUpperCase()}: ${m.statistics.successes} / ${m.statistics.attempts} respuestas · mínimo: ${m.statistics.minimumMs?.toFixed(2)??'—'} ms`)];
 $('supplemental').replaceChildren(...measurements.map(text=>{const p=document.createElement('p');p.textContent=text;return p;}));
}
function renderExplanation(value){
 $('reasoningSummary').textContent=value?.summary||'Inicia el análisis para ver qué señales sostienen el resultado.';
 $('reasoningReasons').replaceChildren(...(value?.reasons||[]).map(text=>{const item=document.createElement('li');item.textContent=text;return item;}));
 const measurements=value?.measurements||[];$('measurements').hidden=!measurements.length;
 $('measurementRows').replaceChildren(...measurements.map(m=>{
  const row=document.createElement('tr');
  for(const text of [`${m.name} · ${m.address} · ${m.countryCode}`,m.minimumMs==null?'—':`${m.minimumMs.toFixed(2)} ms`,m.medianMs==null?'—':`${m.medianMs.toFixed(2)} ms`,`${m.successes} / ${m.attempts}`,m.coordinatesVerified?'Verificadas':'Declaradas']){const cell=document.createElement('td');cell.textContent=text;row.append(cell);}
  const cell=document.createElement('td'),button=document.createElement('button');button.type='button';button.textContent='Ver referencia';button.addEventListener('click',()=>map?.locate(m.position,100));cell.append(button);row.append(cell);return row;
 }));
 $('modelDetail').hidden=!value?.modelDetail;$('modelDescription').textContent=value?.modelDetail||'';
}
async function request(action,data){
 const response=await fetch(api+action+(data===undefined?`?client=${encodeURIComponent(client)}${view?`&version=${view.version}`:''}`:''),data===undefined?{signal:lifetime.signal}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({client,...data}),signal:lifetime.signal});
 if(response.status===204)return null;
 const result=await response.json();if(!response.ok)throw Error(result.error||result.message||'No se pudo completar la operación.');return result;
}
function render(next){
 if(disposed||!next)return;view=next;
 $('start').disabled=busy;$('stop').disabled=busy||!next.running;$('start').textContent=next.running?'Reiniciar':'Iniciar';
 configurationSummary();renderAnalysis(next.analysis);renderExplanation(next.explanation);
 if(next.coverage)$('coverage').textContent=`Catálogo incluido: ${next.coverage.destinations} destinos · ${next.coverage.sites} emplazamientos · ${next.coverage.places} lugares. Las etiquetas de infraestructura no equivalen a un catálogo municipal completo.`;
 $('resultTitle').textContent=next.title;$('resultDescription').textContent=next.description;
 renderLocalities(next.localities);
 if(next.error)failure(Error(next.error));
 $('logCount').textContent=`(${next.packetCount})`;$('logOutput').textContent=next.logs.join('\n')||'Sin registros todavía.';
 $('packets').replaceChildren(...next.packets.map(packet=>{const row=document.createElement('tr');for(const value of [packet.destination,packet.ttl,packet.status,Number.isFinite(packet.rttMs)?`${packet.rttMs.toFixed(2)} ms`:'—']){const td=document.createElement('td');td.textContent=value;row.append(td);}return row;}));
 $('notes').replaceChildren(...next.notes.map(text=>{const li=document.createElement('li');li.textContent=text;return li;}));
 map?.update({...next.map,reset:lastRun!==next.runKey});lastRun=next.runKey;
}
async function poll(){
 if(disposed)return;
 try{render(await request('state'));}catch(error){if(!disposed)failure(error);}finally{if(!disposed)timer=setTimeout(poll,1000);}
}
async function action(name){
 if(busy||!opened)return;busy=true;$('error').hidden=true;$('start').disabled=$('stop').disabled=true;
 try{if(name==='start'){
   for(const field of $('fields').querySelectorAll('input'))if(!field.checkValidity()){
     for(let parent=field.parentElement;parent;parent=parent.parentElement)if(parent.tagName==='DETAILS')parent.open=true;
     field.reportValidity();return;
   }
   render(await request('start',{settings:settings()}));
 }else{await request('stop',{});view=null;render(await request('state'));}}
 catch(error){failure(error);}finally{busy=false;if(view)render(view);}
}
$('controls').addEventListener('submit',event=>{event.preventDefault();action('start');});$('stop').addEventListener('click',()=>action('stop'));
$('fields').addEventListener('input',event=>{
 configurationSummary();
 if(['ip','family'].includes(event.target.name))return;
 $('profile').value=event.target.name==='offline'&&event.target.checked?'offline':'custom';
});
$('publicIp').addEventListener('change',()=>{if($('publicIp').value)$('fields').querySelector('[name=family]').value=$('publicIp').value.includes(':')?'ipv6':'ipv4';});
$('profile').addEventListener('change',()=>{
 const preset=$('profile').value,own=settings();if(preset==='custom')return;
 const value={...defaults,ip:own.ip,family:own.family};
 if(preset==='quick')Object.assign(value,{rounds:3,batchSize:24,samples:3,seconds:120,traceCount:3,maxHops:24,maximumTraceRoutes:8,maximumProbes:2000,traceRepetitions:2});
 if(preset==='detailed')Object.assign(value,{rounds:8,batchSize:48,samples:6,seconds:600,traceCount:6,maxHops:32,maximumTraceRoutes:24,maximumProbes:8000,traceRepetitions:3});
 if(preset==='offline')value.offline=true;fields(value);
});
const resize=new ResizeObserver(()=>{if(!disposed)parent.postMessage({type:'geolocation-height',height:Math.ceil(document.querySelector('main').getBoundingClientRect().height)},location.origin);});resize.observe(document.body);
function dispose(){if(disposed)return;disposed=true;clearTimeout(timer);lifetime.abort();map?.dispose();resize.disconnect();if(opened)fetch(api+'close',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({client}),keepalive:true}).catch(()=>{});}
window.addEventListener('pagehide',dispose,{once:true});
try{
 // Parallel independent loads only after this document has been opened in Settings.
 const [initial,renderer]=await Promise.all([request('open',{}),create($('mapHost'),{streetTiles:true})]);
 if(disposed){renderer.dispose();}else{
   opened=true;map=renderer;
   for(const address of initial.addresses){const option=document.createElement('option');option.value=option.textContent=address;$('publicIp').append(option);}
   fields(initial.settings);$('profile').value='balanced';render(initial);poll();
 }
}catch(error){if(!disposed)failure(error);}
