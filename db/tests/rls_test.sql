-- Isolation tests. Uses synthetic rows and always rolls back.
\set ON_ERROR_STOP on
BEGIN;

INSERT INTO organizations (id, name) VALUES
 ('00000000-0000-0000-0000-00000000000a', 'test-a'), ('00000000-0000-0000-0000-00000000000b', 'test-b');
INSERT INTO users (id, identity_subject, display_name) VALUES
 ('10000000-0000-0000-0000-00000000000a', 't-exec-a', 'exec a'),
 ('10000000-0000-0000-0000-00000000000b', 't-exec-b', 'exec b'),
 ('10000000-0000-0000-0000-00000000000c', 't-pm', 'pm'),
 ('10000000-0000-0000-0000-00000000000d', 't-view-a', 'viewer a'),
 ('10000000-0000-0000-0000-00000000000e', 't-outsider', 'outsider');
INSERT INTO projects (id, name) VALUES ('20000000-0000-0000-0000-000000000001', 'test project');
INSERT INTO project_members VALUES
 ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-00000000000a', '00000000-0000-0000-0000-00000000000a', 'executor'),
 ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-00000000000b', '00000000-0000-0000-0000-00000000000b', 'executor'),
 ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-00000000000c', '00000000-0000-0000-0000-00000000000a', 'project_manager'),
 ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-00000000000d', '00000000-0000-0000-0000-00000000000a', 'viewer');
INSERT INTO tasks (id, project_id, organization_id, legacy_id, sort_order, name) VALUES
 ('30000000-0000-0000-0000-00000000000a', '20000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-00000000000a', 'ta', 1, 'task a'),
 ('30000000-0000-0000-0000-00000000000b', '20000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-00000000000b', 'tb', 2, 'task b'),
 ('30000000-0000-0000-0000-00000000000c', '20000000-0000-0000-0000-000000000001', NULL, 'tc', 3, 'task unassigned');
INSERT INTO documents (id, project_id, organization_id, title, created_by) VALUES
 ('40000000-0000-0000-0000-00000000000b', '20000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-00000000000b', 'doc b', '10000000-0000-0000-0000-00000000000b');
INSERT INTO document_versions (document_id, version_no, storage_key, file_name, content_type, size_bytes, sha256, uploaded_by) VALUES
 ('40000000-0000-0000-0000-00000000000b', 1, 'test/b1', 'b.pdf', 'application/pdf', 1, repeat('0', 64), '10000000-0000-0000-0000-00000000000b');
INSERT INTO audit_log (project_id, actor_user_id, entity_type, entity_id, action)
 VALUES ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-00000000000c', 'task', 'ta', 'update');

CREATE FUNCTION public.t_expect(label text, got bigint, want bigint) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF got IS DISTINCT FROM want THEN RAISE EXCEPTION 'FAIL %: got %, want %', label, got, want; END IF;
  RAISE NOTICE 'pass: %', label;
END $$;
GRANT EXECUTE ON FUNCTION public.t_expect(text, bigint, bigint) TO noor_app;

SET LOCAL ROLE noor_app;

SET LOCAL app.user_id = '10000000-0000-0000-0000-00000000000a';
SELECT public.t_expect('executor sees only own company tasks', count(*), 1) FROM tasks;
SELECT public.t_expect('executor cannot see other company document', count(*), 0) FROM documents;
SELECT public.t_expect('executor cannot see other company file version', count(*), 0) FROM document_versions;
SELECT public.t_expect('executor cannot read audit log', count(*), 0) FROM audit_log;
WITH u AS (UPDATE tasks SET percent_complete = 50 WHERE legacy_id = 'tb' RETURNING 1)
SELECT public.t_expect('executor cannot update other company task', count(*), 0) FROM u;
WITH u AS (UPDATE tasks SET percent_complete = 50 WHERE legacy_id = 'ta' RETURNING 1)
SELECT public.t_expect('executor can update own task', count(*), 1) FROM u;

SAVEPOINT s1;
DO $$ BEGIN
  BEGIN
    UPDATE tasks SET organization_id = '00000000-0000-0000-0000-00000000000b' WHERE legacy_id = 'ta';
    RAISE EXCEPTION 'FAIL: task moved to another company';
  EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'pass: executor cannot move task to another company'; END;
END $$;
DO $$ BEGIN
  BEGIN
    INSERT INTO project_members VALUES ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-00000000000e', '00000000-0000-0000-0000-00000000000a', 'system_admin');
    RAISE EXCEPTION 'FAIL: executor granted a role';
  EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'pass: executor cannot grant roles'; END;
END $$;
RELEASE s1;

SET LOCAL app.user_id = '10000000-0000-0000-0000-00000000000d';
WITH u AS (UPDATE tasks SET percent_complete = 60 WHERE legacy_id = 'ta' RETURNING 1)
SELECT public.t_expect('viewer cannot update task', count(*), 0) FROM u;

SET LOCAL app.user_id = '10000000-0000-0000-0000-00000000000e';
SELECT public.t_expect('non-member sees no tasks', count(*), 0) FROM tasks;
SELECT public.t_expect('non-member sees no projects', count(*), 0) FROM projects;

SET LOCAL app.user_id = '';
SELECT public.t_expect('no user context sees no tasks', count(*), 0) FROM tasks;

SET LOCAL app.user_id = '10000000-0000-0000-0000-00000000000c';
SELECT public.t_expect('project manager sees all tasks', count(*), 3) FROM tasks;
SELECT public.t_expect('project manager sees file version', count(*), 1) FROM document_versions;
SELECT public.t_expect('project manager reads audit log', count(*), 1) FROM audit_log;

DO $$ BEGIN
  BEGIN
    DELETE FROM tasks;
    RAISE EXCEPTION 'FAIL: app role deleted tasks';
  EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'pass: app role cannot hard-delete tasks'; END;
END $$;
DO $$ BEGIN
  BEGIN
    UPDATE audit_log SET action = 'x';
    RAISE EXCEPTION 'FAIL: audit changed';
  EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'pass: app role cannot change audit log'; END;
END $$;

RESET ROLE;
DO $$ BEGIN
  BEGIN
    UPDATE audit_log SET action = 'x';
    RAISE EXCEPTION 'FAIL: owner changed audit';
  EXCEPTION WHEN raise_exception THEN
    IF SQLERRM LIKE 'FAIL%' THEN RAISE; END IF;
    RAISE NOTICE 'pass: even the owner cannot change audit log';
  END;
END $$;

ROLLBACK;
