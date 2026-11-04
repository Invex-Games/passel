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
# All three are written under temporary names and renamed only once all three are complete, so a failed run leaves
# no dump without its archive. The database goes first and the files second while the API keeps running: a file
# uploaded in between is in the archive without a record, and the API's orphan scan removes it after a restore; a
# parcel deleted in between is in the dump without its file. Uploads in flight (.uploading) are left out, and a file
# the API moves or deletes while the archive is written is skipped. Keeping copies elsewhere and pruning old ones is
# left to the offsite tool (restic, rclone or the like). The server's .env, with the ENCODING_ALPHABET every public id
# depends on, is not part of the backup: keep a copy of it off the server separately.
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
finished=()

# Each file is written under a temporary name; whatever was not renamed at the end is removed on exit
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
    finished+=("$name")
}

# The user and database are expanded inside the container, from the variables PostgreSQL itself was started with
# shellcheck disable=SC2016
write "db-$stamp.dump" \
    compose exec -T postgres sh -c 'pg_dump --username="$POSTGRES_USER" --format=custom "$POSTGRES_DB"'

# shellcheck disable=SC2016
write "roles-$stamp.sql" \
    compose exec -T postgres sh -c 'pg_dumpall --username="$POSTGRES_USER" --roles-only'

# parcels-init has the parcels volume at /v and no network. The API keeps running, so a file can vanish between tar
# listing a directory and opening it: an upload renamed from .uploading into place, or a blob the sweep or a user
# deleted. busybox tar reports each one, carries on, and exits 1 at the end with the archive complete. That exit is
# accepted when every error is a vanished file, which is the only error a blob can cause, since the API never rewrites
# one in place; any other error fails the backup. The temps under .uploading are never needed after a restore
# shellcheck disable=SC2016
write "parcels-$stamp.tgz" \
    compose run --rm --no-deps -T parcels-init sh -c '
        cd /v || exit 1
        { errors=$(tar -czf - --exclude=./parcels/.uploading . 2>&1 >&3); status=$?; } 3>&1
        if [ -n "$errors" ]; then
            printf "%s\n" "$errors" >&2
        fi
        if [ "$status" -eq 1 ] && [ -n "$errors" ] &&
            ! printf "%s\n" "$errors" |
            grep -v -e "No such file or directory" -e "error exit delayed from previous errors" >/dev/null; then
            echo "backup.sh: the files above were moved or deleted while the archive was written; it has the rest" >&2
            status=0
        fi
        exit "$status"'

for name in "${finished[@]}"; do
    mv -f "$backup_dir/.$name.partial" "$backup_dir/$name"
    echo "backup.sh: wrote $backup_dir/$name"
done
