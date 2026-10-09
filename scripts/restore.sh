#!/usr/bin/env bash
# Restores a backup produced by backup.sh into a database (default: the original name). Verifies the checksum first.
#   DB_USER=… MYSQL_PWD=… ./restore.sh /var/backups/prospecta/prospecta-20260101T020000Z.sql.gz [target_db]
# Restoring over a live database replaces its content: stop the application first (see docs/operations.md).
set -euo pipefail
file="${1:?usage: restore.sh <backup.sql.gz> [target_db]}"; : "${DB_USER:?DB_USER is required}"
DB_HOST="${DB_HOST:-127.0.0.1}"; DB_PORT="${DB_PORT:-3306}"
target="${2:-$(basename "$file" | sed -E 's/-[0-9]{8}T[0-9]{6}Z\.sql\.gz$//')}"
[ -f "$file" ] || { echo "no such file: $file" >&2; exit 1; }
if [ -f "$file.sha256" ]; then ( cd "$(dirname "$file")" && sha256sum -c "$(basename "$file").sha256" ) || { echo "checksum mismatch: refusing to restore" >&2; exit 2; }; else echo "warning: no checksum file next to the backup" >&2; fi
gzip -t "$file"
mysql --host="$DB_HOST" --port="$DB_PORT" --user="$DB_USER" -e "CREATE DATABASE IF NOT EXISTS \`$target\` CHARACTER SET utf8mb4"
gunzip -c "$file" | mysql --host="$DB_HOST" --port="$DB_PORT" --user="$DB_USER" --default-character-set=utf8mb4 "$target"
echo "restored $file into database '$target'"
