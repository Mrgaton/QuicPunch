// Copy data assets only from the official flag-icons npm package. No package code runs.
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { gunzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
const root = new URL('../QuicNet.Shared/wwwroot/flags/', import.meta.url);
const pinPath = new URL('../QuicNet.Shared/wwwroot/flags/source.json', import.meta.url);
let pin;
try { pin = JSON.parse(await readFile(pinPath, 'utf8')); }
catch (error) { if (error.code !== 'ENOENT') throw error; }
const response = await fetch('https://registry.npmjs.org/flag-icons/' + (pin?.version || 'latest'));
if (!response.ok) throw Error('Cannot read flag-icons metadata: ' + response.status);
const metadata = await response.json();
if (!/^https:\/\/registry\.npmjs\.org\/flag-icons\/-\/flag-icons-[\d.]+\.tgz$/.test(metadata.dist.tarball)) throw Error('Unexpected package location');
const archiveResponse = await fetch(metadata.dist.tarball);
if (!archiveResponse.ok) throw Error('Cannot download flag assets: ' + archiveResponse.status);
const compressed = Buffer.from(await archiveResponse.arrayBuffer());
const integrity = 'sha512-' + createHash('sha512').update(compressed).digest('base64');
if (integrity !== metadata.dist.integrity || (pin && integrity !== pin.integrity)) throw Error('Flag package checksum does not match');
const tar = gunzipSync(compressed);
await mkdir(root, { recursive: true });
const codes = [];
let licensed = false;
for (let offset = 0; offset + 512 <= tar.length;) {
 const header = tar.subarray(offset, offset + 512);
 if (header.every(byte => byte === 0)) break;
 const name = header.subarray(0,100).toString().replace(/\0.*$/, '');
 const size = parseInt(header.subarray(124,136).toString().replace(/\0.*$/, '').trim(),8);
 if (!Number.isSafeInteger(size) || size < 0 || offset + 512 + size > tar.length) throw Error('Invalid package entry');
 const content = tar.subarray(offset + 512, offset + 512 + size);
 const match = name.match(/^package\/flags\/4x3\/([a-z]{2})\.svg$/);
 if (match) {
  const svg = content.toString('utf8');
  if (/<script\b|\bon\w+\s*=|(?:href|src)\s*=\s*["'](?:https?:|\/\/|javascript:)/i.test(svg)) throw Error('Non-local flag SVG: ' + name);
  await writeFile(new URL(match[1] + '.svg', root), content); codes.push(match[1]);
 } else if (name === 'package/LICENSE') { await writeFile(new URL('LICENSE', root), content); licensed = true; }
 offset += 512 + Math.ceil(size / 512) * 512;
}
if (!licensed || codes.length < 249) throw Error('Incomplete country flag package');
codes.sort();
await writeFile(new URL('codes.json', root), JSON.stringify(codes));
await writeFile(pinPath, JSON.stringify({ package: 'flag-icons', version: metadata.version, integrity, source: 'https://github.com/lipis/flag-icons', license: 'MIT' }, null, 2) + '\n');
console.log(`Vendored ${codes.length} local country flags from flag-icons ${metadata.version} into ${fileURLToPath(root)}`);
