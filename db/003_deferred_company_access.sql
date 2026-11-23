-- Companies and access can be assigned at any time after import.
-- A task with no company stays visible to project-wide roles only.

ALTER TABLE project_members ALTER COLUMN organization_id DROP NOT NULL;
ALTER TABLE project_members ADD CONSTRAINT members_company_required
  CHECK (organization_id IS NOT NULL OR role IN ('system_admin', 'project_manager'));

CREATE TABLE project_organizations (
  project_id uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  organization_id uuid NOT NULL REFERENCES organizations(id) ON DELETE RESTRICT,
  PRIMARY KEY (project_id, organization_id)
);
INSERT INTO project_organizations
SELECT DISTINCT project_id, organization_id FROM project_members WHERE organization_id IS NOT NULL;

-- Policy helpers must read membership without re-entering RLS, including when
-- the schema owner is not a superuser.
CREATE ROLE noor_policy NOLOGIN BYPASSRLS;
GRANT SELECT ON project_members, documents, tasks, project_organizations TO noor_policy;
GRANT SELECT, INSERT, UPDATE ON organizations TO noor_policy;
GRANT INSERT ON project_organizations TO noor_policy;

ALTER FUNCTION can_see(uuid, uuid) OWNER TO noor_policy;
ALTER FUNCTION can_write(uuid, uuid) OWNER TO noor_policy;
ALTER FUNCTION is_project_member(uuid) OWNER TO noor_policy;
ALTER FUNCTION has_project_role(uuid, member_role[]) OWNER TO noor_policy;
ALTER FUNCTION can_see_document(uuid) OWNER TO noor_policy;
ALTER FUNCTION can_write_document(uuid) OWNER TO noor_policy;

CREATE FUNCTION can_manage_member(p_project uuid, p_org uuid, p_role member_role) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
  SELECT has_project_role(p_project, 'system_admin')
      OR (p_role <> 'system_admin' AND has_project_role(p_project, 'project_manager'))
      OR (p_role IN ('executor', 'viewer') AND p_org IS NOT NULL
          AND has_project_role(p_project, 'company_admin') AND can_write(p_project, p_org))
$$;
ALTER FUNCTION can_manage_member(uuid, uuid, member_role) OWNER TO noor_policy;

DROP POLICY members_insert ON project_members;
DROP POLICY members_update ON project_members;
CREATE POLICY members_insert ON project_members FOR INSERT
  WITH CHECK (can_manage_member(project_id, organization_id, role));
CREATE POLICY members_update ON project_members FOR UPDATE
  USING (can_manage_member(project_id, organization_id, role))
  WITH CHECK (can_manage_member(project_id, organization_id, role));
CREATE POLICY members_delete ON project_members FOR DELETE
  USING (can_manage_member(project_id, organization_id, role));
GRANT DELETE ON project_members TO noor_app;

ALTER TABLE project_organizations ENABLE ROW LEVEL SECURITY;
ALTER TABLE project_organizations FORCE ROW LEVEL SECURITY;
CREATE POLICY project_organizations_read ON project_organizations FOR SELECT USING (is_project_member(project_id));
GRANT SELECT ON project_organizations TO noor_app;
GRANT SELECT ON project_organizations TO noor_worker;

CREATE FUNCTION create_project_organization(p_project uuid, p_name text) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE v_id uuid;
BEGIN
  IF NOT has_project_role(p_project, 'system_admin', 'project_manager') THEN
    RAISE EXCEPTION 'permission denied' USING ERRCODE = 'insufficient_privilege';
  END IF;
  IF p_name IS NULL OR btrim(p_name) = '' THEN
    RAISE EXCEPTION 'company name is required' USING ERRCODE = 'check_violation';
  END IF;
  INSERT INTO organizations (name) VALUES (p_name)
  ON CONFLICT (name) DO UPDATE SET name = EXCLUDED.name RETURNING id INTO v_id;
  INSERT INTO project_organizations VALUES (p_project, v_id) ON CONFLICT DO NOTHING;
  RETURN v_id;
END $$;
ALTER FUNCTION create_project_organization(uuid, text) OWNER TO noor_policy;

-- Headings above tasks a company can see, so its tree stays readable.
-- Exposes structure only: no dates, progress, notes, or files.
CREATE FUNCTION visible_task_ancestors(p_project uuid)
RETURNS TABLE (id uuid, parent_id uuid, wbs text, name text, sort_order integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
  WITH RECURSIVE up AS (
    SELECT t.parent_id AS ancestor FROM tasks t
    WHERE t.project_id = p_project AND t.deleted_at IS NULL AND t.parent_id IS NOT NULL
      AND can_see(t.project_id, t.organization_id)
    UNION
    SELECT p.parent_id FROM up JOIN tasks p ON p.id = up.ancestor WHERE p.parent_id IS NOT NULL
  )
  SELECT p.id, p.parent_id, p.wbs, p.name, p.sort_order
  FROM up JOIN tasks p ON p.id = up.ancestor
  WHERE p.deleted_at IS NULL AND NOT can_see(p.project_id, p.organization_id)
$$;
ALTER FUNCTION visible_task_ancestors(uuid) OWNER TO noor_policy;
