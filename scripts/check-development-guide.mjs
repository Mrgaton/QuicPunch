import { access, readFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = new URL('../', import.meta.url);
const map = new URL('doc/architecture/development-map.md', root);
const text = await readFile(map, 'utf8');
const failures = [];

for (const [, target] of text.matchAll(/\]\(([^\s)]+)\)/g)) {
  const url = new URL(target, map);
  if (url.protocol !== 'file:') continue;
  try {
    await access(url);
  } catch {
    failures.push(`Missing link in development map: ${target}`);
  }
}

for (const [, suite] of text.matchAll(/`([\w]+(?:WebTests|BrowserTests)\.mjs)`/g)) {
  try {
    await access(new URL(`QuicPunchTests/Tests/${suite}`, root));
  } catch {
    failures.push(`Unknown JavaScript suite in development map: ${suite}`);
  }
}

const [harness, ...extraArgs] = process.argv.slice(2);
if (extraArgs.length) {
  console.error('Usage: node scripts/check-development-guide.mjs [path-to-QuicPunchTests.dll]');
  process.exit(2);
}
if (harness) {
  const list = spawnSync('dotnet', [harness, '--list'], { cwd: fileURLToPath(root), encoding: 'utf8' });
  if (list.status !== 0) {
    failures.push(`Could not list C# suites: ${list.error?.message ?? list.stderr.trim()}`);
  } else {
    const flags = new Set(list.stdout.trim().split(/\r?\n/));
    for (const [, suite] of text.matchAll(/`(--test-[a-z0-9-]+)`/g)) {
      if (!flags.has(suite)) failures.push(`Unknown C# suite in development map: ${suite}`);
    }
  }
}

for (const failure of failures) console.error(failure);
if (!failures.length) console.log('OK development map' + (harness ? ' (links, JS and C# suites)' : ' (links and JS suites)'));
process.exitCode = failures.length ? 1 : 0;

