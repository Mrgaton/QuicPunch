// Media lifetime is scoped to one call room; UI state is supplied explicitly.
(globalThis.QuicNetMedia ??= {}).createScreenAudioCapture = function({api, sendBinary, toast, getCapabilities}) {
  const QP = {api, sendBinary, toast};
  let screenAudioStream = null;
  let screenAudioGeneration = 0;
  let nativeScreenAudioSessionId = null;
  let screenAudioContext = null;
  let screenAudioProcessor = null;
  let screenAudioSource = null;
  async function startScreenAudioCapture(stream, audioSessionId) {
    stopScreenAudioCapture();
    const generation = screenAudioGeneration;
    try {
      const audioTracks = stream.getAudioTracks();
      if (!audioTracks || audioTracks.length === 0) return;

      // Whole-desktop audio includes the native voice output, which a WebView's
      // restrictOwnAudio constraint cannot exclude. Windows excludes the entire
      // QuicNet process tree, including received screen audio in child WebViews.
      const wholeSystem = stream.getVideoTracks()[0]?.getSettings()?.displaySurface === 'monitor';
      if (wholeSystem && getCapabilities()?.systemAudioExcludesCall) {
        nativeScreenAudioSessionId = audioSessionId;
        audioTracks.forEach(track => { track.enabled = false; });
        try {
          await QP.api('/api/voice/screen-share/system-audio/start', {
            method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({audioSessionId})
          });
          if (generation !== screenAudioGeneration) return;
          if (audioTracks.some(track => track.readyState === 'ended')) { stopScreenAudioCapture(); return; }
          // Revoking the browser's audio track also revokes its replacement.
          audioTracks.forEach(track => track.addEventListener('ended', () => {
            if (generation === screenAudioGeneration) stopScreenAudioCapture();
          }, {once:true}));
        } catch (error) {
          if (generation === screenAudioGeneration) {
            stopScreenAudioCapture();
            QP.toast(error.message || 'No se pudo excluir la llamada del audio compartido.', 'error');
          }
        }
        return;
      }

      screenAudioStream = new MediaStream([audioTracks[0]]);
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (!AudioCtx) return;

      screenAudioContext = new AudioCtx({ sampleRate: 48000 });
      screenAudioSource = screenAudioContext.createMediaStreamSource(screenAudioStream);

      // Buffer size 1024 (approx 21ms at 48kHz)
      screenAudioProcessor = screenAudioContext.createScriptProcessor(1024, 1, 1);
      screenAudioProcessor.onaudioprocess = (e) => {
        if (!screenAudioProcessor) return;
        const inputFloat = e.inputBuffer.getChannelData(0);
        const pcmInt16 = new Int16Array(inputFloat.length);
        for (let i = 0; i < inputFloat.length; i++) {
          const s = Math.max(-1, Math.min(1, inputFloat[i]));
          pcmInt16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
        }

        // Tag 0x03: Screen Share Audio PCM Int16 samples
        const packet = new Uint8Array(1 + pcmInt16.byteLength);
        packet[0] = 0x03;
        packet.set(new Uint8Array(pcmInt16.buffer), 1);
        QP.sendBinary(packet.buffer);
      };

      screenAudioSource.connect(screenAudioProcessor);
      const silentGain = screenAudioContext.createGain();
      silentGain.gain.value = 0;
      screenAudioProcessor.connect(silentGain);
      silentGain.connect(screenAudioContext.destination);
      QP.toast('System & tab audio sharing enabled', 'info');
    } catch (err) {
      console.warn('Could not initialize screen audio capture:', err);
    }
  }

  function stopScreenAudioCapture() {
    ++screenAudioGeneration;
    const audioSessionId = nativeScreenAudioSessionId;
    nativeScreenAudioSessionId = null;
    if (audioSessionId) QP.api('/api/voice/screen-share/system-audio/stop', {
      method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({audioSessionId})
    }).catch(error => console.warn('No se pudo detener el audio de la pantalla:', error));
    if (screenAudioProcessor) {
      try { screenAudioProcessor.disconnect(); } catch { }
      screenAudioProcessor = null;
    }
    if (screenAudioSource) {
      try { screenAudioSource.disconnect(); } catch { }
      screenAudioSource = null;
    }
    if (screenAudioContext) {
      try { screenAudioContext.close(); } catch { }
      screenAudioContext = null;
    }
    if (screenAudioStream) {
      try {
        screenAudioStream.getTracks().forEach(t => t.stop());
      } catch { }
      screenAudioStream = null;
    }
  }


  return {start: startScreenAudioCapture, stop: stopScreenAudioCapture, get sessionId() { return nativeScreenAudioSessionId; }};
};
