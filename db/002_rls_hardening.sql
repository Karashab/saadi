-- Closes read paths that 001 left open to the application role.

CREATE FUNCTION is_project_member(p_project uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
  SELECT EXISTS (SELECT 1 FROM project_members m WHERE m.project_id = p_project AND m.user_id = app_user_id())
$$;

CREATE FUNCTION has_project_role(p_project uuid, VARIADIC p_roles member_role[]) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
  SELECT EXISTS (SELECT 1 FROM project_members m
                 WHERE m.project_id = p_project AND m.user_id = app_user_id() AND m.role = ANY (p_roles))
$$;

CREATE FUNCTION can_see_document(p_document uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
  SELECT EXISTS (SELECT 1 FROM documents d WHERE d.id = p_document AND can_see(d.project_id, d.organization_id))
$$;

CREATE FUNCTION can_write_document(p_document uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
  SELECT EXISTS (SELECT 1 FROM documents d WHERE d.id = p_document AND can_write(d.project_id, d.organization_id))
$$;

ALTER TABLE projects ENABLE ROW LEVEL SECURITY;
ALTER TABLE projects FORCE ROW LEVEL SECURITY;
CREATE POLICY projects_read ON projects FOR SELECT USING (is_project_member(id));
CREATE POLICY projects_update ON projects FOR UPDATE
  USING (has_project_role(id, 'system_admin', 'project_manager'))
  WITH CHECK (has_project_role(id, 'system_admin', 'project_manager'));
REVOKE INSERT ON projects FROM noor_app;

ALTER TABLE project_members ENABLE ROW LEVEL SECURITY;
ALTER TABLE project_members FORCE ROW LEVEL SECURITY;
CREATE POLICY members_read ON project_members FOR SELECT USING (
  user_id = app_user_id() OR can_see(project_id, organization_id));
CREATE POLICY members_insert ON project_members FOR INSERT WITH CHECK (
  has_project_role(project_id, 'system_admin', 'project_manager')
  OR (role IN ('executor', 'viewer') AND has_project_role(project_id, 'company_admin')
      AND can_write(project_id, organization_id)));
CREATE POLICY members_update ON project_members FOR UPDATE
  USING (has_project_role(project_id, 'system_admin', 'project_manager')
         OR (role IN ('executor', 'viewer') AND has_project_role(project_id, 'company_admin')
             AND can_write(project_id, organization_id)))
  WITH CHECK (has_project_role(project_id, 'system_admin', 'project_manager')
         OR (role IN ('executor', 'viewer') AND has_project_role(project_id, 'company_admin')
             AND can_write(project_id, organization_id)));

ALTER TABLE document_categories ENABLE ROW LEVEL SECURITY;
ALTER TABLE document_categories FORCE ROW LEVEL SECURITY;
CREATE POLICY categories_read ON document_categories FOR SELECT USING (is_project_member(project_id));
CREATE POLICY categories_insert ON document_categories FOR INSERT
  WITH CHECK (has_project_role(project_id, 'system_admin', 'project_manager'));
CREATE POLICY categories_update ON document_categories FOR UPDATE
  USING (has_project_role(project_id, 'system_admin', 'project_manager'))
  WITH CHECK (has_project_role(project_id, 'system_admin', 'project_manager'));

ALTER TABLE document_versions ENABLE ROW LEVEL SECURITY;
ALTER TABLE document_versions FORCE ROW LEVEL SECURITY;
CREATE POLICY versions_read ON document_versions FOR SELECT USING (can_see_document(document_id));
CREATE POLICY versions_insert ON document_versions FOR INSERT
  WITH CHECK (can_write_document(document_id) AND uploaded_by = app_user_id());
REVOKE UPDATE ON document_versions FROM noor_app;

ALTER TABLE audit_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_log FORCE ROW LEVEL SECURITY;
CREATE POLICY audit_read ON audit_log FOR SELECT USING (
  project_id IS NOT NULL AND has_project_role(project_id, 'system_admin', 'project_manager'));
CREATE POLICY audit_insert ON audit_log FOR INSERT WITH CHECK (
  actor_user_id = app_user_id() AND project_id IS NOT NULL AND is_project_member(project_id));

-- Identity and company records are maintained by the admin/worker connection.
REVOKE INSERT, UPDATE ON organizations, users FROM noor_app;

-- Deadline worker: creates alerts for any user, without access to edit project data.
CREATE ROLE noor_worker NOLOGIN BYPASSRLS;
GRANT SELECT ON tasks, project_members, users, projects TO noor_worker;
GRANT SELECT, INSERT ON notifications TO noor_worker;
