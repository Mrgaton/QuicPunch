globalThis.QuicNetRemoteInput = (() => {
  const endpoint = '/api/voice/remote-input';
  const keys = new Set(['Backspace', 'Tab', 'Enter', 'Escape', 'PageUp', 'PageDown', 'End', 'Home',
    'ArrowLeft', 'ArrowUp', 'ArrowRight', 'ArrowDown', 'Insert', 'Delete']);
  const physicalKeys = new Set([...keys, 'Minus', 'Equal', 'BracketLeft', 'BracketRight', 'Semicolon', 'Quote', 'Backquote',
    'Backslash', 'Comma', 'Period', 'Slash', 'Space', 'IntlBackslash', 'CapsLock', 'NumLock', 'ScrollLock', 'PrintScreen',
    'ContextMenu', 'NumpadEnter', 'NumpadMultiply', 'NumpadSubtract', 'NumpadAdd', 'NumpadDecimal', 'NumpadDivide']);
  const isPhysical = code => physicalKeys.has(code) || /^(Key[A-Z]|Digit[0-9]|Numpad[0-9]|F(?:[1-9]|1[0-9]|2[0-4])|(?:Control|Shift|Alt|Meta)(?:Left|Right))$/.test(code);
  let session = null, revision = -1, driving = null, busy = false, capturePending = false;
  let banner, status, primary, stop, dialog, permission, detail;
  const android = /Android/i.test(navigator.userAgent);
  let clipboardPermission, clipboardConsent, clipboardToggle, clipboardSend, clipboardReceive;
  let lastClipboard = null, pendingClipboard = null, watchClipboard = false, nativeClipboard = false;
  let queue = [], sending = false, flushTimer = 0;
  const viewers = new Set();

  function post(action, data = {}) {
    return QP.api(endpoint, { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ action, ...data }) });
  }

  function update(data) {
    if (!Number.isInteger(data.revision) || data.revision <= revision) return;
    revision = data.revision;
    nativeClipboard = !!data.nativeClipboard;
    if (data.message) QP.toast(data.message, 'warning');
    const oldId = session?.id, wasClipboardActive = clipboardActive();
    session = data.session;
    if (oldId !== session?.id) {
      clearTimeout(flushTimer); flushTimer = 0; queue = []; driving?.pause(false);
    }
    if (oldId !== session?.id || !clipboardActive()) {
      lastClipboard = null; pendingClipboard = null; watchClipboard = false;
    }
    if (dialog.open && (session?.active || !session?.incoming || dialog.dataset.requestId !== session.id)) dialog.close();
    banner.hidden = !session || !!document.fullscreenElement && !session.incoming;
    if (session) {
      status.textContent = session.incoming
        ? session.active ? `${session.peerName} controla tu equipo${android ? '' : ' · Mantén Ctrl + Alt + F12 para detener'}`
          : `${session.peerName} solicita controlar tu ratón y teclado`
        : session.active ? `Control autorizado por ${session.peerName}` : `Esperando la confirmación de ${session.peerName}`;
      primary.hidden = !session.incoming || session.active;
      stop.textContent = session.active ? 'Detener control' : session.incoming ? 'Rechazar' : 'Cancelar solicitud';
    }
    renderClipboard();
    if ((!wasClipboardActive || oldId !== session?.id) && clipboardActive() && !nativeClipboard)
      void readClipboard(false);
  }

  const clipboardActive = () => !!session?.active && !!session.clipboardAllowed && !!session.clipboardEnabled;
  const clipboardCanToggle = () => !!session?.active && session.fullKeyboard && (session.incoming || session.clipboardAllowed);
  function renderClipboard() {
    clipboardToggle.hidden = !clipboardCanToggle();
    clipboardToggle.setAttribute('aria-pressed', String(!!(session?.incoming ? session.clipboardAllowed : session?.clipboardEnabled)));
    clipboardSend.hidden = !clipboardActive();
    clipboardReceive.hidden = !clipboardActive() || pendingClipboard == null;
    for (const viewer of viewers) viewer.render();
  }
  async function setClipboard() {
    if (!clipboardCanToggle()) return;
    const enabled = !(session.incoming ? session.clipboardAllowed : session.clipboardEnabled);
    await act('clipboard-toggle', { id: session.id, enabled });
  }
  async function sendClipboard(text, force = false) {
    if (!clipboardActive() || !text || text.length > 8192 || !force && text === lastClipboard) return;
    const id = session.id;
    const previous = lastClipboard;
    lastClipboard = text;
    try { await post('clipboard', { id, text }); }
    catch (error) {
      if (session?.id === id && lastClipboard === text) lastClipboard = previous;
      QP.toast(error.message, 'error');
    }
  }
  async function readClipboard(send, force = false) {
    if (!clipboardActive()) return;
    const id = session.id;
    try {
      const text = await navigator.clipboard.readText();
      if (session?.id !== id || !clipboardActive()) return;
      watchClipboard = true;
      if (send) await sendClipboard(text, force);
      else lastClipboard = text;
    } catch {
      watchClipboard = false;
      if (send) QP.toast('Pega el texto en el visor para compartirlo con el contacto.', 'warning');
    }
  }
  function sendCurrentClipboard() {
    if (!clipboardActive()) return;
    if (nativeClipboard) void act('clipboard-send-local', { id: session.id });
    else void readClipboard(true, true);
  }
  async function receiveClipboard() {
    const text = pendingClipboard, id = session?.id;
    if (!clipboardActive() || text == null) return;
    try {
      await navigator.clipboard.writeText(text);
      if (session?.id !== id || pendingClipboard !== text) return;
      pendingClipboard = null;
      renderClipboard();
    } catch { renderClipboard(); }
  }

  async function act(action, data) {
    if (busy) return;
    busy = true;
    try { update(await post(action, data)); }
    catch (error) { QP.toast(error.message, 'error'); }
    finally { busy = false; }
  }

  function endOutgoing() {
    if (session && !session.incoming) {
      const id = session.id;
      clearTimeout(flushTimer); flushTimer = 0; queue = [];
      driving?.pause(false);
      void post('stop', { id }).then(update).catch(error => QP.toast(error.message, 'error'));
    }
  }

  function enqueue(input) {
    if (!session?.active || session.incoming) return;
    if (input.kind === 'move' && queue.at(-1)?.kind === 'move') queue[queue.length - 1] = input;
    else queue.push(input);
    if (queue.length > 64) { endOutgoing(); return; }
    if (!flushTimer && !sending) flushTimer = setTimeout(flush, 24);
  }

  async function flush() {
    flushTimer = 0;
    if (!session?.active || session.incoming || !queue.length) return;
    const id = session.id;
    sending = true;
    try {
      while (session?.id === id && queue.length)
        await post('input', { id, inputs: queue.splice(0, 16) });
    } catch (error) {
      if (session?.id === id) {
        QP.toast(error.message, 'error');
        endOutgoing();
      }
    } finally {
      sending = false;
      if (queue.length && !flushTimer) flushTimer = setTimeout(flush, 24);
    }
  }

  function attach(entry, body, actions, openFullscreen, frameGeometry) {
    if (entry.isLocal) return null;
    const control = QP.el('button', 'btn ghost sm screen-input-btn');
    const stopControl = QP.el('button', 'btn danger sm screen-input-btn', 'Detener control');
    const keyboard = QP.el('button', 'btn ghost sm screen-input-btn', 'Teclado');
    const fullKeyboard = QP.el('button', 'btn ghost sm screen-input-btn', 'Teclado completo');
    fullKeyboard.title = 'Enviar atajos · Ctrl + Alt + Mayús + Escape para salir del modo';
    const rightClick = QP.el('button', 'btn ghost sm screen-input-btn screen-input-touch', 'Clic derecho');
    const monitors = QP.el('select', 'screen-input-monitors');
    monitors.setAttribute('aria-label', 'Pantalla remota');
    const clipToggle = QP.el('button', 'btn ghost sm screen-input-btn', 'Portapapeles');
    const clipSend = QP.el('button', 'btn ghost sm screen-input-btn', 'Enviar texto copiado');
    const clipReceive = QP.el('button', 'btn ghost sm screen-input-btn', 'Copiar texto recibido');
    const field = QP.el('input', 'screen-input-field');
    field.type = 'text'; field.autocomplete = 'off'; field.autocapitalize = 'off'; field.spellcheck = false;
    field.setAttribute('aria-label', 'Escribir en el equipo remoto');
    field.placeholder = 'Escribir en el equipo remoto';
    for (const button of [control, stopControl, keyboard, fullKeyboard, rightClick, clipToggle, clipSend, clipReceive]) button.type = 'button';
    actions.append(control, keyboard, fullKeyboard, rightClick, monitors, clipToggle, clipSend, clipReceive, stopControl, field);
    let pointer = null, secondary = false, showKeyboard = false, captureKeys = false, monitor = null, monitorList = null, monitorSignature = '';
    const heldKeys = new Set();

    const viewer = {
      get driving() { return driving === viewer; },
      get monitor() { return monitor; },
      capturesKeyboard(event) {
        if (!viewer.driving || !captureKeys || event.target !== body) return false;
        if (event.key === 'Escape' && event.ctrlKey && event.altKey && event.shiftKey) {
          event.preventDefault(); event.stopImmediatePropagation();
          stopKeyboard();
        }
        return true;
      },
      render() {
        const ours = session?.peerId === entry.peerId && !session.incoming;
        const approved = ours && session.active;
        control.hidden = entry.kind === 'camera' || !!session && !ours;
        control.disabled = ours && !session.active;
        control.textContent = viewer.driving ? 'Vista y zoom' : approved ? 'Controlar' : ours ? 'Esperando permiso…' : 'Solicitar control';
        keyboard.hidden = !viewer.driving || session?.keyboardAvailable === false;
        fullKeyboard.hidden = keyboard.hidden || !session?.fullKeyboard;
        fullKeyboard.setAttribute('aria-pressed', String(captureKeys));
        rightClick.hidden = !viewer.driving;
        rightClick.setAttribute('aria-pressed', String(secondary));
        stopControl.hidden = !ours;
        stopControl.textContent = approved ? 'Detener control' : 'Cancelar solicitud';
        field.hidden = !viewer.driving || !showKeyboard;
        const nextMonitors = approved ? session.monitors ?? null : null;
        const signature = JSON.stringify(nextMonitors);
        if (monitorSignature !== signature) {
          monitorSignature = signature;
          monitorList = nextMonitors;
          const wasCropped = monitor != null;
          monitor = null;
          monitors.replaceChildren(new Option('Todas las pantallas', ''), ...(monitorList || []).map((item, index) => new Option(item.name, String(index))));
          if (wasCropped) entry.viewer.reset();
        }
        monitors.hidden = !monitorList || monitorList.length < 2;
        clipToggle.hidden = !approved || !clipboardCanToggle();
        clipToggle.setAttribute('aria-pressed', String(!!session?.clipboardEnabled));
        clipSend.hidden = !approved || !clipboardActive();
        clipReceive.hidden = !approved || !clipboardActive() || pendingClipboard == null;
        entry.cardEl.classList.toggle('is-driving-screen', viewer.driving);
      },
      pause(release = true) {
        if (!viewer.driving) return false;
        if (release) enqueue({ kind: 'release' });
        stopKeyboard(false);
        driving = null;
        if (pointer && body.hasPointerCapture(pointer.id)) body.releasePointerCapture(pointer.id);
        pointer = null; secondary = false; showKeyboard = false; field.value = '';
        viewer.render();
        return true;
      },
      destroy() {
        viewer.pause();
        viewers.delete(viewer);
      }
    };

    control.addEventListener('click', async () => {
      if (viewer.pause()) return;
      if (!session) { await act('request', { peerId: entry.peerId }); return; }
      if (session.peerId !== entry.peerId || !session.active || session.incoming) return;
      if (!entry.loadingEl.classList.contains('hidden')) {
        QP.toast('Espera a que se vea la pantalla para empezar a controlarla.', 'warning');
        return;
      }
      if (!entry.cardEl.classList.contains('is-tile-fullscreen')) await openFullscreen();
      if (!session?.active || session.incoming || session.peerId !== entry.peerId ||
          !entry.cardEl.classList.contains('is-tile-fullscreen')) return;
      driving = viewer;
      viewer.render();
      body.focus({ preventScroll: true });
    });
    stopControl.addEventListener('click', endOutgoing);
    keyboard.addEventListener('click', () => {
      stopKeyboard();
      showKeyboard = !showKeyboard;
      viewer.render();
      (showKeyboard ? field : body).focus({ preventScroll: true });
    });
    fullKeyboard.addEventListener('click', async () => {
      if (captureKeys) { stopKeyboard(); return; }
      showKeyboard = false;
      captureKeys = true;
      viewer.render();
      body.focus({ preventScroll: true });
      try {
        await navigator.keyboard?.lock?.();
        if (!captureKeys) navigator.keyboard?.unlock?.();
      } catch { /* Forward the keys delivered to the focused viewer. */ }
    });
    monitors.addEventListener('change', () => {
      if (pointer) { enqueue({ kind: 'release' }); pointer = null; }
      monitor = monitors.value === '' ? null : monitorList?.[Number(monitors.value)] ?? null;
      entry.viewer.reset();
    });
    clipToggle.addEventListener('click', setClipboard);
    clipSend.addEventListener('click', sendCurrentClipboard);
    clipReceive.addEventListener('click', receiveClipboard);
    rightClick.addEventListener('click', () => { secondary = !secondary; viewer.render(); });

    function point(event, clamp) {
      const { rect, clip } = frameGeometry();
      if (rect.width <= 0 || rect.height <= 0) return null;
      const left = Math.max(clip.left, rect.left), right = Math.min(clip.right, rect.right);
      const top = Math.max(clip.top, rect.top), bottom = Math.min(clip.bottom, rect.bottom);
      if (!clamp && (event.clientX < left || event.clientX > right || event.clientY < top || event.clientY > bottom)) return null;
      const x = (Math.max(left, Math.min(right, event.clientX)) - rect.left) / rect.width;
      const y = (Math.max(top, Math.min(bottom, event.clientY)) - rect.top) / rect.height;
      return { x: Math.max(0, Math.min(1, x)), y: Math.max(0, Math.min(1, y)) };
    }
    function stopKeyboard(release = true) {
      if (!captureKeys) return;
      captureKeys = false;
      if (release) for (const key of heldKeys) enqueue({ kind: 'keyup', key });
      heldKeys.clear();
      navigator.keyboard?.unlock?.();
      viewer.render();
    }
    body.addEventListener('blur', event => { if (event.relatedTarget !== fullKeyboard) stopKeyboard(); });

    body.addEventListener('pointerdown', event => {
      if (!viewer.driving || pointer || event.button < 0 || event.button > 2) return;
      const position = point(event, false);
      if (!position) return;
      pointer = { id: event.pointerId, button: secondary && event.pointerType === 'touch' ? 2 : event.button };
      secondary = false;
      rightClick.setAttribute('aria-pressed', 'false');
      body.focus({ preventScroll: true });
      body.setPointerCapture(event.pointerId);
      enqueue({ kind: 'down', button: pointer.button, ...position });
    });
    body.addEventListener('pointermove', event => {
      if (!viewer.driving || pointer && pointer.id !== event.pointerId || !pointer && event.pointerType === 'touch') return;
      const position = point(event, !!pointer);
      if (position) enqueue({ kind: 'move', ...position });
    });
    body.addEventListener('pointerup', event => {
      if (pointer?.id !== event.pointerId) return;
      const position = point(event, true);
      enqueue(position ? { kind: 'up', button: pointer.button, ...position } : { kind: 'release' });
      pointer = null;
    });
    for (const name of ['lostpointercapture', 'pointercancel']) body.addEventListener(name, event => {
      if (pointer?.id === event.pointerId) { pointer = null; enqueue({ kind: 'release' }); }
    });
    body.addEventListener('contextmenu', event => { if (viewer.driving) event.preventDefault(); });
    body.addEventListener('wheel', event => {
      if (!viewer.driving || !event.deltaY) return;
      const position = point(event, false);
      if (!position) return;
      event.preventDefault();
      const delta = Math.sign(event.deltaY) * Math.max(1, Math.min(4, Math.ceil(Math.abs(event.deltaY) / 120))) * 120;
      enqueue({ kind: 'scroll', delta, ...position });
    }, { passive: false });
    body.addEventListener('keydown', event => {
      if (!viewer.driving || session?.keyboardAvailable === false || event.isComposing) return;
      if (captureKeys) {
        if (isPhysical(event.code)) { heldKeys.add(event.code); enqueue({ kind: 'keydown', key: event.code }); }
        event.preventDefault();
        return;
      }
      if (event.ctrlKey || event.altKey || event.metaKey || ['Tab', 'Escape'].includes(event.key)) return;
      if (keys.has(event.key)) enqueue({ kind: 'key', key: event.key });
      else if ([...event.key].length === 1) enqueue({ kind: 'text', text: event.key });
      else return;
      event.preventDefault();
    });
    body.addEventListener('keyup', event => {
      if (!captureKeys || !heldKeys.delete(event.code)) return;
      event.preventDefault();
      enqueue({ kind: 'keyup', key: event.code });
    });
    function commitText(value) {
      let text = '';
      for (const character of value) {
        if (character === '\n') { if (text) enqueue({ kind: 'text', text }); text = ''; enqueue({ kind: 'key', key: 'Enter' }); }
        else if (!/[\x00-\x1f\x7f-\x9f]/.test(character)) {
          if (text.length + character.length > 64) { enqueue({ kind: 'text', text }); text = ''; }
          text += character;
        }
      }
      if (text) enqueue({ kind: 'text', text });
      field.value = '';
    }
    field.addEventListener('input', event => { if (!event.isComposing) commitText(field.value); });
    field.addEventListener('compositionend', () => { if (field.value) commitText(field.value); });
    field.addEventListener('beforeinput', event => {
      if (event.inputType === 'deleteContentBackward' && !event.isComposing) {
        enqueue({ kind: 'key', key: 'Backspace' }); event.preventDefault();
      }
    });
    field.addEventListener('keydown', event => {
      if (event.key === 'Enter' && !event.isComposing) { enqueue({ kind: 'key', key: 'Enter' }); event.preventDefault(); }
    });
    viewers.add(viewer);
    viewer.render();
    return viewer;
  }

  document.addEventListener('DOMContentLoaded', () => {
    banner = document.getElementById('remoteInputBanner');
    status = document.getElementById('remoteInputStatus');
    primary = document.getElementById('remoteInputReview');
    stop = document.getElementById('remoteInputStop');
    dialog = document.getElementById('remoteInputConsent');
    permission = document.getElementById('remoteInputPermission');
    detail = document.getElementById('remoteInputDetail');
    clipboardPermission = document.getElementById('remoteInputClipboardPermission');
    clipboardConsent = document.getElementById('remoteInputClipboardConsent');
    clipboardToggle = document.getElementById('remoteInputClipboard');
    clipboardSend = document.getElementById('remoteInputSendClipboard');
    clipboardReceive = document.getElementById('remoteInputReceiveClipboard');
    const accept = document.getElementById('remoteInputAccept');
    primary.addEventListener('click', () => {
      if (!session?.incoming || session.active) return;
      dialog.dataset.requestId = session.id;
      detail.textContent = `${session.peerName} podrá usar el ratón y escribir en la pantalla compartida.`;
      permission.checked = false;
      clipboardPermission.checked = false;
      clipboardConsent.hidden = !session.fullKeyboard;
      accept.disabled = true;
      dialog.showModal();
    });
    permission.addEventListener('change', () => { accept.disabled = !permission.checked; });
    accept.addEventListener('click', () => {
      const approval = { id: dialog.dataset.requestId, confirmed: permission.checked };
      if (clipboardPermission.checked) approval.clipboard = true;
      void act('accept', approval);
    });
    document.getElementById('remoteInputClose').addEventListener('click', () => dialog.close());
    stop.addEventListener('click', () => {
      if (!session) return;
      if (!session.incoming) endOutgoing();
      else void act(session.active ? 'stop' : 'decline', { id: session.id });
    });
    QP.on('remote_input_state', update);
    QP.on('remote_input_clipboard', item => {
      if (!clipboardActive() || item.id !== session.id) return;
      pendingClipboard = lastClipboard = item.text;
      renderClipboard();
      void receiveClipboard();
    });
    clipboardToggle.addEventListener('click', setClipboard);
    clipboardSend.addEventListener('click', sendCurrentClipboard);
    clipboardReceive.addEventListener('click', receiveClipboard);
    document.addEventListener('paste', event => {
      if (!clipboardActive() || nativeClipboard || !(event.target?.closest('.screen-tile-body,.screen-input-field') || event.target === document.body)) return;
      const text = event.clipboardData?.getData('text/plain');
      if (text) void sendClipboard(text);
    });
    const refresh = () => QP.api(endpoint).then(update).catch(() => {});
    QP.on('ws_status', data => { if (data.connected) void refresh(); });
    QP.on('view_changed', data => { if (data?.page !== 'voice') endOutgoing(); });
    document.addEventListener('visibilitychange', () => { if (document.hidden) endOutgoing(); });
    document.addEventListener('fullscreenchange', () => {
      banner.hidden = !session || !!document.fullscreenElement && !session.incoming;
    });
    void refresh();
    setInterval(() => {
      if (session?.active && !session.incoming)
        void post('renew', { id: session.id }).catch(() => endOutgoing());
    }, 8000);
    setInterval(() => {
      if (watchClipboard && navigator.clipboard && document.hasFocus() && !document.hidden) void readClipboard(true);
    }, 1500);
  });

  async function requestWithCapture(peerId) {
    if (capturePending || busy) return;
    if (session) {
      QP.toast('Ya hay una solicitud o sesión de control remoto.', 'warning');
      return;
    }
    QP.toast('Solicitando control remoto. El contacto puede tener que autorizar la captura en su dispositivo.');
    capturePending = true;
    try { update(await post('request-with-capture', { peerId })); }
    catch (error) { QP.toast(error.message, 'error'); }
    finally { capturePending = false; }
  }

  return { attach, requestWithCapture, get isHosting() { return !!session?.incoming; } };
})();
