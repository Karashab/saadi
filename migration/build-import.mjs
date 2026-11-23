// Builds an idempotency-guarded SQL import from export-tasks.mjs output.
// usage: node build-import.mjs <tasks.json> <import.sql>
// The generated SQL contains project data: keep it out of Git.
import fs from 'node:fs';

const [input, output] = process.argv.slice(2);
if (!input || !output) throw new Error('usage: node build-import.mjs <tasks.json> <import.sql>');
const { meta, rows } = JSON.parse(fs.readFileSync(input, 'utf8'));
const json = JSON.stringify(rows);
const metaJson = JSON.stringify(meta);
let tag = 'noor';
while (json.includes(`$${tag}$`) || metaJson.includes(`$${tag}$`)) tag += 'x';
const lit = s => `$${tag}$${s}$${tag}$`;

fs.writeFileSync(output, `\\set ON_ERROR_STOP on
BEGIN;
DO $g$ BEGIN
  IF EXISTS (SELECT 1 FROM projects WHERE code = ${lit(meta.code)}) THEN
    RAISE EXCEPTION 'project already imported; refusing to overwrite';
  END IF;
END $g$;

CREATE TEMP TABLE src ON COMMIT DROP AS
SELECT * FROM jsonb_to_recordset(${lit(json)}::jsonb) AS x(
  legacy_id text, legacy_parent_id text, legacy_group_id text, sort_order int, wbs text, name text,
  is_summary boolean, start_date date, finish_date date, legacy_start text, legacy_finish text,
  duration_days numeric, percent_complete numeric, priority text, assignee_text text,
  predecessors text, notes text, source_info text);

WITH m AS (SELECT ${lit(metaJson)}::jsonb AS j)
INSERT INTO projects (code, name, title, client, contractor, source_meta)
SELECT j->>'code', j->>'name', j->>'title', j->>'client', j->>'contractor', j FROM m;

INSERT INTO tasks (project_id, legacy_id, sort_order, wbs, name, is_summary, start_date, finish_date,
  legacy_start, legacy_finish, duration_days, percent_complete, priority, assignee_text, predecessors, notes, source_info)
SELECT p.id, s.legacy_id, s.sort_order, s.wbs, s.name, s.is_summary, s.start_date, s.finish_date,
  s.legacy_start, s.legacy_finish, s.duration_days, s.percent_complete, s.priority, s.assignee_text,
  s.predecessors, s.notes, s.source_info
FROM src s CROSS JOIN projects p WHERE p.code = ${lit(meta.code)};

UPDATE tasks c SET parent_id = par.id
FROM src s JOIN tasks par ON par.legacy_id = s.legacy_parent_id
WHERE c.legacy_id = s.legacy_id AND par.project_id = c.project_id;

DO $v$
DECLARE missing bigint; extra bigint; diff bigint; bad_parent bigint;
BEGIN
  SELECT count(*) INTO missing FROM src s LEFT JOIN tasks t USING (legacy_id) WHERE t.id IS NULL;
  SELECT count(*) INTO extra FROM tasks t LEFT JOIN src s USING (legacy_id) WHERE s.legacy_id IS NULL;
  SELECT count(*) INTO diff FROM src s JOIN tasks t USING (legacy_id)
   WHERE (s.wbs, s.name, s.is_summary, s.start_date, s.finish_date, s.legacy_start, s.legacy_finish,
          s.duration_days, s.percent_complete, s.priority, s.assignee_text, s.predecessors, s.notes, s.source_info)
     IS DISTINCT FROM
         (t.wbs, t.name, t.is_summary, t.start_date, t.finish_date, t.legacy_start, t.legacy_finish,
          t.duration_days, t.percent_complete, t.priority, t.assignee_text, t.predecessors, t.notes, t.source_info);
  SELECT count(*) INTO bad_parent FROM src s JOIN tasks t USING (legacy_id) LEFT JOIN tasks p ON p.id = t.parent_id
   WHERE p.legacy_id IS DISTINCT FROM s.legacy_parent_id;
  IF missing + extra + diff + bad_parent > 0 THEN
    RAISE EXCEPTION 'verification failed: missing %, extra %, different %, wrong parent %', missing, extra, diff, bad_parent;
  END IF;
  RAISE NOTICE 'verified: % rows, 0 differences', (SELECT count(*) FROM tasks);
END $v$;
COMMIT;
`);
console.log('import SQL written:', rows.length, 'rows');
