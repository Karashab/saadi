-- Users are created on their first sign-in; access is granted separately by a manager.

ALTER TABLE users ADD COLUMN username text;
CREATE UNIQUE INDEX users_username_key ON users (lower(username)) WHERE username IS NOT NULL;

GRANT SELECT, INSERT, UPDATE ON users TO noor_policy;

-- Never reactivates a disabled user and never grants project access.
CREATE FUNCTION ensure_user(p_subject text, p_name text, p_username text, p_email text) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE v_id uuid;
BEGIN
  IF p_subject IS NULL OR p_subject = '' THEN
    RAISE EXCEPTION 'subject is required' USING ERRCODE = 'check_violation';
  END IF;
  INSERT INTO users (identity_subject, display_name, username, email)
  VALUES (p_subject, coalesce(nullif(p_name, ''), nullif(p_username, ''), 'کاربر'), nullif(p_username, ''), nullif(p_email, ''))
  ON CONFLICT (identity_subject) DO UPDATE
    SET display_name = coalesce(nullif(EXCLUDED.display_name, ''), users.display_name),
        username = coalesce(EXCLUDED.username, users.username),
        email = coalesce(EXCLUDED.email, users.email)
  RETURNING id INTO v_id;
  RETURN v_id;
END $$;
ALTER FUNCTION ensure_user(text, text, text, text) OWNER TO noor_policy;
