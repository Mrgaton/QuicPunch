const $=id=>document.getElementById(id),limit=512*1024*1024;
const base=new URL('./',location.href);
const assets=new URL('./',import.meta.url);
let feeds=[],selected,busy=false,objectUrl,detected=false;
const from64=value=>Uint8Array.from(atob(value),c=>c.charCodeAt(0));
function relative(value){const url=new URL(value,base);if(url.origin!==base.origin||!url.pathname.startsWith(base.pathname)||url.search||url.hash||decodeURIComponent(url.pathname).includes('..'))throw Error('La descarga no pertenece a este CDN.');return url;}
function render(){
  $('platform').disabled=$('channel').disabled=busy;
  const platform=$('platform').value,channel=$('channel').value;
  selected=feeds.filter(f=>(channel==='beta'||f.channel==='stable')&&f.artifacts[platform]).sort((a,b)=>b.build-a.build||Number(a.channel==='beta')-Number(b.channel==='beta'))[0];
  $('release').textContent=selected?`QuicNet ${selected.version} · Build ${selected.build} · ${selected.channel==='beta'?'Beta':'Release'}`:'No hay una versión disponible para esta selección.';
  $('download').disabled=busy||!selected?.artifacts[platform]?.compressed;
  $('linuxHelp').hidden=platform!=='linux-x64';
}
async function init(){
  $('verification').hidden=true;$('status').textContent='';$('status').className='';
  if(!detected){if(/Android/i.test(navigator.userAgent))$('platform').value='android-arm64';else if(/Linux/i.test(navigator.userAgent))$('platform').value='linux-x64';detected=true;}
  const pem=await(await fetch(new URL('release-public.pem',base))).text();
  const key=await crypto.subtle.importKey('spki',from64(pem.replace(/-----[^-]+-----|\s/g,'')),{name:'RSASSA-PKCS1-v1_5',hash:'SHA-256'},false,['verify']);
  const results=await Promise.allSettled(['stable','beta'].map(async channel=>{
    const response=await fetch(new URL((channel==='stable'?'latest.json':'latest-beta.json')+'?check='+Math.floor(Date.now()/60000),base));
    if(response.headers.get('cf-mitigated')==='challenge'){
      const error=Error('El servidor de descargas solicita comprobar tu navegador. Abre la verificación y después pulsa Reintentar.');
      const verification=new URL(response.url);verification.search='';
      error.verificationUrl=relative(verification);throw error;
    }
    if(response.status===404)return null;if(!response.ok)throw Error('No se pudo consultar el canal '+channel+'.');
    const bytes=await response.arrayBuffer();if(bytes.byteLength>65536)throw Error('Manifiesto demasiado grande.');
    const envelope=JSON.parse(new TextDecoder().decode(bytes)),payload=from64(envelope.payload);
    if(!await crypto.subtle.verify('RSASSA-PKCS1-v1_5',key,from64(envelope.signature),payload))throw Error('Firma de publicación inválida.');
    const item=JSON.parse(new TextDecoder().decode(payload));
    if(item.schema!==1||(item.channel||'stable')!==channel||!Number.isSafeInteger(item.build)||item.build<=0)throw Error('Canal o versión de publicación incorrectos.');
    return item;
  }));
  feeds=results.filter(r=>r.status==='fulfilled'&&r.value).map(r=>r.value);
  if(!feeds.length){const failure=results.find(r=>r.status==='rejected');if(failure)throw failure.reason;}
  render();
}
$('platform').addEventListener('change',render);$('channel').addEventListener('change',render);
$('download').addEventListener('click',async()=>{
  if(busy||!selected)return;busy=true;render();$('save').hidden=true;$('status').className='';$('progress').hidden=false;$('progress').value=0;
  try{
    const artifact=selected.artifacts[$('platform').value],packed=artifact.compressed;
    if(packed.algorithm!=='zstd'||!Number.isSafeInteger(packed.size)||packed.size<=0||packed.size>limit||!Number.isSafeInteger(artifact.size)||artifact.size<=0||artifact.size>limit||!Number.isInteger(packed.windowLog)||packed.windowLog>27||packed.windowLog<10||!/^[a-f0-9]{64}$/i.test(packed.sha256)||!/^[a-f0-9]{64}$/i.test(artifact.sha256))throw Error('Metadatos de descarga inválidos.');
    const raw=relative(artifact.url),url=relative(packed.url);
    const extension={'win-x64':'.exe','linux-x64':'.bin','android-arm64':'.apk'}[$('platform').value];
    if(!raw.pathname.endsWith(extension)||!url.pathname.endsWith(extension+'.zst'))throw Error('Formato de descarga incorrecto.');
    const response=await fetch(url,{redirect:'error'});if(!response.ok)throw Error('No se pudo descargar la aplicación.');
    if(response.headers.has('content-length')&&Number(response.headers.get('content-length'))!==packed.size)throw Error('Tamaño de descarga incorrecto.');
    const reader=response.body.getReader(),chunks=[];let count=0;
    while(true){const {value,done}=await reader.read();if(done)break;count+=value.byteLength;if(count>packed.size){await reader.cancel();throw Error('La descarga supera el tamaño firmado.');}chunks.push(value);$('progress').value=count/packed.size;$('status').textContent=`Descargando… ${(count/1048576).toFixed(1)} / ${(packed.size/1048576).toFixed(1)} MB`;}
    if(count!==packed.size)throw Error('Descarga incompleta.');
    const bytes=new Uint8Array(count);let offset=0;for(const chunk of chunks){bytes.set(chunk,offset);offset+=chunk.length;}chunks.length=0;
    const hash=[...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(v=>v.toString(16).padStart(2,'0')).join('');if(hash!==packed.sha256.toLowerCase())throw Error('La integridad de la descarga no coincide.');
    $('status').textContent='Descomprimiendo y verificando en tu dispositivo…';
    const output=await new Promise((resolve,reject)=>{const worker=new Worker(new URL('decompress-worker.js',assets),{type:'module'});const timer=setTimeout(()=>{worker.terminate();reject(Error('La descompresión tardó demasiado.'));},120000);worker.onmessage=({data})=>{clearTimeout(timer);worker.terminate();data.error?reject(Error(data.error)):resolve(data.bytes);};worker.onerror=event=>{clearTimeout(timer);worker.terminate();reject(Error(event.message));};worker.postMessage({bytes:bytes.buffer,size:artifact.size,sha256:artifact.sha256},[bytes.buffer]);});
    if(objectUrl)URL.revokeObjectURL(objectUrl);objectUrl=URL.createObjectURL(new Blob([output],{type:'application/octet-stream'}));
    $('save').href=objectUrl;$('save').download=decodeURIComponent(raw.pathname.split('/').pop());$('save').hidden=false;$('save').click();$('status').textContent='Archivo verificado. Ya puedes guardarlo e instalarlo.';
  }catch(error){console.error(error);$('status').textContent=error.message;$('status').className='error';}
  finally{busy=false;render();}
});
function load(){return init().catch(error=>{console.error(error);$('status').textContent=error.message;$('status').className='error';if(error.verificationUrl){$('verifyBrowser').href=error.verificationUrl;$('verification').hidden=false;}render();});}
$('retry').addEventListener('click',load);
load();
