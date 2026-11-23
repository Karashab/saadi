-- Noor platform, phase 1 schema (PostgreSQL)
-- Status: not yet executed; run against a test database before use.

CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TYPE member_role AS ENUM ('system_admin', 'company_admin', 'project_manager', 'executor', 'viewer');
CREATE TYPE notification_kind AS ENUM ('due_soon', 'due_today', 'overdue', 'assigned', 'updated', 'document');

CREATE TABLE organizations (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name text NOT NULL UNIQUE,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE users (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  identity_subject text NOT NULL UNIQUE, -- Keycloak subject
  display_name text NOT NULL,
  email text,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE projects (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  code text UNIQUE,
  name text NOT NULL,
  title text,
  client text,
  contractor text,
  start_date date,
  finish_date date,
  source_meta jsonb NOT NULL DEFAULT '{}',
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE project_members (
  project_id uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  organization_id uuid NOT NULL REFERENCES organizations(id),
  role member_role NOT NULL,
  PRIMARY KEY (project_id, user_id)
);

CREATE TABLE tasks (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  project_id uuid NOT NULL REFERENCES projects(id) ON DELETE RESTRICT,
  parent_id uuid REFERENCES tasks(id) ON DELETE RESTRICT,
  organization_id uuid REFERENCES organizations(id), -- null = visible to project-wide roles only
  legacy_id text NOT NULL,
  sort_order integer NOT NULL,
  wbs text,
  name text NOT NULL, -- copied verbatim from the source; never normalized
  is_summary boolean NOT NULL DEFAULT false,
  start_date date,
  finish_date date,
  legacy_start text,
  legacy_finish text,
  duration_days numeric,
  percent_complete numeric CHECK (percent_complete BETWEEN 0 AND 100),
  priority text,
  assignee_text text,
  assignee_user_id uuid REFERENCES users(id),
  predecessors text,
  notes text,
  source_info text,
  version integer NOT NULL DEFAULT 1,
  deleted_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (project_id, legacy_id)
);
CREATE INDEX tasks_tree_idx ON tasks (project_id, parent_id, sort_order);
CREATE INDEX tasks_due_idx ON tasks (finish_date) WHERE deleted_at IS NULL;

CREATE TABLE document_categories (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  project_id uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  parent_id uuid REFERENCES document_categories(id),
  name text NOT NULL,
  sort_order integer NOT NULL DEFAULT 0,
  UNIQUE (project_id, parent_id, name)
);

CREATE TABLE documents (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  project_id uuid NOT NULL REFERENCES projects(id) ON DELETE RESTRICT,
  organization_id uuid REFERENCES organizations(id),
  category_id uuid REFERENCES document_categories(id),
  task_id uuid REFERENCES tasks(id),
  code text,
  title text NOT NULL,
  deleted_at timestamptz,
  created_by uuid NOT NULL REFERENCES users(id),
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE document_versions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  document_id uuid NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
  version_no integer NOT NULL,
  storage_key text NOT NULL UNIQUE,
  file_name text NOT NULL,
  content_type text NOT NULL,
  size_bytes bigint NOT NULL CHECK (size_bytes >= 0),
  sha256 char(64) NOT NULL,
  uploaded_by uuid NOT NULL REFERENCES users(id),
  uploaded_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (document_id, version_no)
);

CREATE TABLE notifications (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  project_id uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  task_id uuid REFERENCES tasks(id),
  kind notification_kind NOT NULL,
  message text NOT NULL,
  dedupe_key text NOT NULL,
  read_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (user_id, dedupe_key)
);
CREATE INDEX notifications_inbox_idx ON notifications (user_id, read_at, created_at DESC);

CREATE TABLE audit_log (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  project_id uuid,
  actor_user_id uuid,
  entity_type text NOT NULL,
  entity_id text NOT NULL,
  action text NOT NULL,
  before_data jsonb,
  after_data jsonb,
  ip inet,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE FUNCTION reject_audit_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'audit_log is append-only';
END $$;
CREATE TRIGGER audit_log_immutable BEFORE UPDATE OR DELETE ON audit_log
  FOR EACH ROW EXECUTE FUNCTION reject_audit_change();
CREATE TRIGGER audit_log_no_truncate BEFORE TRUNCATE ON audit_log
  FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_change();

-- Row-level isolation. The API must connect as noor_app (not the table owner)
-- and set app.user_id with SET LOCAL inside each transaction.
CREATE ROLE noor_app NOLOGIN;
GRANT SELECT, INSERT, UPDATE ON organizations, users, projects, project_members, tasks,
  document_categories, documents, document_versions, notifications TO noor_app;
GRANT SELECT, INSERT ON audit_log TO noor_app;

CREATE FUNCTION app_user_id() RETURNS uuid LANGUAGE sql STABLE AS
$$ SELECT nullif(current_setting('app.user_id', true), '')::uuid $$;

CREATE FUNCTION can_see(p_project uuid, p_org uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
  SELECT EXISTS (
    SELECT 1 FROM project_members m
    WHERE m.project_id = p_project AND m.user_id = app_user_id()
      AND (m.role IN ('system_admin', 'project_manager')
           OR (p_org IS NOT NULL AND m.organization_id = p_org))
  )
$$;

CREATE FUNCTION can_write(p_project uuid, p_org uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
  SELECT EXISTS (
    SELECT 1 FROM project_members m
    WHERE m.project_id = p_project AND m.user_id = app_user_id()
      AND (m.role IN ('system_admin', 'project_manager')
           OR (m.role IN ('company_admin', 'executor') AND p_org IS NOT NULL AND m.organization_id = p_org))
  )
$$;

ALTER TABLE tasks ENABLE ROW LEVEL SECURITY;
ALTER TABLE tasks FORCE ROW LEVEL SECURITY;
CREATE POLICY tasks_read ON tasks FOR SELECT USING (can_see(project_id, organization_id));
CREATE POLICY tasks_insert ON tasks FOR INSERT WITH CHECK (can_write(project_id, organization_id));
CREATE POLICY tasks_update ON tasks FOR UPDATE
  USING (can_write(project_id, organization_id))
  WITH CHECK (can_write(project_id, organization_id));

ALTER TABLE documents ENABLE ROW LEVEL SECURITY;
ALTER TABLE documents FORCE ROW LEVEL SECURITY;
CREATE POLICY documents_read ON documents FOR SELECT USING (can_see(project_id, organization_id));
CREATE POLICY documents_insert ON documents FOR INSERT WITH CHECK (can_write(project_id, organization_id));
CREATE POLICY documents_update ON documents FOR UPDATE
  USING (can_write(project_id, organization_id))
  WITH CHECK (can_write(project_id, organization_id));

ALTER TABLE notifications ENABLE ROW LEVEL SECURITY;
ALTER TABLE notifications FORCE ROW LEVEL SECURITY;
CREATE POLICY notifications_owner ON notifications FOR ALL
  USING (user_id = app_user_id()) WITH CHECK (user_id = app_user_id());
