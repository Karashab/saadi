// Read-only conversion of a decrypted dashboard snapshot into flat task rows.
// usage: node export-tasks.mjs <snapshot.json> <output-dir>
// Output contains project data: keep it out of Git.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const [input, outDir] = process.argv.slice(2);
if (!input || !outDir) throw new Error('usage: node export-tasks.mjs <snapshot.json> <output-dir>');

const toEn = s => String(s).replace(/[۰-۹]/g, d => '۰۱۲۳۴۵۶۷۸۹'.indexOf(d));
// Calendar table from ICU's Persian calendar; a date absent from it is invalid.
const JALALI = new Map();
const fmt = new Intl.DateTimeFormat('en-u-ca-persian-nu-latn', { timeZone: 'UTC', year: 'numeric', month: 'numeric', day: 'numeric' });
for (let t = Date.UTC(2015, 0, 1); t < Date.UTC(2045, 0, 1); t += 86400000) {
  const p = Object.fromEntries(fmt.formatToParts(t).map(x => [x.type, x.value]));
  JALALI.set(`${+p.year}/${+p.month}/${+p.day}`, new Date(t).toISOString().slice(0, 10));
}
function j2g(text) {
  if (!text) return null;
  const m = toEn(text).trim().match(/^(\d{4})\/(\d{1,2})\/(\d{1,2})$/);
  const iso = m && JALALI.get(`${+m[1]}/${+m[2]}/${+m[3]}`);
  return iso ? { iso } : { invalid: true };
}
if (j2g('1405/08/25').iso !== '2026-11-16') throw new Error('date conversion self-check failed');

const hash = v => crypto.createHash('sha256').update(JSON.stringify(v)).digest('hex');
const snapshot = JSON.parse(fs.readFileSync(input, 'utf8'));
const rows = [], invalidDates = [];
const KNOWN = new Set(['id', 'wbs', 'name', 'children', 'isGroup', 'start', 'finish', 'dur', 'pct', 'priority', 'assignee', 'pred', 'notes', 'src']);
const unknownKeys = new Set();

function visit(n, parentId, groupId, order) {
  Object.keys(n).forEach(k => { if (!KNOWN.has(k)) unknownKeys.add(k); });
  const start = j2g(n.start), finish = j2g(n.finish);
  if (start?.invalid) invalidDates.push({ id: n.id, field: 'start', value: n.start });
  if (finish?.invalid) invalidDates.push({ id: n.id, field: 'finish', value: n.finish });
  const kids = n.children || [];
  rows.push({
    legacy_id: n.id, legacy_parent_id: parentId, legacy_group_id: groupId, sort_order: order,
    wbs: n.wbs ?? null, name: n.name, is_summary: kids.length > 0,
    start_date: start?.iso ?? null, finish_date: finish?.iso ?? null,
    legacy_start: n.start || null, legacy_finish: n.finish || null,
    duration_days: n.dur ?? null, percent_complete: kids.length ? null : (n.pct ?? 0),
    priority: n.priority ?? null, assignee_text: n.assignee ?? null, predecessors: n.pred ?? null,
    notes: n.notes ?? null, source_info: n.src ?? null,
  });
  kids.forEach((c, i) => visit(c, n.id, groupId, i));
}
Object.values(snapshot.groups).sort((a, b) => (a.order || 0) - (b.order || 0))
  .forEach((g, i) => visit(g.node, null, g.id, i));

// Independent verification against the original tree.
const originals = new Map();
const collect = n => { if (originals.has(n.id)) throw new Error('duplicate id ' + n.id); originals.set(n.id, n); (n.children || []).forEach(collect); };
Object.values(snapshot.groups).forEach(g => collect(g.node));
const byId = new Map(rows.map(r => [r.legacy_id, r]));
const mismatches = [];
for (const [id, n] of originals) {
  const r = byId.get(id);
  const leaf = !(n.children || []).length;
  const same = r && r.name === n.name && (r.wbs ?? undefined) === n.wbs
    && (r.legacy_start || '') === (n.start || '') && (r.legacy_finish || '') === (n.finish || '')
    && (r.duration_days ?? undefined) === n.dur && (!leaf || r.percent_complete === (n.pct ?? 0))
    && (r.assignee_text ?? undefined) === n.assignee && (r.predecessors ?? undefined) === n.pred
    && (r.notes ?? undefined) === n.notes && (r.source_info ?? undefined) === n.src
    && (r.priority ?? undefined) === n.priority;
  if (!same) mismatches.push(id);
}

const report = {
  source_sha256: crypto.createHash('sha256').update(fs.readFileSync(input)).digest('hex'),
  groups: Object.keys(snapshot.groups).length,
  source_nodes: originals.size,
  exported_rows: rows.length,
  leaf_tasks: rows.filter(r => !r.is_summary).length,
  tasks_with_progress: rows.filter(r => r.percent_complete > 0).length,
  tasks_with_dates: rows.filter(r => r.legacy_start || r.legacy_finish).length,
  mismatches: mismatches.length,
  invalid_dates: invalidDates,
  unknown_fields: [...unknownKeys],
  names_sha256: hash(rows.map(r => [r.legacy_id, r.name])),
};
fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(path.join(outDir, 'tasks.json'), JSON.stringify({ meta: snapshot.meta, rows }, null, 1));
fs.writeFileSync(path.join(outDir, 'report.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, null, 2));
if (mismatches.length || rows.length !== originals.size || unknownKeys.size) process.exitCode = 1;
