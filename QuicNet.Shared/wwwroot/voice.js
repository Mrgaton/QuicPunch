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
  let voiceSettingsHome = null;
  let screenDecoderAcceleration = 'no-preference';
  let decoderPreferenceRevision = 0;
  try {
    const saved = localStorage.getItem('qp_screen_decoder_acceleration');
    if (['no-preference', 'prefer-hardware', 'prefer-software'].includes(saved)) screenDecoderAcceleration = saved;
  } catch { }
  let fullscreenTile = null;
  let tileFullscreenMode = '';
  let tileFullscreenRevision = 0;

  // Keep one set of device controls: embedded in the call room, available as
  // the existing dialog when opened from another page's global call controls.
  function mountVoiceSettings(inRoom) {
    const settings = document.getElementById('screenShareSettingsModal');
    const host = document.getElementById('voiceDrawerSettings');
    if (!settings || !host) return;
    if (!voiceSettingsHome) { voiceSettingsHome = document.createComment('Call settings home'); settings.before(voiceSettingsHome); }
    if (inRoom) {
      settings.classList.remove('modal-backdrop');
      if (settings.parentElement !== host) host.append(settings);
      settings.setAttribute('role', 'region'); settings.removeAttribute('aria-modal');
    } else {
      settings.classList.add('modal-backdrop', 'hidden');
      voiceSettingsHome.after(settings);
      settings.setAttribute('role', 'dialog'); settings.setAttribute('aria-modal', 'true');
      settings.classList.add('hidden'); setVoiceDrawer(false);
    }
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
    for (const name of ['Contacts', 'Settings']) {
      const selected = tab === name.toLowerCase();
      const button = document.getElementById(`voice${name}Tab`);
      button?.setAttribute('aria-selected', String(selected));
      if (button) button.tabIndex = selected ? 0 : -1;
      document.getElementById(`voiceDrawer${name}`).hidden = !selected;
    }
    document.getElementById('voiceInviteBtn')?.setAttribute('aria-expanded', String(open && tab === 'contacts'));
    document.getElementById('voiceScreenSettingsBtn')?.setAttribute('aria-expanded', String(open && tab === 'settings'));
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
      const settings = event.key === 'End' || (event.key !== 'Home' && voiceDrawerTab !== 'settings');
      document.getElementById(settings ? 'voiceSettingsTab' : 'voiceContactsTab').click();
      document.getElementById(settings ? 'voiceSettingsTab' : 'voiceContactsTab').focus();
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
  let videoOperation = 0;
  let videoStopPending = null;
  let videoCaptureCleanup = Promise.resolve();
  const localVideoStopRequests = new Set();
  let cameraPickerPending = false;
  let cameraFacing = 'user';
  let cameraDeviceId = '';
  try { cameraDeviceId = localStorage.getItem('quicnet.cameraDeviceId') || ''; } catch { }
  let beforeCameraConfig = null;
  const androidCapture = /Android/i.test(navigator.userAgent);
  let pendingRemoteScreenPeerId = null;
  let activeScreenTargetPeerId = null;
  let screenCaptureInterval = null;
  let screenCaptureActive = false;
  let screenTransmissionReady = false;
  let screenCaptureVideo = null;
  let screenCaptureAnimId = null;
  let screenVideoEncoder = null;
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

  function browserTransmissionSettings() {
    const track = screenStream?.getVideoTracks()[0]?.getSettings() || {};
    const profile = getScreenProbeProfile();
    return { codec: screenShareConfig.codec, width: track.width || profile.width, height: track.height || profile.height,
      fps: Number(screenShareConfig.fps), bitrate: Number(screenShareConfig.bitrate), encoder: 'auto',
      preset: screenShareConfig.preset, rateControl: screenShareConfig.rateControl, quality: screenShareConfig.quality,
      keyFrameIntervalMs: screenShareConfig.keyFrameIntervalMs, bufferMs: screenShareConfig.bufferMs };
  }

  async function publishBrowserScreenState() {
    if (nativeSharing || remoteBrowserChangeInFlight || !screenStream || !voiceState.isScreenSharing) return;
    const codecs = screenCodecDefinitions.filter(d => localScreenCodecDetails.get(d.id)?.supported).map(d => d.name);
    const settings = browserTransmissionSettings();
    // Capture APIs may report odd dimensions. The encoder uses even dimensions.
    settings.width -= settings.width % 2; settings.height -= settings.height % 2;
    await QP.api('/api/voice/screen-share/browser-state', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ settings, codecs }) });
  }

  async function applyRemoteBrowserSettings(data) {
    let success = false;
    if (remoteBrowserChangeInFlight || !screenStream || nativeSharing || !voiceState.isScreenSharing) return;
    remoteBrowserChangeInFlight = true;
    const previous = { ...screenShareConfig };
    try {
      if (!screenStream || nativeSharing || !voiceState.isScreenSharing) throw new Error('No hay captura web activa.');
      const q = data.settings;
      screenShareConfig = { ...screenShareConfig, codec: q.Codec, resolution: String(q.Height), fps: q.Fps,
        bitrate: q.Bitrate, preset: q.Preset, rateControl: q.RateControl, quality: q.Quality,
        keyFrameIntervalMs: q.KeyFrameIntervalMs, bufferMs: q.BufferMs };
      const track = screenStream.getVideoTracks()[0];
      screenTransmissionReady = false;
      await track.applyConstraints({ width: { ideal: q.Width }, height: { ideal: q.Height }, frameRate: { ideal: q.Fps, max: q.Fps } });
      const actual = track.getSettings();
      const { width, height } = fitScreenOutput(actual.width || q.Width, actual.height || q.Height, { width: q.Width, height: q.Height });
      const profile = await startCanvasFrameExtraction(screenStream, width, height, getCodecId(q.Codec), q.Fps);
      const compatible = await QP.api('/api/voice/screen-share/decoder-check', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ codec: profile.codecString, width, height, targetPeerId: activeScreenTargetPeerId }) });
      if (!compatible.supported) throw new Error('El receptor no admite este perfil.');
      await QP.api('/api/voice/screen-share/start', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ width, height, codecId: profile.codecId, fps: profile.fps, targetPeerId: activeScreenTargetPeerId, kind: localVideoKind }) });
      screenTransmissionReady = true;
      screenShareConfig.fps = profile.fps;
      try { localStorage.setItem('qp_screen_config', JSON.stringify(screenShareConfig)); } catch {}
      updateScreenSettingsSubtitle();
      createOrUpdateScreenTile('local', (localVideoKind === 'camera' ? 'Tú · Cámara' : 'Tú · Pantalla'), width, height, profile.codecId, profile.fps, true);
      success = true;
      QP.toast('El peer autorizado ha actualizado los ajustes de tu transmisión.', 'success');
    } catch (e) {
      screenShareConfig = previous;
      await stopScreenShare();
      QP.toast(e.message, 'error');
    } finally {
      remoteBrowserChangeInFlight = false;
      await publishBrowserScreenState().catch(() => {});
      await QP.api('/api/voice/screen-share/browser-result', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ requestId: data.requestId, success }) }).catch(() => {});
    }
  }

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
  function chooseConcreteDefaults() {
    const encoders = nativeCapabilities?.nativeEncoders || [];
    if (!['ffmpeg', 'web'].includes(screenShareConfig.backend)) {
      screenShareConfig.backend = encoders.some(e => e.Available) ? 'ffmpeg' : 'web';
      const recommended = encoders.find(e => e.Available && e.Codec === 'h264' && encoderHardware(e)) ||
        encoders.find(e => e.Available && e.Codec === 'h264') || encoders.find(e => e.Available);
      if (recommended) {
        screenShareConfig.encoder = recommended.Id;
        screenShareConfig.codec = recommended.Codec;
        screenShareConfig.acceleration = encoderHardware(recommended) ? 'gpu' : 'cpu';
      }
    }
    const backend = document.getElementById('screenBackendSelect');
    if (backend) backend.value = screenShareConfig.backend;
    const primary = nativeCapabilities?.displays?.find(d => d.Primary);
    if (primary && (!screenShareConfig.source || screenShareConfig.source === 'dxgi:0'))
      screenShareConfig.source = 'display:' + primary.Id;
    if (!screenShareConfig.source) screenShareConfig.source = 'desktop';
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
    for (const [id, key] of [['screenBackendSelect','backend'], ['screenEncoderSelect','encoder'],
      ['screenEncoderPresetSelect','encoderPreset'], ['screenPresetSelect','preset'], ['screenRateControlSelect','rateControl']]) {
      const el = document.getElementById(id);
      if (el) screenShareConfig[key] = el.value;
    }
    for (const [id, key] of [['screenGopInput','keyFrameIntervalMs'], ['screenBufferInput','bufferMs'], ['screenQualityInput','quality']]) {
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
    instructions.textContent = 'En el equipo que ejecuta QuicNet, abre una terminal y ejecuta una de estas opciones. Después pulsa «Ya lo he instalado · comprobar». Mientras tanto puedes usar la captura del navegador.';
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
        const nativeOption = Array.from(document.getElementById('screenBackendSelect')?.options || []).find(option => option.value === 'ffmpeg');
        if (nativeOption) nativeOption.disabled = !data.nativeCaptureAvailable;
        chooseConcreteDefaults();
        const help = document.getElementById('screenNativeStatus');
        if (help) help.textContent = data.nativeCaptureAvailable
          ? 'FFmpeg codifica con el componente seleccionado. GPU y CPU se prueban por separado; el perfil exacto se comprueba al iniciar.'
          : data.ffmpeg?.message || 'FFmpeg no disponible: ' + (data.nativeEncoders?.find(e => e.Reason)?.Reason || 'no hay codificadores operativos en este dispositivo.');
        if (status) status.textContent = `Equipo consultado · ${(data.displays || []).length} pantalla(s) · audio ${data.audioBackend || 'del dispositivo'}`;
        return data;
      }).catch(error => {
        nativeCapabilities = null; // A temporary backend failure must be retryable.
        if (status) status.textContent = 'No se pudo consultar el equipo. Puedes volver a detectar o usar el navegador.';
        return { nativeEncoders: [], error: error.message };
      }).finally(() => { nativeCapabilitiesPromise = null; });
    return nativeCapabilitiesPromise;
  }

  function usesBrowserCapture() {
    return browserCaptureSelected || screenShareConfig.backend === 'web';
  }

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
    if (priority) priority.disabled = browser || !!screenShareConfig.encoderPreset;
    const help = document.getElementById('screenEncoderPresetHelp');
    const automatic = /_nvenc$/.test(encoder) ? {speed:'p1',balanced:'p3',quality:'p5'} :
      encoder === 'libx264' ? {speed:'ultrafast',balanced:'veryfast',quality:'fast'} :
      encoder === 'libx265' ? {speed:'ultrafast',balanced:'ultrafast',quality:'fast'} :
      encoder === 'libvpx-vp9' ? {speed:'8',balanced:'8',quality:'6'} :
      encoder === 'libsvtav1' ? {speed:'12',balanced:'10',quality:'8'} : {speed:'speed',balanced:'balanced',quality:'quality'};
    if (help) help.textContent = browser ? 'Los presets de FFmpeg no se aplican a WebCodecs.' :
      `Preset efectivo: ${screenShareConfig.encoderPreset || automatic[screenShareConfig.preset] || 'automático'}. ` +
      (/_nvenc$/.test(encoder) ? 'P1 prioriza velocidad; P7 dedica más trabajo a la compresión.' :
       /_amf$/.test(encoder) ? 'Speed prioriza velocidad; quality dedica más trabajo a la compresión.' :
       ['libvpx-vp9','libsvtav1'].includes(encoder) ? 'Un número menor dedica más trabajo a la compresión.' : 'Los presets más lentos dedican más CPU a la compresión.') +
      ' Comprueba los FPS reales al compartir. Perfil de salida: ' + (screenShareConfig.codec === 'h264' ? 'H.264 High' : screenShareConfig.codec === 'hevc' ? 'HEVC Main' : screenShareConfig.codec.toUpperCase()) + ' · 8 bits · 4:2:0.';
    const decoder = document.getElementById('screenDecoderAccelerationSelect');
    if (decoder) { decoder.value = screenDecoderAcceleration; decoder.disabled = typeof VideoDecoder === 'undefined'; }
    const decoderHelp = document.getElementById('screenDecoderHelp');
    if (decoderHelp && typeof VideoDecoder === 'undefined') decoderHelp.textContent = 'Este navegador gestiona la decodificación automáticamente y no expone controles WebCodecs.';
  }

  async function changeDecoderPreference(event) {
    const select = event.target, next = select.value, revision = ++decoderPreferenceRevision;
    if (!['no-preference','prefer-hardware','prefer-software'].includes(next)) return;
    try {
      for (const entry of activeScreenStreams.values()) {
        if (!entry.decoderConfig) continue;
        const supported = await VideoDecoder.isConfigSupported({ ...entry.decoderConfig, hardwareAcceleration: next,
          codedWidth: entry.stats?.realWidth || entry.width, codedHeight: entry.stats?.realHeight || entry.height });
        if (!supported.supported) throw new Error('Esta preferencia no admite uno de los vídeos activos. Prueba Automático.');
      }
      if (revision !== decoderPreferenceRevision) return;
      screenDecoderAcceleration = next;
      try { localStorage.setItem('qp_screen_decoder_acceleration', next); } catch { }
      QP.setText('screenDecoderHelp', 'Preferencia aplicada a este dispositivo. Los vídeos activos cambian en el siguiente fotograma clave; los demás participantes conservan sus ajustes.');
      await refreshScreenCodecCapabilities(true);
    } catch (error) {
      if (revision !== decoderPreferenceRevision) return;
      select.value = screenDecoderAcceleration; QP.toast(error.message, 'error');
    }
  }

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

  function syncMediaControls() {
    const browser = usesBrowserCapture();
    const help = document.getElementById('screenNativeStatus');
    if (help) {
      const encoders = nativeCapabilities?.nativeEncoders || [];
      const gpu = encoders.filter(e => e.Available && encoderHardware(e));
      const cpu = encoders.filter(e => e.Available && !encoderHardware(e));
      help.textContent = nativeCapabilities?.ffmpeg?.platform === 'android' ? nativeCapabilities.ffmpeg.message :
        nativeCapabilities?.ffmpeg && !nativeCapabilities.ffmpeg.installed ? nativeCapabilities.ffmpeg.message : browser ? 'WebCodecs prueba solo los formatos expuestos por este navegador. Su acceso a la GPU puede diferir de FFmpeg.' +
        (gpu.some(e => e.Codec === 'hevc') ? ' H.265 por GPU sí está disponible mediante FFmpeg en este equipo.' : '') :
        gpu.length + ' codificador(es) GPU y ' + cpu.length + ' CPU operativos en FFmpeg. ' +
        (gpu.length + cpu.length ? 'El perfil exacto se comprueba al iniciar.' : encoders.find(e => e.Reason)?.Reason || 'Vuelve a detectar el equipo.');
    }
    const encoderHelp = document.getElementById('screenEncoderHelp');
    if (encoderHelp) encoderHelp.textContent = browser ? 'La prueba abre el codificador del navegador. Las preferencias GPU y CPU no identifican el hardware utilizado; FFmpeg usa sus propios codificadores.' : 'NVENC usa la GPU NVIDIA; AMF usa la GPU AMD; los codificadores lib usan la CPU. Solo se preseleccionan codificadores probados.';
    const label = document.getElementById('screenEncoderLabel');
    if (label) label.textContent = browser ? 'Configuración verificada de WebCodecs' : 'Codificador FFmpeg que produce ese formato';
    const encoder = document.getElementById('screenEncoderSelect');
    if (encoder) {
      encoder.replaceChildren();
      if (browser) {
        const value = screenShareConfig.acceleration === 'cpu' ? 'web-software' : screenShareConfig.acceleration === 'browser' ? 'web-browser' : 'web-hardware';
        encoder.append(new Option(value === 'web-software' ? 'WebCodecs · CPU solicitada' : value === 'web-browser' ? 'WebCodecs · preferencia del navegador' : 'WebCodecs · GPU solicitada', value));
        encoder.value = value;
      } else {
        const items = (nativeCapabilities?.nativeEncoders || []).filter(e => e.Codec === screenShareConfig.codec &&
          encoderHardware(e) === (screenShareConfig.acceleration !== 'cpu'));
        for (const item of items) {
          const brand = item.Id.endsWith('_nvenc') ? 'NVIDIA NVENC' : item.Id.endsWith('_amf') ? 'AMD AMF' : item.Id;
          const option = new Option(brand + (item.Available ? ' · probado' : ' · no disponible'), item.Id);
          option.disabled = !item.Available; option.title = item.Reason || 'Prueba real 320×180; perfil completo comprobado al iniciar.';
          encoder.append(option);
        }
        encoder.value = screenShareConfig.encoder;
        if (!encoder.value || !encoder.selectedOptions.length || encoder.selectedOptions[0]?.disabled) encoder.value = items.find(e => e.Available)?.Id || '';
      }
      screenShareConfig.encoder = encoder.value;
      encoder.disabled = browser || !encoder.value;
    }
    for (const id of ['screenRateControlSelect', 'screenBufferInput']) {
      const control = document.getElementById(id);
      if (control) control.disabled = browser;
    }
    const quality = document.getElementById('screenQualityInput');
    if (quality) quality.disabled = browser || screenShareConfig.rateControl !== 'vbr' ||
      !(/_nvenc$/.test(screenShareConfig.encoder) || ['libx264', 'libx265'].includes(screenShareConfig.encoder));
    syncEncoderTuning();
    const bounds = captureBounds();
    const resolution = document.getElementById('screenResolutionSelect');
    if (resolution) {
      const selected = resolution.value || screenShareConfig.resolution;
      resolution.replaceChildren();
      for (const height of nativeCapabilities?.resolutionHeights || [360, 480, 720, 1080, 1440, 2160]) {
        if (!bounds || (height <= bounds.height && height * 16 / 9 <= bounds.width))
          resolution.append(new Option(`${height}p · hasta ${Math.round(height * 16 / 9)}×${height}`, String(height)));
      }
      resolution.append(new Option(bounds?.exact ? `Original · ${bounds.width}×${bounds.height}` : 'Original · se confirma al capturar', 'native'));
      resolution.value = selected;
      if (!resolution.value) resolution.value = 'native';
      screenShareConfig.resolution = resolution.value;
    }
    const fps = document.getElementById('screenFpsSelect');
    if (fps) {
      const selected = fps.value || String(screenShareConfig.fps);
      fps.replaceChildren();
      for (const value of nativeCapabilities?.frameRates || [15, 24, 30, 60, 90, 120, 144]) fps.append(new Option(`${value} FPS · objetivo`, String(value)));
      fps.value = selected;
      if (!fps.value) fps.value = '60';
    }
    const fpsHelp = document.getElementById('screenFpsHelp');
    if (fpsHelp) fpsHelp.textContent = `${bounds?.fps ? `Fuente: ${bounds.fps} ${browser ? 'FPS' : 'Hz'}. ` : ''}Hasta 144 FPS es el límite de la aplicación, no una medida del monitor. WebCodecs envía a un ritmo fijo, reutilizando la última imagen si la pantalla no cambia. Los FPS reales se muestran durante la transmisión.`;
  }

  function renderCodecMatrix() {
    const body = document.getElementById('mediaCodecMatrix');
    if (!body) return;
    body.replaceChildren();
    const browser = usesBrowserCapture();
    for (const [id, text] of [['mediaGpuColumn', browser ? 'WebCodecs · GPU solicitada' : 'FFmpeg · GPU'],
      ['mediaCpuColumn', browser ? 'WebCodecs · CPU solicitada' : 'FFmpeg · CPU']]) {
      const column = document.getElementById(id); if (column) column.textContent = text;
    }
    const browserColumn = document.getElementById('mediaBrowserColumn');
    if (browserColumn) browserColumn.hidden = !browser;
    for (const definition of screenCodecDefinitions) {
      const row = document.createElement('tr');
      const encoders = (nativeCapabilities?.nativeEncoders || []).filter(e => e.Codec === definition.name);
      const detail = localScreenCodecDetails.get(definition.id);
      const tested = hardware => {
        const matches = encoders.filter(e => encoderHardware(e) === hardware);
        return matches.filter(e => e.Available).map(e => e.Id + ' · probado').join(', ') ||
          matches.map(e => e.Id + ': ' + (e.Reason || 'no disponible')).join(' · ') || 'Sin codificador para este formato';
      };
      const web = probe => probe?.supported ? 'Codificación verificada' : probe?.reason || 'No expuesto por el navegador';
      const receive = localScreenDecoderCodecMask & codecMaskBit(definition.id) ? 'Perfil aceptado' : 'No expuesto para este perfil';
      const values = browser ? [definition.label, web(detail?.gpu), web(detail?.cpu), web(detail?.browser), receive] :
        [definition.label, tested(true), tested(false), receive];
      for (const text of values) { const cell = document.createElement('td'); cell.textContent = text; row.append(cell); }
      body.append(row);
    }
  }

  function nativeCanEncode(codec, acceleration = screenShareConfig.acceleration) {
    if (browserCaptureSelected || screenShareConfig.backend === 'web') return false;
    return (nativeCapabilities?.nativeEncoders || []).some(e => e.Available && e.Codec === codec &&
      encoderHardware(e) === (acceleration !== 'cpu'));
  }

  async function answerDecoderProbe(data) {
    const q = data.config;
    let supported = false;
    try {
      const config = { codec: q.codec, codedWidth: q.width, codedHeight: q.height, optimizeForLatency: true,
        hardwareAcceleration: screenDecoderAcceleration };
      if (q.description) config.description = Uint8Array.from(atob(q.description), ch => ch.charCodeAt(0));
      const result = await VideoDecoder.isConfigSupported(config);
      if (result.supported) {
        const decoder = new VideoDecoder({ output: frame => frame.close(), error: () => {} });
        try { decoder.configure(result.config); supported = true; } finally { decoder.close(); }
      }
    } catch {}
    await QP.api('/api/voice/screen-share/decoder-probe', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId: data.peerId, requestId: q.requestId, supported })
    }).catch(() => {});
  }

  async function chooseNativeSource() {
    const modal = document.getElementById('screenNativeCaptureModal');
    const grid = document.getElementById('screenCaptureGallery');
    const search = document.getElementById('screenCaptureSearch');
    const preview = document.getElementById('screenCapturePreview');
    if (!modal || !grid || !search || !preview) throw new Error('No se pudo abrir el selector de captura.');
    let selected = screenShareConfig.source, sources = [], matches = [], category = 'all', previewTimer = null, closed = false, refreshVersion = 0;
    const thumbnails = new Map();
    const status = document.getElementById('screenCaptureSelection');
    const start = document.getElementById('screenNativeCaptureStart');
    start.disabled = true;
    start.textContent = 'Compartir fuente seleccionada';
    document.getElementById('screenNativeCaptureWeb').classList.remove('hidden');
    const showPreview = () => {
      if (closed || !selected) return;
      clearTimeout(previewTimer); previewTimer = null;
      preview.onload = () => {
        const source = sources.find(item => item.Id === selected);
        if (status && source) status.textContent = source.Name + ' · fuente completa, sin recortar';
        if (!closed) previewTimer = setTimeout(showPreview, 1500);
      };
      preview.onerror = () => {
        if (status) status.textContent = 'No se puede previsualizar esta fuente. Restaura la ventana o selecciona otra pantalla.';
        if (!closed) previewTimer = setTimeout(showPreview, 1500);
      };
      preview.src = '/api/voice/screen-share/preview?source=' + encodeURIComponent(selected) + '&t=' + Date.now();
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
        clearTimeout(previewTimer); previewTimer = null;
        preview.onload = preview.onerror = null; preview.removeAttribute('src');
      } else if (previous !== selected || !preview.getAttribute('src')) showPreview();
    };
    const render = () => {
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
          image.loading = 'lazy'; image.alt = 'Vista previa de ' + source.Name;
          image.src = '/api/voice/screen-share/preview?source=' + encodeURIComponent(source.Id);
          thumbnails.set(source.Id, image);
        }
        const name = document.createElement('strong'); name.textContent = source.Name;
        const detail = document.createElement('span');
        detail.textContent = (source.Width ? source.Width + '×' + source.Height : 'Tamaño al iniciar') +
          ((source.Kind || 'display') === 'window' ? ' · ventana' : ' · pantalla');
        const dimensions = detail.textContent;
        button.setAttribute('aria-label', source.Name + ' · ' + dimensions);
        detail.textContent = dimensions + (image.complete && image.naturalWidth ? '' : ' · cargando vista previa…');
        image.onload = () => { detail.textContent = dimensions; };
        image.onerror = () => { detail.textContent = dimensions + ' · vista previa no disponible'; };
        button.append(image, name, detail);
        button.onclick = () => select(source.Id);
        grid.append(button);
      }
      select(matches.some(source => source.Id === selected) ? selected : matches[0]?.Id);
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
    modal.classList.remove('hidden');
    search.value = ''; search.oninput = render;
    return new Promise(resolve => {
      const keydown = event => {
        if (closed || event.isComposing || !modal.contains(event.target)) return;
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
        clearTimeout(previewTimer);
        preview.onload = preview.onerror = null; preview.removeAttribute('src');
        for (const image of thumbnails.values()) image.removeAttribute('src');
        thumbnails.clear(); grid.replaceChildren(); modal.classList.add('hidden');
        document.removeEventListener('keydown', keydown, true);
        search.oninput = null;
        for (const id of ['screenNativeCaptureStart', 'screenNativeCaptureWeb', 'screenNativeCaptureCancel', 'screenCaptureRefresh', 'screenCaptureAll', 'screenCaptureDisplays', 'screenCaptureWindows'])
          document.getElementById(id).onclick = null;
        resolve(choice);
      };
      document.addEventListener('keydown', keydown, true);
      document.getElementById('screenNativeCaptureStart').onclick = () => { if (!start.disabled && selected) done(selected); };
      document.getElementById('screenNativeCaptureWeb').onclick = () => done('web');
      document.getElementById('screenNativeCaptureCancel').onclick = () => done('cancel');
      document.getElementById('screenCaptureRefresh').onclick = () => { thumbnails.clear(); refresh(); };
      for (const [id, kind] of [['screenCaptureAll','all'], ['screenCaptureDisplays','display'], ['screenCaptureWindows','window']])
        document.getElementById(id).onclick = () => { category = kind; render(); };
      refresh();
    });
  }

  async function startNativeScreenShare(targetPeerId, reuseSource = false) {
    if (!nativeCanEncode(screenShareConfig.codec)) {
      if (screenShareConfig.backend === 'ffmpeg') throw new Error('El formato y codificador elegidos no están disponibles. Revisa la tabla de compatibilidad.');
      return false;
    }
    nativeStartPending = true;
    try {
      const source = reuseSource ? screenShareConfig.source : await chooseNativeSource();
      if (source === 'cancel') return true;
      if (source === 'web') { browserCaptureSelected = true; screenShareConfig.backend = 'web'; const select = document.getElementById('screenBackendSelect'); if (select) select.value = 'web'; return false; }
      screenShareConfig.source = source;
      syncMediaControls();
      const profile = getScreenProbeProfile();
      const result = await QP.api('/api/voice/screen-share/native-start', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...screenShareConfig, width: profile.width, height: profile.height, targetPeerId })
      });
      const p = result.profile;
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
      QP.toast(`Call answered with ${petition.peerName || 'peer'}`, 'success');
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
      QP.toast('Call declined');
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
      cameraDeviceId = document.getElementById('voiceCameraPickerSelect').value;
      document.getElementById('voiceCameraPicker').close();
      await startCamera();
    });
    navigator.mediaDevices?.addEventListener?.('devicechange', () => {
      if (document.getElementById('voiceCameraPicker')?.open) refreshCameraChoices('voiceCameraPickerSelect', document.getElementById('voiceCameraPickerSelect').value).catch(error => QP.toast(error.message, 'error'));
    });
    document.getElementById('voiceCameraSelect')?.addEventListener('change', event => {
      cameraDeviceId = event.target.value;
      if (localVideoKind === 'camera') startCamera();
    });
    document.getElementById('voiceCameraFlipBtn')?.addEventListener('click', () => {
      cameraFacing = cameraFacing === 'user' ? 'environment' : 'user'; cameraDeviceId = '';
      startCamera();
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
        const next = event.key === 'Home' ? 'video' : event.key === 'End' ? 'audio' : tab === 'Video' ? 'audio' : 'video';
        selectMediaTab(next);
        document.getElementById(next === 'audio' ? 'mediaAudioTab' : 'mediaVideoTab')?.focus();
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
      if (control) control.addEventListener('change', () => refreshScreenCodecCapabilities(true));
    }

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
      refreshScreenCodecCapabilities(true);
    });
    for (const id of ['screenBackendSelect', 'screenEncoderSelect', 'screenEncoderPresetSelect', 'screenPresetSelect', 'screenRateControlSelect']) {
      document.getElementById(id)?.addEventListener('change', event => { readNativeControls(); if (event.target.id === 'screenBackendSelect') browserCaptureSelected = false; refreshScreenCodecCapabilities(true); });
    }

    // WebSocket Events
    if (window.QP && typeof QP.on === 'function') {
      QP.on('focus_voice_peer', data => {
        if (data?.peerId && !callingPeers.has(data.peerId) && !activeCallsList().some(c => c.peerId === data.peerId)) startCall(data.peerId);
      });
      QP.on('screen_decoder_probe', answerDecoderProbe);
      QP.on('screen_audio_error', data => {
        if (data.audioSessionId && data.audioSessionId !== screenAudioCapture.sessionId) return;
        stopScreenAudioCapture(); QP.toast(data.error || 'Se ha detenido el audio compartido.', 'error');
      });
      QP.on('screen_settings_request', applyRemoteBrowserSettings);
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
      QP.toast(`${type === 'input' ? 'Input' : 'Output'} audio device updated`);
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
      renderVoice();
    } catch (e) {
      console.warn('Error refreshing voice status', e);
    }
  }

  function onVoiceStateUpdated(state) {
    if (!state) return;
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
      QP.toast('Voice call established', 'success');
      refreshVoice();
    } else if (data.signal === 'call-ended' || data.signal === 'call-failed') {
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
      deafenBtn.title = voiceState.isDeafened ? 'Audio deafened (You cannot hear anyone)' : 'Audio active (Listening)';
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
    }
    const leave = document.getElementById('voiceLeaveBtn'); if (leave) leave.disabled = !calls.length && !callingPeers.size;
    document.getElementById('voiceCameraOptions')?.classList.toggle('hidden', !cameraActive);
    document.getElementById('voiceCameraFlipBtn')?.classList.toggle('hidden', !androidCapture);
    for (const id of ['voiceCameraSelect', 'voiceCameraFlipBtn']) {
      const control = document.getElementById(id); if (control) control.disabled = videoStartPending || !!videoStopPending;
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
      const pct = Math.round((voiceState.masterVolume || 1.0) * 100);
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
    const box = document.getElementById('voicePeers'); if (!box) return;
    QP.clear(box); QP.setText('voiceContactCount', String(peers.length));
    const search = (document.getElementById('voiceContactSearch')?.value || '').trim().toLocaleLowerCase();
    const filtered = peers.filter(peer => peer.name?.toLocaleLowerCase().includes(search));
    if (!filtered.length) { box.append(QP.el('p', 'voice-no-contacts', peers.length ? 'No hay contactos con ese nombre.' : 'Añade contactos de confianza desde Inicio.')); return; }
    for (const peer of filtered) {
      const inCall = calls.some(call => call.peerId === peer.id), calling = callingPeers.has(peer.id);
      const row = QP.el('div', 'voice-contact');
      const copy = QP.el('div', 'voice-contact-copy'); copy.append(QP.identity.createCapsule(peer, {snapshot: voiceStatus, slot: 'voice-peer', size: 'compact'}), QP.el('span', inCall ? 'is-connected' : '', inCall ? 'En llamada' : calling ? 'Llamando…' : QP.userPresence?.(peer, voiceStatus)?.label || (peer.isQuicConnected ? 'Disponible' : 'Sin conexión directa')));
      const button = document.createElement('button'); button.type = 'button'; button.className = 'voice-contact-call' + (calling ? ' is-danger' : '');
      const label = inCall ? 'En llamada con ' + peer.name : calling ? 'Cancelar llamada a ' + peer.name : 'Llamar a ' + peer.name;
      button.setAttribute('aria-label', label); button.title = label; button.append(voiceIcon(calling ? 'close' : 'phone')); button.disabled = inCall;
      button.addEventListener('click', () => calling ? hangup(peer.id) : startCall(peer.id));
      row.append(copy, button); box.append(row);
    }
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
      QP.toast('Call ended');
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
      QP.toast(voiceState.isMuted ? 'Microphone muted' : 'Microphone unmuted');
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
      QP.toast(voiceState.isDeafened ? 'Audio deafened (muted all sound)' : 'Audio restored');
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
      QP.toast(next ? 'Peer audio muted' : 'Peer audio unmuted');
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
      QP.toast(next ? `Your mic is now muted for ${peerName}` : `Your mic is now unmuted for ${peerName}`);
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
      QP.toast(next ? `Screen sharing enabled for ${peerName}` : `Screen sharing blocked for ${peerName}`);
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
      QP.toast(voiceState.isTestLoopback ? 'Mic test started: speak to hear your voice through your speakers' : 'Mic test stopped');
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
    const encoderCodecMask = Number(call.screenEncoderCodecMask || 0);
    const decoderCodecMask = Number(call.screenDecoderCodecMask || 0);
    if (encoderCodecMask || decoderCodecMask) {
      remoteScreenCodecCapabilities.set(peerId, { encoderCodecMask, decoderCodecMask });
    }
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

  async function supportsVideoEncoderCodec(definition, profile) {
    const supports = async acceleration => {
      const probe = await probeVideoEncoderConfig(definition.id, profile.width, profile.height, profile.fps, profile.bitrate, acceleration);
      return probe ? { supported: true, probe } : { supported: false, reason: 'El navegador no codifica este perfil con ' + (acceleration === 'cpu' ? 'CPU solicitada' : acceleration === 'browser' ? 'preferencia del navegador' : 'GPU solicitada') };
    };
    // Avoid exhausting hardware encoder sessions during detection.
    const gpu = await supports('gpu');
    const cpu = await supports('cpu');
    const browser = gpu.supported || cpu.supported ? { supported: false } : await supports('browser');
    const selected = screenShareConfig.acceleration === 'cpu' ? cpu : screenShareConfig.acceleration === 'browser' ? browser : gpu;
    return { supported: gpu.supported || cpu.supported || browser.supported, probe: selected.probe, gpu, cpu, browser };
  }

  async function supportsVideoDecoderCodec(definition, profile) {
    if (typeof VideoDecoder === 'undefined' || typeof VideoDecoder.isConfigSupported !== 'function') return false;
    for (const codec of getCodecCandidates(definition.id, profile.width, profile.height, profile.fps)) {
      try {
        const support = await VideoDecoder.isConfigSupported({
          codec,
          codedWidth: profile.width,
          codedHeight: profile.height,
          hardwareAcceleration: screenDecoderAcceleration,
          optimizeForLatency: true
        });
        if (support && support.supported) return true;
      } catch { }
    }
    return false;
  }

  async function refreshScreenCodecCapabilities(force = false) {
    if (screenCodecCapabilityPromise && !force) return screenCodecCapabilityPromise;
    if (force) verifiedEncoderProfiles.clear();
    const probeVersion = ++screenCodecProbeVersion;
    const apply = document.getElementById('saveScreenModalBtn');
    if (apply) apply.disabled = true;
    screenCodecCapabilityPromise = (async () => {
      await loadNativeCapabilities();
      if (probeVersion !== screenCodecProbeVersion) return { stale: true };
      syncMediaControls();
      const profile = getScreenProbeProfile();
      let encoderMask = 0;
      let decoderMask = 0;
      const details = new Map();
      const browser = usesBrowserCapture();
      for (const definition of screenCodecDefinitions) {
        const [encoderSupport, canDecode] = await Promise.all([
          browser ? supportsVideoEncoderCodec(definition, profile) : Promise.resolve(null),
          supportsVideoDecoderCodec(definition, profile)
        ]);
        if (probeVersion !== screenCodecProbeVersion) return { stale: true };
        details.set(definition.id, encoderSupport);
        if ((browser && encoderSupport?.supported) || nativeCanEncode(definition.name, 'gpu') || nativeCanEncode(definition.name, 'cpu')) encoderMask |= codecMaskBit(definition.id);
        if (canDecode) decoderMask |= codecMaskBit(definition.id);
      }
      if (probeVersion !== screenCodecProbeVersion) return { encoderMask, decoderMask, stale: true };
      if (androidCapture) encoderMask |= codecMaskBit(1); // MediaProjection uses native MediaCodec H264, independently of WebCodecs encoding.
      localScreenEncoderCodecMask = encoderMask;
      localScreenDecoderCodecMask = decoderMask;
      if (browser) {
        localScreenCodecDetails.clear();
        for (const [codecId, detail] of details) localScreenCodecDetails.set(codecId, detail);
      }
      updateScreenCodecOptions();
      try {
        await QP.api('/api/voice/screen-share/capabilities', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ encoderCodecMask: encoderMask, decoderCodecMask: decoderMask })
        });
        await publishBrowserScreenState();
      } catch (err) {
        console.warn('Could not publish screen codec capabilities:', err);
      }
      return { encoderMask, decoderMask, profile };
    })();
    try { return await screenCodecCapabilityPromise; }
    finally { if (probeVersion === screenCodecProbeVersion && apply) apply.disabled = false; }
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
      group.label = acceleration === 'browser' ? 'WebCodecs · preferencia del navegador' : (browser ? 'WebCodecs · ' : 'FFmpeg · ') + (acceleration === 'gpu' ? 'GPU · codificación por hardware' : 'CPU · codificación por software');
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
        const backendLabel = browser ? 'WebCodecs' : nativeMatches.filter(e => e.Available).map(e => e.Id).join(', ') || 'FFmpeg';
        const reason = !localSupported ? (browser ? 'no expuesto por este navegador para el perfil solicitado' : 'no disponible en este equipo') : 'codificación verificada';
        const receiver = incompatible.length ? ' · recepción no anunciada por ' + incompatible.join(', ') :
          unknown.length ? ' · recepción pendiente' : '';
        const recommended = definition.name === 'h264' ? ' · recomendado por compatibilidad' :
          definition.name === 'hevc' ? ' · buena compresión; comprueba recepción' :
          definition.name === 'av1' ? ' · equipos recientes; CPU exigente' : ' · alternativa para navegadores';
        const option = new Option(definition.label + ' · ' + backendLabel + ' · ' + (acceleration === 'gpu' ? (browser ? 'GPU solicitada' : 'GPU') : acceleration === 'cpu' ? (browser ? 'CPU solicitada' : 'CPU') : 'aceleración no identificable') + ' · ' + reason + receiver + recommended,
          definition.name + ':' + acceleration);
        option.disabled = !localSupported; // Receiver compatibility is confirmed separately with the exact profile before transmission.
        option.title = usesBrowserCapture() ? (detail?.probe
          ? detail.probe.codecString + ' · ' + detail.probe.hardwarePreference + ' · el navegador puede ignorar la preferencia de aceleración'
          : detail?.reason || reason) : nativeMatches.map(e => e.Id + ': ' + (e.Available ? 'codificación verificada' : e.Reason || 'no disponible')).join(' · ') || reason;
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
        available.find(option => option.value === screenShareConfig.codec + ':browser') || available[0];
      if (recommended) {
        readCodecSelection(recommended.value);
        select.value = recommended.value;
        selectionNotice = 'Se ha preseleccionado ' + recommended.label + '. ';
      }
    }
    // Keep the actual encoder and its acceleration label in sync with the validated option.
    syncMediaControls();
    renderCodecMatrix();
    const count = Array.from(select.options).filter(option => !option.disabled).length;
    const status = document.getElementById('modalHwAccelStatus');
    if (status) status.textContent = count + ' opciones disponibles';
    const help = document.getElementById('screenCodecCompatibility');
    if (help) {
      const profile = getScreenProbeProfile();
      help.textContent = selectionNotice + (unknownPeers ? unknownPeers + ' participante(s) aún no confirman recepción. Puedes guardar tus ajustes; se comprobará el perfil antes de transmitir. ' : calls.length ? 'Recepción anunciada por los participantes. ' : 'Comprobación local; recepción verificada al iniciar. ') +
        profile.width + '×' + profile.height + ' · ' + profile.fps + ' FPS' +
        (captureBounds() ? '.' : ' · perfil provisional hasta elegir fuente.') +
        (usesBrowserCapture() ? ' WebCodecs verifica codificación; GPU/CPU son preferencias del navegador, no una identificación del hardware.' : '');
    }
  }

  function resolveCompatibleScreenCodec(requestedCodec, requireReceivers = true) {
    const requested = screenCodecDefinitions.find(definition => definition.name === requestedCodec);
    const calls = Array.isArray(voiceState.activeCalls) ? voiceState.activeCalls : [];
    const isCompatible = definition => {
      const bit = codecMaskBit(definition.id);
      if ((localScreenEncoderCodecMask & bit) === 0) return false;
      return !requireReceivers || calls.every(call => {
        const capabilities = remoteScreenCodecCapabilities.get(normalizePeerId(call.peerId));
        const decoderMask = capabilities ? capabilities.decoderCodecMask : Number(call.screenDecoderCodecMask || 0);
        return decoderMask !== 0 && (decoderMask & bit) !== 0;
      });
    };
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

    for (const [id, key] of [['screenBackendSelect','backend'], ['screenEncoderSelect','encoder'],
      ['screenEncoderPresetSelect','encoderPreset'], ['screenPresetSelect','preset'], ['screenRateControlSelect','rateControl'],
      ['screenGopInput','keyFrameIntervalMs'], ['screenBufferInput','bufferMs'], ['screenQualityInput','quality']]) {
      const el = document.getElementById(id);
      if (el) el.value = String(screenShareConfig[key]);
    }
    updateScreenSettingsSubtitle();
  }

  function selectMediaTab(tab) {
    if (tab === 'video' && document.getElementById('mediaVideoTab')?.disabled) tab = 'audio';
    for (const name of ['Video', 'Audio']) {
      const active = name.toLowerCase() === tab;
      document.getElementById(`media${name}Panel`)?.classList.toggle('hidden', !active);
      const button = document.getElementById(`media${name}Tab`);
      if (button) { button.classList.toggle('active', active); button.setAttribute('aria-selected', String(active)); button.tabIndex = active ? 0 : -1; }
    }
    document.getElementById('saveScreenModalBtn')?.parentElement?.classList.toggle('hidden', tab === 'audio');
  }

  function openScreenSettingsModal(tab = 'video') {
    const audioOnly = androidCapture || localVideoKind === 'camera';
    if (audioOnly) tab = 'audio';
    const videoTab = document.getElementById('mediaVideoTab');
    if (videoTab) { videoTab.disabled = audioOnly; videoTab.setAttribute('aria-disabled', String(audioOnly)); }
    if (localVideoKind !== 'camera') loadScreenShareSettings();
    syncOpusPreference();
    loadAudioDevices();
    selectMediaTab(tab === 'audio' ? 'audio' : 'video');
    // Refresh inventory on every opening; GPU probes remain cached on the server.
    if (!audioOnly) { nativeCapabilities = null; refreshScreenCodecCapabilities(true); }
    const modal = document.getElementById('screenShareSettingsModal');
    if (document.body.dataset.page === 'voice') { mountVoiceSettings(true); setVoiceDrawer(true, 'settings'); }
    if (modal) modal.classList.remove('hidden');
  }

  function closeScreenSettingsModal() {
    if (document.body.dataset.page === 'voice') setVoiceDrawer(false);
    const modal = document.getElementById('screenShareSettingsModal');
    if (modal) modal.classList.add('hidden');
  }

  async function saveScreenSettingsModal() {
    if (remoteBrowserChangeInFlight) { QP.toast('Hay un cambio remoto de transmisión en curso.', 'warning'); return; }
    const codecSelect = document.getElementById('screenCodecSelect');
    const resSelect = document.getElementById('screenResolutionSelect');
    const fpsSelect = document.getElementById('screenFpsSelect');
    const brSelect = document.getElementById('screenBitrateSelect');

    readNativeControls();
    if (codecSelect) readCodecSelection(codecSelect.value);
    if (resSelect) screenShareConfig.resolution = resSelect.value;
    if (fpsSelect) screenShareConfig.fps = parseInt(fpsSelect.value, 10) || 60;
    if (brSelect) screenShareConfig.bitrate = parseInt(brSelect.value, 10) || 12000000;

    await refreshScreenCodecCapabilities(true);
    syncMediaControls();
    const requestedCodec = screenShareConfig.codec;
    const compatibleCodec = resolveCompatibleScreenCodec(requestedCodec, false);
    if (!compatibleCodec) {
      const selected = Array.from(codecSelect?.options || []).find(option => option.value === codecSelection());
      QP.toast(selected?.label || 'No hay un formato compatible. Comprueba el motor, el perfil y la recepción de los participantes.', 'error');
      return;
    }

    try {
      localStorage.setItem('qp_screen_config', JSON.stringify(screenShareConfig));
    } catch { }

    updateScreenSettingsSubtitle();

    if (nativeSharing || (screenStream && voiceState.isScreenSharing && screenShareConfig.backend !== 'web' && nativeCanEncode(screenShareConfig.codec))) {
      const target = activeScreenTargetPeerId;
      const reuseSource = nativeSharing && screenShareConfig.backend !== 'web';
      await stopScreenShare();
      await startScreenShare(target, false, reuseSource);
      closeScreenSettingsModal();
      return;
    }
    // If screen transmission is currently active, apply changes immediately in real-time!
    if (screenStream && voiceState.isScreenSharing) {
      const requestedFps = screenShareConfig.fps || 60;
      const { width: idealW, height: idealH } = getScreenProbeProfile();

      const codecId = getCodecId(screenShareConfig.codec);

      const videoTrack = screenStream.getVideoTracks()[0];
      if (videoTrack) {
        try {
          await videoTrack.applyConstraints({
            frameRate: { ideal: requestedFps, max: requestedFps },
            ...(screenShareConfig.resolution === 'native' ? {} : { width: { ideal: idealW }, height: { ideal: idealH } })
          }).catch(() => {});
        } catch {}
      }

      const settings = videoTrack ? videoTrack.getSettings() : {};
      const { width, height } = fitScreenOutput(settings.width || idealW, settings.height || idealH);
      const sourceFps = Math.max(1, Math.round(settings.frameRate || requestedFps));

      let profile;
      try {
        profile = await startCanvasFrameExtraction(screenStream, width, height, codecId, requestedFps);

        const compatible = await QP.api('/api/voice/screen-share/decoder-check', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ codec: profile.codecString, width, height, targetPeerId: activeScreenTargetPeerId })
        });
        if (!compatible.supported) throw new Error('Un receptor no admite el perfil exacto de WebCodecs.');
        // Notify backend and peers only after the exact video encoder is configured.
        await QP.api('/api/voice/screen-share/start', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ width, height, codecId: profile.codecId, fps: profile.fps, targetPeerId: activeScreenTargetPeerId, kind: localVideoKind })
        });
        screenTransmissionReady = true;
      } catch (err) {
        screenTransmissionReady = false;
        await stopScreenShare();
        QP.toast(`Could not apply ${getCodecNameFromId(codecId)} at ${width}×${height}: ${err.message}`, 'error');
        return;
      }

      createOrUpdateScreenTile('local', (localVideoKind === 'camera' ? 'Tú · Cámara' : 'Tú · Pantalla'), width, height, profile.codecId, profile.fps, true);
      updateEffectiveFpsStatus(requestedFps, profile.fps);
      await publishBrowserScreenState();
      closeScreenSettingsModal();
      QP.toast(`Live screen settings updated: ${profile.fps} FPS · ${width}x${height} · ${getCodecNameFromId(profile.codecId)}`, 'success');
      return;
    }

    closeScreenSettingsModal();
    QP.toast(`Ajustes de pantalla guardados: ${(screenShareConfig.codec || 'H264').toUpperCase()} · ${screenShareConfig.fps} FPS · ${Math.round(screenShareConfig.bitrate / 1000)} kbps`, 'success');
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

  async function startScreenShare(targetPeerId = null, webOnly = false, reuseNativeSource = false) {
    if (nativeStartPending || videoStartPending) return;
    if (activeCallsList().length === 0) { QP.toast('Inicia una llamada antes de compartir pantalla.', 'warning'); return; }
    localVideoKind = 'screen';
    if (androidCapture) {
      videoStartPending = true; renderVoice();
      try {
        const result = await QP.api('/api/voice/screen-share/platform-start', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ width: 1280, height: 1280, fps: Math.min(60, screenShareConfig.fps || 30), bitrate: Math.min(12000000, screenShareConfig.bitrate || 4000000), targetPeerId })
        });
        const p = result.profile;
        voiceState.isScreenSharing = true; activeScreenTargetPeerId = targetPeerId;
        createOrUpdateScreenTile('local', 'Tú · Pantalla', p.width, p.height, p.codecId, p.fps, true);
      } catch (error) { console.error('Captura de pantalla Android:', error); QP.toast(error.message || 'No se ha autorizado la pantalla.', 'error'); }
      finally { videoStartPending = false; renderVoice(); }
      return;
    }
    browserCaptureSelected = webOnly || screenShareConfig.backend === 'web';
    if (!webOnly && screenShareConfig.backend !== 'web') {
      await loadNativeCapabilities();
      try { if (await startNativeScreenShare(targetPeerId, reuseNativeSource)) return; }
      catch (err) {
        if (screenShareConfig.backend === 'ffmpeg') { QP.toast(err.message, 'error'); return; }
        QP.toast('FFmpeg: ' + err.message + '. Intentando WebCodecs.', 'warning');
      }
    }
    browserCaptureSelected = true;
    screenGeneration = Date.now();
    // 1. Try Browser / Chromium getDisplayMedia API
    if (navigator.mediaDevices && typeof navigator.mediaDevices.getDisplayMedia === 'function') {
      try {
        const fps = screenShareConfig.fps || 60;
        const { width: idealW, height: idealH } = getScreenProbeProfile();

        const stream = await navigator.mediaDevices.getDisplayMedia({
          video: { cursor: 'always', frameRate: { ideal: fps, max: fps } },
          audio: {
            restrictOwnAudio: true,
            echoCancellation: false,
            noiseSuppression: false,
            autoGainControl: false
          },
          windowAudio: 'window',
          selfBrowserSurface: 'exclude'
        });

        screenStream = stream;
        browserSourceBounds = stream.getVideoTracks()[0]?.getSettings() || null;
        await refreshScreenCodecCapabilities(true);
        const requestedCodec = screenShareConfig.codec;
        if (!localScreenCodecDetails.get(getCodecId(requestedCodec))?.supported || !resolveCompatibleScreenCodec(requestedCodec, false))
          throw new Error(`${requestedCodec.toUpperCase()} is unavailable for the selected profile or connected viewers`);
        const videoTrack = stream.getVideoTracks()[0];
        if (videoTrack) {
          videoTrack.onended = () => {
            stopScreenShare();
          };
          try {
            await videoTrack.applyConstraints({
              frameRate: { ideal: fps, max: fps },
              ...(screenShareConfig.resolution === 'native' ? {} : { width: { ideal: getScreenProbeProfile().width }, height: { ideal: getScreenProbeProfile().height } })
            });
          } catch (constraintError) {
            console.warn('The selected capture source could not apply all requested constraints:', constraintError);
          }
        }

        const settings = videoTrack ? videoTrack.getSettings() : {};
        const { width, height } = fitScreenOutput(settings.width || idealW, settings.height || idealH);
        const codecId = getCodecId(screenShareConfig.codec);
        const sourceFps = Math.max(1, Math.round(settings.frameRate || fps));
        const effectiveFps = fps;

        // Use binary canvas frame extraction for rock-solid 0-latency streaming without MSE/WebM black screen bugs
        const profile = await startCanvasFrameExtraction(stream, width, height, codecId, effectiveFps);

        const compatible = await QP.api('/api/voice/screen-share/decoder-check', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ codec: profile.codecString, width, height, targetPeerId })
        });
        if (!compatible.supported) throw new Error('Un receptor no admite el perfil exacto de WebCodecs.');
        // Announce only after the exact requested video codec is configured.
        const audioSessionId = crypto.randomUUID();
        await QP.api('/api/voice/screen-share/start', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ width, height, codecId: profile.codecId, fps: profile.fps, targetPeerId, kind: localVideoKind, audioSessionId })
        });
        screenTransmissionReady = true;
        await startScreenAudioCapture(stream, audioSessionId);
        if (screenStream !== stream) return;

        voiceState.isScreenSharing = true;
        activeScreenTargetPeerId = targetPeerId;
        pendingRemoteScreenPeerId = null;
        document.getElementById('remoteScreenRequestBanner')?.classList.add('hidden');
        renderVoice();
        createOrUpdateScreenTile('local', (localVideoKind === 'camera' ? 'Tú · Cámara' : 'Tú · Pantalla'), width, height, profile.codecId, profile.fps, true);
        await publishBrowserScreenState();
        updateEffectiveFpsStatus(fps, profile.fps);
        QP.toast(`Screen broadcasting live (${getCodecNameFromId(profile.codecId)} · ${profile.fps} FPS)`, 'success');
        return;
      } catch (err) {
        if (err.name === 'NotAllowedError') {
          QP.toast('Screen share selection cancelled');
          return;
        }
        stopScreenAudioCapture();
        stopCanvasFrameExtraction();
        if (screenStream) {
          try { screenStream.getTracks().forEach(track => track.stop()); } catch { }
          screenStream = null;
        }
        console.warn('WebCodecs screen sharing failed:', err);
        QP.toast(`Screen sharing could not start: ${err.message}. No JPEG fallback was applied.`, 'error');
        return;
      }
    }

    QP.toast('This web runtime does not expose display capture. JPEG fallback is disabled.', 'error');
  }

  async function toggleCamera() {
    if (localVideoKind === 'camera' && screenStream) await stopScreenShare();
    else await chooseCamera();
  }

  async function refreshCameraChoices(selectId, selected = cameraDeviceId) {
    const devices = (await navigator.mediaDevices.enumerateDevices()).filter(device => device.kind === 'videoinput');
    const select = document.getElementById(selectId);
    if (!select) return;
    select.replaceChildren(...devices.map((device, index) => new Option(device.label || `Cámara ${index + 1}`, device.deviceId)));
    if (!devices.length) select.appendChild(new Option('Predeterminada del sistema', ''));
    select.value = devices.some(device => device.deviceId === selected) ? selected : (devices[0]?.deviceId || '');
  }

  async function chooseCamera() {
    if (videoStartPending || videoStopPending || cameraPickerPending || !activeCallsList().length) return;
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

  async function acquireCamera(constraints, cancelled) {
    // Some Windows drivers need a short interval to release a stopped capture.
    // Retry only a busy/unreadable source, using the same selected device each time.
    for (let attempt = 0; ; attempt++) {
      if (cancelled()) throw new DOMException('Activación cancelada', 'AbortError');
      try { return await navigator.mediaDevices.getUserMedia(constraints); }
      catch (error) {
        if (error.name !== 'NotReadableError' || attempt >= 3 || cancelled()) throw error;
        await new Promise(resolve => setTimeout(resolve, [150, 300, 600][attempt]));
      }
    }
  }

  async function startCamera() {
    if (videoStartPending || nativeStartPending) return;
    if (!activeCallsList().length) { QP.toast('Inicia una llamada antes de activar la cámara.', 'warning'); return; }
    if (!navigator.mediaDevices?.getUserMedia) { QP.toast('Este dispositivo no permite acceder a la cámara.', 'error'); return; }
    videoStartPending = true;
    const stopping = stopScreenShare();
    const operation = videoOperation;
    const cancelled = () => operation !== videoOperation || !activeCallsList().length;
    try {
      await stopping;
      if (cancelled()) return;
      localVideoKind = 'camera'; renderVoice();
      beforeCameraConfig = { ...screenShareConfig };
      screenShareConfig = { ...screenShareConfig, backend: 'web', resolution: '720', fps: 30, bitrate: 2500000, acceleration: 'browser' };
      const selectedDevice = cameraDeviceId;
      const stream = await acquireCamera({ audio: false, video: {
        width: { ideal: 1280 }, height: { ideal: 720 }, frameRate: { ideal: 30, max: 30 },
        ...(cameraDeviceId ? { deviceId: { exact: cameraDeviceId } } : { facingMode: { ideal: cameraFacing } })
      } }, cancelled);
      if (cancelled()) { stream.getTracks().forEach(t => t.stop()); return; }
      screenStream = stream; browserCaptureSelected = true;
      const track = stream.getVideoTracks()[0];
      if (!track) throw new Error('La cámara no ha proporcionado vídeo.');
      track.onended = () => { if (screenStream === stream && operation === videoOperation) void stopScreenShare(); };
      browserSourceBounds = track.getSettings();
      if (['user', 'environment'].includes(browserSourceBounds.facingMode)) cameraFacing = browserSourceBounds.facingMode;
      await refreshScreenCodecCapabilities(true);
      if (cancelled()) return;
      const codec = ['h264', 'vp9', 'av1'].find(name => localScreenCodecDetails.get(getCodecId(name))?.supported && (voiceState.activeCalls || []).every(call => {
        const mask = remoteScreenCodecCapabilities.get(normalizePeerId(call.peerId))?.decoderCodecMask || Number(call.screenDecoderCodecMask || 0);
        return !mask || (mask & codecMaskBit(getCodecId(name))) !== 0;
      }));
      if (!codec) throw new Error('No hay un códec de vídeo compatible con los participantes.');
      screenShareConfig.codec = codec;
      const { width, height } = fitScreenOutput(browserSourceBounds.width || 1280, browserSourceBounds.height || 720);
      const profile = await startCanvasFrameExtraction(stream, width, height, getCodecId(codec), 30);
      if (cancelled()) { stopCanvasFrameExtraction(); return; }
      const compatible = await QP.api('/api/voice/screen-share/decoder-check', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ codec: profile.codecString, width, height }) });
      if (!compatible.supported) throw new Error('Un participante no admite el vídeo de esta cámara.');
      if (cancelled()) return;
      await QP.api('/api/voice/screen-share/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ width, height, codecId: profile.codecId, fps: profile.fps, kind: 'camera' }) });
      if (cancelled()) { await stopScreenShare(); return; }
      screenTransmissionReady = true; voiceState.isScreenSharing = true;
      createOrUpdateScreenTile('local', 'Tú · Cámara', width, height, profile.codecId, profile.fps, true);
      cameraDeviceId = track.getSettings().deviceId || selectedDevice;
      try { localStorage.setItem('quicnet.cameraDeviceId', cameraDeviceId); } catch { }
      await refreshCameraChoices('voiceCameraSelect');
    } catch (error) {
      if (cancelled()) return;
      console.error('No se pudo activar la cámara:', error);
      await stopScreenShare();
      QP.toast(error.name === 'NotReadableError' ? 'La cámara seleccionada sigue ocupada o no responde. Cierra otras aplicaciones que la usen o elige otra cámara.' : error.message || 'No se pudo activar la cámara.', 'error');
    } finally {
      if (cancelled() && (screenStream || beforeCameraConfig)) await stopScreenShare();
      videoStartPending = false; renderVoice();
    }
  }

  const verifiedEncoderProfiles = new Map();
  async function verifyVideoEncoder(config) {
    const key = JSON.stringify(config);
    if (verifiedEncoderProfiles.has(key)) return verifiedEncoderProfiles.get(key);
    // A support query alone does not open the driver. Encode a synthetic frame
    // so driver failures appear in settings, before the user starts sharing.
    const verification = (async () => {
      if (typeof VideoEncoder !== 'function') return true;
      let encoder, frame, timer;
      try {
        let rejectCodec;
        const failed = new Promise((_, reject) => { rejectCodec = reject; });
        let outputs = 0;
        encoder = new VideoEncoder({ output: () => { outputs++; }, error: rejectCodec });
        encoder.configure(config);
        const canvas = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(config.width, config.height) : document.createElement('canvas');
        canvas.width = config.width; canvas.height = config.height;
        const context = canvas.getContext('2d', { alpha: false });
        context.fillStyle = '#182231'; context.fillRect(0, 0, canvas.width, canvas.height);
        frame = new VideoFrame(canvas, { timestamp: 0 });
        encoder.encode(frame, { keyFrame: true });
        await Promise.race([encoder.flush(), failed, new Promise((_, reject) => {
          timer = setTimeout(() => reject(new Error('El codificador no respondió.')), 5000);
        })]);
        return outputs > 0;
      } catch { return false; }
      finally {
        clearTimeout(timer);
        frame?.close();
        if (encoder?.state !== 'closed') { try { encoder?.close(); } catch {} }
      }
    })();
    if (verifiedEncoderProfiles.size > 48) verifiedEncoderProfiles.clear();
    verifiedEncoderProfiles.set(key, verification);
    return verification;
  }

  async function probeVideoEncoderConfig(codecId, width, height, fps, bitrate, acceleration = screenShareConfig.acceleration) {
    if (typeof VideoEncoder === 'undefined' || typeof VideoEncoder.isConfigSupported !== 'function') return null;

    const targetFps = Math.max(5, Math.min(144, fps || screenShareConfig.fps || 60));
    const targetBitrate = bitrate || screenShareConfig.bitrate || 12000000;

    for (const codec of getCodecCandidates(codecId, width, height, targetFps)) {
      const isAvc = codec.startsWith('avc');
      const isHevc = codec.startsWith('hvc') || codec.startsWith('hev');
      for (const hw of [acceleration === 'cpu' ? 'prefer-software' : acceleration === 'browser' ? 'no-preference' : 'prefer-hardware']) {
        for (const bitrateMode of ['constant', 'variable', null]) {
          const baseConfig = {
            codec,
            width,
            height,
            bitrate: targetBitrate,
            framerate: targetFps,
            hardwareAcceleration: hw,
            latencyMode: 'realtime'
          };
          if (bitrateMode) baseConfig.bitrateMode = bitrateMode;
          const configVariants = [];
          if (isAvc) configVariants.push(Object.assign({}, baseConfig, { avc: { format: 'annexb' } }));
          if (isHevc) configVariants.push(Object.assign({}, baseConfig, { hevc: { format: 'annexb' } }));
          configVariants.push(baseConfig);

          for (const config of configVariants) {
            try {
              const support = await VideoEncoder.isConfigSupported(config);
              if (support && support.supported) {
                if (!await verifyVideoEncoder(support.config || config)) continue;
                return {
                  config: support.config || config,
                  effectiveCodecId: codecId,
                  codecString: codec,
                  hardwarePreference: hw,
                  bitrateMode: bitrateMode || 'runtime-default', verified: true
                };
              }
            } catch { }
          }
        }
      }
    }
    return null;
  }


  // Browser timers require their Window receiver; calling a detached timer as
  // timing.schedule(...) raises "Illegal invocation" in Chromium/WebView2.
  function createScreenFramePacer(fps, emit, timing = {
    now: () => performance.now(),
    schedule: (callback, delay) => window.setTimeout(callback, delay),
    cancel: timer => window.clearTimeout(timer)
  }) {
    const step = 1000 / fps;
    let latest = null, timer = null, stopped = false, next = timing.now(), start = next;
    const tick = () => {
      if (stopped) return;
      const now = timing.now();
      if (latest) emit(latest, Math.round((now - start) * 1000));
      next += step;
      if (next <= now) next += (Math.floor((now - next) / step) + 1) * step;
      timer = timing.schedule(tick, Math.max(0, next - timing.now()));
    };
    return {
      push(frame) {
        if (stopped) { frame.close(); return; }
        latest?.close(); latest = frame;
        if (timer === null) timer = timing.schedule(tick, Math.max(0, next - timing.now()));
      },
      stop() { stopped = true; if (timer !== null) timing.cancel(timer); latest?.close(); latest = null; }
    };
  }

  async function startWebCodecsExtraction(stream, width, height, codecId, fps) {
    const targetW = width || 1920;
    const targetH = height || 1080;
    const targetFps = Math.max(5, Math.min(144, fps || screenShareConfig.fps || 60));
    const targetBitrate = screenShareConfig.bitrate || 12000000;

    const encoderProbe = await probeVideoEncoderConfig(codecId, targetW, targetH, targetFps, targetBitrate);
    if (!encoderProbe) {
      throw new Error(`${getCodecNameFromId(codecId)} is not exposed by WebCodecs for ${targetW}×${targetH} at ${targetFps} FPS`);
    }

    const { config: encoderConfig, effectiveCodecId, codecString } = encoderProbe;
    stopCanvasFrameExtraction();
    screenCaptureActive = true;
    screenTransmissionReady = false;

    const localEntry = activeScreenStreams.get('local');
    if (localEntry) {
      localEntry.stats = {
        framesCount: 0,
        bytesCount: 0,
        lastTick: performance.now(),
        fps: 0,
        bitrateKbps: 0,
        realWidth: targetW,
        realHeight: targetH,
        codecName: getCodecNameFromId(effectiveCodecId)
      };
    }

    let lastEncoderKeyFrameTime = 0;
    let forceNextKeyFrame = true;
    let encoderFailureReported = false;
    let lastDecoderConfig = null;
    const generation = screenGeneration = Math.max(screenGeneration + 1, Date.now());
    const keyFrameIntervalMs = screenShareConfig.keyFrameIntervalMs || 1000; // I-frame every 1.5s for instant receiver synchronization

    try {
      screenVideoEncoder = new VideoEncoder({
        output: (chunk, metadata) => {
          if (!screenCaptureActive || !screenTransmissionReady || screenGeneration !== generation) return;

          let extraBytes = null;
          if (metadata?.decoderConfig) lastDecoderConfig = metadata.decoderConfig;
          const decoderMetadata = lastDecoderConfig;
          const extraObj = { t: chunk.timestamp, g: generation, w: targetW, h: targetH };
          if (decoderMetadata) {
            metadata = { decoderConfig: decoderMetadata };
            if (metadata.decoderConfig.codec) {
              extraObj.c = metadata.decoderConfig.codec;
            } else {
              extraObj.c = codecString;
            }
            if (metadata.decoderConfig.description) {
              const descArr = new Uint8Array(metadata.decoderConfig.description);
              let binary = '';
              for (let i = 0; i < descArr.byteLength; i++) {
                binary += String.fromCharCode(descArr[i]);
              }
              extraObj.d = btoa(binary);
            }
          }
          extraObj.c ||= codecString;
          try { extraBytes = new TextEncoder().encode(JSON.stringify(extraObj)); } catch {}

          const isKey = chunk.type === 'key';
          const hasExtra = extraBytes && extraBytes.length > 0;
          const flags = (isKey ? 0x01 : 0x00) | (hasExtra ? 0x02 : 0x00);

          const extraLen = hasExtra ? extraBytes.length : 0;
          const headerLen = hasExtra ? 3 : 1;
          const packetLen = 18 + headerLen + extraLen + chunk.byteLength;

          const packet = new Uint8Array(packetLen);
          packet[0] = 0x02; // Tag 0x02: screen video stream
          // bytes 1-16: TargetPeerId (Guid.Empty = broadcast)
          packet[17] = effectiveCodecId;
          packet[18] = flags;

          let writeOffset = 19;
          if (hasExtra) {
            packet[19] = (extraLen >> 8) & 0xFF;
            packet[20] = extraLen & 0xFF;
            packet.set(extraBytes, 21);
            writeOffset = 21 + extraLen;
          }

          chunk.copyTo(packet.subarray(writeOffset));
          // Keep roughly one second of transport headroom. If that limit is hit,
          // recover with a keyframe because later inter frames may depend on the
          // dropped chunk.
          const maxBufferedBytes = Math.max(64 * 1024, Math.round(targetBitrate / 8 * 0.1));
          if (!QP.sendBinary(packet.buffer, maxBufferedBytes)) {
            forceNextKeyFrame = true;
            return;
          }

          // Update broadcaster real-time telemetry
          const entry = activeScreenStreams.get('local');
          if (entry) {
            if (!entry.stats) {
              entry.stats = {
                framesCount: 0,
                bytesCount: 0,
                lastTick: performance.now(),
                fps: 0,
                bitrateKbps: 0,
                realWidth: targetW,
                realHeight: targetH,
                codecName: getCodecNameFromId(effectiveCodecId)
              };
            }
            entry.stats.framesCount++;
            entry.stats.bytesCount += packet.byteLength;
            entry.stats.realWidth = targetW;
            entry.stats.realHeight = targetH;

            const tNow = performance.now();
            const elapsed = (tNow - entry.stats.lastTick) / 1000;
            if (elapsed >= 1.0) {
              entry.stats.fps = Math.round((entry.stats.framesCount / elapsed) * 10) / 10;
              entry.stats.bitrateKbps = Math.round((entry.stats.bytesCount * 8) / (elapsed * 1000));
              entry.stats.framesCount = 0;
              entry.stats.bytesCount = 0;
              entry.stats.lastTick = tNow;

              const brStr = entry.stats.bitrateKbps >= 1000
                ? (entry.stats.bitrateKbps / 1000).toFixed(1) + ' Mbps'
                : entry.stats.bitrateKbps + ' kbps';

              const effective = document.getElementById('modalEffectiveFps');
              if (effective) effective.textContent = entry.stats.fps + ' FPS enviados · objetivo ' + targetFps;
              if (entry.codecBadge) {
                entry.codecBadge.textContent = `Enviando · ${entry.stats.codecName || getCodecNameFromId(effectiveCodecId)} · ${entry.stats.realWidth}×${entry.stats.realHeight} · ${entry.stats.fps}/${targetFps} FPS · ${brStr}`;
              }
            }
          }
        },
        error: (err) => {
          console.error('VideoEncoder error:', err);
          if (!screenCaptureActive || screenGeneration !== generation) return;
          screenTransmissionReady = false;
          if (!encoderFailureReported) {
            encoderFailureReported = true;
            QP.toast(`El codificador ${getCodecNameFromId(effectiveCodecId)} se ha detenido: ${err.message}`, 'error');
            stopScreenShare().catch(() => {});
          }
        }
      });

      screenVideoEncoder.configure(encoderConfig);
    } catch (err) {
      stopCanvasFrameExtraction();
      throw new Error(`WebCodecs rejected ${codecString}: ${err.message}`);
    }

    const videoTrack = stream.getVideoTracks()[0];
    const encoder = screenVideoEncoder;
    let scaledCanvas = null, scaledContext = null;
    const pacer = createScreenFramePacer(targetFps, (latest, timestamp) => {
      if (!screenCaptureActive || screenGeneration !== generation || !screenTransmissionReady || encoder.state !== 'configured') return;
      // Bounded latency: a busy encoder skips a tick, rather than accumulating delay.
      if (encoder.encodeQueueSize > 2) return;
      let frame;
      try {
        const sourceWidth = latest.displayWidth || latest.codedWidth;
        const sourceHeight = latest.displayHeight || latest.codedHeight;
        if (sourceWidth !== targetW || sourceHeight !== targetH) {
          if (!scaledCanvas) {
            scaledCanvas = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(targetW, targetH) : document.createElement('canvas');
            scaledCanvas.width = targetW; scaledCanvas.height = targetH;
            scaledContext = scaledCanvas.getContext('2d', { alpha: false });
          }
          // Always resize the whole source. Encoding a large source directly
          // with a smaller configuration can crop it on some implementations.
          scaledContext.drawImage(latest, 0, 0, sourceWidth, sourceHeight, 0, 0, targetW, targetH);
          frame = new VideoFrame(scaledCanvas, { timestamp });
        } else frame = new VideoFrame(latest, { timestamp });
        const now = performance.now();
        const keyFrame = forceNextKeyFrame || now - lastEncoderKeyFrameTime >= keyFrameIntervalMs;
        if (keyFrame) lastEncoderKeyFrameTime = now;
        encoder.encode(frame, { keyFrame });
        forceNextKeyFrame = false;
      } catch (error) {
        if (!encoderFailureReported) {
          encoderFailureReported = true;
          QP.reportError?.(error, 'Codificar pantalla');
          QP.toast('No se pudo codificar la pantalla: ' + error.message, 'error');
          stopScreenShare().catch(() => {});
        }
      } finally { frame?.close(); }
    });
    screenFramePacer = pacer;

    if (typeof MediaStreamTrackProcessor !== 'undefined' && videoTrack) {
      try {
        const reader = new MediaStreamTrackProcessor({ track: videoTrack }).readable.getReader();
        screenVideoReader = reader;
        (async () => {
          try {
            while (screenCaptureActive && screenGeneration === generation) {
              const { done, value } = await reader.read();
              if (done || !screenCaptureActive || screenGeneration !== generation) { value?.close(); break; }
              pacer.push(value);
            }
          } catch (error) {
            if (screenCaptureActive && screenGeneration === generation) {
              QP.reportError?.(error, 'Leer fotogramas de pantalla');
              QP.toast('La fuente de pantalla se ha detenido: ' + error.message, 'error');
              stopScreenShare().catch(() => {});
            }
          }
        })();
        return { codecId: effectiveCodecId, codecString, fps: targetFps, bitrate: targetBitrate };
      } catch (error) { console.warn('Captura directa no disponible; usando vídeo:', error); }
    }

    const video = document.createElement('video');
    screenCaptureVideo = video;
    video.srcObject = stream; video.muted = true; video.playsInline = true;
    await video.play();
    // Source events refresh only the latest image. The pacer independently
    // encodes that image at the requested FPS, including a static desktop.
    const sample = () => {
      if (!screenCaptureActive || screenGeneration !== generation) return;
      if (video.videoWidth > 0 && video.videoHeight > 0)
        pacer.push(new VideoFrame(video, { timestamp: Math.round(performance.now() * 1000) }));
      if (typeof video.requestVideoFrameCallback === 'function') video.requestVideoFrameCallback(sample);
      else screenCaptureAnimId = requestAnimationFrame(sample);
    };
    sample();
    return { codecId: effectiveCodecId, codecString, fps: targetFps, bitrate: targetBitrate };
  }

  async function startCanvasFrameExtraction(stream, width, height, codecId, fps) {
    if (typeof VideoEncoder === 'undefined') {
      throw new Error('VideoEncoder/WebCodecs is unavailable in this runtime');
    }
    if (codecId === 0) {
      throw new Error('JPEG is not a valid automatic video fallback');
    }
    return startWebCodecsExtraction(stream, width, height, codecId, fps);
  }

  function stopCanvasFrameExtraction() {
    screenCaptureActive = false;
    screenFramePacer?.stop(); screenFramePacer = null;
    screenTransmissionReady = false;
    if (screenVideoReader) {
      const reader = screenVideoReader;
      const cleanup = (async () => {
        try { await reader.cancel(); } catch { }
        try { await reader.closed; } catch { }
        try { reader.releaseLock(); } catch { }
      })();
      videoCaptureCleanup = Promise.allSettled([videoCaptureCleanup, cleanup]);
      screenVideoReader = null;
    }
    if (screenVideoEncoder) {
      try { screenVideoEncoder.close(); } catch { }
      screenVideoEncoder = null;
    }
    if (screenCaptureAnimId) {
      cancelAnimationFrame(screenCaptureAnimId);
      screenCaptureAnimId = null;
    }
    if (screenCaptureInterval) {
      clearInterval(screenCaptureInterval);
      screenCaptureInterval = null;
    }
    if (screenCaptureVideo) {
      try {
        screenCaptureVideo.pause();
        screenCaptureVideo.srcObject = null;
      } catch { }
      screenCaptureVideo = null;
    }
    return videoCaptureCleanup;
  }

  function stopScreenShare() {
    ++videoOperation;
    if (videoStopPending) return videoStopPending;
    const stopping = stopScreenShareCore();
    videoStopPending = stopping; renderVoice();
    const finished = () => { if (videoStopPending === stopping) videoStopPending = null; renderVoice(); };
    stopping.then(finished, finished);
    return stopping;
  }

  async function stopScreenShareCore() {
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
    if (wasSharing) QP.toast(wasCamera ? 'Cámara desactivada' : 'Has dejado de compartir la pantalla');
  }

  // --- Dynamic Multi-User Stream Grid Manager ---

  function initScreenViewer(entry, body, media, infoButton, resetButton, info) {
    let scale = 1, x = 0, y = 0, suppressClickUntil = 0, dragged = false;
    const pointers = new Map();
    const clampScale = value => Math.max(1, Math.min(8, value));
    const center = points => ({x: (points[0].x + points[1].x) / 2, y: (points[0].y + points[1].y) / 2});
    const distance = points => Math.hypot(points[1].x - points[0].x, points[1].y - points[0].y);
    const localPoint = (clientX, clientY) => {
      const rect = body.getBoundingClientRect();
      return {x: clientX - rect.left - rect.width / 2, y: clientY - rect.top - rect.height / 2};
    };
    function paint() {
      const {width, height} = body.getBoundingClientRect();
      // Clamp against the fitted picture, including portrait/ultrawide letterboxing.
      const canvasVisible = !entry.canvasEl.classList.contains('hidden');
      const sourceWidth = (canvasVisible ? entry.canvasEl.width : entry.videoEl.videoWidth) || entry.width || 1;
      const sourceHeight = (canvasVisible ? entry.canvasEl.height : entry.videoEl.videoHeight) || entry.height || 1;
      const fit = Math.min(width / sourceWidth, height / sourceHeight);
      const maxX = Math.max(0, (sourceWidth * fit * scale - width) / 2);
      const maxY = Math.max(0, (sourceHeight * fit * scale - height) / 2);
      x = Math.max(-maxX, Math.min(maxX, x)); y = Math.max(-maxY, Math.min(maxY, y));
      media.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
      body.dataset.zoom = String(scale);
      body.classList.toggle('is-zoomed', scale > 1);
      resetButton.textContent = `${Math.round(scale * 100)}%`;
      resetButton.setAttribute('aria-label', `Ajustar imagen completa · zoom ${Math.round(scale * 100)}%`);
    }
    function zoom(next, from, to = from) {
      const previous = scale; scale = clampScale(next);
      x = to.x - (from.x - x) * scale / previous;
      y = to.y - (from.y - y) * scale / previous;
      paint();
    }
    function reset() { scale = 1; x = y = 0; paint(); }
    function setInfo(visible) {
      entry.cardEl.classList.toggle('show-stream-info', visible);
      infoButton.setAttribute('aria-pressed', String(visible));
      info.setAttribute('aria-hidden', String(!visible));
    }
    function toggleInfo() { setInfo(!entry.cardEl.classList.contains('show-stream-info')); }
    infoButton.addEventListener('click', toggleInfo);
    resetButton.addEventListener('click', reset);
    body.tabIndex = 0;
    body.setAttribute('role', 'group');
    body.setAttribute('aria-label', 'Visor de vídeo. Rueda o dos dedos para ampliar; arrastra para mover. Enter muestra la información; 0 ajusta la imagen completa.');
    body.addEventListener('click', () => { if (performance.now() >= suppressClickUntil) toggleInfo(); });
    body.addEventListener('dblclick', reset);
    body.addEventListener('wheel', event => {
      event.preventDefault(); event.stopPropagation();
      const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? body.clientHeight : 1);
      zoom(scale * Math.exp(-Math.max(-300, Math.min(300, delta)) * .002), localPoint(event.clientX, event.clientY));
    }, {passive:false});
    body.addEventListener('pointerdown', event => {
      if (event.button !== 0) return;
      if (!pointers.size) dragged = false;
      pointers.set(event.pointerId, {x:event.clientX, y:event.clientY, originX:event.clientX, originY:event.clientY});
      if (pointers.size > 1) dragged = true;
      body.setPointerCapture(event.pointerId);
    });
    body.addEventListener('pointermove', event => {
      const previous = pointers.get(event.pointerId);
      if (!previous) return;
      const before = [...pointers.values()];
      pointers.set(event.pointerId, {...previous, x:event.clientX, y:event.clientY});
      if (Math.hypot(event.clientX - previous.originX, event.clientY - previous.originY) > 5) dragged = true;
      if (pointers.size === 2) {
        const after = [...pointers.values()], oldCenter = center(before), nextCenter = center(after);
        const oldDistance = distance(before);
        if (oldDistance > 0) zoom(scale * distance(after) / oldDistance,
          localPoint(oldCenter.x, oldCenter.y), localPoint(nextCenter.x, nextCenter.y));
      } else if (pointers.size === 1 && scale > 1) {
        x += event.clientX - previous.x; y += event.clientY - previous.y; paint();
      }
    });
    const release = event => {
      if (!pointers.delete(event.pointerId)) return;
      if (dragged || event.type === 'pointercancel') suppressClickUntil = performance.now() + 400;
    };
    for (const name of ['pointerup', 'pointercancel', 'lostpointercapture']) body.addEventListener(name, release);
    body.addEventListener('keydown', event => {
      if (event.ctrlKey || event.metaKey || event.altKey) return;
      if (event.key === '+' || event.key === '=') zoom(scale * 1.2, {x:0, y:0});
      else if (event.key === '-') zoom(scale / 1.2, {x:0, y:0});
      else if (event.key === '0' || event.key === 'Home') reset();
      else if (event.key === 'Enter' || event.key === ' ') toggleInfo();
      else if (event.key.startsWith('Arrow') && scale > 1) {
        x += event.key === 'ArrowLeft' ? 40 : event.key === 'ArrowRight' ? -40 : 0;
        y += event.key === 'ArrowUp' ? 40 : event.key === 'ArrowDown' ? -40 : 0; paint();
      } else return;
      event.preventDefault(); event.stopPropagation();
    });
    const observer = new ResizeObserver(paint); observer.observe(body);
    entry.videoEl.addEventListener('loadedmetadata', paint);
    setInfo(false); reset();
    return {reset, setInfo, refresh:paint, destroy() {observer.disconnect(); entry.videoEl.removeEventListener('loadedmetadata', paint); pointers.clear();}};
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

      // 2. Body (Video or Canvas viewport)
      const body = QP.el('div', 'screen-tile-body');
      const videoEl = document.createElement('video');
      videoEl.autoplay = true;
      videoEl.playsInline = true;
      videoEl.muted = true; // Muted for instant autoplay

      // Use a permanent double-buffered canvas instead of an <img> tag to eliminate torn image icon & black flicker
      const canvasEl = document.createElement('canvas');
      canvasEl.className = 'hidden';
      const canvasCtx = canvasEl.getContext('2d', { alpha: false });

      const loadingEl = QP.el('div', 'screen-tile-loading', 'Conectando vídeo…');

      // Encoded pixel dimensions must never determine the viewer's layout size.
      const media = QP.el('div', 'screen-tile-media');
      media.append(videoEl, canvasEl);
      body.append(media, loadingEl);
      card.append(header, body);
      grid.appendChild(card);

      // Setup live video playback
      let mediaSource = null;
      let sourceBuffer = null;
      const bufferQueue = [];
      let isAppending = false;

      if (isLocal && screenStream) {
        videoEl.srcObject = screenStream;
        videoEl.play().catch(() => {});
        videoEl.classList.remove('hidden');
        canvasEl.classList.add('hidden');
        loadingEl.classList.add('hidden');
      } else if (!isLocal && typeof window.MediaSource !== 'undefined') {
        try {
          mediaSource = new MediaSource();
          videoEl.src = URL.createObjectURL(mediaSource);

          mediaSource.addEventListener('sourceopen', () => {
            try {
              let mime = 'video/webm; codecs="vp8"';
              if (codecId === 1 && MediaSource.isTypeSupported('video/webm; codecs="h264"')) {
                mime = 'video/webm; codecs="h264"';
              } else if (codecId === 5 && MediaSource.isTypeSupported('video/mp4; codecs="hvc1"')) {
                mime = 'video/mp4; codecs="hvc1"';
              } else if (codecId === 3 && MediaSource.isTypeSupported('video/webm; codecs="vp9"')) {
                mime = 'video/webm; codecs="vp9"';
              } else if (codecId === 4 && MediaSource.isTypeSupported('video/webm; codecs="av01"')) {
                mime = 'video/webm; codecs="av01"';
              } else if (codecId === 2 && MediaSource.isTypeSupported('video/webm; codecs="vp8"')) {
                mime = 'video/webm; codecs="vp8"';
              } else if (MediaSource.isTypeSupported('video/webm; codecs="h264"')) {
                mime = 'video/webm; codecs="h264"';
              } else if (MediaSource.isTypeSupported('video/webm; codecs="vp8"')) {
                mime = 'video/webm; codecs="vp8"';
              }
              sourceBuffer = mediaSource.addSourceBuffer(mime);
              sourceBuffer.mode = 'sequence';

              sourceBuffer.addEventListener('updateend', () => {
                isAppending = false;
                if (bufferQueue.length > 0 && sourceBuffer && !sourceBuffer.updating) {
                  isAppending = true;
                  const next = bufferQueue.shift();
                  try { sourceBuffer.appendBuffer(next); } catch { isAppending = false; }
                }
              });

              // Flush early chunks that arrived before sourceopen fired
              if (bufferQueue.length > 0 && !sourceBuffer.updating) {
                isAppending = true;
                const next = bufferQueue.shift();
                try { sourceBuffer.appendBuffer(next); } catch { isAppending = false; }
              }
            } catch (err) {
              console.warn('Could not add SourceBuffer:', err);
            }
          });
        } catch (err) {
          console.warn('MediaSource setup failed:', err);
        }
      }

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
        if (entry.loadingEl) entry.loadingEl.classList.add('hidden');
      }
    }

    paintScreenAvatar(entry);
    return entry;
  }

  function removeScreenTile(peerId) {
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
        entry.videoEl.pause();
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
  const {jpeg: handleJpegVideoChunk, video: handleWebCodecsVideoChunk} = QuicNetMedia.createScreenVideoDecoder({
    getCodecCandidates, getCodecNameFromId, getAcceleration: () => screenDecoderAcceleration
  });

  // --- WebSocket Stream Chunk Delivery ---

  function onWsBinaryReceived(arrayBuffer) {
    if (!arrayBuffer || arrayBuffer.byteLength < 17) return;
    const view = new Uint8Array(arrayBuffer);
    const tag = view[0];

    // Screen Audio Chunk (Tag 0x04): [Tag 0x04 (1B)][PeerId 16B][PCM Int16 Bytes]
    if (tag === 0x04) {
      const peerId = parseGuidFromBytes(view, 1);
      const pcmBytes = view.subarray(17);
      playScreenAudioChunk(peerId, pcmBytes);
      return;
    }

    // Video Chunk (Tag 0x02): [Tag 0x02 (1B)][PeerId 16B][CodecId 1B][Video Bytes]
    if (tag === 0x02 && arrayBuffer.byteLength >= 18) {
      const parsedPeerId = parseGuidFromBytes(view, 1);
      const peerId = parsedPeerId === '00000000-0000-0000-0000-000000000000' ? 'local' : parsedPeerId;
      const codecId = view[17];
      const videoChunk = view.subarray(18);

      let entry = activeScreenStreams.get(peerId);
      if (!entry) {
        const peerName = resolvePeerName(peerId);
        entry = createOrUpdateScreenTile(peerId, peerName, 1920, 1080, codecId, 30, peerId === 'local');
      }
      if (!entry) return;

      // Real-time telemetry tracking for actual received video
      if (!entry.stats) {
        entry.stats = {
          frameCount: 0,
          byteCount: 0,
          lastTick: performance.now(),
          fps: 0,
          bitrateKbps: 0,
          realWidth: 0,
          realHeight: 0,
          codecName: getCodecNameFromId(codecId)
        };
      }
      entry.stats.frameCount++;
      entry.stats.byteCount += videoChunk.byteLength;

      const now = performance.now();
      const elapsed = (now - entry.stats.lastTick) / 1000;
      if (elapsed >= 1.0) {
        entry.stats.fps = Math.round((entry.stats.frameCount / elapsed) * 10) / 10;
        entry.stats.bitrateKbps = Math.round((entry.stats.byteCount * 8) / (elapsed * 1000));
        entry.stats.frameCount = 0;
        entry.stats.byteCount = 0;
        entry.stats.lastTick = now;

        const brStr = entry.stats.bitrateKbps >= 1000
          ? (entry.stats.bitrateKbps / 1000).toFixed(1) + ' Mbps'
          : entry.stats.bitrateKbps + ' kbps';

        const codecName = entry.stats.codecName || getCodecNameFromId(codecId);
        const resW = entry.stats.realWidth || entry.width || 1280;
        const resH = entry.stats.realHeight || entry.height || 720;

        if (entry.codecBadge) {
          entry.codecBadge.textContent = `${codecName} · ${resW}x${resH} · ${entry.stats.fps} FPS · ${brStr}`;
          entry.codecBadge.title = `Actual live incoming telemetry: ${entry.stats.fps} FPS, ${resW}x${resH}, ${brStr}`;
        }
      }

      if (entry.loadingEl && !entry.loadingEl.classList.contains('hidden')) {
        entry.loadingEl.classList.add('hidden');
      }

      // 1. JPEG Check: codecId === 0 or payload starting with JPEG SOI (0xFF 0xD8)
      if (codecId === 0 || (videoChunk.length > 3 && videoChunk[0] === 0xFF && videoChunk[1] === 0xD8)) {
        handleJpegVideoChunk(entry, videoChunk);
        return;
      }

      // 2. Hardware-accelerated GPU WebCodecs decoding pipeline
      if (typeof VideoDecoder !== 'undefined') {
        handleWebCodecsVideoChunk(entry, codecId, videoChunk);
        return;
      }

      // 3. Fallback: Feed continuous video chunk to MediaSource SourceBuffer if supported
      const sb = entry.getSourceBuffer ? entry.getSourceBuffer() : entry.sourceBuffer;
      if (sb) {
        try {
          if (entry.stats && entry.videoEl && entry.videoEl.videoWidth > 0) {
            entry.stats.realWidth = entry.videoEl.videoWidth;
            entry.stats.realHeight = entry.videoEl.videoHeight;
          }
          if (!sb.updating && entry.bufferQueue.length === 0) {
            entry.setIsAppending(true);
            sb.appendBuffer(videoChunk);
          } else {
            entry.bufferQueue.push(videoChunk);
            if (entry.bufferQueue.length > 60) {
              entry.bufferQueue.shift();
            }
          }
          if (entry.videoEl) {
            entry.videoEl.classList.remove('hidden');
            if (entry.videoEl.paused) entry.videoEl.play().catch(() => {});
          }
          if (entry.canvasEl) entry.canvasEl.classList.add('hidden');
        } catch { }
      } else {
        entry.bufferQueue.push(videoChunk);
        if (entry.bufferQueue.length > 60) {
          entry.bufferQueue.shift();
        }
      }
    }
  }

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
      ++videoOperation;
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
    const peerName = resolvePeerName(normId);
    removeScreenTile(normId);
    QP.toast(`${peerName || 'A participant'} stopped screen sharing`);
  }

  function onScreenFrameReceived(data) {
    // Handled primarily via high-efficiency WebSocket binary stream
  }
})();
