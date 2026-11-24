#!/usr/bin/env bash
# Applies db/*.sql once each, in order, and keeps the API login role in sync.
set -euo pipefail
: "${API_DB_PASSWORD:?API_DB_PASSWORD is required}"
DIR="${MIGRATIONS_DIR:-/migrations}"

psql -v ON_ERROR_STOP=1 -q -c "CREATE TABLE IF NOT EXISTS schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())"
for file in "$DIR"/[0-9][0-9][0-9]_*.sql; do
  name="$(basename "$file")"
  if [ -n "$(psql -tAq -v name="$name" <<< "SELECT 1 FROM schema_migrations WHERE name = :'name'")" ]; then continue; fi
  echo "applying $name"
  { cat "$file"; printf "\nINSERT INTO schema_migrations(name) VALUES ('%s');\n" "$name"; } | psql -v ON_ERROR_STOP=1 -q -1
done

psql -v ON_ERROR_STOP=1 -q -v pw="$API_DB_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE noor_api LOGIN NOINHERIT PASSWORD %L', :'pw') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'noor_api') \gexec
SELECT format('ALTER ROLE noor_api WITH LOGIN NOINHERIT PASSWORD %L', :'pw') \gexec
GRANT noor_app, noor_worker TO noor_api;
SQL
echo "database is up to date"
