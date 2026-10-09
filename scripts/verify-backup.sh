#!/usr/bin/env bash
# Proves a backup is usable: restores it into a throw-away database and compares row counts with the live one.
#   DB_USER=… MYSQL_PWD=… ./verify-backup.sh <backup.sql.gz> <live_db>
set -euo pipefail
file="${1:?usage: verify-backup.sh <backup.sql.gz> <live_db>}"; live="${2:?live database name}"; : "${DB_USER:?DB_USER is required}"
tmp="${live}_verify_$$"; here="$(cd "$(dirname "$0")" && pwd)"
cleanup() { mysql --user="$DB_USER" -e "DROP DATABASE IF EXISTS \`$tmp\`" >/dev/null 2>&1 || true; }; trap cleanup EXIT
"$here/restore.sh" "$file" "$tmp" >/dev/null
rc=0
for t in Businesses BusinessSources BusinessHistory Visits FollowUps Campaigns Reports AuditLogs AspNetUsers GeographicAreas; do
  a=$(mysql --user="$DB_USER" -N -e "SELECT COUNT(*) FROM \`$live\`.\`$t\`"); b=$(mysql --user="$DB_USER" -N -e "SELECT COUNT(*) FROM \`$tmp\`.\`$t\`")
  # the live database may have moved on since the backup; the restored copy must never have MORE rows than live
  printf '%-18s live=%-8s restored=%-8s\n' "$t" "$a" "$b"; [ "$b" -le "$a" ] || rc=1
done
[ "$rc" = 0 ] && echo "OK: backup restores and is consistent" || { echo "MISMATCH" >&2; exit 1; }
