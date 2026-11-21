// Shared Node entry point for fast VM tests and isolated real-browser suites.
import {readdir, mkdir, writeFile} from 'node:fs/promises';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {resolve} from 'node:path';
import {randomUUID} from 'node:crypto';

const root = fileURLToPath(new URL('../', import.meta.url));
const args = process.argv.slice(2);
const web = args.includes('--web'), browser = args.includes('--browser');
const verbose = args.includes('--verbose'), list = args.includes('--list');
const noBuild = args.includes('--no-build');
const filters = args.filter(arg => arg.startsWith('--filter=')).map(arg => arg.slice(9).toLowerCase());
const unknown = args.filter(arg => !['--web','--browser','--verbose','--list','--no-build'].includes(arg) && !arg.startsWith('--filter='));
if ((!web && !browser) || unknown.length || filters.includes('')) {
  console.error('Usage: node scripts/test-web.mjs --web|--browser [--filter=Name ...] [--list] [--no-build] [--verbose]');
  process.exit(2);
}
const directory = resolve(root, 'QuicPunchTests/Tests');
const available = (await readdir(directory)).filter(name =>
  (web && name.endsWith('WebTests.mjs')) || (browser && name.endsWith('BrowserTests.mjs'))).sort();
const unmatched = filters.filter(filter => !available.some(name => name.toLowerCase().includes(filter)));
if (unmatched.length) { console.error('No matching test suites for: ' + unmatched.join(', ')); process.exit(2); }
const files = filters.length ? available.filter(name => filters.some(filter => name.toLowerCase().includes(filter))) : available;
if (!files.length) { console.error('No matching test suites.'); process.exit(2); }
if (list) { console.log(files.join('\n')); process.exit(0); }
const logs = resolve(root, '.artifacts/test-output');
await mkdir(logs, {recursive: true});
const run = randomUUID();
const env = {...process.env, QUICNET_TEST_NO_BUILD: noBuild ? '1' : '0'};
let failed = 0;
function execute(command, args) {
  return new Promise(resolveResult => {
    let output = '';
    const child = spawn(command, args, {cwd: root, env, windowsHide: true, stdio: ['ignore','pipe','pipe']});
    child.stdout.on('data', data => {output += data; if (verbose) process.stdout.write(data);});
    child.stderr.on('data', data => {output += data; if (verbose) process.stderr.write(data);});
    child.once('error', error => {output += error.stack;});
    child.once('close', code => resolveResult({code, output}));
  });
}
for (const file of files) {
  const start = performance.now();
  const result = await execute(process.execPath, [resolve(directory, file)]);
  const duration = ((performance.now() - start) / 1000).toFixed(1);
  const path = resolve(logs, `${file.slice(0,-4)}-${run}.log`);
  await writeFile(path, result.output);
  if (result.code === 0) console.log(`OK ${file.slice(0,-4)} (${duration}s)`);
  else {
    failed++;
    console.error(`FAIL ${file.slice(0,-4)} (${duration}s, exit=${result.code})`);
    if (!verbose) console.error(result.output.split(/\r?\n/).filter(line => /Error|Assertion|^\s+at /.test(line)).slice(0,3).join('\n'));
    console.error(`Log: ${path}`);
  }
}
console.log(`${files.length - failed}/${files.length} suites passed.`);
process.exitCode = failed ? 1 : 0;
