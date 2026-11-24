#!/usr/bin/env bash
# Nightly database dump and file archive, keeping KEEP_DAYS days.
set -uo pipefail
KEEP_DAYS="${KEEP_DAYS:-14}"
while true; do
  stamp="$(date +%Y%m%d-%H%M)"
  if pg_dump -Fc -f "/backups/db-$stamp.dump.part" && mv "/backups/db-$stamp.dump.part" "/backups/db-$stamp.dump"; then
    echo "$(date -Is) database backup ok"
  else
    echo "$(date -Is) database backup FAILED" >&2
  fi
  if tar -czf "/backups/files-$stamp.tar.gz.part" -C /data files && mv "/backups/files-$stamp.tar.gz.part" "/backups/files-$stamp.tar.gz"; then
    echo "$(date -Is) file backup ok"
  else
    echo "$(date -Is) file backup FAILED" >&2
  fi
  find /backups -maxdepth 1 -type f \( -name 'db-*.dump' -o -name 'files-*.tar.gz' \) -mtime +"$KEEP_DAYS" -delete
  sleep 86400
done
