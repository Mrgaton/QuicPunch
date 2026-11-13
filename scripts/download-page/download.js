const $=id=>document.getElementById(id),limit=512*1024*1024;
const base=new URL('./',location.href);
const assets=new URL('./',import.meta.url);
let feeds=[],selected,busy=false,objectUrl,detected=false,loading=false,feedError=null,downloadController=null;
const from64=value=>Uint8Array.from(atob(value),c=>c.charCodeAt(0));
function relative(value){const url=new URL(value,base);if(url.origin!==base.origin||!url.pathname.startsWith(base.pathname)||url.search||url.hash||decodeURIComponent(url.pathname).includes('..'))throw Error('La descarga no pertenece a este CDN.');return url;}
function clearDownload(){
  if(objectUrl)URL.revokeObjectURL(objectUrl);objectUrl=null;
  $('save').hidden=true;$('save').removeAttribute('href');$('save').removeAttribute('download');
  $('progress').hidden=true;$('progress').value=0;
  $('status').textContent='';$('status').className='';
}
function errorMessage(error){return error.name==='TypeError'?'No se pudo conectar con el servidor de descargas. Comprueba tu conexión y vuelve a intentarlo.':error.message;}
async function fetchMetadata(url,maximum){
  const controller=new AbortController();
  const timeout=setTimeout(()=>controller.abort(new DOMException('El servidor tardó demasiado en responder. Pulsa Reintentar consulta.','TimeoutError')),15000);
  try{
    const response=await fetch(url,{signal:controller.signal,redirect:'error'});
    if(response.headers.get('cf-mitigated')==='challenge'){
      const error=Error('El servidor de descargas solicita comprobar tu navegador. Abre la verificación y después pulsa Reintentar consulta.');
      const verification=new URL(response.url);verification.search='';error.verificationUrl=relative(verification);throw error;
    }
    if(!response.ok)return {response,bytes:null};
    const reader=response.body.getReader(),chunks=[];let count=0;
    while(true){const {value,done}=await reader.read();if(done)break;count+=value.byteLength;if(count>maximum){await reader.cancel();throw Error('La respuesta del servidor supera el tamaño permitido.');}chunks.push(value);}
    const bytes=new Uint8Array(count);let offset=0;for(const chunk of chunks){bytes.set(chunk,offset);offset+=chunk.length;}
    return {response,bytes};
  }catch(error){throw controller.signal.aborted?controller.signal.reason:error;}
  finally{clearTimeout(timeout);controller.abort();}
}
function render(){
  $('platform').disabled=$('channel').disabled=busy||loading;
  const platform=$('platform').value,channel=$('channel').value;
  selected=feeds.filter(f=>(channel==='beta'||f.channel==='stable')&&f.artifacts[platform]).sort((a,b)=>b.build-a.build||Number(a.channel==='beta')-Number(b.channel==='beta'))[0];
  $('release').textContent=loading?'Buscando la última versión…':selected?`QuicNet ${selected.version} · Build ${selected.build} · ${selected.channel==='beta'?'Beta':'Release'}`:feedError?'No se pudo comprobar si hay una versión disponible.':'No hay una versión disponible para esta selección.';
  $('download').disabled=busy||loading||!selected?.artifacts[platform]?.compressed;
  $('retry').hidden=!feedError;$('retry').disabled=busy||loading;
  $('cancel').hidden=!busy;
  $('download').textContent=busy?'Descargando y verificando…':'Descargar QuicNet';
  $('linuxHelp').hidden=platform!=='linux-x64';
  $('windowsHelp').hidden=platform!=='win-x64';$('androidHelp').hidden=platform!=='android-arm64';
}
async function init(){
  $('verification').hidden=true;$('status').textContent='';$('status').className='';
  if(!detected){if(/Android/i.test(navigator.userAgent))$('platform').value='android-arm64';else if(/Linux/i.test(navigator.userAgent))$('platform').value='linux-x64';detected=true;}
  if(!globalThis.crypto?.subtle)throw Error('Este navegador no permite verificar la descarga. Abre esta página por HTTPS en un navegador actualizado.');
  const {response:keyResponse,bytes:keyBytes}=await fetchMetadata(new URL('release-public.pem',base),16384);
  if(!keyResponse.ok)throw Error('No se pudo consultar la clave pública de las descargas. Reintenta en unos instantes.');
  const pem=new TextDecoder().decode(keyBytes);
  let key;
  try{key=await crypto.subtle.importKey('spki',from64(pem.replace(/-----[^-]+-----|\s/g,'')),{name:'RSASSA-PKCS1-v1_5',hash:'SHA-256'},false,['verify']);}
  catch{throw Error('La clave de verificación recibida no es válida. Reintenta la consulta; no se ha preparado ninguna descarga.');}
  const results=await Promise.allSettled(['stable','beta'].map(async channel=>{
    const {response,bytes}=await fetchMetadata(new URL((channel==='stable'?'latest.json':'latest-beta.json')+'?check='+Math.floor(Date.now()/60000),base),65536);
    if(response.status===404)return null;if(!response.ok)throw Error('No se pudo consultar el canal '+channel+'.');
    let envelope,payload;
    try{envelope=JSON.parse(new TextDecoder().decode(bytes));payload=from64(envelope.payload);}
    catch{throw Error('La publicación recibida no tiene un formato válido. Reintenta la consulta.');}
    if(!await crypto.subtle.verify('RSASSA-PKCS1-v1_5',key,from64(envelope.signature),payload))throw Error('Firma de publicación inválida.');
    const item=JSON.parse(new TextDecoder().decode(payload));
    if(!item||item.schema!==1||(item.channel||'stable')!==channel||!Number.isSafeInteger(item.build)||item.build<=0||typeof item.version!=='string'||!item.version.trim()||!item.artifacts||typeof item.artifacts!=='object'||Array.isArray(item.artifacts))throw Error('Los datos de publicación no son válidos. No se ha preparado esta descarga.');
    return item;
  }));
  feeds=results.filter(r=>r.status==='fulfilled'&&r.value).map(r=>r.value);
  const failure=results.find(r=>r.status==='rejected');
  if(failure)throw failure.reason;
  render();
}
function selectionChanged(){clearDownload();if(feedError){$('status').textContent='Consulta incompleta. Puedes reintentar para buscar la versión más reciente.';$('status').className='error';}render();}
$('platform').addEventListener('change',selectionChanged);$('channel').addEventListener('change',selectionChanged);
$('cancel').addEventListener('click',()=>downloadController?.abort(new DOMException('Descarga cancelada. Puedes volver a descargarla cuando quieras.','AbortError')));
$('download').addEventListener('click',async()=>{
  if(busy||!selected)return;clearDownload();busy=true;downloadController=new AbortController();
  const controller=downloadController;let idleTimer;
  const resetDeadline=()=>{clearTimeout(idleTimer);idleTimer=setTimeout(()=>controller.abort(new DOMException('La descarga dejó de responder. Comprueba tu conexión y vuelve a descargarla.','TimeoutError')),30000);};
  render();$('progress').hidden=false;$('progress').value=0;$('status').textContent='Conectando con el servidor de descargas…';
  try{
    const artifact=selected.artifacts[$('platform').value],packed=artifact.compressed;
    if(packed.algorithm!=='zstd'||!Number.isSafeInteger(packed.size)||packed.size<=0||packed.size>limit||!Number.isSafeInteger(artifact.size)||artifact.size<=0||artifact.size>limit||!Number.isInteger(packed.windowLog)||packed.windowLog>27||packed.windowLog<10||!/^[a-f0-9]{64}$/i.test(packed.sha256)||!/^[a-f0-9]{64}$/i.test(artifact.sha256))throw Error('Metadatos de descarga inválidos.');
    const raw=relative(artifact.url),url=relative(packed.url);
    const extension={'win-x64':'.exe','linux-x64':'.bin','android-arm64':'.apk'}[$('platform').value];
    if(!raw.pathname.endsWith(extension)||!url.pathname.endsWith(extension+'.zst'))throw Error('Formato de descarga incorrecto.');
    resetDeadline();const response=await fetch(url,{redirect:'error',signal:controller.signal});if(!response.ok)throw Error('No se pudo descargar la aplicación. Vuelve a intentarlo.');
    if(response.headers.has('content-length')&&Number(response.headers.get('content-length'))!==packed.size)throw Error('Tamaño de descarga incorrecto.');
    const reader=response.body.getReader(),chunks=[];let count=0;
    while(true){const {value,done}=await reader.read();if(done)break;resetDeadline();count+=value.byteLength;if(count>packed.size){await reader.cancel();throw Error('La descarga supera el tamaño firmado.');}chunks.push(value);$('progress').value=count/packed.size;$('status').textContent=`Descargando… ${(count/1048576).toFixed(1)} / ${(packed.size/1048576).toFixed(1)} MB`;}
    clearTimeout(idleTimer);controller.signal.throwIfAborted();
    if(count!==packed.size)throw Error('Descarga incompleta.');
    const bytes=new Uint8Array(count);let offset=0;for(const chunk of chunks){bytes.set(chunk,offset);offset+=chunk.length;}chunks.length=0;
    const hash=[...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(v=>v.toString(16).padStart(2,'0')).join('');if(hash!==packed.sha256.toLowerCase())throw Error('La integridad de la descarga no coincide.');
    controller.signal.throwIfAborted();
    $('status').textContent='Descomprimiendo y verificando en tu dispositivo…';
    const output=await new Promise((resolve,reject)=>{
      const worker=new Worker(new URL('decompress-worker.js',assets),{type:'module'});
      const cleanup=()=>{clearTimeout(timer);worker.terminate();controller.signal.removeEventListener('abort',abort);};
      const abort=()=>{cleanup();reject(controller.signal.reason);};
      const timer=setTimeout(()=>{cleanup();reject(Error('La descompresión tardó demasiado. Vuelve a descargar el archivo.'));},120000);
      controller.signal.addEventListener('abort',abort,{once:true});
      worker.onmessage=({data})=>{cleanup();data.error?reject(Error(data.error)):resolve(data.bytes);};
      worker.onerror=event=>{cleanup();reject(Error(event.message||'No se pudo preparar el archivo. Vuelve a intentarlo.'));};
      worker.postMessage({bytes:bytes.buffer,size:artifact.size,sha256:artifact.sha256},[bytes.buffer]);
    });
    controller.signal.throwIfAborted();
    if(objectUrl)URL.revokeObjectURL(objectUrl);objectUrl=URL.createObjectURL(new Blob([output],{type:'application/octet-stream'}));
    $('save').href=objectUrl;$('save').download=decodeURIComponent(raw.pathname.split('/').pop());$('save').hidden=false;$('save').click();$('status').textContent='Archivo verificado. Ya puedes guardarlo e instalarlo.';
  }catch(error){const failure=controller.signal.aborted?controller.signal.reason:error;console.error(failure);$('status').textContent=errorMessage(failure);$('status').className=failure.name==='AbortError'?'':'error';}
  finally{const cancelledFocus=document.activeElement===$('cancel');clearTimeout(idleTimer);controller.abort();downloadController=null;busy=false;$('progress').hidden=true;render();if(cancelledFocus)$('download').focus();}
});
async function load(){
  if(loading||busy)return;clearDownload();loading=true;feeds=[];feedError=null;render();
  try{await init();}
  catch(error){console.error(error);feedError=error;$('status').textContent=(feeds.length?'Consulta incompleta; puedes descargar la versión verificada disponible o reintentar para buscar la más reciente. ':'')+errorMessage(error);$('status').className='error';if(error.verificationUrl){$('verifyBrowser').href=error.verificationUrl;$('verification').hidden=false;}}
  finally{loading=false;render();}
}
$('retry').addEventListener('click',load);
load();
