#!/usr/bin/env bash
# Consistent MySQL backup of the Prospecta database (no table lock: --single-transaction), compressed, checksummed, with retention.
# The application stores nothing else on disk that matters (no uploaded files); logs are not part of the backup.
#
#   DB_NAME=prospecta DB_USER=prospecta MYSQL_PWD='…' BACKUP_DIR=/var/backups/prospecta RETENTION_DAYS=30 ./backup.sh
#   Optional: DB_HOST=127.0.0.1 DB_PORT=3306 GPG_RECIPIENT=ops@your-company.dz (encrypts the dump with gpg)
set -euo pipefail
: "${DB_NAME:?DB_NAME is required}" "${DB_USER:?DB_USER is required}" "${BACKUP_DIR:?BACKUP_DIR is required}"
DB_HOST="${DB_HOST:-127.0.0.1}"; DB_PORT="${DB_PORT:-3306}"; RETENTION_DAYS="${RETENTION_DAYS:-30}"
[ -n "${MYSQL_PWD:-}" ] || echo "warning: MYSQL_PWD not set (using the account without password / socket auth)" >&2
umask 077; mkdir -p "$BACKUP_DIR"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"; out="$BACKUP_DIR/${DB_NAME}-${stamp}.sql.gz"
mysqldump --host="$DB_HOST" --port="$DB_PORT" --user="$DB_USER" --single-transaction --quick --routines --triggers --hex-blob \
  --default-character-set=utf8mb4 --set-gtid-purged=OFF --no-tablespaces "$DB_NAME" | gzip -9 > "$out.tmp"
gzip -t "$out.tmp"                       # the archive must be readable
mv "$out.tmp" "$out"
if [ -n "${GPG_RECIPIENT:-}" ]; then gpg --batch --yes --trust-model always -r "$GPG_RECIPIENT" -o "$out.gpg" -e "$out" && shred -u "$out" 2>/dev/null || rm -f "$out"; out="$out.gpg"; fi
( cd "$BACKUP_DIR" && sha256sum "$(basename "$out")" > "$(basename "$out").sha256" )
find "$BACKUP_DIR" -maxdepth 1 -type f \( -name "${DB_NAME}-*.sql.gz*" \) -mtime +"$RETENTION_DAYS" -delete
echo "backup written: $out ($(du -h "$out" | cut -f1))"
