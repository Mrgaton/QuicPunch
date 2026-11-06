/* ISO BMFF AVIF sequence of independent AV1 frames produced by our libavif encoder. */
const ascii = text => new TextEncoder().encode(text);
const join = (...parts) => { const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0)); let at = 0; for (const p of parts) { out.set(p, at); at += p.length; } return out; };
const zeros = n => new Uint8Array(n);
const u64 = value => { const out = zeros(8); new DataView(out.buffer).setBigUint64(0, BigInt(value)); return out; };
const u32 = (...values) => { const out = zeros(values.length * 4), view = new DataView(out.buffer); values.forEach((v, i) => view.setUint32(i * 4, v)); return out; };
const u16 = (...values) => { const out = zeros(values.length * 2), view = new DataView(out.buffer); values.forEach((v, i) => view.setUint16(i * 2, v)); return out; };
const box = (type, ...data) => { const payload = join(...data); return join(u32(payload.length + 8), ascii(type), payload); };
const full = (type, flags, ...data) => box(type, u32(flags), ...data);
const matrix = u32(65536, 0, 0, 0, 65536, 0, 0, 0, 1073741824);

function children(bytes) {
  const result = [];
  for (let at = 0; at < bytes.length;) {
    if (at + 8 > bytes.length) throw new Error('AVIF incompleto.');
    const length = new DataView(bytes.buffer, bytes.byteOffset + at, 4).getUint32(0);
    if (length < 8 || at + length > bytes.length) throw new Error('AVIF no válido.');
    result.push({ type: new TextDecoder().decode(bytes.subarray(at + 4, at + 8)), data: bytes.subarray(at + 8, at + length) }); at += length;
  }
  return result;
}
function item(parent, type, skip = 0) { const found = children(parent.subarray(skip)).find(v => v.type === type); if (!found) throw new Error(`AVIF sin ${type}.`); return found.data; }
export function readStillFrame(buffer) {
  const bytes = new Uint8Array(buffer), meta = item(bytes, 'meta'), properties = item(item(meta, 'iprp', 4), 'ipco');
  const spatial = item(properties, 'ispe'), view = new DataView(spatial.buffer, spatial.byteOffset, spatial.byteLength);
  const width = view.getUint32(4), height = view.getUint32(8);
  if (width !== height || width < 1 || width > 512) throw new Error('El AVIF debe ser cuadrado y de hasta 512 píxeles.');
  // Animation input is composited onto the selected background: a single AV1 color item.
  if (children(properties).some(v => v.type === 'auxC')) throw new Error('La animación necesita un fondo opaco.');
  return { width, height, meta, config: item(properties, 'av1C'), color: children(properties).find(v => v.type === 'colr')?.data, data: item(bytes, 'mdat') };
}
export function muxAvifSequence(frames) {
  if (frames.length < 2 || frames.length > 120) throw new Error('La animación debe tener entre 2 y 120 fotogramas.');
  const first = frames[0], size = first.width;
  if (frames.some(f => f.width !== size || f.height !== size || !f.data?.length)) throw new Error('Los fotogramas deben tener el mismo tamaño cuadrado.');
  if (frames.some(f => !Number.isFinite(f.duration) || f.duration < 33 || f.duration > 10000)) throw new Error('Duración de fotograma no válida.');
  const durations = frames.map(f => Math.round(f.duration)), duration = durations.reduce((a, b) => a + b, 0);
  if (duration > 10000) throw new Error('El fragmento no puede superar 10 segundos.');
  const ftyp = box('ftyp', ascii('avis'), u32(0), ascii('avisavifmsf1iso8mif1miafMA1B'));
  const movie = offset => {
    const entry = box('av01', zeros(6), u16(1), zeros(16), u16(size, size), u32(4718592, 4718592, 0), u16(1), zeros(32), u16(24, 65535), box('av1C', first.config), ...(first.color ? [box('colr', first.color)] : []));
    const stbl = box('stbl', full('stsd', 0, u32(1), entry), full('stts', 0, u32(frames.length), ...durations.map(d => u32(1, d))), full('stsc', 0, u32(1, 1, frames.length, 1)), full('stsz', 0, u32(0, frames.length), ...frames.map(f => u32(f.data.length))), full('stco', 0, u32(1, offset)));
    const dinf = box('dinf', full('dref', 0, u32(1), full('url ', 1)));
    const minf = box('minf', full('vmhd', 1, zeros(8)), dinf, stbl);
    const mdia = box('mdia', full('mdhd', 0, u32(0, 0, 1000, duration), u16(21956, 0)), full('hdlr', 0, u32(0), ascii('pict'), zeros(12), ascii('PictureHandler\0')), minf);
    const edits = box('edts', full('elst', 1, u32(1, duration, 0), u16(1, 0)));
    const trak = box('trak', full('tkhd', 0x01000003, u64(0), u64(0), u32(1, 0), u64(0xffffffffffffffffn), zeros(8), u16(0, 0, 0, 0), matrix, u32(size * 65536, size * 65536)), edits, mdia);
    return box('moov', full('mvhd', 0x01000000, u64(0), u64(0), u32(1000), u64(0xffffffffffffffffn), u32(65536), u16(256, 0), zeros(8), matrix, zeros(24), u32(2)), trak);
  };
  // The primary item supplies the poster; the movie track supplies the animation.
  const metadata = new Uint8Array(first.meta);
  const locations = item(metadata, 'iloc', 4);
  if (locations.length !== 22 || locations[0] !== 0 || locations[4] !== 0x44 || locations[5] !== 0 || new DataView(locations.buffer, locations.byteOffset).getUint16(6) !== 1) throw new Error('Disposición AVIF no compatible.');
  const meta = box('meta', metadata), moov = movie(0), offset = ftyp.length + meta.length + moov.length + 8;
  new DataView(locations.buffer, locations.byteOffset).setUint32(14, offset);
  return join(ftyp, box('meta', metadata), movie(offset), box('mdat', ...frames.map(f => f.data))).buffer;
}
