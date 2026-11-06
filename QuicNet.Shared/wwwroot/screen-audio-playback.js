// Media lifetime is scoped to one call room; UI state is supplied explicitly.
(globalThis.QuicNetMedia ??= {}).createScreenAudioPlayback = function({getStreams, normalizePeerId, toast}) {
  function updateScreenAudioGain(entry) {
    if (!entry.gainNode) return;
    const gain = entry.gainNode.gain;
    const volume = entry.audioMuted ? 0 : (entry.audioVolume ?? 1);
    if (gain.setTargetAtTime) gain.setTargetAtTime(volume, entry.audioContext.currentTime, .008);
    else gain.value = volume;
  }

  function resetScreenAudioPlayback(entry) {
    entry.audioPending = []; entry.audioPendingFrames = 0;
    entry.audioPlayer?.reset();
  }

  function drainScreenAudioPlayback(entry) {
    if (!entry.audioPlayer || entry.audioContext?.state !== 'running') return;
    const pending = entry.audioPending;
    entry.audioPending = []; entry.audioPendingFrames = 0;
    if (entry.audioMuted || entry.audioVolume === 0) return;
    for (const samples of pending) entry.audioPlayer.push(samples);
  }

  async function initializeScreenAudioPlayback(entry, context) {
    let player;
    if (context.audioWorklet && typeof AudioWorkletNode === 'function') {
      try {
        await context.audioWorklet.addModule('/screen-audio-worklet.js');
        if (entry.audioContext !== context || context.state === 'closed') return;
        const node = new AudioWorkletNode(context, 'quicnet-screen-audio', {numberOfInputs:0, numberOfOutputs:1, outputChannelCount:[1]});
        player = {node, push:samples=>node.port.postMessage(samples, [samples.buffer]), reset:()=>node.port.postMessage('reset'),
          destroy:()=>{node.disconnect();node.port.close();}};
      } catch (error) { console.warn('AudioWorklet no disponible; usando reproducción PCM con búfer:', error); }
    }
    if (!player) {
      const {ScreenAudioBuffer} = await import('/screen-audio-buffer.js');
      if (entry.audioContext !== context || context.state === 'closed') return;
      const queue = new ScreenAudioBuffer(context.sampleRate);
      const node = context.createScriptProcessor(1024, 0, 1);
      node.onaudioprocess = event => queue.render(event.outputBuffer.getChannelData(0));
      player = {node, push:samples=>queue.push(samples), reset:()=>queue.reset(), destroy:()=>{node.onaudioprocess=null;node.disconnect();}};
    }
    if (entry.audioContext !== context || context.state === 'closed') { player.destroy(); return; }
    player.node.connect(entry.gainNode);
    entry.audioPlayer = player;
    drainScreenAudioPlayback(entry);
  }

  function playScreenAudioChunk(peerId, pcmBytes) {
    if (!pcmBytes || pcmBytes.byteLength < 2) return;
    const normId = normalizePeerId(peerId);
    let entry = getStreams().get(normId);

    if (!entry) {
      for (const [k, v] of getStreams().entries()) {
        if (k.includes(normId) || normId.includes(k)) {
          entry = v;
          break;
        }
      }
    }

    if (!entry || entry.isLocal || entry.audioFailed) return;
    if (entry.audioMuted || entry.audioVolume === 0) return;

    if (!entry.audioContext) {
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (!AudioCtx) return;
      entry.audioContext = new AudioCtx({ sampleRate: 48000, latencyHint: 'interactive' });
      entry.gainNode = entry.audioContext.createGain();
      entry.gainNode.gain.value = (typeof entry.audioVolume === 'number') ? entry.audioVolume : 1.0;
      entry.gainNode.connect(entry.audioContext.destination);
      entry.audioContext.onstatechange = () => {
        if (entry.audioContext?.state === 'running') drainScreenAudioPlayback(entry);
        else entry.audioPlayer?.reset();
      };
      const context = entry.audioContext;
      entry.audioSetup = initializeScreenAudioPlayback(entry, context).catch(error => {
        if (entry.audioContext !== context) return;
        entry.audioFailed = true; resetScreenAudioPlayback(entry);
        console.error('No se pudo iniciar el audio del vídeo:', error);
        toast('No se pudo iniciar el audio del vídeo compartido.', 'error');
      });
    }

    if (entry.audioContext.state === 'suspended') {
      entry.audioContext.resume().catch(() => {});
    }

    const availableSamples = Math.floor(pcmBytes.byteLength / 2);
    const sampleCount = Math.min(11520, availableSamples);
    if (sampleCount === 0) return;

    const channel = new Float32Array(sampleCount);
    const dv = new DataView(pcmBytes.buffer, pcmBytes.byteOffset, pcmBytes.byteLength);

    for (let i = 0; i < sampleCount; i++) {
      const s = dv.getInt16((availableSamples - sampleCount + i) * 2, true);
      channel[i] = s / 32768.0;
    }

    if (entry.audioPlayer && entry.audioContext.state === 'running') entry.audioPlayer.push(channel);
    else {
      entry.audioPending.push(channel); entry.audioPendingFrames += channel.length;
      while (entry.audioPendingFrames > 11520 && entry.audioPending.length > 1)
        entry.audioPendingFrames -= entry.audioPending.shift().length;
    }
  }

  return {play: playScreenAudioChunk, reset: resetScreenAudioPlayback, updateGain: updateScreenAudioGain};
};
