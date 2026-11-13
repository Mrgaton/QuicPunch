(() => {
  const $ = id => document.getElementById(id);
  let peer = '', shares = [], selected = null, exploring = false, path = '', skip = 0, generation = 0, busy = false, poll = null, mountUrl = null;
  let cancelled = false;
  let navigating = false;
  let pathContext = null;
  const android = () => !!window.__qp_is_android || QP.state.status?.platform === 'android';
  const canWrite = () => !navigating && selected && (!peer || selected.writable) && !exploring;
  const node = (tag, text, cls) => { const n = document.createElement(tag); n.textContent = text; if (cls) n.className = cls; return n; };
  const status = (text, error = false) => { $('diskStatus').textContent = text; $('diskStatus').dataset.error = String(error); };
  const button = (text, fn) => { const b = node('button', text, 'btn sm'); b.type = 'button'; b.addEventListener('click', () => run(fn)); return b; };
  async function run(fn) { try { await fn(); } catch (e) { status(e.message, true); } }
  async function op(request, target = peer) {
    const result = await QP.api('/api/virtual-disk/operation', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId:target || null,request})});
    if (!result.success) { const error = Error(result.error || 'Operación denegada'); error.code = result.code; throw error; }
    return result;
  }
  function contacts(snapshot) {
    const devices = $('diskDevice'), recipients = $('diskRecipient');
    if (!devices || busy || navigating) return;
    const trusted = [...new Map([...(snapshot?.peers || []), ...(snapshot?.savedPeers || [])].filter(p => p.isTrusted !== false && p.certHash).map(p => [p.certHash,p])).values()];
    const old = devices.value, recipient = recipients.value;
    devices.replaceChildren(new Option('Este dispositivo', '')); recipients.replaceChildren(new Option('Selecciona un contacto…', ''));
    trusted.forEach(p => { devices.add(new Option(p.name || p.savedName || 'Contacto', p.id)); recipients.add(new Option(p.name || p.savedName || 'Contacto', p.certHash)); });
    if ([...devices.options].some(o => o.value === old)) devices.value = old;
    else if (old && peer === old) {
      peer = ''; generation++; selected = null; exploring = false; path = ''; shares = [];
      $('diskFiles').replaceChildren(node('p','El equipo seleccionado ya no está disponible. Elige otro equipo para continuar.','muted'));
      renderShares(); status('El equipo seleccionado ya no está disponible. Se muestran las carpetas de este dispositivo.');
      run(refresh);
    }
    if ([...recipients.options].some(o => o.value === recipient)) recipients.value = recipient;
    if (!trusted.length) recipients.replaceChildren(new Option('No hay contactos autorizados', ''));
    controls();
  }
  async function refresh(refreshFiles = false) {
    if (navigating) return;
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
    else status(exploring ? 'Exploración de administrador.' : !peer ? 'Puedes añadir archivos. El contacto tiene acceso de '+(selected.writable ? 'lectura y escritura.' : 'solo lectura.') : selected.writable ? 'Lectura y escritura.' : 'Solo lectura.');
    if (refreshFiles && selected && !exploring && skip <= 200) await list();
  }
  async function enter(url = location.href) {
    if (busy) return;
    const receivedShare = new URL(url, location.origin).searchParams.get('share');
    if (receivedShare) {
      peer = ''; $('diskDevice').value = ''; generation++; selected = null; exploring = false; path = '';
      await refresh(); selected = shares.find(s => s.id === receivedShare) || null;
      if (selected) { await list(); renderShares(); }
      return;
    }
    const requested = new URL(url, location.origin).searchParams.get('shareWith');
    const contact = [...(QP.state.status?.peers || []), ...(QP.state.status?.savedPeers || [])].find(p => p.id === requested && p.isTrusted !== false);
    if (contact?.certHash) {
      peer = ''; $('diskDevice').value = ''; $('diskRecipient').value = contact.certHash;
      generation++; selected = null; exploring = !android(); path = '';
      await refresh(); if (exploring) await list(); status('Elige archivos o una carpeta para compartir con '+(contact.name || 'este contacto')+'.');
    } else await refresh();
  }
  function renderShares() {
    $('diskShares').replaceChildren();
    if (!shares.length) $('diskShares').append(node('p', 'Sin carpetas compartidas.', 'muted'));
    shares.forEach(s => {
      const row = node('div', '', 'disk-share'); row.dataset.selected = String(selected?.id === s.id);
      const choose = button(s.name, async () => { if (busy) return; await navigate('', s, false); }); choose.classList.add('disk-name');
      choose.disabled = busy || navigating;
      choose.append(node('small', (s.writable ? 'Lectura y escritura' : 'Solo lectura') + (s.allowDelete ? ' · Permite borrar' : '') + (s.expiresUtc ? ' · Hasta ' + new Date(s.expiresUtc).toLocaleString() : '')));
      if (!peer) {
        const recipient = [...$('diskRecipient').options].find(o => o.value === s.recipient)?.textContent || 'Contacto autorizado';
        choose.append(node('small', 'Para: '+recipient), node('small', s.root || ''));
      }
      row.append(choose);
      if (!peer || s.administratorGrant) row.append(button('Revocar', async () => { if (busy || navigating) return; await op({operation:'revoke',shareId:s.id}); await refresh(); }));
      if (!android()) row.append(button('Montar unidad', async () => {
        const result = await QP.api('/api/virtual-disk/mount',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId:peer||null,shareId:s.id,native:!window.__qp_is_android})});
        if (result.success === false || !result.mountUrl) throw Error(result.error || 'No se pudo conectar la unidad. Puedes abrir los archivos desde esta vista.');
        mountUrl = result.mountUrl; $('diskMountPanel').hidden = false; $('diskMountPath').value = result.nativePath || result.mountUrl;
        $('diskMountStatus').textContent = result.code === 'Mounted' ? 'Unidad conectada en el explorador del sistema.' : 'Abre esta dirección con un cliente WebDAV local. Si Windows no conecta, utiliza la vista de archivos.';
      }));
      $('diskShares').append(row);
    });
  }
  function controls() {
    const locked = busy || navigating;
    // Polls must not replace an unfinished path, including after focus moves away.
    const context = JSON.stringify([peer, selected?.id, exploring, path, generation]);
    if (context !== pathContext) { $('diskPath').value = path; pathContext = context; }
    $('diskGrantPanel').hidden = !exploring;
    $('diskQuickPanel').hidden = !!peer;
    $('diskPickFolder').hidden = !android();
    $('diskUploadLabel').hidden = !canWrite() || locked;
    $('diskFolderUploadLabel').hidden = !canWrite() || locked || android();
    $('diskDropHint').hidden = !canWrite() || android();
    $('diskMkdir').hidden = !canWrite() || locked;
    const canBrowse = !!selected || exploring, hasRecipient = !!$('diskRecipient').value;
    $('diskUp').disabled = locked || !canBrowse || !path;
    $('diskGrant').disabled = !path;
    $('diskAllowDelete').disabled = $('diskPermission').value !== 'write';
    for (const id of ['diskDevice','diskRecipient','diskSend','diskReceive','diskPickFolder','diskExplore','diskRefresh','diskConnect','diskPath','diskGrant']) $(id).disabled = locked || (id === 'diskGrant' && !path);
    $('diskPath').disabled = locked || !canBrowse;
    $('diskGo').disabled = locked || !canBrowse;
    for (const id of ['diskSend','diskReceive','diskPickFolder']) $(id).disabled = locked || !hasRecipient;
    $('diskGrant').disabled = locked || !path || (!peer && !hasRecipient);
    $('diskRecipientHelp').textContent = hasRecipient ? '' : [...$('diskRecipient').options].some(option => option.value)
      ? 'Selecciona quién recibirá los archivos o podrá enviarte archivos.'
      : 'Añade un contacto y autorízalo en Contactos para compartir archivos o activar la recepción.';
    $('diskConnect').textContent = peer ? 'Conectar' : 'Ver mis carpetas';
    $('diskCancel').hidden = !busy; $('diskCancel').disabled = cancelled;
  }
  async function navigate(nextPath, nextShare = selected, nextExploring = exploring) {
    if (busy || navigating || (!nextShare && !nextExploring)) return;
    const previous = {path, selected, exploring, nodes: Array.from($('diskFiles').childNodes)};
    const epoch = ++generation;
    path = nextPath; selected = nextShare; exploring = nextExploring;
    navigating = true;
    $('diskFiles').replaceChildren(node('p', 'Cargando archivos…', 'muted'));
    controls();
    try { await list(); if (epoch === generation) renderShares(); }
    catch (error) {
      if (epoch === generation) {
        path = previous.path; selected = previous.selected; exploring = previous.exploring;
        $('diskFiles').replaceChildren(...previous.nodes); controls(); renderShares();
      }
      throw error;
    }
    finally { navigating = false; controls(); renderShares(); }
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
      if (entry.directory) { name.type = 'button'; name.addEventListener('click', () => run(() => navigate(entry.path))); }
      row.append(name);
      if (!entry.directory && !exploring) {
        if (android()) {
          const destinationPeer = peer, shareId = selected.id;
          row.append(button('Guardar en el móvil', async () => {
            status('Elige dónde guardar '+entry.name+'.');
            const reply = await QP.api('/api/virtual-disk/save',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId:destinationPeer||null,shareId,path:entry.path})});
            if (!reply.success) throw Error(reply.error || 'No se pudo guardar el archivo.');
            status('Archivo guardado y verificado: '+entry.name);
          }));
        } else {
          const link = node('a','Descargar','btn sm'); link.href = '/api/virtual-disk/download?' + new URLSearchParams({peerId:peer || '00000000-0000-0000-0000-000000000000',shareId:selected.id,path:entry.path}); link.download = entry.name; row.append(link);
        }
      }
      if (!exploring && selected?.allowDelete) row.append(button('Borrar', async () => {
        if (busy) return;
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
  async function upload(files) {
    files = Array.from(files || []);
    if (!files.length || !canWrite() || busy) return;
    const target = peer, shareId = selected.id, base = path, epoch = generation;
    busy = true; cancelled = false; controls(); $('diskProgress').hidden = false; $('diskProgress').value = 0;
    const total = files.reduce((sum, file) => sum + file.size, 0);
    let completedBytes = 0, completedFiles = 0, failed = 0;
    const directories = new Set();
    $('diskQueue').replaceChildren();
    const rows = files.map(file => { const row = node('li', file.webkitRelativePath || file.name); row.dataset.state = 'pending'; $('diskQueue').append(row); return row; });
    const checkCancelled = () => { if (cancelled) throw Error('Envío cancelado'); };
    try {
      for (let index = 0; index < files.length; index++) {
        if (cancelled) break;
        const file = files[index], row = rows[index];
        const relative = file.webkitRelativePath || file.name;
        let transfer = null;
        try {
          checkCancelled();
          if (file.size > 10737418240) throw Error('El límite por archivo es de 10 GiB.');
          const parts = relative.split('/');
          if (parts.some(part => !part || part === '.' || part === '..' || /[\\:\x00]/.test(part))) throw Error('Ruta de archivo no válida.');
          for (let count = 1; count < parts.length; count++) {
            const folder = [base, ...parts.slice(0,count)].filter(Boolean).join('/');
            if (directories.has(folder)) continue;
            checkCancelled();
            try { await op({operation:'mkdir',shareId,path:folder},target); }
            catch (e) { if (e.code !== 'Conflict') throw e; await op({operation:'list',shareId,path:folder},target); }
            directories.add(folder);
          }
          const destination = [base, relative].filter(Boolean).join('/');
          row.dataset.state = 'active'; row.textContent = relative+' · Enviando';
          try { transfer = (await op({operation:'begin-write',shareId,path:destination,length:file.size},target)).transferId; }
          catch (e) {
            if (e.code !== 'Conflict' || !confirm('Ya existe '+relative+'. ¿Reemplazarlo?')) throw e;
            const read = await op({operation:'open-read',shareId,path:destination},target);
            await op({operation:'close',transferId:read.transferId},target);
            checkCancelled();
            transfer = (await op({operation:'begin-write',shareId,path:destination,length:file.size,version:read.version},target)).transferId;
          }
          for (let offset = 0; offset < file.size; offset += 131072) {
            checkCancelled();
            const bytes = new Uint8Array(await file.slice(offset,offset+131072).arrayBuffer());
            let binary = ''; for (let i=0;i<bytes.length;i+=8192) binary += String.fromCharCode(...bytes.subarray(i,i+8192));
            checkCancelled();
            await op({operation:'write',transferId:transfer,offset,data:btoa(binary)},target);
            $('diskProgress').value = total ? (completedBytes+offset+bytes.length)/total : 0;
            $('diskTransfer').textContent = 'Enviando '+(index+1)+' de '+files.length+' · '+file.name+' · '+Math.round((offset+bytes.length)/file.size*100)+'%';
          }
          checkCancelled();
          await op({operation:'commit',transferId:transfer},target); transfer = null;
          completedFiles++; row.dataset.state = 'done'; row.textContent = relative+' · Guardado y verificado';
        } catch (e) {
          row.dataset.state = cancelled ? 'cancelled' : 'error'; row.textContent = relative+' · '+e.message;
          if (!cancelled) failed++;
        } finally {
          completedBytes += file.size;
          if (transfer) try { await op({operation:'close',transferId:transfer},target); } catch {}
        }
      }
      for (const row of rows) if (row.dataset.state === 'pending') { row.dataset.state = 'cancelled'; row.textContent += ' · Cancelado'; }
      $('diskTransfer').textContent = completedFiles+' de '+files.length+' archivos guardados y verificados'+(failed?' · '+failed+' con error':'')+(cancelled?' · Envío cancelado':'');
      status(cancelled ? 'Envío cancelado. Los archivos completados se conservan.' : failed ? 'Revisa los archivos con error antes de volver a enviarlos.' : 'Transferencia terminada. El contacto puede abrir los archivos desde su disco virtual.', failed > 0);
    } finally {
      busy = false; $('diskProgress').hidden = true;
      for (const id of ['diskUpload','diskFolderUpload','diskSendFiles']) $(id).value = '';
      controls();
      if (epoch === generation) await list();
    }
  }
  async function prepareExchange(operation) {
    if (busy) throw Error('Espera a que termine el envío actual.');
    const recipient = $('diskRecipient').value;
    if (!recipient) throw Error('Añade o autoriza un contacto antes de compartir.');
    busy = true; controls(); $('diskCancel').hidden = true;
    try {
      const reply = await op({operation,recipient,expiresUtc:new Date(Date.now()+86400000).toISOString()},'');
      peer = ''; $('diskDevice').value = ''; generation++; selected = reply.shares[0]; exploring = false; path = '';
      await refresh(); await list();
      status(operation === 'inbox' ? 'Recepción activada durante 24 horas. El contacto puede conectar y enviar archivos a esta carpeta.' : 'Carpeta de envío lista. Elige los archivos que quieres compartir.');
    } finally { busy = false; controls(); }
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
      if (page === 'virtual-disk') { run(() => enter(url)); poll = setInterval(() => { if (!busy && !document.hidden) run(() => refresh(true)); },5000); }
    });
    if (document.body.dataset.page === 'virtual-disk') { run(() => enter()); poll = setInterval(() => { if (!busy && !document.hidden) run(() => refresh(true)); },5000); }
    $('diskDevice').addEventListener('change', () => { peer = $('diskDevice').value; generation++; selected = null; exploring = false; path = ''; $('diskFiles').replaceChildren(node('p','Selecciona una carpeta compartida para ver sus archivos.','muted')); $('diskShares').replaceChildren(); controls(); if (!peer) run(refresh); else status('Pulsa Conectar. Si no eres administrador, el propietario debe aceptar la conexión y compartir una carpeta.'); });
    $('diskRecipient').addEventListener('change', controls);
    $('diskConnect').addEventListener('click', () => run(async () => { if (!peer) return refresh(); await QP.api('/api/virtual-disk/connect',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId:peer})}); status('Conectando; espera la aceptación del propietario si es necesaria.'); }));
    $('diskRefresh').addEventListener('click', () => run(async () => { await refresh(); if (selected || exploring) await list(); }));
    $('diskExplore').addEventListener('click', () => run(() => navigate('', null, true)));
    $('diskUp').addEventListener('click', () => run(() => navigate(exploring ? path.replace(/[\\/]+$/,'').replace(/[^\\/]+$/,'') : path.split('/').slice(0,-1).join('/'))));
    $('diskPathForm').addEventListener('submit', e => { e.preventDefault(); run(() => navigate($('diskPath').value)); });
    $('diskMore').addEventListener('click', () => run(() => list(true)));
    $('diskPermission').addEventListener('change', controls);
    $('diskGrantForm').addEventListener('submit', e => { e.preventDefault(); run(async () => {
      if (busy) return;
      const hours = Number($('diskExpiry').value);
      await op({operation:'grant',path,recipient:$('diskRecipient').value,writable:$('diskPermission').value==='write',allowDelete:$('diskAllowDelete').checked,expiresUtc:hours?new Date(Date.now()+hours*3600000).toISOString():null}); await refresh();
      status('Carpeta compartida. Selecciónala para acceder a sus archivos.');
    }); });
    for (const id of ['diskUpload','diskFolderUpload']) $(id).addEventListener('change', () => run(() => upload($(id).files)));
    for (const [buttonId, inputId] of [['diskUploadLabel','diskUpload'], ['diskFolderUploadLabel','diskFolderUpload']])
      $(buttonId).addEventListener('click', () => { if (canWrite() && !busy) $(inputId).click(); });
    $('diskCancel').addEventListener('click', () => { cancelled = true; $('diskCancel').disabled = true; $('diskTransfer').textContent = 'Cancelando después del bloque en curso…'; });
    $('diskSend').addEventListener('click', () => { if (busy) return; if (!$('diskRecipient').value) { status('Añade o autoriza un contacto antes de compartir.',true); return; } $('diskSendFiles').value = ''; $('diskSendFiles').click(); });
    // Preparing the share after selection keeps the native picker inside the user gesture.
    const sendInput = $('diskSendFiles');
    sendInput.addEventListener('change', async () => {
      const files = Array.from(sendInput.files || []);
      if (!files.length) return;
      await run(async () => { await prepareExchange('outbox'); await upload(files); });
    });
    $('diskReceive').addEventListener('click', () => run(() => prepareExchange('inbox')));
    $('diskPickFolder').addEventListener('click', () => run(async () => {
      if (busy) return;
      const reply = await QP.api('/api/virtual-disk/pick-folder',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
      if (!reply.success) throw Error(reply.error || 'No se seleccionó una carpeta.');
      generation++; selected = null; exploring = true; path = reply.nativePath;
      $('diskFiles').replaceChildren(node('p','Carpeta seleccionada. Elige los permisos y pulsa Compartir carpeta seleccionada.'));
      controls(); status('Android ha autorizado esta carpeta. Revisa con quién quieres compartirla.');
      $('diskGrantPanel').scrollIntoView({behavior:'smooth',block:'nearest'});
    }));
    const drop = $('diskFiles');
    drop.addEventListener('dragover', e => { if (canWrite() && !busy) { e.preventDefault(); drop.classList.add('disk-dragging'); } });
    drop.addEventListener('dragleave', () => drop.classList.remove('disk-dragging'));
    drop.addEventListener('drop', e => {
      e.preventDefault(); drop.classList.remove('disk-dragging');
      if ([...e.dataTransfer.items].some(item => item.webkitGetAsEntry?.()?.isDirectory)) { status('Para enviar una carpeta, utiliza «Enviar carpeta».',true); return; }
      run(() => upload(e.dataTransfer.files));
    });
    $('diskMkdir').addEventListener('click', () => run(async () => { const name = prompt('Nombre de la carpeta'); if (!name || /[/\\]/.test(name)) return; await op({operation:'mkdir',shareId:selected.id,path:path?path+'/'+name:name}); await list(); }));
    $('diskAuditRefresh').addEventListener('click', () => run(async () => { const reply = await op({operation:'audit'},''); const rows = (reply.audit || []).slice(-50).reverse().map(a => node('p',new Date(a.utc).toLocaleString()+' · '+a.actor+' · '+a.operation+' · '+a.path+' · '+(a.success?'Correcto':'Denegado'))); $('diskAudit').replaceChildren(...(rows.length ? rows : [node('p','Todavía no hay actividad de archivos en este dispositivo.','muted')])); }));
    $('diskUnmount').addEventListener('click', () => run(async () => { if (mountUrl) await QP.api('/api/virtual-disk/unmount',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({url:mountUrl})}); mountUrl = null; $('diskMountPanel').hidden = true; }));
  });
})();
