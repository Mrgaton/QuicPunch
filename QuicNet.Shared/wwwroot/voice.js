(() => {
  let voiceStatus = null;
  let voiceState = {
    isMuted: false,
    isDeafened: false,
    masterVolume: 1.0,
    isTestLoopback: false,
    currentInputDevice: 'default',
    currentOutputDevice: 'default',
    backendName: 'Native Audio',
    activeCalls: []
  };

  const callingPeers = new Set();
  const callOperations = new Map();
  const voiceSignalVersions = new Map();
  const localPeerVolumes = new Map();
  const localPeerMutes = new Map();
  const localOutboundMutes = new Map();
  const localScreenAllowed = new Map();
  const talkingPeers = new Set();
  let localSpeaking = false;
  const participantCards = new Map();
  let voiceDrawerTab = 'contacts';
  let settingsReturnToPicker = false;
  const decoderDiagnostics = new Map();
  const decoderWarnings = new Map();
  let codecPreviewTarget = null;
  function codecReceiverState(codecName, target = null) {
    const definition = screenCodecDefinitions.find(d => d.name === codecName);
    const bit = codecMaskBit(definition?.id || 1), profile = getScreenProbeProfile();
    return (voiceState.activeCalls || []).filter(call => call.screenShareAllowed !== false &&
      (!target || normalizePeerId(call.peerId) === normalizePeerId(target))).map(call => {
      const id = normalizePeerId(call.peerId), reported = remoteScreenCodecCapabilities.get(id);
      const mask = reported?.decoderCodecMask ?? call.screenDecoderCodecMask;
      const known = !!reported || call.screenCapabilitiesReceived || Number(mask) > 0;
      let supported = known ? !!(Number(mask) & bit) : null;
      const hardwareMask = reported?.decoderHardwarePreferenceMask ?? call.screenDecoderHardwarePreferenceMask;
      let hardware = hardwareMask == null ? null : !!(Number(hardwareMask) & bit);
      let reason = supported === null ? 'Todavía no ha enviado su compatibilidad.' : supported ? 'Códec anunciado por el receptor. El perfil exacto se comprueba al compartir.' : 'El receptor no anuncia soporte para este códec.';
      const diagnostic = decoderDiagnostics.get('remote:' + id);
      const family = diagnostic?.codec?.startsWith('avc1') ? 'h264' : /^(hvc1|hev1)/.test(diagnostic?.codec || '') ? 'hevc' : diagnostic?.codec?.startsWith('vp09') ? 'vp9' : diagnostic?.codec?.startsWith('av01') ? 'av1' : null;
      if (family === codecName && diagnostic.width === profile.width && diagnostic.height === profile.height) {
        supported = /no respondió|timeout/i.test(diagnostic.reason || '') ? null : diagnostic.supported === true;
        hardware = supported && diagnostic.mode === 'prefer-hardware' ? true : supported === null ? null : hardware;
        reason = diagnostic.reason || (supported ? 'Perfil aceptado.' : 'Perfil no compatible.');
      }
      return {id, name:call.peerName || resolvePeerName(id), supported, hardware, reason};
    });
  }
  function renderCodecCompatibility() {
    const select=document.getElementById('screenCodecSelect'),host=document.getElementById('screenCodecOptions');
    if(!select||!host)return;
    const focus=host.contains(document.activeElement)?document.activeElement.dataset.value:null;
    host.replaceChildren();
    const descriptions={h264:['H.264 / AVC','Compatibilidad amplia'],hevc:['H.265 / HEVC','Compresión eficiente'],vp9:['VP9','Alternativa abierta'],av1:['AV1','Más compresión, mayor exigencia']};
    for(const [codec,[name,description]] of Object.entries(descriptions)){
      const card=document.createElement('div');card.className='codec-choice';card.dataset.codec=codec;
      const title=document.createElement('strong');title.textContent=name;
      const note=document.createElement('span');note.className='codec-choice-description';note.textContent=description;
      const choices=document.createElement('div');choices.className='codec-processing';choices.setAttribute('role','group');choices.setAttribute('aria-label',name);
      for(const mode of ['gpu','cpu']){
        const value=codec+':'+mode,option=Array.from(select.options).find(o=>o.value===value);
        const button=document.createElement('button');button.type='button';button.className='codec-choice-button';button.dataset.value=value;
        button.disabled=!option||option.disabled;button.setAttribute('aria-pressed',String(select.value===value));
        button.setAttribute('aria-label',name+' · '+mode.toUpperCase());
        button.textContent=mode.toUpperCase()+(select.value===value?' ✓':'');
        button.title=button.disabled?'No hay un codificador disponible en este dispositivo':mode==='gpu'?'Codificar con la GPU de este dispositivo':'Codificar con la CPU de este dispositivo';
        button.addEventListener('click',()=>{select.value=value;select.dispatchEvent(new Event('change',{bubbles:true}));});
        choices.append(button);
      }
      card.classList.toggle('is-selected',select.value.startsWith(codec+':'));
      card.append(title,note,choices);host.append(card);
    }
    if(focus)host.querySelector(`[data-value="${focus}"]`)?.focus({preventScroll:true});
  }
  function initCodecPicker() {
    const host=document.getElementById('screenCodecOptions');if(!host)return;
    host.addEventListener('keydown',event=>{
      if(!['ArrowDown','ArrowUp','ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;
      const items=[...host.querySelectorAll('button:not(:disabled)')],index=items.indexOf(document.activeElement);if(index<0)return;
      event.preventDefault();const backwards=['ArrowUp','ArrowLeft'].includes(event.key);
      items[event.key==='Home'?0:event.key==='End'?items.length-1:(index+(backwards?-1:1)+items.length)%items.length]?.focus();
    });
  }
  function syncCodecParticipants() {
    const active = new Set((voiceState.activeCalls || []).map(c => normalizePeerId(c.peerId)));
    for (const id of remoteScreenCodecCapabilities.keys()) if (!active.has(id)) remoteScreenCodecCapabilities.delete(id);
    for (const [key, item] of decoderDiagnostics) if (!active.has(item.peerId)) decoderDiagnostics.delete(key);
    renderCodecCompatibility();
  }
  function decoderWarningText(item) {
    const name = resolvePeerName(item.peerId), profile = item.codec || '';
    const codec = /^hvc1|^hev1/.test(profile) ? 'H.265 / HEVC' : /^avc1/.test(profile) ? 'H.264 / AVC' :
      /^vp09/.test(profile) ? 'VP9' : /^av01/.test(profile) ? 'AV1' : profile || 'el códec elegido';
    const reception = /no respondió/i.test(item.reason || '') ? 'no ha confirmado que pueda reproducir' : 'no puede reproducir';
    return item.remote
      ? `${name} ${reception} este perfil de ${codec}. ${voiceState.isScreenSharing
          ? 'Puedes seguir emitiendo; los demás participantes no se ven afectados.'
          : 'Revisa los detalles de compatibilidad antes de volver a intentar compartir.'}`
      : `Tu dispositivo no puede reproducir ${codec} de ${name}. La transmisión del emisor continúa.`;
  }
  function updateDecoderWarningForStream(entry) {
    if (!entry?.loadingEl || entry.isLocal) return;
    const warning = decoderWarnings.get('local:' + normalizePeerId(entry.peerId));
    if (warning) {
      entry.loadingEl.textContent = decoderWarningText(warning);
      entry.loadingEl.classList.add('codec-incompatible'); entry.loadingEl.classList.remove('hidden');
    } else if (entry.loadingEl.classList.contains('codec-incompatible')) {
      entry.loadingEl.classList.remove('codec-incompatible'); entry.loadingEl.textContent = 'Conectando vídeo…';
    }
  }
  function renderDecoderWarnings() {
    const host = document.getElementById('screenCompatibilityNotice');
    if (!host) return;
    host.replaceChildren(); host.classList.toggle('hidden', decoderWarnings.size === 0);
    for (const item of decoderWarnings.values()) {
      const text = document.createElement('p'); text.textContent = decoderWarningText(item); host.append(text);
    }
    if (decoderWarnings.size) {
      const button = document.createElement('button'); button.className = 'btn sm'; button.type = 'button'; button.textContent = 'Ver detalles de compatibilidad';
      button.onclick = () => openScreenSettingsModal('video'); host.append(button);
    }
    for (const entry of activeScreenStreams.values()) updateDecoderWarningForStream(entry);
  }
  function clearDecoderWarnings(remote, peerId) {
    for (const [key, item] of decoderWarnings) if (item.remote === remote && (!peerId || normalizePeerId(item.peerId) === normalizePeerId(peerId))) decoderWarnings.delete(key);
    renderDecoderWarnings();
  }
  function decoderModeLabel(mode) {
    return mode === 'prefer-software' ? 'CPU solicitada · alternativa por software' :
      mode === 'prefer-hardware' ? 'GPU solicitada' : 'Selección automática';
  }
  function recordDecoderDiagnostic(peerId, diagnostic, remote = false) {
    peerId = normalizePeerId(peerId);
    const key = (remote ? 'remote:' : 'local:') + peerId;
    if (decoderDiagnostics.size >= 24 && !decoderDiagnostics.has(key)) decoderDiagnostics.delete(decoderDiagnostics.keys().next().value);
    const item = {...diagnostic, peerId, remote};
    decoderDiagnostics.set(key, item);
    if (item.supported) decoderWarnings.delete(key);
    else {
      if (!decoderWarnings.has(key)) QP.toast?.(decoderWarningText(item), 'warning');
      if (decoderWarnings.size >= 24 && !decoderWarnings.has(key)) decoderWarnings.delete(decoderWarnings.keys().next().value);
      decoderWarnings.set(key, item);
    }
    renderDecoderWarnings(); renderCodecCompatibility();
  }
  function updateCaptureEncodingSummary() {
    const summary = document.getElementById('screenCaptureEncodingSummary');
    renderCodecCompatibility();
    if (summary) summary.textContent = `${getCodecNameFromId(getCodecId(screenShareConfig.codec))} · ${screenShareConfig.resolution === 'native' ? 'Resolución original' : screenShareConfig.resolution + 'p'} · ${screenShareConfig.fps} FPS · ${screenShareConfig.bitrate / 1000000} Mbps`;
  }
  let screenDecoderAcceleration = 'no-preference';
  let decoderPreferenceRevision = 0;
  try {
    const saved = localStorage.getItem('qp_screen_decoder_acceleration');
    if (['no-preference', 'prefer-hardware', 'prefer-software'].includes(saved)) screenDecoderAcceleration = saved;
  } catch { }
  let fullscreenTile = null;
  let tileFullscreenMode = '';
  let tileFullscreenRevision = 0;

  // The full studio always lives outside the narrow contacts drawer.
  function mountVoiceSettings() {
    const settings = document.getElementById('screenShareSettingsModal');
    if (!settings) return;
    settings.classList.add('modal-backdrop');
    settings.setAttribute('role', 'dialog'); settings.setAttribute('aria-modal', 'true');
    const camera = document.getElementById('voiceCameraOptions');
    const actions = document.querySelector('#view-voice .voice-call-action');
    if (camera && actions && !actions.contains(camera)) actions.prepend(camera);
  }

  function setVoiceDrawer(open, tab = voiceDrawerTab) {
    const drawer = document.getElementById('voiceDrawer');
    const toggle = document.getElementById('voiceTrayToggle');
    if (!drawer || !toggle) return;
    if (!open && drawer.contains(document.activeElement)) toggle.focus({ preventScroll: true });
    voiceDrawerTab = tab;
    drawer.hidden = !open; drawer.inert = !open;
    toggle.setAttribute('aria-expanded', String(open));
    toggle.title = open ? 'Cerrar panel lateral' : 'Abrir contactos y ajustes';
    QP.setText('voiceTrayLabel', open ? 'Cerrar panel' : 'Contactos y ajustes');
    document.getElementById('voiceContactsTab')?.setAttribute('aria-pressed', 'true');
    document.getElementById('voiceDrawerContacts').hidden = false;
    document.getElementById('voiceInviteBtn')?.setAttribute('aria-expanded', String(open && tab === 'contacts'));
  }

  function initVoiceDrawer() {
    const toggle = document.getElementById('voiceTrayToggle');
    const drawer = document.getElementById('voiceDrawer');
    const navigation = document.querySelector('.nav-wrap');
    const sizeNavigation = () => document.documentElement.style.setProperty('--voice-nav-height', window.matchMedia('(max-width:680px)').matches ? `${navigation?.getBoundingClientRect().height || 0}px` : '0px');
    if (navigation) new ResizeObserver(sizeNavigation).observe(navigation);
    window.addEventListener('resize', sizeNavigation); sizeNavigation();
    toggle.addEventListener('click', () => {
      if (drawer.hidden && voiceDrawerTab === 'settings') openScreenSettingsModal('audio');
      else setVoiceDrawer(drawer.hidden);
    });
    document.getElementById('voiceDrawerClose').addEventListener('click', () => setVoiceDrawer(false));
    const room = document.getElementById('view-voice');
    let idleTimer;
    const revealMediaLabels = () => {
      room.classList.remove('is-media-idle');
      clearTimeout(idleTimer);
      idleTimer = setTimeout(() => {
        // Keep an open participant menu usable until the user dismisses it.
        if (!room.querySelector('.voice-person-menu[open]')) room.classList.add('is-media-idle');
      }, 3000);
    };
    for (const event of ['pointermove', 'pointerdown', 'keydown', 'focusin', 'wheel', 'scroll']) document.addEventListener(event, revealMediaLabels, { passive: true, capture: true });
    document.addEventListener('toggle', revealMediaLabels, true);
    revealMediaLabels();
    document.getElementById('voiceContactsTab').addEventListener('click', () => setVoiceDrawer(true, 'contacts'));
    document.getElementById('voiceSettingsTab').addEventListener('click', () => openScreenSettingsModal('audio'));
    document.querySelector('.voice-drawer-tabs').addEventListener('keydown', event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const ids = ['voiceContactsTab', 'voiceSettingsTab'];
      const next = event.key === 'Home' ? ids[0] : event.key === 'End' ? ids[1] : ids[event.target.id === ids[0] ? 1 : 0];
      document.getElementById(next)?.focus();
    });
    document.getElementById('view-voice').addEventListener('keydown', event => {
      if (event.key === 'Escape' && !event.defaultPrevented && !drawer.hidden && !document.querySelector('dialog[open], .voice-person-menu[open], .modal-backdrop:not(.hidden):not(#screenShareSettingsModal)')) {
        setVoiceDrawer(false); toggle.focus({ preventScroll: true }); event.preventDefault();
      }
    });
    mountVoiceSettings(document.body.dataset.page === 'voice');
  }

  function voiceIcon(name, crossed = false) {
    const ns = 'http://www.w3.org/2000/svg';
    const icon = document.createElementNS(ns, 'svg');
    icon.setAttribute('class', 'voice-icon'); icon.setAttribute('viewBox', '0 0 24 24'); icon.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS(ns, 'use'); use.setAttribute('href', '#voice-icon-' + name); icon.append(use);
    if (crossed) { const slash = document.createElementNS(ns, 'path'); slash.setAttribute('d', 'M3 3 21 21'); icon.append(slash); }
    return icon;
  }

  function updateGlobalCallMeta() {
    const bar = document.getElementById('globalActiveCallBar');
    if (!bar || bar.classList.contains('hidden')) return;
    const calls = activeCallsList();
    const starts = calls.map(call => Number(call.startedAtUnixMs || voiceState.activeCalls?.find(item => item.peerId === call.peerId)?.startedAtUnixMs)).filter(time => Number.isFinite(time) && time > 0);
    const duration = starts.length ? QP.formatDuration((Date.now() - Math.min(...starts)) / 1000) + ' · ' : '';
    QP.setText('globalCallMeta', `${duration}${voiceState.isMuted ? 'Micrófono silenciado' : 'Micrófono activo'}${voiceState.isDeafened ? ' · Audio silenciado' : ''}`);
  }

  function updateGlobalCallControl(id, icon, pressed, label, action) {
    const button = document.getElementById(id);
    if (!button) return;
    const signature = `${icon}:${pressed}`;
    if (button.dataset.iconState !== signature) {
      const text = QP.el('span', 'call-state-label', label);
      button.replaceChildren(voiceIcon(icon, pressed), text);
      button.dataset.iconState = signature;
    }
    button.className = `btn sm ${pressed ? 'danger' : 'ghost'}`;
    button.title = action;
    button.setAttribute('aria-label', action);
    button.setAttribute('aria-pressed', String(pressed));
  }

  function setVoiceControl(key, icon, label, description, state = '') {
    const button = document.getElementById(key === 'Settings' ? 'voiceScreenSettingsBtn' : `voicePanel${key}Btn`);
    if (!button) return;
    button.className = 'voice-ctrl-btn' + (state ? ' ' + state : '');
    button.title = description; button.setAttribute('aria-label', description);
    button.setAttribute('aria-pressed', String(!!state));
    const holder = document.getElementById(`voicePanel${key}Icon`);
    if (holder && holder.dataset.iconState !== icon + state) {
      holder.replaceChildren(voiceIcon(icon, state === 'is-muted')); holder.dataset.iconState = icon + state;
    }
    QP.setText(`voicePanel${key}Text`, label); QP.setText(`voicePanel${key}Sub`, description);
  }

  // Screen Sharing State & Multi-Stream Grid Manager
  let screenStream = null;
  let localVideoKind = 'screen';
  let videoStartPending = false;
  let videoSettingsPending = false;
  let cancelNativeSourcePicker = null;
  let videoOperation = 0;
  let videoStopPending = null;
  let videoCaptureCleanup = Promise.resolve();
  const localVideoStopRequests = new Set();
  let cameraPickerPending = false;
  let cameraSwitchPending = false;
  let cameraFacing = 'user';
  let cameraDeviceId = '';
  try {
    cameraDeviceId = localStorage.getItem('quicnet.cameraDeviceId') || '';
    if (localStorage.getItem('quicnet.cameraFacing') === 'environment') cameraFacing = 'environment';
  } catch { }
  let beforeCameraConfig = null;
  const androidCapture = /Android/i.test(navigator.userAgent);
  let pendingRemoteScreenPeerId = null;
  let activeScreenTargetPeerId = null;
  let screenCaptureInterval = null;
  let screenCaptureActive = false;
  let screenTransmissionReady = false;
  let screenCaptureVideo = null;
  let screenCaptureAnimId = null;

  let screenVideoReader = null;
  let screenFramePacer = null;
  const activeScreenStreams = new Map(); // peerId.toLowerCase() -> entry
  const remoteScreenCodecCapabilities = new Map();
  let screenOffscreenCanvas = null;
  let screenCodecCapabilityPromise = null;
  let localScreenEncoderCodecMask = 0;
  let localScreenDecoderCodecMask = 0;
  const localScreenCodecDetails = new Map();

  let screenShareConfig = {
    codec: 'h264',
    resolution: '1080',
    fps: 60,
    bitrate: 12000000,
    backend: '', encoder: '', encoderPreset: '', acceleration: 'gpu', source: '', preset: 'balanced',
    rateControl: 'cbr', quality: 23, keyFrameIntervalMs: 1000, bufferMs: 50
  };

  let currentIncomingCall = null;
  let nativeCapabilities = null;
  let nativeCapabilitiesPromise = null;
  let nativeSharing = false;
  let browserCaptureSelected = false;
  let browserSourceBounds = null;
  let nativeStartPending = false;
  let screenGeneration = Date.now();
  let fallbackInProgress = false;
  let remoteBrowserChangeInFlight = false;
  let browserStateTimer = null;

  function syncNativeScreenState(state) {
    if (!state.isNativeScreenSharing || !state.nativeSettings) return;
    const q = state.nativeSettings;
    activeScreenTargetPeerId = state.screenShareTargetPeerId || null;
    screenShareConfig = { ...screenShareConfig, codec: q.Codec, resolution: String(q.Height), fps: q.Fps,
      bitrate: q.Bitrate, encoder: q.Encoder, encoderPreset: q.EncoderPreset || '', source: q.Source, preset: q.Preset, rateControl: q.RateControl,
      quality: q.Quality, keyFrameIntervalMs: q.KeyFrameIntervalMs, bufferMs: q.BufferMs };
    try { localStorage.setItem('qp_screen_config', JSON.stringify(screenShareConfig)); } catch {}
    updateScreenSettingsSubtitle();
  }

  async function remoteScreenCommand(peerId, action, extra = {}) {
    return QP.api('/api/voice/screen-share/remote', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId, action, ...extra }) });
  }



  async function publishBrowserScreenState(){}



  async function openRemoteScreenSettings(peerId) {
    let overlay;
    try {
      QP.toast('Consultando los ajustes disponibles del emisor…');
      const reply = await remoteScreenCommand(peerId, 'capabilities');
      if (!reply.Success || !reply.Capabilities) throw new Error(reply.Error || 'El peer no admite control de transmisión.');
      const caps = reply.Capabilities;
      if (caps.Version !== 1 || !Array.isArray(caps.Codecs) || caps.Codecs.length > 4 ||
          caps.Codecs.some(c => !['h264','hevc','vp9','av1'].includes(c)) ||
          !Array.isArray(caps.Encoders) || caps.Encoders.length > 32 || caps.Encoders.some(e => typeof e !== 'string' || e.length > 64) ||
          !Array.isArray(caps.Heights) || caps.Heights.length > 16 || caps.Heights.some(h => !Number.isInteger(h) || h < 128 || h > 4320) ||
          !Array.isArray(caps.FrameRates) || caps.FrameRates.length > 16 || caps.FrameRates.some(f => !Number.isInteger(f) || f < 5 || f > 144) ||
          !Number.isInteger(caps.MinQuality) || !Number.isInteger(caps.MaxQuality) || caps.MinQuality < 0 || caps.MaxQuality > 51 || caps.MaxQuality < caps.MinQuality ||
          caps.MinBitrate < 500000 || caps.MaxBitrate > 100000000 || caps.MaxBitrate < caps.MinBitrate || !caps.Current)
        throw new Error('El emisor ha enviado opciones de transmisión inválidas.');
      if (!caps.Sharing) throw new Error('Este peer todavía no está compartiendo pantalla.');
      if (!caps.CanConfigure) throw new Error('El emisor debe darte permiso para cambiar su transmisión o marcarte como administrador.');
      if (!caps.Codecs.length) throw new Error('La captura no tiene opciones disponibles; comprueba que su interfaz está abierta.');
      document.getElementById('remoteScreenSettingsModal')?.remove();
      overlay = QP.el('div', 'modal-backdrop'); overlay.id = 'remoteScreenSettingsModal';
      const card = QP.el('div', 'modal-card remote-screen-modal'); card.setAttribute('role', 'dialog'); card.setAttribute('aria-modal', 'true');
      const head = QP.el('div', 'modal-head');
      head.append(QP.el('h2', '', `Transmisión de ${resolvePeerName(peerId)}`));
      const close = QP.button('Cerrar', '', () => overlay.remove()); head.append(close);
      const body = QP.el('div', 'modal-body');
      body.append(QP.el('p', 'muted', `Opciones enviadas por el emisor · ${caps.Backend === 'native' ? 'Captura nativa' : 'Captura web'}`));
      const controls = {};
      const field = (key, label, values, current) => {
        const container = QP.el('label', 'field', label);
        const select = document.createElement('select'); select.className = 'select';
        for (const [value, text] of values) select.append(new Option(text, String(value)));
        select.value = String(current); if (!select.value) select.selectedIndex = 0;
        controls[key] = select; container.append(select); body.append(container);
      };
      field('Codec', 'Códec', caps.Codecs.map(c => [c, c.toUpperCase()]), caps.Current.Codec);
      field('Height', 'Resolución máxima (16:9)', caps.Heights.map(h => [h, `${h}p`]), caps.Current.Height);
      field('Fps', 'Fotogramas por segundo', caps.FrameRates.map(f => [f, `${f} FPS`]), caps.Current.Fps);
      const bitrateField = QP.el('label', 'field', `Bitrate (Mbps, ${caps.MinBitrate / 1e6}–${caps.MaxBitrate / 1e6})`);
      const bitrate = document.createElement('input'); bitrate.type = 'number'; bitrate.className = 'input';
      bitrate.min = caps.MinBitrate / 1e6; bitrate.max = caps.MaxBitrate / 1e6; bitrate.step = '0.5';
      bitrate.value = caps.Current.Bitrate / 1e6; bitrateField.append(bitrate); body.append(bitrateField);
      if (caps.Backend === 'native') {
        field('Encoder', 'Encoder del equipo remoto', caps.Encoders.filter(e => e !== 'auto').map(e => [e, e]), caps.Current.Encoder);
        const presetNames = { speed: 'Velocidad', balanced: 'Equilibrada', quality: 'Calidad' };
        field('Preset', 'Prioridad de codificación', caps.Presets.map(p => [p, presetNames[p] || p]), caps.Current.Preset);
        field('RateControl', 'Control de bitrate', caps.RateControls.map(r => [r, r.toUpperCase()]), caps.Current.RateControl);
        field('Quality', 'Calidad VBR (menor valor, más calidad)', Array.from({ length: caps.MaxQuality - caps.MinQuality + 1 }, (_, i) => [i + caps.MinQuality, String(i + caps.MinQuality)]), caps.Current.Quality);
        const refreshEncoders = () => {
          const selected = controls.Encoder.value;
          controls.Encoder.replaceChildren();
          for (const encoder of caps.Encoders.filter(e => e !== 'auto' && encoderCodec(e) === controls.Codec.value))
            controls.Encoder.append(new Option(encoder, encoder));
          controls.Encoder.value = selected;
          if (!controls.Encoder.value) controls.Encoder.selectedIndex = 0;
          refreshQuality();
        };
        const refreshQuality = () => {
          const encoder = controls.Encoder.value;
          const supportsQuality = encoder.endsWith('_nvenc') || ['libx264', 'libx265'].includes(encoder);
          controls.Quality.disabled = controls.RateControl.value !== 'vbr' || !supportsQuality;
          controls.Quality.title = controls.Quality.disabled ? 'Este ajuste requiere VBR y NVENC, libx264 o libx265.' : 'Calidad objetivo de codificación variable.';
        };
        controls.Encoder.addEventListener('change', refreshQuality);
        controls.RateControl.addEventListener('change', refreshQuality);
        controls.Codec.addEventListener('change', refreshEncoders); refreshEncoders();
      }
      const numericFields = [
        ['KeyFrameIntervalMs', 'Intervalo de fotogramas clave (ms)', caps.MinKeyFrameIntervalMs, caps.MaxKeyFrameIntervalMs],
        ...(caps.Backend === 'native' ? [['BufferMs', 'Buffer de codificación (ms)', caps.MinBufferMs, caps.MaxBufferMs]] : [])
      ];
      for (const [key, label, min, max] of numericFields) {
        const container = QP.el('label', 'field', label), input = document.createElement('input');
        input.type = 'number'; input.className = 'input'; input.min = min; input.max = max;
        input.value = caps.Current[key]; controls[key] = input; container.append(input); body.append(container);
      }
      const error = QP.el('p', 'modal-error'); body.append(error);
      const foot = QP.el('div', 'modal-foot');
      const apply = QP.button('Aplicar en el emisor', 'accent', async () => {
        apply.disabled = true; error.textContent = '';
        try {
          const settings = { ...caps.Current };
          for (const [key, control] of Object.entries(controls)) settings[key] = ['Height','Fps','Quality','KeyFrameIntervalMs','BufferMs'].includes(key) ? Number(control.value) : control.value;
          settings.Width = settings.Height * 16 / 9; settings.Bitrate = Math.round(Number(bitrate.value) * 1e6);
          if (!Number.isFinite(settings.Bitrate) || settings.Bitrate < caps.MinBitrate || settings.Bitrate > caps.MaxBitrate) throw new Error('Bitrate fuera del rango disponible.');
          const result = await remoteScreenCommand(peerId, 'configure', { revision: caps.Revision, settings });
          if (!result.Success) throw new Error(result.Error || result.Code);
          const current = result.Capabilities.Current;
          QP.toast(`Emisor actualizado: ${current.Width}×${current.Height} · ${current.Fps} FPS · ${current.Codec.toUpperCase()} · ${current.Bitrate / 1e6} Mbps`, 'success');
          overlay.remove();
        } catch (e) { error.textContent = e.message; apply.disabled = false; }
      });
      foot.append(apply); card.append(head, body, foot); overlay.append(card); document.body.append(overlay);
    } catch (e) { overlay?.remove(); QP.toast(e.message, 'error'); }
  }


  function encoderHardware(item) {
    return item.Hardware ?? !String(item.Id).startsWith('lib');
  }
  function encoderCodec(id) {
    return ({ libx264: 'h264', libx265: 'hevc', 'libvpx-vp9': 'vp9', libsvtav1: 'av1' })[id] || id.split('_')[0];
  }
  function codecSelection() { return screenShareConfig.codec + ':' + (screenShareConfig.acceleration || 'gpu'); }
  function readCodecSelection(value) {
    const [codec, acceleration] = value.split(':');
    screenShareConfig.codec = codec;
    if (acceleration) screenShareConfig.acceleration = acceleration;
  }
  function chooseConcreteDefaults(){
  screenShareConfig.backend='ffmpeg';browserCaptureSelected=false;
  const encoders=nativeCapabilities?.nativeEncoders||[];
  let selected=encoders.find(e=>e.Available&&e.Id===screenShareConfig.encoder&&e.Codec===screenShareConfig.codec);
  if(!selected)selected=encoders.find(e=>e.Available&&e.Codec===screenShareConfig.codec&&encoderHardware(e)===(screenShareConfig.acceleration!=='cpu'));
  if(!selected&&!screenShareConfig.encoder)selected=encoders.find(e=>e.Available&&e.Codec===screenShareConfig.codec);
  if(selected){screenShareConfig.encoder=selected.Id;screenShareConfig.acceleration=encoderHardware(selected)?'gpu':'cpu';}
}

  async function ensureMicrophonePermission() {
    if (!/Android/i.test(navigator.userAgent || '') || !/Android/i.test(voiceState.backendName || '')) return;
    if (!navigator.mediaDevices?.getUserMedia) throw new Error('Este WebView no permite solicitar el micrófono. Revisa los permisos de Android.');
    try {
      const permissionStream = await navigator.mediaDevices.getUserMedia({ audio: true });
      permissionStream.getTracks().forEach(track => track.stop()); // Release before native AudioRecord opens it.
    } catch (error) {
      throw new Error(error.name === 'NotAllowedError' ? 'Permite el micrófono para llamar. Puedes activarlo en Ajustes de Android → QuicPunch → Permisos.' :
        'No se pudo preparar el micrófono: ' + error.message);
    }
  }

  function readNativeControls() {
    for (const [id, key] of [['screenEncoderSelect','encoder'],
      ['screenEncoderPresetSelect','encoderPreset'], ['screenPresetSelect','preset'], ['screenRateControlSelect','rateControl']]) {
      const el = document.getElementById(id);
      if (el) screenShareConfig[key] = el.value;
    }
    for (const [id,key] of [['screenResolutionSelect','resolution']]) {const el=document.getElementById(id);if(el?.value)screenShareConfig[key]=el.value;}
    for (const [id, key] of [['screenFpsSelect','fps'],['screenBitrateSelect','bitrate'],['screenGopInput','keyFrameIntervalMs'], ['screenBufferInput','bufferMs'], ['screenQualityInput','quality']]) {
      const el = document.getElementById(id);
      if (el) screenShareConfig[key] = Number(el.value);
    }
  }

  function renderFfmpegInstallation(installation) {
    const panel = document.getElementById('ffmpegInstallationGuide');
    if (!panel) return;
    panel.replaceChildren();
    panel.classList.toggle('hidden', !installation || installation.installed || installation.platform === 'android');
    if (!installation || installation.installed || installation.platform === 'android') return;
    const title = document.createElement('h3');
    title.textContent = 'Instalar FFmpeg · ' + installation.system;
    const description = document.createElement('p');
    description.textContent = installation.message;
    panel.append(title, description);
    const instructions = document.createElement('p');
    instructions.textContent = 'En el equipo que ejecuta QuicNet, abre una terminal y ejecuta una de estas opciones. Después pulsa «Ya lo he instalado · comprobar». La recepción utiliza las bibliotecas FFmpeg integradas.';
    panel.append(instructions);
    for (const option of installation.options || []) {
      const item = document.createElement('div'); item.className = 'ffmpeg-install-option';
      const heading = document.createElement('h4'); heading.textContent = option.title;
      const note = document.createElement('p'); note.className = 'inline-help'; note.textContent = option.note;
      item.append(heading, note);
      if (option.command) {
        const command = document.createElement('textarea');
        command.className = 'ffmpeg-install-command'; command.readOnly = true; command.rows = 3;
        command.value = option.command; command.setAttribute('aria-label', 'Comando: ' + option.title);
        const copy = document.createElement('button'); copy.type = 'button'; copy.className = 'btn sm'; copy.textContent = 'Copiar comando';
        copy.addEventListener('click', async () => {
          try {
            if (navigator.clipboard?.writeText) await navigator.clipboard.writeText(option.command);
            else { command.focus(); command.select(); if (!document.execCommand('copy')) throw new Error('copy'); }
            copy.textContent = 'Copiado';
          } catch {
            command.focus(); command.select(); copy.textContent = 'Seleccionado · pulsa Ctrl+C';
          }
        });
        item.append(command, copy);
      }
      // The backend supplies fixed documentation URLs. Never render commands as HTML.
      if (/^https:\/\//.test(option.url || '')) {
        const link = document.createElement('a'); link.href = option.url; link.target = '_blank';
        link.rel = 'noopener noreferrer'; link.textContent = 'Ver instrucciones y descargas';
        item.append(link);
      }
      panel.append(item);
    }
    const refresh = document.createElement('button'); refresh.type = 'button'; refresh.className = 'btn primary';
    refresh.textContent = 'Ya lo he instalado · comprobar';
    refresh.addEventListener('click', () => document.getElementById('refreshMediaCapabilitiesBtn')?.click());
    panel.append(refresh);
  }

  async function loadNativeCapabilities(refresh = false) {
    if (nativeCapabilitiesPromise) return nativeCapabilitiesPromise;
    if (nativeCapabilities && !refresh) return nativeCapabilities;
    const status = document.getElementById('mediaDetectionStatus');
    if (status) status.textContent = 'Consultando pantallas y probando codificadores del equipo…';
    nativeCapabilitiesPromise = QP.api('/api/voice/media-capabilities' + (refresh ? '?refresh=1' : ''))
      .then(data => {
        nativeCapabilities = data;
        renderFfmpegInstallation(data.ffmpeg);
        screenShareConfig.backend='ffmpeg';
        chooseConcreteDefaults();
        const help = document.getElementById('screenNativeStatus');
        if (help) help.textContent = data.nativeCaptureAvailable
          ? 'FFmpeg codifica con el componente seleccionado. GPU y CPU se prueban por separado; el perfil exacto se comprueba al iniciar.'
          : data.ffmpeg?.message || 'FFmpeg no disponible: ' + (data.nativeEncoders?.find(e => e.Reason)?.Reason || 'no hay codificadores operativos en este dispositivo.');
        if (status) status.textContent = `Equipo consultado · ${(data.displays || []).length} pantalla(s) · audio ${data.audioBackend || 'del dispositivo'}`;
        return data;
      }).catch(error => {
        nativeCapabilities = null; // A temporary backend failure must be retryable.
        if (status) status.textContent = 'No se pudo consultar el equipo. Puedes volver a detectar desde los ajustes de vídeo.';
        return { nativeEncoders: [], error: error.message };
      }).finally(() => { nativeCapabilitiesPromise = null; });
    return nativeCapabilitiesPromise;
  }

  function usesBrowserCapture(){return false;}

  function encoderPresets(encoder) {
    const reported = nativeCapabilities?.nativeEncoders?.find(item => item.Id === encoder)?.Presets;
    if (Array.isArray(reported)) return reported;
    if (/_nvenc$/.test(encoder)) return ['p1','p2','p3','p4','p5','p6','p7'];
    if (/_amf$/.test(encoder)) return ['speed','balanced','quality'];
    if (['libx264','libx265'].includes(encoder)) return ['ultrafast','superfast','veryfast','faster','fast','medium','slow'];
    if (encoder === 'libvpx-vp9') return ['8','7','6','5','4'];
    if (encoder === 'libsvtav1') return ['12','11','10','9','8','7','6'];
    return [];
  }

  function syncEncoderTuning() {
    const browser = usesBrowserCapture(), encoder = screenShareConfig.encoder || '';
    const presetField = document.getElementById('screenEncoderPresetField');
    if (presetField) presetField.hidden = browser;
    const presets = encoderPresets(encoder);
    const preset = document.getElementById('screenEncoderPresetSelect');
    if (!presets.includes(screenShareConfig.encoderPreset)) screenShareConfig.encoderPreset = '';
    if (preset) {
      preset.replaceChildren(new Option('Automático según la prioridad', ''));
      for (const value of presets) preset.append(new Option(value, value));
      preset.value = screenShareConfig.encoderPreset;
      preset.disabled = browser || !presets.length;
    }
    const priority = document.getElementById('screenPresetSelect');
    if (priority) priority.disabled = androidCapture || browser || !!screenShareConfig.encoderPreset;
    const help = document.getElementById('screenEncoderPresetHelp');
    const automatic = /_nvenc$/.test(encoder) ? {speed:'p1',balanced:'p3',quality:'p5'} :
      encoder === 'libx264' ? {speed:'ultrafast',balanced:'veryfast',quality:'fast'} :
      encoder === 'libx265' ? {speed:'ultrafast',balanced:'ultrafast',quality:'fast'} :
      encoder === 'libvpx-vp9' ? {speed:'8',balanced:'8',quality:'6'} :
      encoder === 'libsvtav1' ? {speed:'12',balanced:'10',quality:'8'} : {speed:'speed',balanced:'balanced',quality:'quality'};
    if (help) help.textContent = browser ? 'Los presets de FFmpeg no se aplican a FFmpeg nativo.' :
      `Preset efectivo: ${screenShareConfig.encoderPreset || automatic[screenShareConfig.preset] || 'automático'}. ` +
      (/_nvenc$/.test(encoder) ? 'P1 prioriza velocidad; P7 dedica más trabajo a la compresión.' :
       /_amf$/.test(encoder) ? 'Speed prioriza velocidad; quality dedica más trabajo a la compresión.' :
       ['libvpx-vp9','libsvtav1'].includes(encoder) ? 'Un número menor dedica más trabajo a la compresión.' : 'Los presets más lentos dedican más CPU a la compresión.') +
      ' Comprueba los FPS reales al compartir. Perfil de salida: ' + (screenShareConfig.codec === 'h264' ? 'H.264 High' : screenShareConfig.codec === 'hevc' ? 'HEVC Main' : screenShareConfig.codec.toUpperCase()) + ' · 8 bits · 4:2:0.';
    const summary=document.getElementById('screenTuningSummary');
    if(summary)summary.textContent=`${encoder || 'Sin codificador'} · ${androidCapture?'ajustes MediaCodec':'preset '+(screenShareConfig.encoderPreset || automatic[screenShareConfig.preset] || screenShareConfig.preset)} · ${screenShareConfig.fps} FPS · ${screenShareConfig.bitrate/1000000} Mbps · ${screenShareConfig.rateControl.toUpperCase()} · clave cada ${screenShareConfig.keyFrameIntervalMs} ms`;
    if(androidCapture){if(help)help.textContent='FFmpeg controla MediaCodec. Se aplican códec, resolución, FPS, bitrate, CBR/VBR e intervalo de fotogramas clave; los presets y CRF no están disponibles.';const quality=document.getElementById('screenQualityInput');if(quality)quality.disabled=true;const buffer=document.getElementById('screenBufferInput');if(buffer)buffer.disabled=true;}
    const decoder = document.getElementById('screenDecoderAccelerationSelect');
    if (decoder) { decoder.disabled=false; }
    const decoderHelp = document.getElementById('screenDecoderHelp');

  }

  async function changeDecoderPreference(){await QuicNetNativeVideo.applyPlayback();}

  function captureBounds() {
    if (usesBrowserCapture()) {
      const track = browserSourceBounds || screenStream?.getVideoTracks()[0]?.getSettings();
      return track?.width && track?.height ? { width: track.width, height: track.height, exact: true, fps: track.frameRate } : null;
    }
    // Settings no longer choose a source. Until the share action confirms one,
    // use display limits rather than binding the choices to the last window.
    if (!nativeSharing && !nativeStartPending) {
      const displays = nativeCapabilities?.displays || [];
      if (displays.length) return { width: Math.max(...displays.map(d => d.Width)), height: Math.max(...displays.map(d => d.Height)), exact: false };
      return null;
    }
    const source = nativeCapabilities?.sources?.find(item => item.Id === screenShareConfig.source);
    if (source?.Width && source?.Height) return { width: source.Width, height: source.Height, exact: true, fps: source.RefreshRate };
    // Output 0 is not necessarily the primary display. Bound presets by real
    // display modes without pretending to know the DXGI-to-Win32 association.
    const displays = nativeCapabilities?.displays || [];
    if (screenShareConfig.source === 'dxgi:0' && displays.length) {
      const largest = displays.reduce((a, b) => a.Width * a.Height >= b.Width * b.Height ? a : b);
      return { width: largest.Width, height: largest.Height, exact: false };
    }
    return null;
  }

  function syncMediaControls(){
  screenShareConfig.backend='ffmpeg';
  const encoders=(nativeCapabilities?.nativeEncoders||[]).filter(e=>e.Available&&e.Codec===screenShareConfig.codec&&encoderHardware(e)===(screenShareConfig.acceleration!=='cpu'));
  if(encoders.length&&!encoders.some(e=>e.Id===screenShareConfig.encoder)){screenShareConfig.encoder=encoders[0].Id;screenShareConfig.encoderPreset='';}
  const selector=document.getElementById('screenEncoderSelect');if(selector){selector.replaceChildren(...encoders.map(e=>new Option(e.Id,e.Id)));selector.value=screenShareConfig.encoder;}
  const field=document.getElementById('screenEncoderField');if(field)field.hidden=false;
  const bounds=captureBounds()||{width:1920,height:1080};
  const resolutions=document.getElementById('screenResolutionSelect');if(resolutions){const current=screenShareConfig.resolution;resolutions.replaceChildren(new Option('Original · '+bounds.width+'×'+bounds.height,'native'),...[360,480,720,1080,1440,2160,4320].filter(h=>h<=Math.max(bounds.width,bounds.height)).map(h=>new Option(h+'p',String(h))));resolutions.value=String(current);if(!resolutions.value)resolutions.value='native';}
  const fps=document.getElementById('screenFpsSelect');if(fps){fps.replaceChildren(...[15,24,30,60,90,120,144].filter(n=>!androidCapture||n<=60).map(n=>new Option(n+' FPS',String(n))));fps.value=String(screenShareConfig.fps);if(!fps.value)fps.value='30';}
  const status=document.getElementById('screenNativeStatus');if(status)status.textContent=androidCapture?'FFmpeg nativo · MediaProjection / Camera2 → codificador del sistema.':'FFmpeg nativo · '+encoders.length+' codificador(es) disponibles para la selección.';
  syncEncoderTuning();renderCodecMatrix();renderCodecCompatibility();
}

  function renderCodecMatrix(){
  const body=document.getElementById('mediaCodecMatrix');if(!body)return;body.replaceChildren();
  const hidden=document.getElementById('mediaBrowserColumn');if(hidden)hidden.hidden=true;
  for(const definition of screenCodecDefinitions){const row=document.createElement('tr');const list=(nativeCapabilities?.nativeEncoders||[]).filter(e=>e.Available&&e.Codec===definition.name);const receiver=QuicNetNativeVideo.snapshot.local.decoders?.find(d=>d.codecId===definition.id);
    for(const value of [definition.label,list.filter(encoderHardware).map(e=>e.Id).join(', ')||'No disponible',list.filter(e=>!encoderHardware(e)).map(e=>e.Id).join(', ')||'No disponible',receiver?.hardware?'Hardware verificado':receiver?.software?'Software verificado':receiver?.status==='unsupported'?'No compatible':'Pendiente']){const cell=document.createElement('td');cell.dataset.label=['Códec','Codificación por GPU','Codificación por CPU','Recepción nativa'][row.children.length];cell.textContent=value;row.append(cell);}body.append(row);}
}

  function nativeCanEncode(codec, acceleration = screenShareConfig.acceleration) {

    return (nativeCapabilities?.nativeEncoders || []).some(e => e.Available && e.Codec === codec &&
      encoderHardware(e) === (acceleration !== 'cpu'));
  }



  async function chooseNativeSource(targetPeerId = null) {
    codecPreviewTarget = targetPeerId;
    const modal = document.getElementById('screenNativeCaptureModal');
    const grid = document.getElementById('screenCaptureGallery');
    const search = document.getElementById('screenCaptureSearch');
    const preview = document.getElementById('screenCapturePreview');
    if (!modal || !grid || !search || !preview) throw new Error('No se pudo abrir el selector de captura.');
    let selected = screenShareConfig.source, sources = [], matches = [], category = 'all', previewTimer = null, closed = false, refreshVersion = 0;
    const thumbnails = new Map();
    const failedThumbnails = new Set();
    let cancelPreview = null, cancelThumbnail = null, activeThumbnail = null, thumbnailQueue = [], previewLoading = false, filterTimer = null;
    const status = document.getElementById('screenCaptureSelection');
    const start = document.getElementById('screenNativeCaptureStart');
    start.disabled = true;
    start.textContent = 'Compartir fuente seleccionada';
    document.getElementById('screenNativeCaptureWeb').classList.add('hidden');
    const loadPreviewImage = (image, source, completed) => {
      let settled = false;
      const finish = (success, timedOut = false) => {
        if (settled) return;
        settled = true; clearTimeout(deadline);
        image.onload = image.onerror = null;
        if (!success) image.removeAttribute('src');
        completed(success, timedOut);
      };
      const deadline = setTimeout(() => finish(false, true), 8000);
      image.onload = () => finish(image.naturalWidth > 0);
      image.onerror = () => finish(false);
      image.src = '/api/voice/screen-share/preview?source=' + encodeURIComponent(source) + '&t=' + Date.now();
      return () => {
        if (settled) return;
        settled = true; clearTimeout(deadline);
        image.onload = image.onerror = null; image.removeAttribute('src');
      };
    };
    const stopPreview = () => {
      clearTimeout(previewTimer); previewTimer = null;
      cancelPreview?.(); cancelPreview = null; previewLoading = false;
    };
    const stopThumbnail = (requeue = false) => {
      cancelThumbnail?.(); cancelThumbnail = null;
      if (requeue && activeThumbnail) thumbnailQueue.unshift(activeThumbnail);
      activeThumbnail = null;
    };
    const loadNextThumbnail = () => {
      if (closed || previewLoading || activeThumbnail || !thumbnailQueue.length) return;
      const item = activeThumbnail = thumbnailQueue.shift();
      item.detail.textContent = item.dimensions + ' · cargando vista previa…';
      cancelThumbnail = loadPreviewImage(item.image, item.id, success => {
        activeThumbnail = null; cancelThumbnail = null;
        if (!success) failedThumbnails.add(item.id);
        item.detail.textContent = item.dimensions + (success ? '' : ' · vista previa no disponible');
        loadNextThumbnail();
      });
    };
    const showPreview = () => {
      if (closed || !selected) return;
      stopPreview();
      const source = sources.find(item => item.Id === selected);
      previewLoading = true;
      if (status) status.textContent = (source?.Name || 'Fuente') + ' · cargando vista previa…';
      cancelPreview = loadPreviewImage(preview, selected, (success, timedOut) => {
        cancelPreview = null; previewLoading = false;
        if (status) status.textContent = success
          ? source.Name + ' · fuente completa, sin recortar'
          : timedOut ? 'La vista previa no ha respondido en 8 segundos. Pulsa Actualizar para reintentar o selecciona otra fuente.'
            : 'No se puede previsualizar esta fuente. Restaura la ventana o selecciona otra pantalla.';
        // Failed sources retry only through a deliberate selection or refresh.
        if (success && !closed) previewTimer = setTimeout(showPreview, 2500);
        loadNextThumbnail();
      });
    };
    const select = (id, focus = false) => {
      const previous = selected;
      selected = matches.some(source => source.Id === id) ? id : null;
      for (const button of grid.querySelectorAll('.capture-source-card')) {
        const active = button.dataset.sourceId === selected;
        button.classList.toggle('selected', active);
        button.setAttribute('aria-selected', String(active)); button.tabIndex = active ? 0 : -1;
        if (active && focus) { button.focus({preventScroll:true}); button.scrollIntoView({block:'nearest',inline:'nearest'}); }
      }
      const source = matches.find(item => item.Id === selected);
      if (status) status.textContent = source ? source.Name + ' · se compartirá completa, sin recortar' : 'Selecciona una pantalla o ventana.';
      start.disabled = !source;
      if (!source) {
        stopPreview(); preview.removeAttribute('src');
      } else if (previous !== selected || !preview.getAttribute('src')) {
        stopThumbnail(true); showPreview();
      }
    };
    const render = () => {
      clearTimeout(filterTimer); filterTimer = null;
      stopPreview(); stopThumbnail(); thumbnailQueue = [];
      preview.removeAttribute('src');
      for (const [id, kind] of [['screenCaptureAll','all'], ['screenCaptureDisplays','display'], ['screenCaptureWindows','window']]) {
        const button = document.getElementById(id);
        button.classList.toggle('active', category === kind); button.setAttribute('aria-pressed', String(category === kind));
      }
      grid.replaceChildren();
      const query = search.value.trim().toLocaleLowerCase();
      matches = sources.filter(source => (category === 'all' || (source.Kind || 'display') === category) &&
        source.Name.toLocaleLowerCase().includes(query));
      for (const source of matches) {
        const button = document.createElement('button');
        button.type = 'button'; button.className = 'capture-source-card' + (source.Id === selected ? ' selected' : '');
        button.dataset.sourceId = source.Id; button.setAttribute('role', 'option'); button.title = source.Name;
        let image = thumbnails.get(source.Id);
        if (!image) {
          image = document.createElement('img');
          image.alt = 'Vista previa de ' + source.Name;
          thumbnails.set(source.Id, image);
        }
        const name = document.createElement('strong'); name.textContent = source.Name;
        const detail = document.createElement('span');
        detail.textContent = (source.Width ? source.Width + '×' + source.Height : 'Tamaño al iniciar') +
          ((source.Kind || 'display') === 'window' ? ' · ventana' : ' · pantalla');
        const dimensions = detail.textContent;
        button.setAttribute('aria-label', source.Name + ' · ' + dimensions);
        const loaded = image.complete && image.naturalWidth > 0;
        detail.textContent = dimensions + (loaded ? '' : failedThumbnails.has(source.Id) ? ' · vista previa no disponible' : ' · vista previa pendiente');
        if (!loaded && !failedThumbnails.has(source.Id)) thumbnailQueue.push({ id: source.Id, image, detail, dimensions });
        button.append(image, name, detail);
        button.onclick = () => select(source.Id);
        grid.append(button);
      }
      select(matches.some(source => source.Id === selected) ? selected : matches[0]?.Id);
      loadNextThumbnail();
      document.getElementById('screenCaptureResults').textContent = matches.length === 1 ? '1 fuente' : matches.length + ' fuentes';
      if (!matches.length) grid.textContent = 'No hay fuentes que coincidan. Cambia la búsqueda o actualiza las ventanas.';
    };
    const refresh = async () => {
      const version = ++refreshVersion;
      if (status) status.textContent = 'Buscando pantallas y ventanas…';
      try {
        const data = await QP.api('/api/voice/screen-share/sources');
        if (closed || version !== refreshVersion) return;
        sources = data.sources || [];
        if (nativeCapabilities) nativeCapabilities.sources = sources;
        if (!sources.some(source => source.Id === selected)) selected = sources.find(source => source.Kind !== 'window')?.Id || sources[0]?.Id;
        render();
      } catch (error) { if (!closed && version === refreshVersion && status) status.textContent = 'No se pudieron consultar las fuentes: ' + error.message; }
    };
    updateCaptureEncodingSummary();
    modal.classList.remove('hidden');
    search.value = ''; search.oninput = () => {
      stopPreview(); stopThumbnail(); thumbnailQueue = [];
      clearTimeout(filterTimer); start.disabled = true;
      filterTimer = setTimeout(render, 180);
    };
    return new Promise(resolve => {
      const keydown = event => {
        if (closed || modal.classList.contains('hidden') || event.isComposing || !modal.contains(event.target)) return;
        if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); done('cancel'); return; }
        if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey && event.key.length > 1) return;
        if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.key)) {
          event.preventDefault(); event.stopPropagation();
          const buttons = [...grid.querySelectorAll('.capture-source-card')];
          const current = buttons.find(button => button.dataset.sourceId === selected);
          if (!current) return;
          const origin = current.getBoundingClientRect();
          const horizontal = event.key === 'ArrowLeft' || event.key === 'ArrowRight';
          const forward = event.key === 'ArrowRight' || event.key === 'ArrowDown';
          const candidates = buttons.filter(button => button !== current).map(button => ({button,rect:button.getBoundingClientRect()}))
            .filter(({rect}) => horizontal
              ? Math.abs(rect.top - origin.top) < 2 && (forward ? rect.left > origin.left : rect.left < origin.left)
              : forward ? rect.top > origin.top + 2 : rect.top < origin.top - 2)
            .sort((a,b) => horizontal
              ? Math.abs(a.rect.left-origin.left)-Math.abs(b.rect.left-origin.left)
              : Math.abs(a.rect.top-origin.top)-Math.abs(b.rect.top-origin.top) || Math.abs(a.rect.left-origin.left)-Math.abs(b.rect.left-origin.left));
          select(candidates[0]?.button.dataset.sourceId || selected, true); return;
        }
        if (event.key === 'Enter') {
          // Tabbed-to actions retain their normal Enter activation.
          const button = event.target.closest('button');
          if (button && !grid.contains(button) && button !== start) return;
          event.preventDefault(); event.stopPropagation();
          if (!start.disabled && selected) done(selected);
          return;
        }
        if (event.key.length === 1 && !event.target.closest('input,textarea,select,[contenteditable]')) {
          event.preventDefault(); event.stopPropagation(); search.focus({preventScroll:true});
          search.setRangeText(event.key, search.selectionStart ?? search.value.length, search.selectionEnd ?? search.value.length, 'end');
          search.dispatchEvent(new Event('input', {bubbles:true}));
        }
      };
      const done = choice => {
        if (closed) return;
        closed = true;
        cancelNativeSourcePicker = null;
        if (choice === 'cancel') settingsReturnToPicker = false;
        clearTimeout(filterTimer);
        stopPreview(); stopThumbnail(); thumbnailQueue = [];
        preview.removeAttribute('src');
        for (const image of thumbnails.values()) image.removeAttribute('src');
        thumbnails.clear(); grid.replaceChildren(); modal.classList.add('hidden');
        document.removeEventListener('keydown', keydown, true);
        search.oninput = null;
        for (const id of ['screenNativeCaptureStart', 'screenNativeCaptureWeb', 'screenNativeCaptureCancel', 'screenCaptureRefresh', 'screenCaptureAll', 'screenCaptureDisplays', 'screenCaptureWindows', 'screenCaptureEncodingBtn'])
          document.getElementById(id).onclick = null;
        resolve(choice);
      };
      cancelNativeSourcePicker = () => done('cancel');
      document.addEventListener('keydown', keydown, true);
      document.getElementById('screenNativeCaptureStart').onclick = () => { if (!start.disabled && selected) done(selected); };
      document.getElementById('screenNativeCaptureWeb').onclick = () => done('web');
      document.getElementById('screenCaptureEncodingBtn').onclick = () => { settingsReturnToPicker = true; modal.classList.add('hidden'); openScreenSettingsModal('video'); };
      document.getElementById('screenNativeCaptureCancel').onclick = () => done('cancel');
      document.getElementById('screenCaptureRefresh').onclick = () => {
        stopPreview(); stopThumbnail(); thumbnailQueue = [];
        failedThumbnails.clear(); refresh();
      };
      for (const [id, kind] of [['screenCaptureAll','all'], ['screenCaptureDisplays','display'], ['screenCaptureWindows','window']])
        document.getElementById(id).onclick = () => { category = kind; render(); };
      refresh();
    });
  }

  async function startNativeScreenShare(targetPeerId, reuseSource = false, cancelled = () => false) {
    if (!nativeCanEncode(screenShareConfig.codec)) {
      if (screenShareConfig.backend === 'ffmpeg') throw new Error('El formato y codificador elegidos no están disponibles. Revisa la tabla de compatibilidad.');
      return false;
    }
    nativeStartPending = true;
    try {
      const source = reuseSource ? screenShareConfig.source : await chooseNativeSource(targetPeerId);
      if (cancelled()) return true;
      if (source === 'cancel') return true;

      screenShareConfig.source = source;
      syncMediaControls();
      const profile = getScreenProbeProfile();
      const result = await QP.api('/api/voice/screen-share/native-start', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...screenShareConfig, width: profile.width, height: profile.height, targetPeerId })
      });
      const p = result.profile;
      if (cancelled()) { await stopScreenShare(); return true; }
      nativeSharing = true;
      voiceState.isScreenSharing = true;
      activeScreenTargetPeerId = targetPeerId;
      try { localStorage.setItem('qp_screen_config', JSON.stringify(screenShareConfig)); } catch { }
      createOrUpdateScreenTile('local', 'Tú · Pantalla', p.Width, p.Height, p.CodecId, p.Fps, true);
      renderVoice();
      updateEffectiveFpsStatus(profile.fps, p.Fps);
      QP.toast('Captura FFmpeg activa · ' + p.Encoder + ' · ' + p.CaptureMode, 'success');
      return true;
    } finally { nativeStartPending = false; }
  }


  function normalizePeerId(id) {
    if (!id) return '';
    return String(id).toLowerCase().trim();
  }

  function resolvePeerName(peerId) {
    if (!peerId) return 'Peer';
    const norm = normalizePeerId(peerId);
    if (norm === 'local' || norm === 'me') return 'You';

    const active = (voiceState.activeCalls || []).find(c => normalizePeerId(c.peerId) === norm);
    if (active && active.peerName) return active.peerName;

    const netCalls = (voiceStatus?.activeVoiceCalls || []).find(c => normalizePeerId(c.peerId) === norm);
    if (netCalls && netCalls.peerName) return netCalls.peerName;

    const trusted = (voiceStatus?.peers || []).find(p => normalizePeerId(p.id) === norm);
    if (trusted && trusted.name) return trusted.name;

    return 'Peer';
  }

  function parseGuidFromBytes(bytes, offset = 0) {
    if (!bytes || bytes.length < offset + 16) return '';
    const d1 = (bytes[offset + 3] << 24 | bytes[offset + 2] << 16 | bytes[offset + 1] << 8 | bytes[offset]) >>> 0;
    const d2 = (bytes[offset + 5] << 8 | bytes[offset + 4]) >>> 0;
    const d3 = (bytes[offset + 7] << 8 | bytes[offset + 6]) >>> 0;

    const p1 = d1.toString(16).padStart(8, '0');
    const p2 = d2.toString(16).padStart(4, '0');
    const p3 = d3.toString(16).padStart(4, '0');

    let p4 = '';
    for (let i = 8; i < 10; i++) p4 += bytes[offset + i].toString(16).padStart(2, '0');
    let p5 = '';
    for (let i = 10; i < 16; i++) p5 += bytes[offset + i].toString(16).padStart(2, '0');

    return `${p1}-${p2}-${p3}-${p4}-${p5}`.toLowerCase();
  }

  function showIncomingCall(petition) {
    currentIncomingCall = petition;
    const banner = document.getElementById('incomingCallBanner');
    if (!banner) return;
    const snapshot = voiceStatus || QP.state?.status;
    const peer = QP.identity.findPeer(petition.peerId, snapshot) || {id: petition.peerId, name: petition.peerName || 'Contacto'};
    QP.identity.mount(document.getElementById('incomingCallIdentity'), peer,
      {snapshot, slot: 'incoming-call', size: 'large', nameId: 'incomingCallPeerName', avatarId: 'incomingCallAvatar'});
    banner.classList.remove('hidden');
  }

  function hideIncomingCall() {
    currentIncomingCall = null;
    const banner = document.getElementById('incomingCallBanner');
    if (banner) banner.classList.add('hidden');
  }

  async function acceptIncomingCall() {
    if (!currentIncomingCall) return;
    const petition = currentIncomingCall;
    hideIncomingCall();
    try {
      await ensureMicrophonePermission();
      await QP.api('/api/respond-petition', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ requestId: petition.requestId, accept: true })
      });
      QP.toast(`Llamada aceptada con ${petition.peerName || 'el contacto'}`, 'success');
      QP.navigate('voice');
      refreshVoice();
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function declineIncomingCall() {
    if (!currentIncomingCall) return;
    const petition = currentIncomingCall;
    hideIncomingCall();
    try {
      await QP.api('/api/respond-petition', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ requestId: petition.requestId, accept: false })
      });
      QP.toast('Llamada rechazada');
    } catch { }
  }

  document.addEventListener('DOMContentLoaded', () => {
    initVoiceDrawer();
    for (const name of ['fullscreenchange', 'webkitfullscreenchange']) {
      document.addEventListener(name, () => {
        if (!fullscreenTile) return;
        if (currentFullscreenElement() === fullscreenTile) tileFullscreenMode = 'native';
        else if (tileFullscreenMode === 'native') clearTileFullscreen();
      });
    }
    document.addEventListener('keydown', event => {
      if (!fullscreenTile) return;
      if (event.key === 'Escape') { event.preventDefault(); event.stopImmediatePropagation(); exitTileFullscreen(); }
      if (event.key === 'Tab') {
        const controls = [...fullscreenTile.querySelectorAll('button:not(:disabled),input:not(:disabled),.screen-tile-body[tabindex]')].filter(el => el.getClientRects().length);
        const index = controls.indexOf(document.activeElement);
        if (controls.length && (index < 0 || event.shiftKey && index === 0 || !event.shiftKey && index === controls.length - 1)) {
          event.preventDefault(); controls[event.shiftKey ? controls.length - 1 : 0].focus();
        }
      }
    }, true);
    // Global Topbar & Call Bar Buttons
    const micBtn = document.getElementById('globalMicBtn');
    if (micBtn) micBtn.addEventListener('click', toggleMute);

    const deafenBtn = document.getElementById('globalDeafenBtn');
    if (deafenBtn) deafenBtn.addEventListener('click', toggleDeafen);

    const callMicBtn = document.getElementById('globalCallMicBtn');
    if (callMicBtn) callMicBtn.addEventListener('click', toggleMute);

    const callDeafenBtn = document.getElementById('globalCallDeafenBtn');
    if (callDeafenBtn) callDeafenBtn.addEventListener('click', toggleDeafen);

    const callHangupBtn = document.getElementById('globalCallHangupBtn');
    if (callHangupBtn) callHangupBtn.addEventListener('click', hangupAll);

    const callGoToVoiceBtn = document.getElementById('globalCallGoToVoiceBtn');
    if (callGoToVoiceBtn) callGoToVoiceBtn.addEventListener('click', () => QP.navigate('voice'));
    setInterval(updateGlobalCallMeta, 1000);

    // Llamadas "My Audio & Streaming" Panel Buttons
    const panelMicBtn = document.getElementById('voicePanelMicBtn');
    if (panelMicBtn) panelMicBtn.addEventListener('click', toggleMute);

    const panelDeafenBtn = document.getElementById('voicePanelDeafenBtn');
    if (panelDeafenBtn) panelDeafenBtn.addEventListener('click', toggleDeafen);

    const panelScreenBtn = document.getElementById('voicePanelScreenBtn');
    if (panelScreenBtn) panelScreenBtn.addEventListener('click', toggleScreenShare);
    document.getElementById('voiceLeaveBtn')?.addEventListener('click', hangupAll);
    const callPicker = document.getElementById('voiceCallPicker');
    document.getElementById('voiceStartCallBtn')?.addEventListener('click', () => {
      document.getElementById('voiceCallPickerSearch').value = '';
      QP.setText('voiceCallPickerHelp', activeCallsList().length ? 'Llama a otro contacto para añadirlo a la conversación.' : 'Elige a quién quieres llamar.');
      callPicker.showModal();
      renderVoicePeers(QP.trusted(voiceStatus), activeCallsList());
      document.getElementById('voiceCallPickerSearch').focus();
    });
    document.getElementById('voiceCallPickerClose')?.addEventListener('click', () => callPicker.close());
    callPicker?.addEventListener('keydown', event => {
      if (event.key !== 'Escape') return;
      event.preventDefault(); event.stopPropagation(); callPicker.close();
    });
    callPicker?.addEventListener('close', () => QP.clear(document.getElementById('voiceCallPickerPeers')));
    callPicker?.addEventListener('click', event => {
      if (event.target !== callPicker) return;
      const bounds = callPicker.getBoundingClientRect();
      if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) callPicker.close();
    });
    document.getElementById('voiceCallPickerSearch')?.addEventListener('input', () => renderVoicePeers(QP.trusted(voiceStatus), activeCallsList()));
    document.getElementById('voiceContactSearch')?.addEventListener('input', () => renderVoicePeers(QP.trusted(voiceStatus), activeCallsList()));
    document.getElementById('voiceInviteBtn')?.addEventListener('click', () => {
      setVoiceDrawer(true, 'contacts');
      document.getElementById('voiceContactSearch')?.focus();
    });
    document.addEventListener('click', event => {
      document.querySelectorAll('.voice-person-menu[open]').forEach(menu => { if (!menu.contains(event.target)) menu.open = false; });
    });
    document.addEventListener('keydown', event => {
      if (event.key !== 'Escape') return;
      const menu = document.querySelector('.voice-person-menu[open]');
      if (menu) { menu.open = false; menu.querySelector('summary')?.focus(); event.preventDefault(); }
    });
    document.getElementById('voicePanelCameraBtn')?.addEventListener('click', toggleCamera);
    document.getElementById('voiceCameraPickerCancel')?.addEventListener('click', () => document.getElementById('voiceCameraPicker')?.close());
    document.getElementById('voiceCameraPickerForm')?.addEventListener('submit', async event => {
      event.preventDefault();
      readCameraChoice(document.getElementById('voiceCameraPickerSelect').value);
      document.getElementById('voiceCameraPicker').close();
      await startCamera();
    });
    navigator.mediaDevices?.addEventListener?.('devicechange', () => {
      if (document.getElementById('voiceCameraPicker')?.open) refreshCameraChoices('voiceCameraPickerSelect', document.getElementById('voiceCameraPickerSelect').value).catch(error => QP.toast(error.message, 'error'));
    });
    document.getElementById('voiceCameraSelect')?.addEventListener('change', event => {
      readCameraChoice(event.target.value);
      if (localVideoKind === 'camera') startCamera();
    });
    document.getElementById('voiceCameraFlipBtn')?.addEventListener('click', async () => {
      try{const cameras=await QP.api('/api/voice/cameras');if(cameras.length<2)return;cameraDeviceId=cameras[(cameras.findIndex(c=>c.id===cameraDeviceId)+1)%cameras.length].id;cameraSwitchPending=true;await startCamera();}catch(error){QP.toast(error.message,'error');}
    });

    // Incoming Call Banner Buttons
    const acceptCallBtn = document.getElementById('acceptIncomingCallBtn');
    if (acceptCallBtn) acceptCallBtn.addEventListener('click', acceptIncomingCall);

    const declineCallBtn = document.getElementById('declineIncomingCallBtn');
    if (declineCallBtn) declineCallBtn.addEventListener('click', declineIncomingCall);

    // Device Selects
    const micSelect = document.getElementById('micSelect');
    if (micSelect) {
      micSelect.addEventListener('change', () => {
        setDevice('input', micSelect.value);
      });
    }

    const speakerSelect = document.getElementById('speakerSelect');
    if (speakerSelect) {
      speakerSelect.addEventListener('change', () => {
        setDevice('output', speakerSelect.value);
      });
    }

    // Master Volume
    const masterSlider = document.getElementById('masterVolumeSlider');
    if (masterSlider) {
      masterSlider.addEventListener('input', () => {
        const val = Number(masterSlider.value) / 100;
        setMasterVolume(val);
        const label = document.getElementById('masterVolumeLabel');
        if (label) label.textContent = `${masterSlider.value}%`;
      });
    }

    // Test Mic Button
    const testBtn = document.getElementById('testMicBtn');
    if (testBtn) testBtn.addEventListener('click', toggleTestMic);

    document.getElementById('screenDecoderAccelerationSelect')?.addEventListener('change', changeDecoderPreference);
    for (const tab of ['Video', 'Audio']) {
      const button = document.getElementById(`media${tab}Tab`);
      button?.addEventListener('click', () => selectMediaTab(tab.toLowerCase()));
      button?.addEventListener('keydown', event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const tabs = ['Video','Audio'].filter(name => !document.getElementById(`media${name}Tab`)?.disabled);
        const index = tabs.indexOf(tab);
        const next = event.key === 'Home' ? tabs[0] : event.key === 'End' ? tabs.at(-1) : tabs[(index + (event.key === 'ArrowLeft' ? tabs.length - 1 : 1)) % tabs.length];
        selectMediaTab(next.toLowerCase());
        document.getElementById(`media${next}Tab`)?.focus();
      });
    }
    document.getElementById('refreshMediaCapabilitiesBtn')?.addEventListener('click', async event => {
      event.target.disabled = true;
      try { await loadNativeCapabilities(true); await refreshScreenCodecCapabilities(true); }
      finally { event.target.disabled = false; }
    });

    document.getElementById('saveOpusModalBtn')?.addEventListener('click', saveOpusModal);
    document.getElementById('retryVoiceAudioBtn')?.addEventListener('click', async () => {
      try { await ensureMicrophonePermission(); await setDevice('input', voiceState.currentInputDevice || 'default'); await refreshVoice(); }
      catch (error) { QP.toast(error.message, 'error'); }
    });

    // Screen Settings Modal & Buttons
    const screenSettingsBtn = document.getElementById('voiceScreenSettingsBtn');
    if (screenSettingsBtn) screenSettingsBtn.addEventListener('click', () => openScreenSettingsModal('audio'));

    const screenGlobalSettingsBtn = document.getElementById('screenShareGlobalSettingsBtn');
    if (screenGlobalSettingsBtn) screenGlobalSettingsBtn.addEventListener('click', openScreenSettingsModal);

    const closeScreenBtn = document.getElementById('closeScreenModalBtn');
    if (closeScreenBtn) closeScreenBtn.addEventListener('click', closeScreenSettingsModal);

    const saveScreenBtn = document.getElementById('saveScreenModalBtn');
    if (saveScreenBtn) saveScreenBtn.addEventListener('click', saveScreenSettingsModal);

    document.getElementById('remoteScreenRequestStartBtn')?.addEventListener('click', () => startScreenShare(pendingRemoteScreenPeerId));

    for (const id of ['screenResolutionSelect', 'screenFpsSelect', 'screenBitrateSelect']) {
      const control = document.getElementById(id);
      if (control) control.addEventListener('change', () => {readNativeControls();refreshScreenCodecCapabilities();});
    }

    initCodecPicker();
    loadScreenShareSettings();
    loadNativeCapabilities();
    let codecTuning = {};
    try { codecTuning = JSON.parse(localStorage.getItem('qp_screen_codec_tuning') || '{}'); } catch {}
    const tuningFields = [['screenEncoderSelect','encoder'], ['screenEncoderPresetSelect','encoderPreset'], ['screenPresetSelect','preset'], ['screenRateControlSelect','rateControl'],
      ['screenGopInput','keyFrameIntervalMs'], ['screenBufferInput','bufferMs'], ['screenQualityInput','quality']];
    document.getElementById('screenCodecSelect')?.addEventListener('change', event => {
      readNativeControls();
      codecTuning[screenShareConfig.codec] = Object.fromEntries(tuningFields.map(([, key]) => [key, screenShareConfig[key]]));
      readCodecSelection(event.target.value);
      Object.assign(screenShareConfig, { encoder: '', encoderPreset: '', preset: 'balanced', rateControl: 'cbr', quality: 23,
        keyFrameIntervalMs: 1000, bufferMs: 50 }, codecTuning[screenShareConfig.codec] || {});
      for (const [id, key] of tuningFields) {
        const el = document.getElementById(id);
        if (el) el.value = String(screenShareConfig[key]);
      }
      try { localStorage.setItem('qp_screen_codec_tuning', JSON.stringify(codecTuning)); } catch {}
      refreshScreenCodecCapabilities();
    });
    for (const id of ['screenEncoderSelect', 'screenEncoderPresetSelect', 'screenPresetSelect', 'screenRateControlSelect']) {
      document.getElementById(id)?.addEventListener('change', event => { readNativeControls(); refreshScreenCodecCapabilities(); });
    }

    // WebSocket Events
    if (window.QP && typeof QP.on === 'function') {
      QP.on('focus_voice_peer', data => {
        if (data?.peerId && !callingPeers.has(data.peerId) && !activeCallsList().some(c => c.peerId === data.peerId)) startCall(data.peerId);
      });

      QP.on('screen_decoder_diagnostic', data => recordDecoderDiagnostic(data.peerId, {...data.config.diagnostic, supported:data.config.supported}, true));
      QP.on('screen_audio_error', data => {
        if (data.audioSessionId && data.audioSessionId !== screenAudioCapture.sessionId) return;
        stopScreenAudioCapture(); QP.toast(data.error || 'Se ha detenido el audio compartido.', 'error');
      });

      QP.on('screen_encoder_failed', async data => {
        if (androidCapture) { console.error('Captura Android detenida:', data.reason); QP.toast('La captura se ha detenido: ' + data.reason, 'error'); return; }
        if ((!nativeSharing && !nativeStartPending) || fallbackInProgress) return;
        const target = activeScreenTargetPeerId;
        nativeSharing = false;
        browserCaptureSelected = false;
        browserSourceBounds = null;
        voiceState.isScreenSharing = false;
        fallbackInProgress = true;
        if (screenShareConfig.backend === 'ffmpeg') {
          try { await stopScreenShare(); QP.toast('FFmpeg se ha detenido: ' + data.reason, 'error'); }
          finally { fallbackInProgress = false; }
          return;
        }
        QP.toast('FFmpeg se ha detenido: ' + data.reason + '. Intentando captura web.', 'warning');
        try { await stopScreenShare(); await startScreenShare(target, true); }
        finally { fallbackInProgress = false; }
      });
      QP.on('voice_vad', onVoiceVad);
      QP.on('voice_state', onVoiceStateUpdated);
      QP.on('voice_signal', onVoiceSignal);
      QP.on('screen_share_started', onScreenShareStarted);
      QP.on('screen_share_stopped', onScreenShareDetenido);
      QP.on('screen_share_request', data => {
        QP.navigate('voice');
        pendingRemoteScreenPeerId = data.peerId;
        const banner = document.getElementById('remoteScreenRequestBanner');
        const label = document.getElementById('remoteScreenRequestText');
        if (label) label.textContent = `${data.peerName || 'Un peer autorizado'} solicita que compartas tu pantalla. Elige la ventana o monitor si el sistema lo pide.`;
        banner?.classList.remove('hidden');
        QP.toast(`${data.peerName || 'Un peer autorizado'} solicita que compartas tu pantalla.`);
      });
      QP.on('screen_share_request_denied', data =>
        QP.toast(`${data.peerName || 'El peer'} no ha autorizado peticiones para compartir su pantalla.`, 'error'));
      QP.on('screen_codec_capabilities', onScreenCodecCapabilities);
      QP.on('screen_frame', onScreenFrameReceived);
      QP.on('ws_binary', onWsBinaryReceived);
      QP.on('petition_received', petition => {
        if (!petition || petition.notify === false) return;
        const proto = (petition.protocolName || '').toLowerCase();
        if (proto.includes('voice') || proto.includes('call')) {
          showIncomingCall(petition);
        }
      });
      QP.on('status_updated', status => {
        voiceStatus = status;
        if (currentIncomingCall) {
          const stillPending = (status?.pendingPetitions || []).some(
            p => (currentIncomingCall.requestId && p.requestId === currentIncomingCall.requestId)
              || (currentIncomingCall.id && p.id === currentIncomingCall.id)
          );
          if (!stillPending) {
            hideIncomingCall();
          } else showIncomingCall(currentIncomingCall);
        }
        renderVoice();
      });
      QP.on('peer_connected', () => refreshVoice());
      QP.on('peer_disconnected', () => refreshVoice());
      QP.on('view_changed', data => {
        if (data?.page !== 'voice') exitTileFullscreen();
        mountVoiceSettings(data?.page === 'voice');
        renderVoice();
        if (data && data.page === 'voice') refreshVoice();
      });
    }

    loadAudioDevices();
    refreshScreenCodecCapabilities();
    if (browserStateTimer) clearInterval(browserStateTimer);
    browserStateTimer = setInterval(() => publishBrowserScreenState().catch(() => {}), 10000);
    refreshVoice();
  });

  async function loadAudioDevices() {
    try {
      const res = await QP.api('/api/voice/devices');
      if (!res) return;

      const micSelect = document.getElementById('micSelect');
      if (micSelect && Array.isArray(res.inputs)) {
        QP.clear(micSelect);
        res.inputs.forEach(dev => {
          const opt = document.createElement('option');
          opt.value = dev.id;
          opt.textContent = dev.name;
          if (dev.id === res.currentInput) opt.selected = true;
          micSelect.appendChild(opt);
        });
      }

      const speakerSelect = document.getElementById('speakerSelect');
      if (speakerSelect && Array.isArray(res.outputs)) {
        QP.clear(speakerSelect);
        res.outputs.forEach(dev => {
          const opt = document.createElement('option');
          opt.value = dev.id;
          opt.textContent = dev.name;
          if (dev.id === res.currentOutput) opt.selected = true;
          speakerSelect.appendChild(opt);
        });
      }
    } catch (e) {
      console.warn('Could not load native audio devices', e);
    }
  }

  async function setDevice(type, deviceId) {
    try {
      await QP.api('/api/voice/device', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ type, deviceId })
      });
      QP.toast(type === 'input' ? 'Micrófono actualizado' : 'Dispositivo de salida actualizado');
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function refreshVoice() {
    try {
      const netPromise = (QP.state && QP.state.status) ? Promise.resolve(QP.state.status) : (QP.getStatus ? QP.getStatus() : QP.api('/api/status'));
      const [status, state] = await Promise.all([
        netPromise,
        QP.api('/api/voice/state').catch(() => null)
      ]);
      voiceStatus = status;
      if (state) {
        voiceState = state;
        nativeSharing = !!state.isNativeScreenSharing;
        syncNativeScreenState(state);
        if (nativeSharing) activeScreenTargetPeerId = state.screenShareTargetPeerId || null;
        if (Array.isArray(state.activeCalls)) {
          state.activeCalls.forEach(c => {
            localPeerVolumes.set(c.peerId, c.volume);
            localPeerMutes.set(c.peerId, c.muted);
            localOutboundMutes.set(c.peerId, !!c.outboundMuted);
            localScreenAllowed.set(c.peerId, c.screenShareAllowed !== false);
            updateRemoteScreenCodecCapabilities(c);
          });
        }
      }
      syncCodecParticipants();
      renderVoice();
    } catch (e) {
      console.warn('Error refreshing voice status', e);
    }
  }

  function onVoiceStateUpdated(state) {
    if (!state) return;
    voiceState = state;
    if (Array.isArray(state.activeCalls)) {
      for (const [key, item] of decoderWarnings) if (!state.activeCalls.some(call => normalizePeerId(call.peerId) === item.peerId)) decoderWarnings.delete(key);
      renderDecoderWarnings();
    }
    nativeSharing = !!state.isNativeScreenSharing;
    syncNativeScreenState(state);
    if (nativeSharing) activeScreenTargetPeerId = state.screenShareTargetPeerId || null;
    if (Array.isArray(state.activeCalls)) {
      state.activeCalls.forEach(c => {
        localPeerVolumes.set(c.peerId, c.volume);
        localPeerMutes.set(c.peerId, c.muted);
        localOutboundMutes.set(c.peerId, !!c.outboundMuted);
        localScreenAllowed.set(c.peerId, c.screenShareAllowed !== false);
        updateRemoteScreenCodecCapabilities(c);
      });
    }
    syncCodecParticipants();
    renderVoice();
    publishBrowserScreenState().catch(() => {});
  }

  function onVoiceSignal(data) {
    if (!data) return;
    voiceSignalVersions.set(data.peerId, (voiceSignalVersions.get(data.peerId) || 0) + 1);
    const operation = callOperations.get(data.peerId);
    if (operation && ['call-established', 'call-ended', 'call-failed'].includes(data.signal)) operation.finished = true;
    if (data.signal === 'call-established') {
      callingPeers.delete(data.peerId);
      callOperations.delete(data.peerId);
      hideIncomingCall();
      QP.toast('Llamada conectada', 'success');
      refreshVoice();
    } else if (data.signal === 'call-ended' || data.signal === 'call-failed') {
      remoteScreenCodecCapabilities.delete(normalizePeerId(data.peerId));
      decoderDiagnostics.delete('remote:' + normalizePeerId(data.peerId));
      decoderDiagnostics.delete('local:' + normalizePeerId(data.peerId));
      callingPeers.delete(data.peerId);
      callOperations.delete(data.peerId);
      talkingPeers.delete(data.peerId);
      if (voiceStatus?.activeVoiceCalls) voiceStatus.activeVoiceCalls = voiceStatus.activeVoiceCalls.filter(call => call.peerId !== data.peerId);
      if (!activeCallsList().length) void stopScreenShare();
      hideIncomingCall();
      removeScreenTile(data.peerId);
      refreshVoice();
    }
  }

  function onVoiceVad(data) {
    if (!data) return;
    if (data.isLocal) {
      const meter = document.getElementById('micMeter');
      if (meter) {
        const pct = Math.min(100, Math.round(data.level * 100));
        meter.style.width = `${pct}%`;
      }
      localSpeaking = !!data.speaking;
      updateLocalSpeaking();
    } else if (data.peerId) {
      if (data.speaking) talkingPeers.add(data.peerId);
      else talkingPeers.delete(data.peerId);
      const stream = activeScreenStreams.get(normalizePeerId(data.peerId));
      stream?.cardEl.classList.toggle('is-speaking', !!data.speaking);
      const peerCard = document.querySelector(`[data-call-peer-id="${data.peerId}"]`);
      if (peerCard) {
        peerCard.classList.toggle('is-speaking', !!data.speaking);
        const avatar = peerCard.querySelector('.avatar');
        const badge = peerCard.querySelector('.live-badge');
        if (data.speaking) {
          avatar?.classList.add('talking');
          if (badge) {
            badge.className = 'voice-participant-status live-badge talking-badge';
            badge.textContent = 'Hablando';
          }
        } else {
          avatar?.classList.remove('talking');
          if (badge) {
            badge.className = 'voice-participant-status live-badge';
            badge.textContent = 'En llamada';
          }
        }
      }
    }
  }

  function activeCallsList() {
    return voiceStatus?.activeVoiceCalls || [];
  }

  function updateLocalSpeaking() {
    if (voiceState.isMuted || voiceState.captureRunning === false || voiceState.captureError || !activeCallsList().length) localSpeaking = false;
    const self = document.querySelector('.voice-self-tile');
    self?.classList.toggle('is-speaking', localSpeaking);
    document.getElementById('voiceSelfAvatar')?.classList.toggle('talking', localSpeaking);
    for (const entry of activeScreenStreams.values()) if (entry.isLocal) entry.cardEl.classList.toggle('is-speaking', localSpeaking);
    const status = self?.querySelector('.voice-self-status');
    if (status) status.textContent = voiceState.isMuted ? 'Micrófono silenciado' : localSpeaking ? 'Hablando' : 'Micrófono activo';
  }

  function syncMediaSettingsVisibility(calls) {
    document.getElementById('voiceScreenSettingsBtn')?.classList.remove('hidden');
    document.getElementById('screenShareGlobalSettingsBtn')?.classList.toggle('hidden', !calls.length);
  }

  function renderVoice() {
    const calls = activeCallsList();
    syncMediaSettingsVisibility(calls);
    const peers = QP.trusted(voiceStatus);
    const microphoneFailed = !!voiceState.captureError || (calls.length > 0 && voiceState.captureRunning === false);

    const audioStatus = document.getElementById('voiceAudioStatus');
    if (audioStatus) audioStatus.textContent = voiceState.captureError || voiceState.playbackError ||
      (voiceState.captureRunning ? 'Micrófono abierto y capturando audio.' : 'El micrófono se abrirá al llamar o al iniciar una prueba.');
    // Badges & Top Actions
    QP.setText('voiceBadge', `${calls.length} active`);
    const voiceBadge = document.getElementById('voiceBadge');
    if (voiceBadge) {
      if (calls.length > 0) voiceBadge.classList.remove('hidden');
      else voiceBadge.classList.add('hidden');
    }

    const backendBadge = document.getElementById('audioBackendBadge');
    if (backendBadge && voiceState.backendName) {
      backendBadge.textContent = voiceState.backendName;
    }

    // 1. Global Topbar Buttons
    const micBtn = document.getElementById('globalMicBtn');
    if (micBtn) {
      micBtn.textContent = voiceState.isMuted ? '🔇' : '🎙️';
      micBtn.className = `btn sm ${voiceState.isMuted || microphoneFailed ? 'danger' : 'green'}`;
      micBtn.title = microphoneFailed ? (voiceState.captureError || 'El micrófono no está capturando audio') : voiceState.isMuted ? 'Micrófono silenciado' : 'Micrófono preparado';
    }

    const deafenBtn = document.getElementById('globalDeafenBtn');
    if (deafenBtn) {
      deafenBtn.textContent = voiceState.isDeafened ? '🔇' : '🎧';
      deafenBtn.className = `btn sm ${voiceState.isDeafened ? 'danger' : 'green'}`;
      deafenBtn.title = voiceState.isDeafened ? 'Audio silenciado: no escuchas a los participantes' : 'Audio de la llamada activado';
    }

    // One predictable control dock; state changes do not change button size.
    const cameraActive = localVideoKind === 'camera' && !!screenStream;
    const screenActive = localVideoKind !== 'camera' && (!!screenStream || !!voiceState.isScreenSharing);
    setVoiceControl('Mic', 'mic', 'Micrófono', microphoneFailed ? 'Micrófono no disponible: abre Ajustes para reintentarlo' : voiceState.isMuted ? 'Activar micrófono' : 'Silenciar micrófono', voiceState.isMuted || microphoneFailed ? 'is-muted' : '');
    setVoiceControl('Deafen', 'headphones', 'Audio', voiceState.isDeafened ? 'Activar audio de la llamada' : 'Silenciar audio de la llamada', voiceState.isDeafened ? 'is-muted' : '');
    setVoiceControl('Camera', 'camera', 'Cámara', cameraActive ? 'Desactivar cámara' : 'Activar cámara', cameraActive ? 'is-on' : '');
    setVoiceControl('Screen', 'screen', 'Pantalla', screenActive ? 'Dejar de compartir pantalla' : 'Compartir pantalla', screenActive ? 'is-on' : '');
    for (const id of ['voicePanelCameraBtn', 'voicePanelScreenBtn']) {
      const button = document.getElementById(id); if (button) button.disabled = videoStartPending || !!videoStopPending || cameraPickerPending || !calls.length;
      if (button && !calls.length) button.title = 'Disponible durante una llamada';
    }
    const leave = document.getElementById('voiceLeaveBtn'); if (leave) leave.disabled = !calls.length && !callingPeers.size;
    document.getElementById('voiceCameraOptions')?.classList.toggle('hidden', !cameraActive && !cameraSwitchPending);
    document.getElementById('voiceCameraFlipBtn')?.classList.toggle('hidden', !androidCapture);
    const cameraFlip = document.getElementById('voiceCameraFlipBtn');
    if (cameraFlip) {
      cameraFlip.textContent = cameraSwitchPending ? 'Cambiando cámara…' : 'Cambiar cámara';
      cameraFlip.title = 'Cambiar a la siguiente cámara del dispositivo';
      cameraFlip.setAttribute('aria-label', cameraFlip.title);
    }
    for (const id of ['voiceCameraSelect', 'voiceCameraFlipBtn']) {
      const control = document.getElementById(id); if (control) control.disabled = videoStartPending || videoSettingsPending || !!videoStopPending;
    }
    if (!calls.length && document.getElementById('voiceCameraPicker')?.open) document.getElementById('voiceCameraPicker').close();
    document.querySelector('.voice-stage')?.classList.toggle('in-call', calls.length > 0);
    QP.setText('voiceRoomStatus', calls.length ? 'En llamada' : callingPeers.size ? 'Conectando…' : 'Listo para llamar');
    QP.setText('voiceRoomCount', calls.length ? `${calls.length + 1} participantes` : '');
    QP.setText('voiceDockHint', microphoneFailed ? 'El micrófono necesita atención. Abre Ajustes.' : screenActive ? 'Tu pantalla es visible para los participantes' : cameraActive ? 'Tu cámara está activada' : voiceState.isMuted ? 'Tu micrófono está silenciado' : calls.length ? 'Conectado por audio' : 'Prepara tu audio antes de llamar');

    // 3. Global Floating Call Bar
    const callBar = document.getElementById('globalActiveCallBar') || document.getElementById('globalCallBar');
    if (callBar) {
      const isVoicePage = QP.currentPage === 'voice' || document.body.dataset.page === 'voice';
      if (calls.length > 0 && !isVoicePage) {
        callBar.classList.remove('hidden');
        const names = calls.map(c => c.peerName || 'Contacto').join(', ');
        QP.setText('globalCallPeerName', `En llamada con ${names}`);
        updateGlobalCallMeta();
        updateGlobalCallControl('globalCallMicBtn', 'mic', voiceState.isMuted, voiceState.isMuted ? 'Silenciado' : 'Micrófono', voiceState.isMuted ? 'Activar micrófono' : 'Silenciar micrófono');
        updateGlobalCallControl('globalCallDeafenBtn', 'headphones', voiceState.isDeafened, 'Audio', voiceState.isDeafened ? 'Escuchar audio de la llamada' : 'Silenciar audio de la llamada');
      } else {
        callBar.classList.add('hidden');
      }
    }

    // Test Mic Button text
    const testBtn = document.getElementById('testMicBtn');
    if (testBtn) {
      testBtn.textContent = voiceState.isTestLoopback ? 'Detener prueba' : 'Probar micrófono';
      testBtn.className = voiceState.isTestLoopback ? 'btn danger' : 'btn';
    }

    // Master Volume Slider
    const masterSlider = document.getElementById('masterVolumeSlider');
    if (masterSlider && document.activeElement !== masterSlider) {
      const pct = Math.round((voiceState.masterVolume ?? 1.0) * 100);
      masterSlider.value = String(pct);
      const label = document.getElementById('masterVolumeLabel');
      if (label) label.textContent = `${pct}%`;
    }

    renderActiveCalls(calls, peers);
    renderVoicePeers(peers, calls);
    updateLocalSpeaking();
    for (const entry of activeScreenStreams.values()) paintScreenAvatar(entry);
  }

  function renderActiveCalls(calls, peers) {
    const host = document.getElementById('activeCalls');
    if (!host) return;
    document.getElementById('voiceCallEmpty')?.classList.toggle('hidden', !!calls.length);
    host.classList.toggle('hidden', !calls.length);
    const ids = new Set(calls.map(call => call.peerId));
    host.dataset.count = String(calls.length + 1);
    for (const [id, entry] of participantCards) if (!ids.has(id)) { entry.menu.remove(); entry.card.remove(); participantCards.delete(id); }
    if (!calls.length) { document.querySelector('.voice-self-tile')?.remove(); updateGridState(); return; }
    for (const call of calls) {
      const peer = peers.find(p => p.id === call.peerId) || { id: call.peerId, name: call.peerName || 'Contacto' };
      let entry = participantCards.get(peer.id);
      if (!entry) {
        const card = QP.el('article', 'voice-participant'); card.dataset.callPeerId = peer.id;
        const avatar = QP.el('div', 'avatar voice-participant-avatar', QP.initials(peer.name));
        QP.paintAvatar?.(avatar, peer, voiceStatus, 'voice-call');
        const name = QP.el('strong', 'voice-participant-name', peer.name);
        const status = QP.el('span', 'voice-participant-status live-badge');
        const meta = QP.el('span', 'voice-participant-meta');
        const flags = QP.el('div', 'voice-participant-flags');
        const menu = document.createElement('details'); menu.className = 'voice-person-menu';
        const summary = document.createElement('summary'); summary.title = 'Opciones de ' + peer.name; summary.setAttribute('aria-label', summary.title); summary.append(voiceIcon('more'));
        const panel = QP.el('div', 'voice-person-popover');
        const heading = QP.el('strong', 'voice-person-heading', peer.name); panel.append(heading);
        const volume = QP.el('label', 'voice-person-volume'); const value = QP.el('output', '', '100%');
        const volumeHead = QP.el('span', 'voice-volume-heading'); volumeHead.append(QP.el('span', '', 'Volumen'), value);
        const slider = document.createElement('input'); slider.type = 'range'; slider.min = '0'; slider.max = '200'; slider.step = '5'; slider.setAttribute('aria-label', 'Volumen de ' + peer.name);
        slider.addEventListener('input', () => { value.textContent = slider.value + '%'; setPeerVolume(peer.id, Number(slider.value) / 100); });
        volume.append(volumeHead, slider); panel.append(volume);
        const action = (icon, label, handler, toggle = false) => {
          const button = document.createElement('button'); button.type = 'button'; button.className = 'voice-menu-action';
          button.append(voiceIcon(icon), QP.el('span', '', label));
          if (toggle) button.setAttribute('aria-pressed', 'false');
          button.addEventListener('click', () => { if (!toggle) menu.open = false; handler(peer.id); }); panel.append(button); return button;
        };
        const mute = action('headphones', 'Silenciar a esta persona', togglePeerMute, true);
        const outbound = action('mic', 'Silenciar mi voz para ella', toggleOutboundMute, true);
        const screen = action('screen', 'Permitir que vea mi vídeo', togglePeerScreenShare, true);
        const divider = QP.el('div', 'voice-menu-divider'); panel.append(divider);
        action('screen', 'Pedir que comparta pantalla', requestPeerScreenShare);
        action('settings', 'Ajustes de su transmisión', openRemoteScreenSettings);
        const hangupButton = action('hangup', 'Finalizar esta llamada', hangup); hangupButton.classList.add('is-danger');
        menu.append(summary, panel);
        menu.addEventListener('toggle', () => {
          if (!menu.open) return;
          document.querySelectorAll('.voice-person-menu[open]').forEach(other => { if (other !== menu) other.open = false; });
          panel.style.transform = '';
          const bounds = panel.getBoundingClientRect();
          const shift = Math.max(8 - bounds.left, 0) - Math.max(bounds.right - window.innerWidth + 8, 0);
          if (shift) panel.style.transform = `translateX(${shift}px)`;
        });
        card.append(menu, avatar, name, status, flags, meta); host.append(card);
        entry = { card, menu, avatar, name, status, meta, flags, heading, summary, mute, outbound, screen, slider, value }; participantCards.set(peer.id, entry);
      }
      entry.name.textContent = peer.name; entry.heading.textContent = peer.name;
      QP.paintAvatar?.(entry.avatar, peer, voiceStatus, 'voice-call');
      entry.summary.setAttribute('aria-label', 'Opciones de ' + peer.name);
      entry.summary.title = 'Opciones de ' + peer.name;
      entry.meta.textContent = Number.isFinite(peer.ping) ? `${Math.round(peer.ping)} ms` : '';
      entry.slider.setAttribute('aria-label', 'Volumen de ' + peer.name);
      const speaking = talkingPeers.has(peer.id); entry.avatar.classList.toggle('talking', speaking); entry.card.classList.toggle('is-speaking', speaking);
      entry.status.textContent = speaking ? 'Hablando' : 'En llamada';
      entry.mute.setAttribute('aria-pressed', String(!!localPeerMutes.get(peer.id)));
      entry.outbound.setAttribute('aria-pressed', String(!!localOutboundMutes.get(peer.id)));
      entry.screen.setAttribute('aria-pressed', String(localScreenAllowed.get(peer.id) !== false));
      const flagState = [!!localPeerMutes.get(peer.id), !!localOutboundMutes.get(peer.id), localScreenAllowed.get(peer.id) === false];
      if (entry.flags.dataset.state !== String(flagState)) {
        entry.flags.replaceChildren();
        [['headphones', 'Audio silenciado para ti'], ['mic', 'Tu micrófono está silenciado para esta persona'], ['screen', 'No puede ver tu vídeo']].forEach(([icon, label], index) => {
          if (!flagState[index]) return;
          const marker = document.createElement('span'); marker.title = label; marker.setAttribute('aria-label', label); marker.setAttribute('role', 'img'); marker.append(voiceIcon(icon, true)); entry.flags.append(marker);
        });
        entry.flags.dataset.state = String(flagState);
      }
      if (document.activeElement !== entry.slider) entry.slider.value = String(Math.round((localPeerVolumes.get(peer.id) ?? 1) * 100));
      entry.value.textContent = entry.slider.value + '%';
    }
    let self = document.querySelector('.voice-self-tile');
    if (!self) {
      self = QP.el('article', 'voice-participant voice-self-tile');
      const avatar = QP.el('div', 'avatar voice-participant-avatar local-avatar'); avatar.id = 'voiceSelfAvatar'; avatar.append(voiceIcon('user'));
      self.append(avatar, QP.el('strong', 'voice-participant-name', 'Tú'), QP.el('span', 'voice-self-status'));
      host.append(self);
    }
    if (voiceStatus?.node) QP.paintAvatar?.(document.getElementById('voiceSelfAvatar'), voiceStatus.node, voiceStatus, 'voice-self');
    self.classList.toggle('is-muted', !!voiceState.isMuted);
    updateGridState();
  }

  function renderVoicePeers(peers, calls) {
    QP.setText('voiceContactCount', String(peers.length));
    renderVoicePeerList(peers, calls, 'voicePeers', 'voiceContactSearch');
    if (document.getElementById('voiceCallPicker')?.open) renderVoicePeerList(peers, calls, 'voiceCallPickerPeers', 'voiceCallPickerSearch', true);
  }

  function renderVoicePeerList(peers, calls, listId, searchId, picker = false) {
    const box = document.getElementById(listId); if (!box) return;
    const search = (document.getElementById(searchId)?.value || '').trim().toLocaleLowerCase();
    const filtered = peers.filter(peer => (peer.name || 'Contacto').toLocaleLowerCase().includes(search));
    if (!filtered.length) {
      if (!box.querySelector('.voice-no-contacts')) box.replaceChildren(QP.el('p', 'voice-no-contacts'));
      box.firstElementChild.textContent = peers.length ? 'No hay contactos con ese nombre.' : 'Añade contactos de confianza desde Inicio.';
      return;
    }
    const focused = box.contains(document.activeElement) ? document.activeElement : null;
    const existing = new Map([...box.children].filter(row => row.dataset.peerId).map(row => [row.dataset.peerId, row]));
    const keep = new Set(filtered.map(peer => peer.id));
    for (const row of [...box.children]) if (!keep.has(row.dataset.peerId)) row.remove();
    for (const [index, peer] of filtered.entries()) {
      const inCall = calls.some(call => call.peerId === peer.id), calling = callingPeers.has(peer.id);
      let row = existing.get(peer.id);
      if (!row) {
        row = QP.el('div', 'voice-contact'); row.dataset.peerId = peer.id;
        const copy = QP.el('div', 'voice-contact-copy');
        copy.append(QP.identity.createCapsule(peer, {snapshot: voiceStatus, slot: 'voice-peer', size: 'compact'}), QP.el('span', 'voice-contact-status'));
        const action = QP.el('button', 'voice-contact-call'); action.type = 'button';
        action.addEventListener('click', () => {
          if (picker) document.getElementById('voiceCallPicker').close();
          if (callingPeers.has(peer.id)) hangup(peer.id); else startCall(peer.id);
        });
        row.append(copy, action);
      }
      QP.identity.updateCapsule(row.querySelector('.ci'), peer, {snapshot: voiceStatus});
      const status = row.querySelector('.voice-contact-status');
      status.classList.toggle('is-connected', inCall);
      status.textContent = inCall ? 'En llamada' : calling ? 'Llamando…' : QP.userPresence?.(peer, voiceStatus)?.label || (peer.isQuicConnected ? 'Disponible' : 'Sin conexión directa');
      const button = row.querySelector('.voice-contact-call');
      const mode = inCall ? 'active' : calling ? 'calling' : 'ready';
      if (button.dataset.mode !== mode) {
        button.dataset.mode = mode;
        button.replaceChildren(voiceIcon(calling ? 'close' : 'phone'));
        if (picker) button.append(QP.el('span', '', inCall ? 'En llamada' : calling ? 'Cancelar' : 'Llamar'));
      }
      const label = (inCall ? 'En llamada con ' : calling ? 'Cancelar llamada a ' : 'Llamar a ') + (peer.name || 'Contacto');
      button.setAttribute('aria-label', label); button.title = label; button.disabled = inCall;
      button.classList.toggle('is-danger', calling);
      if (box.children[index] !== row) box.insertBefore(row, box.children[index] || null);
    }
    if (focused?.isConnected && !focused.disabled && document.activeElement !== focused) focused.focus({preventScroll: true});
  }

  async function startCall(peerId) {
    if (callingPeers.has(peerId)) return;
    const operation = {};
    callOperations.set(peerId, operation);
    callingPeers.add(peerId);
    renderVoice();
    try {
      await ensureMicrophonePermission();
      if (operation.cancelled) return;
      operation.requested = true;
      await QP.api('/api/connect-peer', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, protocol: 'voice' })
      });
      if (operation.cancelled) {
        await QP.api('/api/voice-hangup', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId})});
        return;
      }
      if (operation.finished) return;
      QP.toast('Llamando…');
    } catch (e) {
      if (callOperations.get(peerId) === operation) { callOperations.delete(peerId); callingPeers.delete(peerId); }
      renderVoice();
      QP.toast(e.message, 'error');
    }
  }

  async function hangup(peerId) {
    const signalVersion = voiceSignalVersions.get(peerId) || 0;
    const wasCalling = callingPeers.has(peerId);
    const previousCall = activeCallsList().find(call => call.peerId === peerId);
    const previousStateCall = voiceState.activeCalls?.find(call => call.peerId === peerId);
    callingPeers.delete(peerId);
    const operation = callOperations.get(peerId);
    if (operation) operation.cancelled = true;
    callOperations.delete(peerId);
    talkingPeers.delete(peerId);
    if (Array.isArray(voiceState.activeCalls)) {
      voiceState.activeCalls = voiceState.activeCalls.filter(c => c.peerId !== peerId);
    }
    if (voiceStatus && Array.isArray(voiceStatus.activeVoiceCalls)) {
      voiceStatus.activeVoiceCalls = voiceStatus.activeVoiceCalls.filter(c => c.peerId !== peerId);
    }
    removeScreenTile(peerId);

    // If no active calls remain, stop screen broadcast
    if (activeCallsList().length === 0 && (screenStream || voiceState.isScreenSharing || videoStartPending || nativeStartPending)) {
      stopScreenShare();
    }

    renderVoice();

    try {
      await QP.api('/api/voice-hangup', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId })
      });
      QP.toast('Llamada finalizada');
    } catch (e) {
      console.warn('Error hanging up', e);
      if ((voiceSignalVersions.get(peerId) || 0) === signalVersion && !callOperations.has(peerId)) {
        if (wasCalling && (!operation || operation.requested)) {
          callingPeers.add(peerId);
          if (operation) callOperations.set(peerId, operation);
        }
        if (previousCall && !activeCallsList().some(call => call.peerId === peerId)) voiceStatus.activeVoiceCalls.push(previousCall);
        if (previousStateCall && !voiceState.activeCalls?.some(call => call.peerId === peerId)) (voiceState.activeCalls ||= []).push(previousStateCall);
        QP.toast('No se pudo colgar. Puedes volver a intentarlo: ' + e.message, 'error');
      }
      renderVoice();
    }
    refreshVoice();
  }

  async function hangupAll() {
    const calls = activeCallsList();
    const ids = new Set([
      ...calls.map(c => c.peerId),
      ...Array.from(callingPeers.values()),
      ...(voiceState.activeCalls || []).map(c => c.peerId)
    ]);
    if (ids.size === 0) return;
    for (const peerId of ids) {
      await hangup(peerId);
    }
    if (nativeSharing || screenStream || voiceState.isScreenSharing) {
      stopScreenShare();
    }
  }

  async function toggleMute() {
    try {
      const res = await QP.api('/api/voice/mute', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ muted: !voiceState.isMuted })
      });
      voiceState.isMuted = res.isMuted;
      renderVoice();
      QP.toast(voiceState.isMuted ? 'Micrófono silenciado' : 'Micrófono activado');
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function toggleDeafen() {
    try {
      const res = await QP.api('/api/voice/deafen', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ deafened: !voiceState.isDeafened })
      });
      voiceState.isDeafened = res.isDeafened;
      renderVoice();
      QP.toast(voiceState.isDeafened ? 'Audio de la llamada silenciado' : 'Audio de la llamada activado');
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function setMasterVolume(val) {
    voiceState.masterVolume = val;
    try {
      await QP.api('/api/voice/volume', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ masterVolume: val })
      });
    } catch { }
  }

  async function setPeerVolume(peerId, volume) {
    localPeerVolumes.set(peerId, volume);
    try {
      await QP.api('/api/voice/volume', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, volume })
      });
    } catch { }
  }

  async function togglePeerMute(peerId) {
    const current = localPeerMutes.get(peerId) || false;
    const next = !current;
    localPeerMutes.set(peerId, next);
    try {
      await QP.api('/api/voice/volume', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, muted: next })
      });
      renderVoice();
      QP.toast(next ? 'Audio del contacto silenciado' : 'Audio del contacto activado');
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function toggleOutboundMute(peerId) {
    const current = localOutboundMutes.get(peerId) || false;
    const next = !current;
    localOutboundMutes.set(peerId, next);
    try {
      await QP.api('/api/voice/volume', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, outboundMuted: next })
      });
      renderVoice();
      const peerName = resolvePeerName(peerId);
      QP.toast(next ? `Tu micrófono está silenciado para ${peerName}` : `Tu micrófono está activado para ${peerName}`);
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function togglePeerScreenShare(peerId) {
    const current = localScreenAllowed.get(peerId) !== false;
    const next = !current;
    localScreenAllowed.set(peerId, next);
    try {
      await QP.api('/api/voice/screen-share/peer-toggle', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, allowed: next })
      });
      renderVoice();
      const peerName = resolvePeerName(peerId);
      QP.toast(next ? `Pantalla compartida habilitada para ${peerName}` : `Pantalla compartida bloqueada para ${peerName}`);
    } catch (e) {
      localScreenAllowed.set(peerId, current);
      renderVoice();
      QP.toast(e.message, 'error');
    }
  }

  async function requestPeerScreenShare(peerId) {
    try {
      QP.toast('Solicitando la pantalla del peer…');
      const reply = await QP.api('/api/voice/screen-share/request', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId })
      });
      if (!reply.Success && reply.Code !== 'ConsentRequired') throw new Error(reply.Error || reply.Code);
      QP.toast(reply.Code === 'Requested' ? 'Solicitud enviada al cliente anterior; espera su respuesta.' : reply.Success ? 'El peer ha iniciado su pantalla.' : reply.Error, reply.Success ? 'success' : 'info');
    } catch (e) { QP.toast(e.message, 'error'); }
  }

  async function toggleTestMic() {
    try {
      if (!voiceState.isTestLoopback) await ensureMicrophonePermission();
      const res = await QP.api('/api/voice/test-mic', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: !voiceState.isTestLoopback })
      });
      voiceState.isTestLoopback = res.isTestLoopback;
      renderVoice();
      QP.toast(voiceState.isTestLoopback ? 'Prueba de micrófono iniciada: habla para escucharte por los altavoces' : 'Prueba de micrófono detenida');
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  function syncOpusPreference() {
    const select = document.getElementById('opusBitrateSelect');
    const requestedBitrate = voiceState.requestedOpusBitrate ?? voiceState.opusBitrate;
    if (select && requestedBitrate) select.value = String(requestedBitrate);
  }

  function openOpusModal() {
    syncOpusPreference();
    if (document.getElementById('screenShareSettingsModal')) openScreenSettingsModal('audio');
  }

  async function saveOpusModal() {
    const select = document.getElementById('opusBitrateSelect');
    const bitrate = select ? parseInt(select.value, 10) : 96000;
    try {
      const res = await QP.api('/api/voice/codec-settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ bitrate })
      });
      voiceState.opusBitrate = res.bitrate;
      voiceState.requestedOpusBitrate = res.requestedBitrate ?? bitrate;
      syncOpusPreference();
      QP.toast(`Calidad de audio guardada: ${Math.round(bitrate / 1000)} kbps`, 'success');
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  // --- Screen Sharing Settings Modal & Persistence ---

  const screenCodecDefinitions = [
    { name: 'h264', id: 1, label: 'H.264 / AVC' },
    { name: 'hevc', id: 5, label: 'H.265 / HEVC' },
    { name: 'vp9', id: 3, label: 'VP9' },
    { name: 'av1', id: 4, label: 'AV1' }
  ];
  let screenCodecProbeVersion = 0;

  function codecMaskBit(codecId) {
    return 1 << codecId;
  }

  function updateRemoteScreenCodecCapabilities(call) {
    if (!call || !call.peerId) return;
    const peerId = normalizePeerId(call.peerId);
    const encoderCodecMask = Number(call.encoderCodecMask ?? call.screenEncoderCodecMask ?? 0);
    const decoderCodecMask = Number(call.decoderCodecMask ?? call.screenDecoderCodecMask ?? 0);
    if ((call.capabilitiesReceived ?? Object.hasOwn(call, 'decoderCodecMask')) || call.screenCapabilitiesReceived || encoderCodecMask || decoderCodecMask) {
      remoteScreenCodecCapabilities.set(peerId, { encoderCodecMask, decoderCodecMask,
        decoderHardwarePreferenceMask: call.decoderHardwarePreferenceMask ?? call.screenDecoderHardwarePreferenceMask ?? null });
    }
    else if (call.capabilitiesReceived === false) remoteScreenCodecCapabilities.delete(peerId);
  }

  function onScreenCodecCapabilities(data) {
    updateRemoteScreenCodecCapabilities(data);
    updateScreenCodecOptions();
  }

  function pickLevel(load, levels, frameSize = 0, bitrate = 0) {
    for (const level of levels) {
      if (load <= level.max && frameSize <= level.frame && bitrate <= level.bitrate) return level.value;
    }
    return levels[levels.length - 1].value;
  }

  function getCodecCandidates(codecId, width, height, fps) {
    const area = width * height, pixelRate = area * fps;
    const bitrate = Number(screenShareConfig.bitrate || 12000000);
    // A valid level must fit one complete frame as well as the sample rate.
    // Rate-only selection incorrectly rejects large screens at low FPS.
    if (codecId === 1) {
      const macroblocks = Math.ceil(width / 16) * Math.ceil(height / 16);
      const level = pickLevel(macroblocks * fps, [
        { max: 108000, frame: 3600, bitrate: 14000000, value: '1F' },
        { max: 216000, frame: 5120, bitrate: 20000000, value: '20' },
        { max: 245760, frame: 8192, bitrate: 20000000, value: '28' },
        { max: 245760, frame: 8192, bitrate: 50000000, value: '29' },
        { max: 522240, frame: 8704, bitrate: 50000000, value: '2A' },
        { max: 589824, frame: 22080, bitrate: 135000000, value: '32' },
        { max: 983040, frame: 36864, bitrate: 240000000, value: '33' },
        { max: 2073600, frame: 36864, bitrate: 240000000, value: '34' },
        { max: 4177920, frame: 139264, bitrate: 240000000, value: '3C' },
        { max: 8355840, frame: 139264, bitrate: 480000000, value: '3D' },
        { max: Infinity, frame: Infinity, bitrate: Infinity, value: '3E' }
      ], macroblocks, bitrate);
      return [`avc1.6400${level}`, `avc1.4D00${level}`, `avc1.4200${level}`];
    }
    if (codecId === 5) {
      const level = pickLevel(pixelRate, [
        { max: 33177600, frame: 983040, bitrate: 10000000, value: '93' },
        { max: 66846720, frame: 2228224, bitrate: 12000000, value: '120' },
        { max: 133693440, frame: 2228224, bitrate: 20000000, value: '123' },
        { max: 267386880, frame: 8912896, bitrate: 25000000, value: '150' },
        { max: 534773760, frame: 8912896, bitrate: 40000000, value: '153' },
        { max: 1069547520, frame: 8912896, bitrate: 60000000, value: '156' },
        { max: 1069547520, frame: 35651584, bitrate: 60000000, value: '180' },
        { max: 2139095040, frame: 35651584, bitrate: 120000000, value: '183' },
        { max: Infinity, frame: Infinity, bitrate: Infinity, value: '186' }
      ], area, bitrate);
      return [`hvc1.1.6.L${level}`, `hvc1.1.6.L${level}.B0`, `hev1.1.6.L${level}`, `hev1.1.6.L${level}.B0`];
    }
    if (codecId === 3) {
      const level = pickLevel(pixelRate, [
        { max: 36864000, frame: 983040, bitrate: 12000000, value: '31' },
        { max: 83558400, frame: 2228224, bitrate: 18000000, value: '40' },
        { max: 160432128, frame: 2228224, bitrate: 30000000, value: '41' },
        { max: 311951360, frame: 8912896, bitrate: 60000000, value: '50' },
        { max: 588251136, frame: 8912896, bitrate: 120000000, value: '51' },
        { max: 1176502272, frame: 8912896, bitrate: 180000000, value: '52' },
        { max: 1176502272, frame: 35651584, bitrate: 180000000, value: '60' },
        { max: 2353004544, frame: 35651584, bitrate: 240000000, value: '61' },
        { max: Infinity, frame: Infinity, bitrate: Infinity, value: '62' }
      ], area, bitrate);
      return [`vp09.00.${level}.08.00`];
    }
    if (codecId === 4) {
      const level = pickLevel(pixelRate, [
        { max: 70778880, frame: 2359296, bitrate: 12000000, value: '08' },
        { max: 141557760, frame: 2359296, bitrate: 20000000, value: '09' },
        { max: 267386880, frame: 8912896, bitrate: 30000000, value: '12' },
        { max: 534773760, frame: 8912896, bitrate: 40000000, value: '13' },
        { max: 1069547520, frame: 8912896, bitrate: 60000000, value: '14' },
        { max: 1069547520, frame: 35651584, bitrate: 60000000, value: '16' },
        { max: 2139095040, frame: 35651584, bitrate: 100000000, value: '17' },
        { max: Infinity, frame: Infinity, bitrate: Infinity, value: '18' }
      ], area, bitrate);
      return [`av01.0.${level}M.08`];
    }
    return [];
  }

  function getScreenProbeProfile() {
    const camera = localVideoKind === 'camera';
    const resolution = (!camera && document.getElementById('screenResolutionSelect')?.value) || screenShareConfig.resolution;
    const fps = Number((!camera && document.getElementById('screenFpsSelect')?.value) || screenShareConfig.fps || 60);
    const bitrate = Number((!camera && document.getElementById('screenBitrateSelect')?.value) || screenShareConfig.bitrate || 12000000);
    const bounds = captureBounds();
    let width = bounds?.width || 1280, height = bounds?.height || 720;
    if (resolution !== 'native' && Number(resolution) > 0) {
      const scale = Math.min(1, Number(resolution) / height, Number(resolution) * 16 / 9 / width);
      width *= scale; height *= scale;
    }
    const scale = Math.min(1, 7680 / width, 4320 / height);
    return { width: Math.max(2, Math.floor(width * scale / 2) * 2), height: Math.max(2, Math.floor(height * scale / 2) * 2), fps, bitrate };
  }

  function fitScreenOutput(width, height, bounds = getScreenProbeProfile()) {
    const scale = Math.min(1, bounds.width / width, bounds.height / height);
    return { width: Math.max(2, Math.floor(width * scale / 2) * 2), height: Math.max(2, Math.floor(height * scale / 2) * 2) };
  }





  async function refreshScreenCodecCapabilities(force=false){
  if(screenCodecCapabilityPromise)return screenCodecCapabilityPromise;
  screenCodecCapabilityPromise=(async()=>{
    const [capture,receive]=await Promise.all([loadNativeCapabilities(force),QuicNetNativeVideo.refresh(force)]);
    localScreenEncoderCodecMask=0;
    for(const item of capture.nativeEncoders||[])if(item.Available)localScreenEncoderCodecMask|=codecMaskBit(getCodecId(item.Codec));

    localScreenDecoderCodecMask=receive.local.decoderMask||0;
    updateScreenCodecOptions();syncMediaControls();
    await QP.api('/api/voice/screen-share/capabilities',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({encoderCodecMask:localScreenEncoderCodecMask})});
    return {encoderMask:localScreenEncoderCodecMask,decoderMask:localScreenDecoderCodecMask};
  })();try{return await screenCodecCapabilityPromise;}finally{screenCodecCapabilityPromise=null;}
}

  function updateScreenCodecOptions() {
    const select = document.getElementById('screenCodecSelect');
    if (!select) return;
    select.replaceChildren();
    const calls = Array.isArray(voiceState.activeCalls) ? voiceState.activeCalls : [];
    let unknownPeers = 0;
    const browser = usesBrowserCapture();
    for (const acceleration of browser ? ['gpu', 'cpu', 'browser'] : ['gpu', 'cpu']) {
      const group = document.createElement('optgroup');
      group.label = acceleration === 'browser' ? 'FFmpeg nativo · preferencia del navegador' : (browser ? 'FFmpeg nativo · ' : 'FFmpeg · ') + (acceleration === 'gpu' ? 'GPU · codificación por hardware' : 'CPU · codificación por software');
      for (const definition of screenCodecDefinitions) {
        if (acceleration === 'browser' && !localScreenCodecDetails.get(definition.id)?.browser?.supported) continue;
        const bit = codecMaskBit(definition.id);
        const detail = localScreenCodecDetails.get(definition.id)?.[acceleration];
        const localSupported = usesBrowserCapture() ? detail?.supported : nativeCanEncode(definition.name, acceleration);
        const incompatible = [], unknown = [];
        for (const call of calls) {
          const reported = remoteScreenCodecCapabilities.get(normalizePeerId(call.peerId));
          const mask = reported ? Number(reported.decoderCodecMask || 0) : Number(call.screenDecoderCodecMask || 0);
          if (!mask) unknown.push(call.peerName || 'Contacto');
          else if ((mask & bit) === 0) incompatible.push(call.peerName || 'Contacto');
        }
        unknownPeers = Math.max(unknownPeers, unknown.length);
        const nativeMatches = (nativeCapabilities?.nativeEncoders || []).filter(e => e.Codec === definition.name && encoderHardware(e) === (acceleration === 'gpu'));
        const reason = !localSupported ? (browser ? 'No expuesto por este navegador para el perfil solicitado' : 'No disponible en este equipo') : 'Codificación verificada';
        const receiver = incompatible.length ? ' · Recepción no anunciada por ' + incompatible.join(', ') :
          unknown.length ? ' · Recepción pendiente' : '';
        const option = new Option(definition.label + ' · ' + (acceleration === 'gpu' ? 'GPU' : acceleration === 'cpu' ? 'CPU' : 'Automático') + (localSupported ? '' : ' · No disponible'), definition.name + ':' + acceleration);
        option.disabled = !localSupported;
        option.title = (browser ? (detail?.probe
          ? detail.probe.codecString + ' · ' + detail.probe.hardwarePreference + ' · el navegador puede ignorar la preferencia de aceleración'
          : detail?.reason || reason) : nativeMatches.map(e => e.Id + ': ' + (e.Available ? 'codificación verificada' : e.Reason || 'no disponible')).join(' · ') || reason) + receiver;
        group.append(option);
      }
      select.append(group);
    }
    select.value = codecSelection();
    const previous = Array.from(select.options).find(option => option.value === codecSelection());
    let selectionNotice = '';
    if (!previous || previous.disabled) {
      const available = Array.from(select.options).filter(option => !option.disabled);
      const recommended = available.find(option => option.value === screenShareConfig.codec + ':gpu') ||
        available.find(option => option.value === screenShareConfig.codec + ':cpu') ||
        available.find(option => option.value === screenShareConfig.codec + ':browser');
      if (recommended) {
        readCodecSelection(recommended.value);
        select.value = recommended.value;
        selectionNotice = 'Se ha preseleccionado ' + recommended.label + '. ';
      }
    }
    // Keep the actual encoder and its acceleration label in sync with the validated option.
    syncMediaControls();
    renderCodecMatrix();
    renderCodecCompatibility();
    const count = Array.from(select.options).filter(option => !option.disabled).length;
    const status = document.getElementById('modalHwAccelStatus');
    if (status) status.textContent = count + ' opciones disponibles';
    const help = document.getElementById('screenCodecCompatibility');
    if (help) {
      const profile = getScreenProbeProfile();
      const missing = calls.filter(call => {
        const mask = remoteScreenCodecCapabilities.get(normalizePeerId(call.peerId))?.decoderCodecMask ?? call.screenDecoderCodecMask;
        return mask && !(mask & codecMaskBit(getCodecId(screenShareConfig.codec)));
      });
      help.textContent = selectionNotice + profile.width + '×' + profile.height + ' · ' + profile.fps + ' FPS. ' +
        'Las capacidades de recepción están en la tabla; el perfil se valida al recibir vídeo.';
    }
  }

  function resolveCompatibleScreenCodec(requestedCodec) {
    const requested = screenCodecDefinitions.find(definition => definition.name === requestedCodec);
    const isCompatible = definition => (localScreenEncoderCodecMask & codecMaskBit(definition.id)) !== 0;
    const choice = document.getElementById('screenCodecSelect');
    const selected = choice && Array.from(choice.options).find(option => option.value === codecSelection());
    if (requested && isCompatible(requested) && !selected?.disabled) return requested;
    return null;
  }

  function updateScreenSettingsSubtitle() {
    const subEl = document.getElementById('voiceScreenSettingsSub');
    if (subEl) {
      const resStr = screenShareConfig.resolution === 'native' ? 'Original' : screenShareConfig.resolution + 'p';
      subEl.textContent = `${screenShareConfig.fps} FPS · ${resStr} · ${(screenShareConfig.codec || 'H264').toUpperCase()}`;
    }
  }

  function updateEffectiveFpsStatus(requestedFps, effectiveFps) {
    const el = document.getElementById('modalEffectiveFps');
    if (!el) return;
    el.textContent = effectiveFps < requestedFps
      ? `${effectiveFps} FPS declarados por la captura · objetivo ${requestedFps}`
      : `${effectiveFps} FPS configurados`;
  }

  function loadScreenShareSettings() {
    try {
      const saved = localStorage.getItem('qp_screen_config');
      if (saved) {
        const parsed = JSON.parse(saved);
        if (parsed && typeof parsed === 'object') {
          screenShareConfig = Object.assign(screenShareConfig, parsed);
        }
      }
    } catch { }

    screenShareConfig.backend='ffmpeg';
    if (!screenCodecDefinitions.some(definition => definition.name === screenShareConfig.codec)) {
      screenShareConfig.codec = 'h264';
    }

    const codecSelect = document.getElementById('screenCodecSelect');
    if (codecSelect && screenShareConfig.codec) codecSelect.value = codecSelection();

    const resSelect = document.getElementById('screenResolutionSelect');
    if (resSelect && screenShareConfig.resolution) resSelect.value = screenShareConfig.resolution;

    const fpsSelect = document.getElementById('screenFpsSelect');
    if (fpsSelect && screenShareConfig.fps) fpsSelect.value = String(screenShareConfig.fps);

    const brSelect = document.getElementById('screenBitrateSelect');
    if (brSelect && screenShareConfig.bitrate) brSelect.value = String(screenShareConfig.bitrate);

    for (const [id, key] of [['screenEncoderSelect','encoder'],
      ['screenEncoderPresetSelect','encoderPreset'], ['screenPresetSelect','preset'], ['screenRateControlSelect','rateControl'],
      ['screenGopInput','keyFrameIntervalMs'], ['screenBufferInput','bufferMs'], ['screenQualityInput','quality']]) {
      const el = document.getElementById(id);
      if (el) el.value = String(screenShareConfig[key]);
    }
    updateScreenSettingsSubtitle();
  }

  function selectMediaTab(tab) {

    for (const name of ['Video', 'Audio']) {
      const active = name.toLowerCase() === tab;
      document.getElementById(`media${name}Panel`)?.classList.toggle('hidden', !active);
      const button = document.getElementById(`media${name}Tab`);
      if (button) { button.classList.toggle('active', active); button.setAttribute('aria-selected', String(active)); button.tabIndex = active ? 0 : -1; }
    }
    document.getElementById('saveScreenModalBtn')?.parentElement?.classList.toggle('hidden', tab !== 'video');
  }

  function openScreenSettingsModal(tab = 'video') {
    if (!['video','audio'].includes(tab)) tab = 'video';
    const audioOnly = false;

    const videoTab = document.getElementById('mediaVideoTab');
    if (videoTab) { videoTab.disabled = false; videoTab.setAttribute('aria-disabled', 'false'); }
    if (localVideoKind !== 'camera') loadScreenShareSettings();
    syncOpusPreference();
    loadAudioDevices();
    selectMediaTab(tab);
    syncEncoderTuning();
    // Refresh inventory on every opening; GPU probes remain cached on the server.
    if (!audioOnly) { nativeCapabilities = null; refreshScreenCodecCapabilities(); }
    const modal = document.getElementById('screenShareSettingsModal');
    mountVoiceSettings(); setVoiceDrawer(false); renderCodecCompatibility();
    if (modal) modal.classList.remove('hidden');
  }

  function closeScreenSettingsModal() {
    if (document.body.dataset.page === 'voice') setVoiceDrawer(false);
    const modal = document.getElementById('screenShareSettingsModal');
    if (modal) modal.classList.add('hidden');
    if (settingsReturnToPicker) {
      settingsReturnToPicker = false;
      updateCaptureEncodingSummary();
      document.getElementById('screenNativeCaptureModal')?.classList.remove('hidden');
      requestAnimationFrame(() => document.getElementById('screenCaptureEncodingBtn')?.focus());
    }
  }

  async function saveScreenSettingsModal() {
    // Settings opened from the source picker remain editable before capture starts.
    if (videoSettingsPending || videoStopPending || (videoStartPending && !cancelNativeSourcePicker)) return;
    videoSettingsPending = true;
    renderVoice();
    try { await applyScreenSettingsModal(); }
    catch (error) { QP.toast('No se pudieron guardar los ajustes: ' + error.message, 'error'); }
    finally { videoSettingsPending = false; renderVoice(); }
  }

  async function applyScreenSettingsModal(){
  const wasSharing=nativeSharing;const kind=localVideoKind;const target=activeScreenTargetPeerId;
  screenShareConfig.resolution=document.getElementById('screenResolutionSelect').value;screenShareConfig.fps=Number(document.getElementById('screenFpsSelect').value);screenShareConfig.bitrate=Number(document.getElementById('screenBitrateSelect').value);
  readCodecSelection(document.getElementById('screenCodecSelect').value);readNativeControls();screenShareConfig.backend='ffmpeg';
  try{localStorage.setItem('qp_screen_config',JSON.stringify(screenShareConfig));}catch{}
  if(wasSharing){await stopScreenShare();if(kind==='camera')await startCamera();else await startScreenShare(target,false,true,true);}
  closeScreenSettingsModal();updateScreenSettingsSubtitle();
}

  function getCodecId(codecName) {
    switch ((codecName || '').toLowerCase()) {
      case 'h264': return 1;
      case 'vp9': return 3;
      case 'av1': return 4;
      case 'hevc': return 5;
      default: return 0; // Invalid for new senders; codec 0 is legacy receive-only JPEG.
    }
  }

  function getCodecNameFromId(codecId) {
    switch (codecId) {
      case 1: return 'H.264 / AVC';
      case 3: return 'VP9';
      case 4: return 'AV1';
      case 5: return 'H.265 / HEVC';
      default: return 'JPEG';
    }
  }

  function getCodecKeyFromId(codecId) {
    const definition = screenCodecDefinitions.find(item => item.id === codecId);
    return definition ? definition.name : 'h264';
  }

  // --- High-Quality Screen Sharing Implementation ---

  async function toggleScreenShare() {
    if (localVideoKind === 'camera') { await stopScreenShare(); await startScreenShare(); return; }
    if (nativeSharing || screenStream || voiceState.isScreenSharing) {
      await stopScreenShare();
    } else {
      await startScreenShare();
    }
  }

  async function startScreenShare(targetPeerId=null,unused=false,reuseNativeSource=false,settingsRestart=false){
  if(nativeStartPending||videoStartPending||videoStopPending||(videoSettingsPending&&!settingsRestart))return;
  if(!activeCallsList().length){QP.toast('Inicia una llamada antes de compartir.','warning');return;}
  videoStartPending=true;const operation=++videoOperation;const cancelled=()=>operation!==videoOperation||!activeCallsList().length;
  try{localVideoKind='screen';screenShareConfig.backend='ffmpeg';await loadNativeCapabilities();if(cancelled())return;
    if(androidCapture){const p=getScreenProbeProfile();const result=await QP.api('/api/voice/screen-share/platform-start',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({...screenShareConfig,encoder:'auto',encoderPreset:null,source:'desktop',width:p.width,height:p.height,fps:Math.min(60,p.fps),targetPeerId})});
      if(cancelled()){await stopScreenShare();return;}const value=result.profile;nativeSharing=true;voiceState.isScreenSharing=true;activeScreenTargetPeerId=targetPeerId;createOrUpdateScreenTile('local','Tú · Pantalla',value.Width||value.width,value.Height||value.height,value.CodecId||value.codecId,value.Fps||value.fps,true);
    }else await startNativeScreenShare(targetPeerId,reuseNativeSource,cancelled);
  }catch(error){QP.toast('No se pudo iniciar la captura nativa: '+error.message,'error');}finally{videoStartPending=false;renderVoice();}
}

  async function toggleCamera(){if(localVideoKind==='camera'&&nativeSharing)await stopScreenShare();else await chooseCamera();}

  function readCameraChoice(value) {
    if (value === 'facing:user' || value === 'facing:environment') {
      cameraFacing = value.slice(7); cameraDeviceId = '';
    } else cameraDeviceId = value;
  }

  async function refreshCameraChoices(selectId,selected=cameraDeviceId){const devices=await QP.api('/api/voice/cameras');const select=document.getElementById(selectId);if(!select)return;select.replaceChildren(...devices.map(d=>new Option(d.name,d.id)));if(devices.some(d=>d.id===selected))select.value=selected;}

  async function chooseCamera() {
    if (videoStartPending || videoSettingsPending || videoStopPending || cameraPickerPending || !activeCallsList().length) return;
    const dialog = document.getElementById('voiceCameraPicker');
    if (dialog.open) return;
    cameraPickerPending = true; renderVoice();
    try {
      await refreshCameraChoices('voiceCameraPickerSelect');
      if (!activeCallsList().length) return;
      dialog.showModal(); document.getElementById('voiceCameraPickerSelect').focus();
    } catch (error) { QP.toast('No se pudieron consultar las cámaras: ' + error.message, 'error'); }
    finally { cameraPickerPending = false; renderVoice(); }
  }



  async function startCamera(){
  if(videoStartPending||videoStopPending||!activeCallsList().length)return;
  const source=cameraDeviceId||document.getElementById('voiceCameraPickerSelect')?.value;if(!source){QP.toast('Elige una cámara nativa.','warning');return;}
  if(nativeSharing||voiceState.isScreenSharing)await stopScreenShare();
  videoStartPending=true;const operation=++videoOperation;
  try{localVideoKind='camera';const p=getScreenProbeProfile();const settings={...screenShareConfig,source,width:p.width,height:p.height,fps:Math.min(60,p.fps),targetPeerId:null};if(androidCapture)Object.assign(settings,{encoder:'auto',encoderPreset:null});
    const result=await QP.api('/api/voice/screen-share/'+(androidCapture?'platform-start':'native-start'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(settings)});
    if(operation!==videoOperation||!activeCallsList().length){await stopScreenShare();return;}
    const value=result.profile;nativeSharing=true;voiceState.isScreenSharing=true;cameraDeviceId=source;
    createOrUpdateScreenTile('local','Tú · Cámara',value.Width||value.width,value.Height||value.height,value.CodecId||value.codecId,value.Fps||value.fps,true);
    await refreshCameraChoices('voiceCameraSelect',source);
  }catch(error){QP.toast('No se pudo activar la cámara: '+error.message,'error');}finally{videoStartPending=false;cameraSwitchPending=false;renderVoice();}
}

  const verifiedEncoderProfiles = new Map();





  // Browser timers require their Window receiver; calling a detached timer as
  // timing.schedule(...) raises "Illegal invocation" in Chromium/WebView2.






  function stopCanvasFrameExtraction(){screenCaptureActive=false;screenTransmissionReady=false;return Promise.resolve();}

  function stopScreenShare() {
    ++videoOperation;
    cancelNativeSourcePicker?.();
    if (videoStopPending) return videoStopPending;
    const stopping = stopScreenShareCore();
    videoStopPending = stopping; renderVoice();
    const finished = () => { if (videoStopPending === stopping) videoStopPending = null; renderVoice(); };
    stopping.then(finished, finished);
    return stopping;
  }

  async function stopScreenShareCore() {
    clearDecoderWarnings(true);
    const wasSharing = !!screenStream || nativeSharing || !!voiceState.isScreenSharing;
    const wasCamera = localVideoKind === 'camera';
    localVideoKind = 'screen';
    if (beforeCameraConfig) { screenShareConfig = beforeCameraConfig; beforeCameraConfig = null; }
    nativeSharing = false;
    browserCaptureSelected = false;
    browserSourceBounds = null;
    stopScreenAudioCapture();
    const captureStopped = stopCanvasFrameExtraction();

    if (screenStream) {
      screenStream.getTracks().forEach(t => {
        try { t.onended = null; t.stop(); } catch { }
      });
      screenStream = null;
    }
    removeScreenTile('local');
    await captureStopped;

    try {
      const requestId = crypto.randomUUID();
      localVideoStopRequests.add(requestId);
      if (localVideoStopRequests.size > 64) localVideoStopRequests.delete(localVideoStopRequests.values().next().value);
      await QP.api('/api/voice/screen-share/stop', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ requestId })
      });
    } catch { }

    voiceState.isScreenSharing = false;
    activeScreenTargetPeerId = null;
    renderVoice();

    removeScreenTile('local');
    if (wasSharing && !cameraSwitchPending) QP.toast(wasCamera ? 'Cámara desactivada' : 'Has dejado de compartir la pantalla');
  }

  // --- Dynamic Multi-User Stream Grid Manager ---

  function initScreenViewer(entry,body,media,infoButton,resetButton){
    const setInfo=visible=>{entry.cardEl.classList.toggle('show-stream-info',visible);infoButton.setAttribute('aria-expanded',String(visible));};
    infoButton.addEventListener('click',()=>setInfo(!entry.cardEl.classList.contains('show-stream-info')));
    resetButton.hidden=true;setInfo(false);
    body.setAttribute('role','img');body.setAttribute('aria-label','Vídeo presentado por FFmpeg en una superficie nativa');
    return {reset(){QuicNetNativeVideo.schedule();},setInfo,refresh(){QuicNetNativeVideo.schedule();},destroy(){}};
  }

  function getOrInitScreenGrid() {
    const section = document.getElementById('screenShareGridSection');
    const grid = document.getElementById('screenShareGrid');
    return { section, grid };
  }

  function updateGridState() {
    const { section, grid } = getOrInitScreenGrid();
    if (!section || !grid) return;

    const host = document.getElementById('activeCalls');
    // Reuse participant nodes and menus so volume/focus and media survive updates.
    for (const [id, participant] of participantCards) {
      const stream = activeScreenStreams.get(normalizePeerId(id));
      participant.card.classList.toggle('hidden', !!stream);
      const parent = activeScreenStreams.size && !stream ? grid : host;
      if (parent && participant.card.parentElement !== parent) parent.append(participant.card);
      const menuParent = stream?.cardEl.querySelector('.screen-tile-actions') || participant.card;
      if (participant.menu.parentElement !== menuParent) menuParent.append(participant.menu);
      stream?.cardEl.classList.toggle('is-speaking', talkingPeers.has(id));
    }
    const self = document.querySelector('.voice-self-tile');
    if (self) {
      const local = [...activeScreenStreams.values()].find(entry => entry.isLocal);
      self.classList.toggle('hidden', !!local);
      const parent = activeScreenStreams.size && !local ? grid : host;
      if (parent && self.parentElement !== parent) parent.append(self);
      local?.cardEl.classList.toggle('is-speaking', localSpeaking);
    }
    host?.classList.toggle('hidden', !!activeScreenStreams.size || !activeCallsList().length);
    const count = grid.querySelectorAll(':scope > :not(.hidden)').length;
    document.querySelector('.voice-stage')?.classList.toggle('has-video', count > 0);
    if (count === 0) {
      section.classList.add('hidden');
      grid.classList.remove('single-stream', 'theater-mode');
    } else {
      section.classList.remove('hidden');
      for (const name of ['grid-1', 'grid-2', 'grid-4', 'grid-many']) grid.classList.remove(name);
      grid.classList.add(count === 1 ? 'grid-1' : count === 2 ? 'grid-2' : count <= 4 ? 'grid-4' : 'grid-many');
    }
  }

  function paintScreenAvatar(entry) {
    const snapshot = voiceStatus || QP.state?.status;
    const peer = entry.isLocal ? snapshot?.node :
      (snapshot?.peers || []).find(peer => normalizePeerId(peer.id) === entry.peerId) ||
      (snapshot?.savedPeers || []).find(peer => normalizePeerId(peer.id) === entry.peerId);
    QP.paintAvatar?.(entry.avatarEl, peer || {id: entry.isLocal ? '' : entry.peerId, name: entry.peerName}, snapshot, 'voice-stream');
  }

  function createOrUpdateScreenTile(peerId, peerName, width, height, codecId, fps, isLocal) {
    const { grid } = getOrInitScreenGrid();
    if (!grid) return null;

    const normId = normalizePeerId(peerId);
    let entry = activeScreenStreams.get(normId);
    const resolvedName = peerName || resolvePeerName(normId);

    if (!entry) {
      const card = QP.el('div', 'screen-tile');
      card.dataset.streamPeerId = normId;
      card.setAttribute('role', 'group'); card.setAttribute('aria-label', resolvedName);
      if (isLocal) card.classList.add('is-local-stream');

      // 1. Header (User Info + Badges + Controls)
      const header = QP.el('div', 'screen-tile-header');
      const userBox = QP.el('div', 'screen-tile-user');

      const avatar = QP.el('div', 'avatar', isLocal ? 'TÚ' : QP.initials(resolvedName));
      const metaBox = QP.el('div', 'screen-tile-meta');
      const nameEl = QP.el('span', 'screen-tile-name', resolvedName);
      const codecBadge = QP.el('span', 'screen-tile-codec', isLocal
        ? `Transmitiendo · ${getCodecNameFromId(codecId)} · ${width}×${height} · ${fps} FPS`
        : `Conectando · ${getCodecNameFromId(codecId)}…`);
      metaBox.append(nameEl, codecBadge);
      userBox.append(avatar, metaBox);

      const actions = QP.el('div', 'screen-tile-actions');
      const liveBadge = QP.el('span', 'badge green screen-tile-live-badge', isLocal ? 'Tu vídeo' : 'En directo');
      const infoButton = QP.el('button', 'btn ghost sm screen-info-btn', 'ⓘ');
      infoButton.type = 'button'; infoButton.title = 'Información del vídeo';
      infoButton.setAttribute('aria-label', infoButton.title);
      codecBadge.id = `stream-info-${normId}`; infoButton.setAttribute('aria-controls', codecBadge.id);
      const resetButton = QP.el('button', 'btn ghost sm screen-zoom-reset', '100%');
      resetButton.type = 'button'; resetButton.title = 'Ajustar imagen completa';
      actions.append(infoButton, resetButton);

      let muteBtn = null;
      let volSlider = null;
      let volGroup = null;

      if (!isLocal) {
        volGroup = QP.el('div', 'screen-tile-vol-group');
        muteBtn = document.createElement('button');
        muteBtn.className = 'btn ghost sm icon-only-btn screen-mute-btn';
        muteBtn.replaceChildren(voiceIcon('volume'));
        muteBtn.title = 'Silenciar audio del vídeo'; muteBtn.setAttribute('aria-label', muteBtn.title);

        volSlider = document.createElement('input');
        volSlider.type = 'range';
        volSlider.min = '0';
        volSlider.max = '100';
        volSlider.value = '100';
        volSlider.className = 'screen-tile-volume';
        volSlider.title = 'Volumen del vídeo'; volSlider.setAttribute('aria-label', volSlider.title);

        volGroup.append(muteBtn, volSlider);
      }

      const theaterBtn = document.createElement('button');
      theaterBtn.className = 'btn ghost sm icon-only-btn';
      theaterBtn.append(voiceIcon('focus'));
      theaterBtn.title = 'Destacar este vídeo'; theaterBtn.setAttribute('aria-label', theaterBtn.title);
      theaterBtn.addEventListener('click', () => toggleTheaterStream(normId));

      const fullscreenBtn = document.createElement('button');
      fullscreenBtn.className = 'btn ghost sm icon-only-btn screen-fullscreen-btn';
      fullscreenBtn.type = 'button';
      fullscreenBtn.setAttribute('aria-pressed', 'false');
      fullscreenBtn.append(voiceIcon('expand'));
      fullscreenBtn.title = 'Pantalla completa'; fullscreenBtn.setAttribute('aria-label', fullscreenBtn.title);
      fullscreenBtn.addEventListener('click', () => toggleTileFullscreen(card));

      if (volGroup) {
        actions.append(liveBadge, volGroup, theaterBtn, fullscreenBtn);
      } else {
        actions.append(liveBadge, theaterBtn, fullscreenBtn);
      }

      if (isLocal) {
        const stopBtn = document.createElement('button');
        stopBtn.className = 'btn sm danger';
        stopBtn.append(voiceIcon('stop'));
        stopBtn.title = 'Detener mi vídeo'; stopBtn.setAttribute('aria-label', stopBtn.title);
        stopBtn.addEventListener('click', stopScreenShare);
        actions.append(stopBtn);
      }

      header.append(userBox, actions);

      // Native presentation viewport; the WebView contains only controls.
      const body = QP.el('div', 'screen-tile-body');
      const videoEl = document.createElement('div');

      // Hidden compatibility placeholders contain no decoded pixels.
      const canvasEl = document.createElement('div');
      canvasEl.className = 'hidden';
      const canvasCtx = null;

      const loadingEl = QP.el('div', 'screen-tile-loading', 'Conectando vídeo…');

      // Encoded pixel dimensions must never determine the viewer's layout size.
      const media = QP.el('div', 'screen-tile-media');
      media.append(videoEl, canvasEl);
      body.append(media, loadingEl);
      card.append(header, body);
      grid.appendChild(card);

      const mediaSource=null, sourceBuffer=null, bufferQueue=[]; let isAppending=false;
      entry = {
        peerId: normId,
        peerName: resolvedName,
        width,
        height,
        codecId,
        fps,
        mediaSource,
        getSourceBuffer: () => sourceBuffer,
        bufferQueue,
        isAppendingRef: () => isAppending,
        setIsAppending: (val) => { isAppending = val; },
        videoEl,
        canvasEl,
        canvasCtx,
        loadingEl,
        cardEl: card,
        codecBadge,
        nameEl,
        avatarEl: avatar,
        isLocal,
        audioContext: null,
        gainNode: null,
        audioVolume: 1.0,
        audioMuted: false,
        audioPlayer: null,
        audioPending: [],
        audioPendingFrames: 0,
        audioSetup: null,
        audioFailed: false,
        volSlider,
        muteBtn
      };
      entry.viewer = initScreenViewer(entry, body, media, infoButton, resetButton, codecBadge);
      QuicNetNativeVideo.attach(entry, body);

      if (muteBtn && volSlider) {
        muteBtn.addEventListener('click', (e) => {
          e.stopPropagation();
          entry.audioMuted = !entry.audioMuted;
          resetScreenAudioPlayback(entry);
          muteBtn.replaceChildren(voiceIcon('volume', entry.audioMuted));
          muteBtn.title = entry.audioMuted ? 'Unmute stream audio' : 'Mute stream audio';
          muteBtn.classList.toggle('is-muted', entry.audioMuted);
          updateScreenAudioGain(entry);
          if (entry.audioContext && entry.audioContext.state === 'suspended') {
            entry.audioContext.resume().catch(() => {});
          }
        });

        volSlider.addEventListener('input', (e) => {
          e.stopPropagation();
          const val = parseFloat(volSlider.value) / 100;
          entry.audioVolume = val;
          if (val === 0) resetScreenAudioPlayback(entry);
          updateScreenAudioGain(entry);
          if (val === 0) {
            muteBtn.replaceChildren(voiceIcon('volume', true));
          } else if (!entry.audioMuted) {
            muteBtn.replaceChildren(voiceIcon('volume'));
          }
          if (entry.audioContext && entry.audioContext.state === 'suspended') {
            entry.audioContext.resume().catch(() => {});
          }
        });
      }

      card.addEventListener('click', () => {
        if (entry && entry.audioContext && entry.audioContext.state === 'suspended') {
          entry.audioContext.resume().catch(() => {});
        }
      });

      activeScreenStreams.set(normId, entry);

      updateGridState();
    } else {
      // Update metadata
      entry.width = width;
      entry.height = height;
      entry.codecId = codecId;
      entry.fps = fps;
      if (entry.nameEl && resolvedName) {
        entry.peerName = resolvedName;
        entry.nameEl.textContent = resolvedName;
        entry.cardEl.setAttribute('aria-label', resolvedName);
      }
      if (entry.codecBadge && (!entry.stats || entry.stats.fps === 0)) {
        entry.codecBadge.textContent = isLocal
          ? `Transmitiendo · ${getCodecNameFromId(codecId)} · ${width}×${height} · ${fps} FPS`
          : `Conectando · ${getCodecNameFromId(codecId)}…`;
      }
      if (isLocal && screenStream && entry.videoEl && !entry.videoEl.srcObject) {
        entry.videoEl.srcObject = screenStream;
        entry.videoEl.play().catch(() => {});
        entry.videoEl.classList.remove('hidden');
        if (entry.canvasEl) entry.canvasEl.classList.add('hidden');
      }
    }

    paintScreenAvatar(entry);
    updateDecoderWarningForStream(entry);
    return entry;
  }

  function removeScreenTile(peerId) {
    QuicNetNativeVideo.detach(normalizePeerId(peerId));
    if (!peerId) return;
    const normId = normalizePeerId(peerId);
    let entry = activeScreenStreams.get(normId);

    if (!entry) {
      for (const [k, v] of activeScreenStreams.entries()) {
        if (k.includes(normId) || normId.includes(k)) {
          entry = v;
          break;
        }
      }
    }

    if (!entry) return;

    entry.viewer?.destroy();
    for (const participant of participantCards.values()) {
      if (entry.cardEl.contains(participant.menu)) participant.card.append(participant.menu);
    }
    if (entry.cardEl === fullscreenTile) exitTileFullscreen();
    if (entry.cardEl && entry.cardEl.parentNode) {
      entry.cardEl.parentNode.removeChild(entry.cardEl);
    }
    if (entry.videoEl) {
      try {
        if (entry.videoFrameCallbackId != null) entry.videoEl.cancelVideoFrameCallback?.(entry.videoFrameCallbackId);
        entry.videoEl.pause?.();
        entry.videoEl.src = '';
        entry.videoEl.srcObject = null;
      } catch { }
    }
    if (entry.videoDecoder) {
      try { entry.videoDecoder.close(); } catch { }
      entry.videoDecoder = null;
    }
    resetScreenAudioPlayback(entry);
    entry.audioPlayer?.destroy(); entry.audioPlayer = null;
    if (entry.audioContext) {
      entry.audioContext.onstatechange = null;
      try { entry.audioContext.close(); } catch { }
      entry.audioContext = null;
      entry.gainNode = null;
    }
    activeScreenStreams.delete(entry.peerId);
    updateGridState();
  }

  function toggleTheaterStream(peerId) {
    const { grid } = getOrInitScreenGrid();
    if (!grid) return;

    const normId = normalizePeerId(peerId);
    const isTheater = grid.classList.contains('theater-mode');
    if (isTheater) {
      grid.classList.remove('theater-mode');
      grid.querySelectorAll('.screen-tile').forEach(t => t.classList.remove('theater-focused'));
    } else {
      grid.classList.add('theater-mode');
      grid.querySelectorAll('.screen-tile').forEach(t => {
        if (normalizePeerId(t.dataset.streamPeerId) === normId) {
          t.classList.add('theater-focused');
          grid.prepend(t);
        } else {
          t.classList.remove('theater-focused');
        }
      });
    }
  }

  function currentFullscreenElement() {
    return document.fullscreenElement || document.webkitFullscreenElement;
  }

  function clearTileFullscreen() {
    ++tileFullscreenRevision;
    const tile = fullscreenTile;
    fullscreenTile = null; tileFullscreenMode = '';
    if (!tile) return;
    tile.classList.remove('is-tile-fullscreen');
    activeScreenStreams.get(tile.dataset.streamPeerId)?.viewer?.reset();
    const button = tile.querySelector('.screen-fullscreen-btn');
    if (button) {
      button.disabled = false;
      button.title = 'Pantalla completa'; button.setAttribute('aria-label', button.title);
      button.setAttribute('aria-pressed', 'false'); button.replaceChildren(voiceIcon('expand'));
      if (tile.isConnected && document.body.dataset.page === 'voice') button.focus({ preventScroll: true });
    }
  }

  async function exitTileFullscreen() {
    const nativeTile = fullscreenTile && currentFullscreenElement() === fullscreenTile;
    clearTileFullscreen();
    if (nativeTile) {
      try {
        if (document.exitFullscreen) await document.exitFullscreen();
        else document.webkitExitFullscreen?.();
      } catch (error) { console.warn('No se pudo salir de pantalla completa:', error); }
    }
  }

  async function toggleTileFullscreen(tile) {
    if (fullscreenTile === tile) { await exitTileFullscreen(); return; }
    if (fullscreenTile) await exitTileFullscreen();
    if (!tile.isConnected) return;
    const revision = ++tileFullscreenRevision;
    fullscreenTile = tile; tileFullscreenMode = 'fallback';
    tile.classList.add('is-tile-fullscreen');
    const viewer = activeScreenStreams.get(tile.dataset.streamPeerId)?.viewer;
    viewer?.setInfo(false); viewer?.reset();
    const button = tile.querySelector('.screen-fullscreen-btn');
    button.title = 'Salir de pantalla completa'; button.setAttribute('aria-label', button.title);
    button.setAttribute('aria-pressed', 'true'); button.replaceChildren(voiceIcon('close'));
    button.focus({ preventScroll: true });
    // Keep the controls with the media, including streams decoded onto a canvas.
    // Unsupported or rejected native requests still expand inside the app.
    try {
      if (tile.requestFullscreen && document.fullscreenEnabled !== false) {
        button.disabled = true; await tile.requestFullscreen();
      } else if (tile.webkitRequestFullscreen) {
        tile.webkitRequestFullscreen();
      }
    } catch (error) { console.info('Vídeo ampliado dentro de la aplicación:', error.message); }
    finally { button.disabled = false; }
    if (revision !== tileFullscreenRevision && currentFullscreenElement() === tile) {
      try {
        if (document.exitFullscreen) await document.exitFullscreen();
        else document.webkitExitFullscreen?.();
      } catch { /* The stream may already have been removed. */ }
    }
  }

  const screenAudioCapture = QuicNetMedia.createScreenAudioCapture({
    api: (...args) => QP.api(...args), sendBinary: packet => QP.sendBinary(packet),
    toast: (...args) => QP.toast(...args), getCapabilities: () => nativeCapabilities
  });
  const {start: startScreenAudioCapture, stop: stopScreenAudioCapture} = screenAudioCapture;
  const {play: playScreenAudioChunk, reset: resetScreenAudioPlayback, updateGain: updateScreenAudioGain} =
    QuicNetMedia.createScreenAudioPlayback({getStreams: () => activeScreenStreams, normalizePeerId, toast: (...args) => QP.toast(...args)});
  function recordScreenFrameRendered(entry) {
    if (activeScreenStreams.get(entry.peerId) !== entry) return;
    entry.renderedFrameCount = (entry.renderedFrameCount || 0) + 1;
    entry.playbackCountUnavailable = false;
    entry.loadingEl?.classList.add('hidden');
  }

  // --- WebSocket Stream Chunk Delivery ---

  function onWsBinaryReceived(arrayBuffer){if(!arrayBuffer||arrayBuffer.byteLength<17)return;const view=new Uint8Array(arrayBuffer);if(view[0]===4)playScreenAudioChunk(parseGuidFromBytes(view,1),view.subarray(17));}

  function onScreenShareStarted(data) {
    if (!data) return;
    const isLocal = data.peerId === 'local' || data.peerId === 'me';
    const normId = normalizePeerId(data.peerId);
    let peerName = data.peerName;
    if (isLocal && data.kind === 'camera') localVideoKind = 'camera';
    if (!peerName || peerName === 'Contacto' || peerName === 'Peer') {
      peerName = resolvePeerName(normId);
    }
    createOrUpdateScreenTile(normId, (isLocal ? 'Tú' : peerName) + (data.kind === 'camera' ? ' · Cámara' : ' · Pantalla'), data.width || 1280, data.height || 720, data.codecId ?? 1, data.fps || 30, isLocal);
  }

  function onScreenShareDetenido(data) {
    if (!data) return;
    const normId = normalizePeerId(data.peerId);
    if (normId === 'local' || normId === 'me') {
      // This stop was already applied locally. Its delayed server acknowledgement
      // must not stop a new capture that has since taken over the camera.
      if (data.requestId && localVideoStopRequests.has(data.requestId)) return;
      clearDecoderWarnings(true);
      ++videoOperation;
      cancelNativeSourcePicker?.();
      localVideoKind = 'screen';
      if (beforeCameraConfig) { screenShareConfig = beforeCameraConfig; beforeCameraConfig = null; }
      nativeSharing = false;
      browserCaptureSelected = false;
      browserSourceBounds = null;
      voiceState.isScreenSharing = false;
      stopCanvasFrameExtraction();
      stopScreenAudioCapture();
      screenStream?.getTracks().forEach(track => track.stop());
      screenStream = null;
      renderVoice();
    }
    clearDecoderWarnings(false, normId);
    const peerName = resolvePeerName(normId);
    removeScreenTile(normId);
    QP.toast(`${peerName || 'A participant'} stopped screen sharing`);
  }

  function onScreenFrameReceived(data) {
    // Handled primarily via high-efficiency WebSocket binary stream
  }
})();
