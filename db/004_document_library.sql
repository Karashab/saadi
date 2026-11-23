-- Document library defaults and category management.

CREATE UNIQUE INDEX document_categories_root_name ON document_categories (project_id, name) WHERE parent_id IS NULL;
CREATE INDEX documents_library_idx ON documents (project_id, category_id, created_at DESC) WHERE deleted_at IS NULL;
CREATE INDEX documents_task_idx ON documents (task_id) WHERE deleted_at IS NULL;

GRANT SELECT, INSERT ON document_categories TO noor_policy;
GRANT DELETE ON document_categories TO noor_app;

CREATE POLICY categories_delete ON document_categories FOR DELETE
  USING (has_project_role(project_id, 'system_admin', 'project_manager'));

CREATE FUNCTION seed_document_categories() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  INSERT INTO document_categories (project_id, name, sort_order)
  SELECT NEW.id, c.name, c.ord
  FROM unnest(ARRAY['قراردادها', 'نقشه‌ها', 'مجوزها', 'صورت‌جلسات', 'نامه‌ها', 'گزارش‌ها', 'مدارک فنی', 'ازبیلت'])
       WITH ORDINALITY AS c(name, ord)
  ON CONFLICT DO NOTHING;
  RETURN NEW;
END $$;
ALTER FUNCTION seed_document_categories() OWNER TO noor_policy;

CREATE TRIGGER projects_seed_categories AFTER INSERT ON projects
  FOR EACH ROW EXECUTE FUNCTION seed_document_categories();

INSERT INTO document_categories (project_id, name, sort_order)
SELECT p.id, c.name, c.ord
FROM projects p CROSS JOIN unnest(ARRAY['قراردادها', 'نقشه‌ها', 'مجوزها', 'صورت‌جلسات', 'نامه‌ها', 'گزارش‌ها', 'مدارک فنی', 'ازبیلت'])
     WITH ORDINALITY AS c(name, ord)
ON CONFLICT DO NOTHING;
