import { ZSTDDecoder } from './vendor/zstddec-0.3.1.js';
self.onmessage = async ({data}) => {
  try {
    const decoder = new ZSTDDecoder();
    await decoder.init();
    const result = decoder.decode(new Uint8Array(data.bytes), data.size);
    if (result.byteLength !== data.size) throw Error('El archivo descomprimido tiene un tamaño incorrecto.');
    const hash = [...new Uint8Array(await crypto.subtle.digest('SHA-256', result))].map(v=>v.toString(16).padStart(2,'0')).join('');
    if (hash !== data.sha256.toLowerCase()) throw Error('La integridad del archivo descomprimido no coincide.');
    self.postMessage({bytes:result.buffer}, [result.buffer]);
  } catch(error) { self.postMessage({error:error.message}); }
};
