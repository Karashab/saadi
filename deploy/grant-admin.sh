#!/usr/bin/env bash
# Makes a signed-in user system admin of every project. The user must have signed in once.
# usage: ./grant-admin.sh <keycloak-username>
set -euo pipefail
cd "$(dirname "$0")"
: "${1:?usage: ./grant-admin.sh <username>}"
docker compose exec -T db psql -U noor_owner -d noor -q -v ON_ERROR_STOP=1 -v username="$1" < grant-admin.sql
