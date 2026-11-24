#!/usr/bin/env bash
# Pulls the platform branch from GitHub and redeploys only when it changed. Safe to run from cron.
set -euo pipefail
cd "$(dirname "$0")/.."
git fetch --quiet origin platform
if [ "$(git rev-parse HEAD)" = "$(git rev-parse origin/platform)" ] && [ "${1:-}" != "--force" ]; then
  exit 0
fi
git merge --ff-only --quiet origin/platform
cd deploy
docker compose up -d --build --remove-orphans
echo "$(date -Is) deployed $(git rev-parse --short HEAD)"
