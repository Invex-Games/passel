#!/usr/bin/env bash
# Backs up Passel's data on this server (ADR 0004, docs/guides/deploying.md): the database, its roles and the
# uploaded files. Run it from host cron as the deploy user, from the running release:
#
#   15 3 * * * /opt/passel/current/backup.sh /var/backups/passel >>/var/log/passel/backup.log 2>&1
#
# It writes three files with the same UTC timestamp into the directory given (<root>/backups by default):
#
#   db-<time>.dump      pg_dump of the database in the custom format, for pg_restore
#   roles-<time>.sql    pg_dumpall --roles-only: the logins and passel_app, with their password hashes
#   parcels-<time>.tgz  the parcels volume
#
# The database goes first and the files second, so every file the dump records is in the archive; a file uploaded in
# between is in the archive without a record, and the API's orphan scan removes it after a restore. Keeping copies
# elsewhere and pruning old ones is left to the offsite tool (restic, rclone or the like).
set -euo pipefail

umask 077

release_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
root=$(cd -- "$release_dir/../.." && pwd -P)
env_file=$root/.env
release_file=$root/release.env
backup_dir=${1:-$root/backups}

[ -f "$env_file" ] || {
    echo "backup.sh: $env_file is missing" >&2
    exit 1
}
[ -f "$release_file" ] || {
    echo "backup.sh: $release_file is missing; nothing has been deployed yet" >&2
    exit 1
}

cd "$release_dir"
mkdir -p "$backup_dir"

compose() {
    docker compose --env-file "$env_file" --env-file "$release_file" "$@"
}

stamp=$(date -u +%Y%m%dT%H%M%SZ)
partial=()

# A file is written under a temporary name and renamed once complete, so a backup that fails part way leaves nothing
# that looks finished
cleanup() {
    if [ "${#partial[@]}" -gt 0 ]; then
        rm -f "${partial[@]}"
    fi
}

trap cleanup EXIT

write() {
    local name=$1
    shift
    local temp="$backup_dir/.$name.partial"
    partial+=("$temp")
    "$@" >"$temp"
    mv -f "$temp" "$backup_dir/$name"
    echo "backup.sh: wrote $backup_dir/$name"
}

# The user and database are expanded inside the container, from the variables PostgreSQL itself was started with
# shellcheck disable=SC2016
write "db-$stamp.dump" \
    compose exec -T postgres sh -c 'pg_dump --username="$POSTGRES_USER" --format=custom "$POSTGRES_DB"'

# shellcheck disable=SC2016
write "roles-$stamp.sql" \
    compose exec -T postgres sh -c 'pg_dumpall --username="$POSTGRES_USER" --roles-only'

# parcels-init has the parcels volume at /v and no network
write "parcels-$stamp.tgz" \
    compose run --rm --no-deps -T parcels-init sh -c 'cd /v && tar -czf - .'
