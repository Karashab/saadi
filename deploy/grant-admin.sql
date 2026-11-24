-- Used by grant-admin.sh; run with psql -v ON_ERROR_STOP=1 -v username=<name>.
BEGIN;
SELECT set_config('noor.username', :'username', true);
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM users WHERE lower(username) = lower(current_setting('noor.username'))) THEN
    RAISE EXCEPTION 'user % has not signed in yet', current_setting('noor.username');
  END IF;
END $$;
INSERT INTO project_members (project_id, user_id, organization_id, role)
SELECT p.id, u.id, NULL, 'system_admin' FROM projects p CROSS JOIN users u WHERE lower(u.username) = lower(:'username')
ON CONFLICT (project_id, user_id) DO UPDATE SET role = 'system_admin', organization_id = NULL;
COMMIT;
SELECT count(*) AS projects_granted FROM project_members m JOIN users u ON u.id = m.user_id
WHERE lower(u.username) = lower(:'username') AND m.role = 'system_admin';
