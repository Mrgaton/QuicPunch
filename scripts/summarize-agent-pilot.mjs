import { readFile } from 'node:fs/promises';

const [file, ...extraArgs] = process.argv.slice(2);
if (!file || extraArgs.length) {
  console.error('Usage: node scripts/summarize-agent-pilot.mjs <runs.jsonl>');
  process.exit(2);
}

const isLabel = value => typeof value === 'string' && value.trim().length > 0;
const isDuration = value => Number.isFinite(value) && value >= 0;
function parseRows(input) {
  return input.split(/\r?\n/).flatMap((line, index) => {
    if (!line.trim()) return [];
    try {
      const row = JSON.parse(line);
      if (![row.task, row.agent, row.model].every(isLabel) ||
          !Number.isInteger(row.pair) || row.pair < 1 ||
          !['A', 'B'].includes(row.variant) || typeof row.success !== 'boolean' ||
          !isDuration(row.elapsedSeconds) || row.elapsedSeconds === 0 ||
          (row.firstValidationSeconds != null &&
            (!isDuration(row.firstValidationSeconds) || row.firstValidationSeconds > row.elapsedSeconds)) ||
          (row.billedTokens != null && (!Number.isSafeInteger(row.billedTokens) || row.billedTokens < 0))) {
        throw new Error('invalid run fields');
      }
      return [row];
    } catch (error) {
      throw new Error(`Line ${index + 1}: ${error.message}`, { cause: error });
    }
  });
}

function groupBy(rows, key) {
  const groups = new Map();
  for (const row of rows) {
    const id = JSON.stringify(key(row));
    if (!groups.has(id)) groups.set(id, []);
    groups.get(id).push(row);
  }
  return groups;
}

function median(values) {
  if (!values.length) return null;
  const sorted = values.toSorted((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
}

function describe(rows) {
  const valid = rows.filter(row => row.success);
  const totalSeconds = rows.reduce((sum, row) => sum + row.elapsedSeconds, 0);
  return {
    runs: rows.length,
    successes: valid.length,
    totalSeconds,
    successfulRunMedianSeconds: median(valid.map(row => row.elapsedSeconds)),
    firstValidationMedianSeconds: median(rows.flatMap(row => row.firstValidationSeconds ?? [])),
    secondsPerValidDelivery: valid.length ? totalSeconds / valid.length : null,
    billedTokensPerValidDelivery: valid.length && rows.every(row => row.billedTokens != null)
      ? rows.reduce((sum, row) => sum + row.billedTokens, 0) / valid.length : null
  };
}

try {
  const rows = parseRows(await readFile(file, 'utf8'));
  if (!rows.length) throw new Error('The input contains no runs.');
  const pairs = [];
  for (const [key, runs] of groupBy(rows, row => [row.agent, row.model, row.task, row.pair])) {
    const a = runs.find(row => row.variant === 'A');
    const b = runs.find(row => row.variant === 'B');
    if (runs.length !== 2 || !a || !b) throw new Error(`Expected exactly one A and one B for ${key}.`);
    pairs.push({ a, b, agent: a.agent, model: a.model, task: a.task });
  }

  const result = [];
  for (const [key, runs] of groupBy(pairs, pair => [pair.agent, pair.model])) {
    const [agent, model] = JSON.parse(key);
    const byTask = [];
    for (const [task, outcomes] of groupBy(runs, pair => pair.task)) {
      const validPairs = outcomes.filter(({ a, b }) => a.success && b.success);
      const deltas = validPairs.map(({ a, b }) => 100 * (b.elapsedSeconds - a.elapsedSeconds) / a.elapsedSeconds);
      byTask.push({
        task: JSON.parse(task),
        pairs: outcomes.length,
        A: describe(outcomes.map(pair => pair.a)),
        B: describe(outcomes.map(pair => pair.b)),
        pairedSuccessfulRuns: deltas.length,
        medianPairedDeltaPercent: median(deltas),
        pairedDeltaRangePercent: deltas.length ? [Math.min(...deltas), Math.max(...deltas)] : []
      });
    }
    result.push({
      agent, model, tasks: byTask.length, pairs: runs.length,
      A: describe(runs.map(pair => pair.a)),
      B: describe(runs.map(pair => pair.b)),
      medianTaskPairedDeltaPercent: median(byTask.flatMap(task => task.medianPairedDeltaPercent ?? [])),
      byTask
    });
  }
  console.log(JSON.stringify(result, null, 2));
} catch (error) {
  console.error(error.message);
  process.exitCode = 2;
}

