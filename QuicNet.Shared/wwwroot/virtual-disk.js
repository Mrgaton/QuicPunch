(() => {
  const $ = id => document.getElementById(id);
  let peer = '', shares = [], selected = null, exploring = false, path = '', skip = 0, generation = 0, busy = false, poll = null, mountUrl = null;
  const node = (tag, text, cls) => { const n = document.createElement(tag); n.textContent = text; if (cls) n.className = cls; return n; };
  const status = (text, error = false) => { $('diskStatus').textContent = text; $('diskStatus').dataset.error = String(error); };
  const button = (text, fn) => { const b = node('button', text, 'btn sm'); b.type = 'button'; b.addEventListener('click', () => run(fn)); return b; };
  async function run(fn) { try { await fn(); } catch (e) { status(e.message, true); } }
  async function op(request, target = peer) {
    const result = await QP.api('/api/virtual-disk/operation', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId:target || null,request})});
    if (!result.success) throw Error(result.error || 'Operación denegada');
    return result;
  }
  function contacts(snapshot) {
    const devices = $('diskDevice'), recipients = $('diskRecipient');
    if (!devices || busy) return;
    const trusted = [...new Map([...(snapshot?.peers || []), ...(snapshot?.savedPeers || [])].filter(p => p.isTrusted !== false && p.certHash).map(p => [p.certHash,p])).values()];
    const old = devices.value, recipient = recipients.value;
    devices.replaceChildren(new Option('Este dispositivo', '')); recipients.replaceChildren();
    trusted.forEach(p => { devices.add(new Option(p.name || p.savedName || 'Contacto', p.id)); recipients.add(new Option(p.name || p.savedName || 'Contacto', p.certHash)); });
    if ([...devices.options].some(o => o.value === old)) devices.value = old;
    if ([...recipients.options].some(o => o.value === recipient)) recipients.value = recipient;
  }
  async function refresh() {
    const epoch = generation, target = peer;
    const result = await op({operation:'shares'}, target);
    if (epoch !== generation) return;
    shares = result.shares || [];
    $('diskExplore').hidden = !result.canExplore;
    if (exploring && !result.canExplore) { exploring = false; path = ''; $('diskFiles').replaceChildren(); }
    if (selected) {
      selected = shares.find(s => s.id === selected.id) || null;
      if (!selected) { path = ''; $('diskFiles').replaceChildren(node('p','El acceso ha caducado o ha sido revocado.')); }
    }
    renderShares(); controls();
    if (!exploring && !selected) status(shares.length ? 'Selecciona una carpeta compartida.' : 'No hay carpetas compartidas. El propietario puede compartir una carpeta desde su dispositivo.');
    else status(exploring ? 'Exploración de administrador.' : selected.writable ? 'Lectura y escritura.' : 'Solo lectura.');
  }
  async function enter(url = location.href) {
    const requested = new URL(url, location.origin).searchParams.get('shareWith');
    const contact = [...(QP.state.status?.peers || []), ...(QP.state.status?.savedPeers || [])].find(p => p.id === requested && p.isTrusted !== false);
    if (contact?.certHash) {
      peer = ''; $('diskDevice').value = ''; $('diskRecipient').value = contact.certHash;
      generation++; selected = null; exploring = true; path = '';
      await refresh(); await list(); status('Selecciona la carpeta que quieres compartir con '+(contact.name || 'este contacto')+'.');
    } else await refresh();
  }
  function renderShares() {
    $('diskShares').replaceChildren();
    if (!shares.length) $('diskShares').append(node('p', 'Sin carpetas compartidas.', 'muted'));
    shares.forEach(s => {
      const row = node('div', '', 'disk-share'); row.dataset.selected = String(selected?.id === s.id);
      const choose = button(s.name, async () => { generation++; selected = s; exploring = false; path = ''; await list(); renderShares(); }); choose.classList.add('disk-name');
      choose.append(node('small', (s.writable ? 'Lectura y escritura' : 'Solo lectura') + (s.allowDelete ? ' · Permite borrar' : '') + (s.expiresUtc ? ' · Hasta ' + new Date(s.expiresUtc).toLocaleString() : '')));
      if (!peer) choose.append(node('small', s.root || ''));
      row.append(choose, button('Revocar', async () => { await op({operation:'revoke',shareId:s.id}); await refresh(); }));
      row.append(button('Montar unidad', async () => {
        const result = await QP.api('/api/virtual-disk/mount',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId:peer||null,shareId:s.id,native:!window.__qp_is_android})});
        mountUrl = result.mountUrl; $('diskMountPanel').hidden = false; $('diskMountPath').value = result.nativePath || result.mountUrl;
        $('diskMountStatus').textContent = result.code === 'Mounted' ? 'Unidad conectada en el explorador del sistema.' : 'Abre esta dirección con un cliente WebDAV local. Si Windows no conecta, utiliza la vista de archivos.';
      }));
      $('diskShares').append(row);
    });
  }
  function controls() {
    $('diskPath').value = path;
    $('diskGrantPanel').hidden = !exploring;
    $('diskRecipientField').hidden = !!peer;
    $('diskUploadLabel').hidden = exploring || !selected?.writable || busy;
    $('diskMkdir').hidden = exploring || !selected?.writable || busy;
    $('diskUp').disabled = !path;
    $('diskGrant').disabled = !path;
    $('diskAllowDelete').disabled = $('diskPermission').value !== 'write';
  }
  async function list(append = false) {
    if (!exploring && !selected) return;
    if (!append) skip = 0;
    const epoch = generation;
    const result = await op({operation:exploring?'explore':'list',shareId:selected?.id,path,skip});
    if (epoch !== generation) return;
    if (!append) $('diskFiles').replaceChildren();
    const entries = result.entries || [];
    for (const entry of entries) {
      const row = node('div', '', 'disk-file');
      const name = node(entry.directory?'button':'span', (entry.directory?'📁 ':'')+entry.name, 'disk-name');
      if (entry.directory) { name.type = 'button'; name.addEventListener('click', () => run(async () => { generation++; path = entry.path; await list(); })); }
      row.append(name);
      if (!entry.directory && !exploring) {
        const link = node('a','Descargar','btn sm'); link.href = '/api/virtual-disk/download?' + new URLSearchParams({peerId:peer || '00000000-0000-0000-0000-000000000000',shareId:selected.id,path:entry.path}); link.download = entry.name; row.append(link);
      }
      if (!exploring && selected?.allowDelete) row.append(button('Borrar', async () => {
        if (!confirm('¿Borrar '+entry.name+'? Esta acción no se puede deshacer.')) return;
        let version = null;
        if (!entry.directory) { const read = await op({operation:'open-read',shareId:selected.id,path:entry.path}); version = read.version; await op({operation:'close',transferId:read.transferId}); }
        await op({operation:'delete',shareId:selected.id,path:entry.path,version}); await list();
      }));
      $('diskFiles').append(row);
    }
    if (!entries.length && !append) $('diskFiles').append(node('p','La carpeta está vacía.','muted'));
    skip += entries.length; $('diskMore').hidden = !result.more; controls();
  }
  async function upload(file) {
    if (!file || !selected?.writable || exploring || busy) return;
    const target = peer, shareId = selected.id, destination = path ? path.replace(/\/$/,'')+'/'+file.name : file.name;
    busy = true; controls(); $('diskProgress').hidden = false; $('diskProgress').value = 0;
    let transfer;
    try {
      // Creates fail on an existing name; overwrites are an explicit choice with an expected content version.
      let version = null;
      try { transfer = (await op({operation:'begin-write',shareId,path:destination,length:file.size},target)).transferId; }
      catch (e) {
        if (!confirm(e.message+' ¿Intentar reemplazar el archivo existente?')) throw e;
        const read = await op({operation:'open-read',shareId,path:destination},target);
        version = read.version; await op({operation:'close',transferId:read.transferId},target);
        transfer = (await op({operation:'begin-write',shareId,path:destination,length:file.size,version},target)).transferId;
      }
      for (let offset = 0; offset < file.size; offset += 131072) {
        const bytes = new Uint8Array(await file.slice(offset,offset+131072).arrayBuffer());
        let binary = ''; for (let i=0;i<bytes.length;i+=8192) binary += String.fromCharCode(...bytes.subarray(i,i+8192));
        await op({operation:'write',transferId:transfer,offset,data:btoa(binary)},target);
        $('diskProgress').value = Math.min(1,(offset+bytes.length)/file.size); $('diskTransfer').textContent = 'Subiendo '+file.name+' · '+Math.round($('diskProgress').value*100)+'%';
      }
      await op({operation:'commit',transferId:transfer},target); transfer = null;
      $('diskTransfer').textContent = 'Archivo guardado y verificado: '+file.name;
      if (peer === target) await list();
    } finally {
      if (transfer) try { await op({operation:'close',transferId:transfer},target); } catch {}
      busy = false; $('diskProgress').hidden = true; $('diskUpload').value = ''; controls();
    }
  }
  document.addEventListener('DOMContentLoaded', () => {
    const nav = document.querySelector('.nav');
    if (nav) {
      const a = node('a','','nav-link'); a.href = '/virtual-disk.html'; a.dataset.page = 'virtual-disk';
      const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      for (const [name, value] of Object.entries({class:'icon',viewBox:'0 0 24 24',fill:'none',stroke:'currentColor','stroke-width':'1.7','stroke-linecap':'round','stroke-linejoin':'round','aria-hidden':'true',focusable:'false'})) icon.setAttribute(name, value);
      icon.innerHTML = '<path d="M5 4h14l3 10v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-4L5 4Z"/><path d="M2 14h20M6 17h4"/><circle cx="18" cy="17" r=".9" fill="currentColor" stroke="none"/>';
      a.append(icon,node('span','Disco virtual','nav-label')); nav.insertBefore(a,nav.querySelector('[data-page="settings"]'));
    }
    contacts(QP.state.status); controls();
    QP.on('status_updated', contacts);
    QP.on('view_changed', ({page,url}) => {
      clearInterval(poll); poll = null;
      if (page === 'virtual-disk') { run(() => enter(url)); poll = setInterval(() => { if (!busy && !document.hidden) run(refresh); },5000); }
    });
    if (document.body.dataset.page === 'virtual-disk') { run(() => enter()); poll = setInterval(() => { if (!busy && !document.hidden) run(refresh); },5000); }
    $('diskDevice').addEventListener('change', () => { peer = $('diskDevice').value; generation++; selected = null; exploring = false; path = ''; $('diskFiles').replaceChildren(); $('diskShares').replaceChildren(); controls(); if (!peer) run(refresh); else status('Pulsa Conectar. Si no eres administrador, el propietario debe aceptar la conexión y compartir una carpeta.'); });
    $('diskConnect').addEventListener('click', () => run(async () => { if (!peer) return refresh(); await QP.api('/api/virtual-disk/connect',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId:peer})}); status('Conectando; espera la aceptación del propietario si es necesaria.'); }));
    $('diskRefresh').addEventListener('click', () => run(async () => { await refresh(); if (selected || exploring) await list(); }));
    $('diskExplore').addEventListener('click', () => run(async () => { generation++; selected = null; exploring = true; path = ''; await list(); }));
    $('diskUp').addEventListener('click', () => run(async () => { generation++; path = exploring ? path.replace(/[\\/]+$/,'').replace(/[^\\/]+$/,'') : path.split('/').slice(0,-1).join('/'); await list(); }));
    $('diskPathForm').addEventListener('submit', e => { e.preventDefault(); generation++; path = $('diskPath').value; run(list); });
    $('diskMore').addEventListener('click', () => run(() => list(true)));
    $('diskPermission').addEventListener('change', controls);
    $('diskGrantForm').addEventListener('submit', e => { e.preventDefault(); run(async () => {
      const hours = Number($('diskExpiry').value);
      await op({operation:'grant',path,recipient:$('diskRecipient').value,writable:$('diskPermission').value==='write',allowDelete:$('diskAllowDelete').checked,expiresUtc:hours?new Date(Date.now()+hours*3600000).toISOString():null}); await refresh();
      status('Carpeta compartida. Selecciónala para acceder a sus archivos.');
    }); });
    $('diskUpload').addEventListener('change', () => run(() => upload($('diskUpload').files[0])));
    $('diskMkdir').addEventListener('click', () => run(async () => { const name = prompt('Nombre de la carpeta'); if (!name || /[/\\]/.test(name)) return; await op({operation:'mkdir',shareId:selected.id,path:path?path+'/'+name:name}); await list(); }));
    $('diskAuditRefresh').addEventListener('click', () => run(async () => { const reply = await op({operation:'audit'},''); $('diskAudit').replaceChildren(...(reply.audit || []).slice(-50).reverse().map(a => node('p',new Date(a.utc).toLocaleString()+' · '+a.actor+' · '+a.operation+' · '+a.path+' · '+(a.success?'Correcto':'Denegado')))); }));
    $('diskUnmount').addEventListener('click', () => run(async () => { if (mountUrl) await QP.api('/api/virtual-disk/unmount',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({url:mountUrl})}); mountUrl = null; $('diskMountPanel').hidden = true; }));
  });
})();
