// Isolated design preview. All identities and responses are fixtures; no P2P, VPN or microphone is started.
// Run: node scripts/preview-webui.mjs [--empty] [--port=5099]
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve, extname, sep } from 'node:path';
import { createHash } from 'node:crypto';
const root = fileURLToPath(new URL('../QuicNet.Shared/wwwroot/', import.meta.url));
const watchRoot = fileURLToPath(new URL('../QuicNet.Shared/Watch/wwwroot/', import.meta.url));
const port = Number(process.argv.find(a => a.startsWith('--port='))?.split('=')[1] ?? 5099);
const caps = { platform: 'desktop', capabilities: Object.fromEntries(['peers','chat','voice','vpn','speedtest','watch'].map(k => [k,{available:true}])) };
const people = ['Ana · Portátil', 'Mi ordenador', 'Dispositivo nuevo'].map((name,i) => ({id:`00000000-0000-0000-0000-00000000000${i+1}`,name,certHash:Buffer.alloc(32,i+1).toString('base64'),canonicalId:`aabbccdd0000000${i}`,isTrusted:i<2,isSaved:i<2,isQuicConnected:i===0,connectionState:i===0?'connected':'available',networkType:'Wan',addresses:['192.0.2.'+(i+1)],ports:[5000],endpoint:'192.0.2.1:5000',lastSeenSecondsAgo:1,unresponsiveSeconds:1,ping:i===0?24:null,hasCipher:true,automaticPermissions:[]}));
let state={node:{name:'Mi portátil · Vista de prueba',wanToken:'PREVIEW-INVITATION-NOT-A-REAL-TOKEN',torToken:'',listenerPort:5000,networkType:'Cone'},wan:{isStarted:true,running:true,enabled:true,desiredEnabled:true},tor:{running:false,enabled:false},discovery:{running:true,enabled:true,desiredEnabled:true,connectedRelays:2,relayCount:2},preferences:{WanEnabled:true},lan:{enabled:false,isActive:false,localIp:'10.203.24.8',subnetMask:'255.255.255.0',mtu:1420,autoAssign:true,peers:[],canStartWithoutElevation:true},peers:process.argv.includes('--empty')?[]:people,savedPeers:process.argv.includes('--empty')?[]:people.slice(0,2),discoveredPeers:[],activeInterrogations:[],pendingPetitions:[],activeChats:[],activeVoiceCalls:[],notifications:[],logs:[],quicTelemetry:[],chatMessages:[],disappearingMessages:{},apps:{chat:{active:0},voice:{active:0},lan:{active:0}},stun:{publicAddresses:[],localAddresses:[],ports:[],exactMappings:[],serverCount:0}};
let failFirstTorMode = process.argv.includes('--mode-fail-first-tor');
const vpnNoAdmin = process.argv.includes('--vpn-no-admin');
let rejectVpnSave = process.argv.includes('--vpn-reject-first');
let vpnConfiguration = {version:1,revision:1,mode:'simple',autoAssign:true,mtu:1420,enabled:false,autoConnect:true,peers:[],localAddress:'10.0.0.8',subnetMask:'255.255.255.0'};
Object.assign(state.lan, {active:false,desiredEnabled:false,localIp:'10.0.0.8',vpnRevision:1,activePeers:[],requiresAdmin:vpnNoAdmin,canStartWithoutElevation:!vpnNoAdmin});
people[0].vpnCanStart=true;
function vpnResponse() {
  const pascal = value => Array.isArray(value) ? value.map(pascal) : value && typeof value === 'object'
    ? Object.fromEntries(Object.entries(value).map(([key,item]) => [key[0].toUpperCase()+key.slice(1),pascal(item)])) : value;
  return {success:true,configuration:process.argv.includes('--vpn-legacy')?pascal(vpnConfiguration):vpnConfiguration,
    peerDefaults:people.slice(0,2).map(p=>({certHash:Buffer.from(p.certHash,'base64').toString('hex').toUpperCase(),autoAccept:false,accessibleDestinations:['10.0.0.8/32']})),path:'Configuración de prueba'};
}
const sockets=new Set();
const socialActions=[];
function socialSnapshot(peerId){
  const messages=state.chatMessages.filter(m=>m.peerId===peerId&&(!m.deliveryState||m.deliveryState==='sent'));
  const states=socialActions.filter(a=>a.peerId===peerId&&messages.some(m=>m.msgId===a.msgId));
  const pins=messages.filter(m=>states.filter(a=>a.msgId===m.msgId&&a.kind==='pin').sort((a,b)=>b.revision-a.revision||Number(b.active)-Number(a.active))[0]?.active);
  return {success:true,peerId,states,pins};
}
function send(socket, data){const payload=Buffer.from(JSON.stringify(data));const header=Buffer.alloc(payload.length<126?2:4);header[0]=0x81;if(payload.length<126)header[1]=payload.length;else{header[1]=126;header.writeUInt16BE(payload.length,2);}if(!socket.destroyed)socket.write(Buffer.concat([header,payload]));}
function broadcast(){for(const socket of sockets)send(socket,{type:'status_updated',data:state});}
async function html(file,base){let content=await readFile(resolve(base,file),'utf8');const matches=[...content.matchAll(/<!--\s*@include\s+([^\s]+)\s*-->/g)];for(const match of matches)content=content.replace(match[0],await html(match[1],base));return content;}
const server=http.createServer(async(req,res)=>{
 try{
  const url=new URL(req.url,'http://127.0.0.1');const path=url.pathname;
  let body={};if(req.method==='POST'){let raw='';for await(const part of req)raw+=part;body=JSON.parse(raw||'{}');}
  const json=(data,code=200)=>{res.writeHead(code,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify(data));};
  if(path==='/api/status')return json(state);
  if(path==='/api/capabilities')return json(caps);
  if(path==='/api/connection-mode') {
    if(!['wan','tor','both'].includes(body.mode)) return json({success:false,error:'Modo no válido'},400);
    const wan=body.mode!=='tor',tor=body.mode!=='wan';
    Object.assign(state.wan,{desiredEnabled:wan,enabled:wan});Object.assign(state.tor,{desiredEnabled:tor,enabled:tor,lastError:''});
    if(!wan)state.wan.isStarted=false;if(!tor){state.tor.isStarted=false;state.tor.running=false;}
    broadcast(); await new Promise(resolve=>setTimeout(resolve,200));
    if(tor&&failFirstTorMode){failFirstTorMode=false;state.tor.lastError='Tor no disponible en esta prueba';broadcast();return json({success:false,error:state.tor.lastError},503);}
    Object.assign(state.wan,{isStarted:wan,running:wan});Object.assign(state.tor,{isStarted:tor,running:tor,bootstrapProgress:tor?100:0});
    state.node.wanToken=wan?'PREVIEW-INVITATION-NOT-A-REAL-TOKEN':'';state.node.torToken=tor?'PREVIEW-TOR-INVITATION-NOT-A-REAL-TOKEN':'';
    broadcast();return json({success:true});
  }
  if(path==='/api/connect-token')return json({success:false,error:'Esta vista de prueba no conecta dispositivos reales.'},400);
  if(path==='/api/connect-peer'&&body.protocol==='chat'){
    if(body.peerId!==people[0].id)return json({success:false,error:'Este contacto de prueba está desconectado.'},409);
    setTimeout(()=>{state.activeChats=[{peerId:body.peerId}];broadcast();},500);
    return json({success:true});
  }
  if(path==='/api/chat/messages')return json({messages:state.chatMessages.filter(m=>m.peerId===url.searchParams.get('peerId')),nextBefore:null});
  if(path==='/api/chat/message'){const message=state.chatMessages.find(m=>m.peerId===url.searchParams.get('peerId')&&m.msgId===url.searchParams.get('msgId'));return message?json({success:true,message}):json({success:false,error:'Mensaje no disponible'},404);}
  if(path==='/api/chat/interactions'){
    if(req.method==='GET')return json(socialSnapshot(url.searchParams.get('peerId')));
    const message=state.chatMessages.find(m=>m.peerId===body.peerId&&m.msgId===body.msgId);
    if(!message||message.deliveryState&&message.deliveryState!=='sent')return json({success:false,error:'Mensaje no disponible'},409);
    const action={...body,mine:true,revision:Math.max(Date.now(),...socialActions.map(a=>a.revision+1))};
    const existing=socialActions.find(a=>a.peerId===body.peerId&&a.msgId===body.msgId&&a.kind===body.kind&&a.key===body.key&&a.mine);
    if(existing)Object.assign(existing,action);else socialActions.push(action);
    for(const socket of sockets)send(socket,{type:'chat_social_changed',data:{peerId:body.peerId}});
    return json(socialSnapshot(body.peerId));
  }
  if(path==='/api/chat/outbox'){
    let message=state.chatMessages.find(m=>m.peerId===body.peerId&&m.msgId===body.msgId);
    if(!message){message={...body,sender:'Tú',isMe:true,isConfirmed:false,time:'12:00',timestampMs:Date.now(),deliveryState:'pending'};state.chatMessages.push(message);}
    for(const socket of sockets)send(socket,{type:'chat_msg',data:message});
    json({success:true,message});
    if(state.activeChats.some(c=>c.peerId===body.peerId))setTimeout(()=>{
      if(!state.chatMessages.includes(message))return;
      message.deliveryState='sent';message.isConfirmed=true;for(const socket of sockets)send(socket,{type:'chat_msg',data:message});
    },250);
    return;
  }
  if(path==='/api/chat/message/delete'){
    const message=state.chatMessages.find(m=>m.peerId===body.peerId&&m.msgId===body.msgId);
    if(body.pendingOnly&&message?.deliveryState==='sent')return json({success:false,error:'El mensaje ya se ha enviado.'},409);
    state.chatMessages=state.chatMessages.filter(m=>m.peerId!==body.peerId||m.msgId!==body.msgId);
    for(const socket of sockets)send(socket,{type:'chat_message_deleted',data:{peerId:body.peerId,msgId:body.msgId}});
    return json({success:true});
  }
  if(path==='/api/chat-send'){
    if(!state.activeChats.some(c=>c.peerId===body.peerId))return json({success:false,error:'Contacto de prueba desconectado.'},409);
    const message={peerId:body.peerId,msgId:String(Date.now()),message:body.message,sender:'Tú',isMe:true,isConfirmed:true,time:'12:00'};
    state.chatMessages.push(message);for(const socket of sockets)send(socket,{type:'chat_msg',data:message});
    return json({success:true,msgId:message.msgId});
  }
  if(path==='/api/peer/trust'){const peer=state.peers.find(p=>p.id===body.peerId);if(peer){peer.isTrusted=body.trust;if(!body.trust)state.preferences.ReviewedUntrustedPeers=[...new Set([...(state.preferences.ReviewedUntrustedPeers||[]),peer.certHash])];}broadcast();return json({success:true});}
  if(path==='/api/chat/history')return json({messages:[],disappearingSeconds:0});
  if(path==='/api/voice/devices')return json({inputs:[{id:'default',name:'Micrófono predeterminado'}],outputs:[{id:'default',name:'Altavoces predeterminados'}],backend:'Vista de prueba'});
  if(path==='/api/voice/state')return json({activeCalls:[],isMuted:false,isDeafened:false,volume:1,backendName:'Vista de prueba'});
  if(path==='/api/voice/codec-settings')return json({bitrate:96000,requestedBitrate:96000});
  if(path==='/api/voice/screen-share/capabilities')return json({available:false,supported:false,reason:'Vista de prueba',codecs:[]});
  if(path==='/api/vpn/config'){
    if(req.method==='POST'){
      if(rejectVpnSave){rejectVpnSave=false;return json({success:false,error:'Fallo de escritura simulado. Vuelve a guardar.'},400);}
      if(body.expectedRevision!==vpnConfiguration.revision)return json({success:false,error:'La configuración ha cambiado.'},400);
      if(body.configuration.peers.some(p=>!/^[0-9A-F]{64}$/.test(p.certHash)))return json({success:false,error:'Identidad inválida.'},400);
      vpnConfiguration={...body.configuration,revision:vpnConfiguration.revision+1};
      Object.assign(state.lan,{desiredEnabled:vpnConfiguration.enabled,vpnRevision:vpnConfiguration.revision});broadcast();
    }
    return json(vpnResponse());
  }
  if(path==='/api/lan-toggle'){
    vpnConfiguration.enabled=body.enabled;vpnConfiguration.revision++;
    Object.assign(state.lan,{desiredEnabled:body.enabled,vpnRevision:vpnConfiguration.revision});broadcast();
    return json({success:true,active:false,desiredEnabled:body.enabled});
  }
  if(path==='/api/speedtest/status')return json({activeTests:[],history:[]});
  if(path==='/watch/api/status')return json({local:{name:'Mi portátil · Vista de prueba',token:state.node.wanToken},peers:[],room:null});
  if(path.startsWith('/api/')||path.startsWith('/watch/api/'))return json({success:true,items:[],messages:[],sources:[],codecs:[]});
  const watch=path.startsWith('/watch/');const base=watch&&path!=='/watch/diagnostics.js'?watchRoot:root;
  let file=watch?path.slice(7):path.slice(1);
  if(!file||['index.html','chat.html','call.html','voice.html','vpn.html','speedtest.html','watch.html','virtual-disk.html','settings.html'].includes(file))file='index.html';
  const absolute=resolve(base,file);if(!absolute.startsWith(resolve(base)+sep))return json({error:'Not found'},404);
  let data=extname(file)==='.html'?await html(file,base):await readFile(absolute);
  if(extname(file)==='.html'){data=data.replace('__QUICWATCH_CSRF__','preview').replace('</head>',`<script>window.__qp_csrf='preview';window.__qp_capabilities=${JSON.stringify(caps)};</script></head>`);}
  res.writeHead(200,{'Content-Type':({'.html':'text/html; charset=utf-8','.css':'text/css','.js':'application/javascript','.json':'application/json','.svg':'image/svg+xml'})[extname(file)]||'application/octet-stream','Cache-Control':'no-store'});res.end(data);
 }catch(error){res.writeHead(500,{'Content-Type':'text/plain'});res.end(error.message);}
});
server.on('upgrade',(req,socket)=>{
 const key=req.headers['sec-websocket-key'];if(!key)return socket.end();
 const accept=createHash('sha1').update(key+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
 socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: '+accept+'\r\n\r\n');
 if(req.url.startsWith('/watch/'))send(socket,{type:'status',data:{local:{name:state.node.name,token:state.node.wanToken},peers:[],room:null}});
 else{sockets.add(socket);send(socket,{type:'status_updated',data:state});}
 let pending=Buffer.alloc(0);
 socket.on('data',chunk=>{
  pending=Buffer.concat([pending,chunk]);
  while(pending.length>=2){
   const opcode=pending[0]&15, masked=!!(pending[1]&128);let length=pending[1]&127, offset=2;
   if(length===126){if(pending.length<4)return;length=pending.readUInt16BE(2);offset=4;}
   if(length===127){socket.destroy();return;}
   const maskOffset=offset;if(masked)offset+=4;if(pending.length<offset+length)return;
   const payload=Buffer.from(pending.subarray(offset,offset+length));
   if(masked)for(let i=0;i<length;i++)payload[i]^=pending[maskOffset+i%4];
   pending=pending.subarray(offset+length);
   if(opcode===8){socket.end(Buffer.from([0x88,0]));return;}
   if(opcode!==1)continue;
   try{const message=JSON.parse(payload.toString());
    if(message.type==='sync'||message.type==='get_status')send(socket,{type:'status_updated',data:state});
    if(message.type==='request')send(socket,{type:'response',id:message.id,success:message.command==='status.get',data:message.command==='status.get'?state:null,error:'Comando no disponible en esta vista de prueba.'});
   }catch{}
  }
 });
 socket.on('error',()=>sockets.delete(socket));socket.on('close',()=>sockets.delete(socket));
});
server.listen(port,'127.0.0.1',()=>console.log(`QuicNet fixture preview: http://127.0.0.1:${server.address().port}/`));
setInterval(broadcast,10000).unref();

// Stop fixture servers even if an assertion or browser route aborts the parent test.
if (process.send) process.once('disconnect', () => process.exit(0));
