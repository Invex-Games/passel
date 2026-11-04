#!/usr/bin/env bash
# Deploys one release of Passel on this server (ADR 0004, docs/guides/deploying.md).
#
#   deploy.sh <tag> [--no-migrate]
#
# It runs from the release's own directory, <root>/releases/<tag>, which deploy-gate.sh extracted from the
# passel-deploy image of the same tag, so the compose files always match the images. The server's settings are in
# <root>/.env. In order it:
#
#   1. pulls the images (Passel's own only when missing, since a version tag never moves; the others too with
#      --no-migrate, and a cached one is used when its registry can't be reached),
#   2. starts PostgreSQL and waits for it to be healthy,
#   3. runs db-roles, parcels-init and db-migrate (skipped with --no-migrate, as a rollback does),
#   4. starts the API, the web app and any profile's services and waits for them to be healthy,
#   5. checks SMOKE_URL (https://API_HOST/health by default).
#
# Only when every step succeeds does it record the release: release.env becomes release.env.previous, the new
# release.env names the tag, a line is appended to releases.log and <root>/current points at the release. On a
# failure it prints the API's and the web app's logs and exits non-zero with release.env untouched; deploying the
# tag in release.env again with --no-migrate goes back to it.
set -Eeuo pipefail

die() {
    echo "deploy.sh: $*" >&2
    exit 1
}

usage() {
    echo "usage: deploy.sh <tag> [--no-migrate]" >&2
    exit 2
}

[ $# -ge 1 ] && [ $# -le 2 ] || usage

tag=$1
migrate=true

if [ $# -eq 2 ]; then
    [ "$2" = "--no-migrate" ] || usage
    migrate=false
fi

# The same pattern as the build's image tags and deploy-gate.sh
tag_pattern='^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$'
[[ $tag =~ $tag_pattern ]] || die "'$tag' is not a valid image tag"

release_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
root=$(cd -- "$release_dir/../.." && pwd -P)
env_file=$root/.env
next_file=$root/release.env.next

[ -f "$env_file" ] || die "$env_file is missing; copy .env.example there and fill it in"
command -v docker >/dev/null || die "docker is not installed"
command -v curl >/dev/null || die "curl is not installed"

# Compose 2.24 brought !reset, which compose.external-proxy.yaml needs, and two --env-file options that both count
compose_version=$(docker compose version --short 2>/dev/null) || die "the Docker Compose plugin is not installed"
compose_version=${compose_version#v}
compose_major=${compose_version%%.*}
compose_minor=${compose_version#*.}
compose_minor=${compose_minor%%.*}

if ! [[ $compose_major =~ ^[0-9]+$ && $compose_minor =~ ^[0-9]+$ ]] ||
    ((compose_major < 2 || (compose_major == 2 && compose_minor < 24))); then
    die "Docker Compose $compose_version is too old; 2.24 or later is needed"
fi

# One deploy at a time on this server
if command -v flock >/dev/null; then
    exec 9>"$root/.deploy.lock"
    flock -n 9 || die "another deploy is running"
fi

# COMPOSE_FILE in .env names files relative to the release directory
cd "$release_dir"

printf 'PASSEL_TAG=%s\n' "$tag" >"$next_file"

compose() {
    docker compose --env-file "$env_file" --env-file "$next_file" "$@"
}

# Reads one key from .env, without its quotes, for the values deploy.sh needs itself
env_value() {
    sed -n "s/^$1=//p" "$env_file" | tail -n 1 | sed -e "s/^['\"]//" -e "s/['\"]\$//"
}

on_failure() {
    local status=$?
    trap - ERR

    # A command substitution inherits the trap; only the script itself reports
    if [ "$BASH_SUBSHELL" -gt 0 ]; then
        exit "$status"
    fi

    echo "deploy.sh: deploying $tag failed (exit $status); the logs of the API and the web app follow" >&2
    compose logs --no-color --tail 200 api web >&2 || true
    rm -f "$next_file"
    echo "deploy.sh: release.env is unchanged; deploy its tag with --no-migrate to go back to it" >&2
    exit "$status"
}

trap on_failure ERR

echo "Deploying $tag from $release_dir"

services_list=$(compose config --services)
mapfile -t services <<<"$services_list"

# Passel's images and the others, and the services that keep running and those that run once
ours=()
theirs=()
long_running=()

for service in "${services[@]}"; do
    case $service in
    db-migrate) ours+=("$service") ;;
    api | web)
        ours+=("$service")
        long_running+=("$service")
        ;;
    postgres | db-roles | parcels-init) theirs+=("$service") ;;
    *)
        theirs+=("$service")
        long_running+=("$service")
        ;;
    esac
done

# A version tag of Passel's images never moves, so one already on the server is never pulled again. The other images
# follow their moving tags, which brings their patches in, except with --no-migrate: a rollback uses what the server
# has, so it needs no registry and moves no image under the release. When Docker Hub or MCR can't be reached, a
# deploy goes on with the images the server already has; only a missing one fails it, when it is started
compose pull --quiet --policy missing "${ours[@]}"

if [ "$migrate" = true ]; then
    compose pull --quiet --ignore-pull-failures "${theirs[@]}"
else
    compose pull --quiet --policy missing "${theirs[@]}"
fi

compose up --detach --wait --wait-timeout 180 postgres

compose run --rm --no-deps -T db-roles </dev/null
compose run --rm --no-deps -T parcels-init </dev/null

if [ "$migrate" = true ]; then
    compose run --rm --no-deps -T db-migrate </dev/null
else
    echo "Skipping db-migrate (--no-migrate)"
fi

compose up --detach --wait --wait-timeout 300 --no-deps "${long_running[@]}"

smoke_url=$(env_value SMOKE_URL)

if [ -z "$smoke_url" ]; then
    api_host=$(env_value API_HOST)
    [ -n "$api_host" ] || die "API_HOST is not set in $env_file"
    smoke_url="https://$api_host/health"
fi

echo "Checking $smoke_url"
curl --fail --silent --show-error --max-time 10 --retry 6 --retry-delay 5 --retry-all-errors \
    --output /dev/null "$smoke_url"

# <root>/current is the running release, for backup.sh and for running compose by hand; replaced in one rename
ln -sfn "${release_dir#"$root"/}" "$root/.current.next"
mv -fT "$root/.current.next" "$root/current"

trap - ERR

if [ -f "$root/release.env" ]; then
    mv -f "$root/release.env" "$root/release.env.previous"
fi

mv -f "$next_file" "$root/release.env"

migrated=$([ "$migrate" = true ] && echo migrated || echo not-migrated)
printf '%s\t%s\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$tag" "$migrated" >>"$root/releases.log"

# Dangling images only: the tags of earlier releases stay for a rollback
docker image prune --force >/dev/null

echo "Deployed $tag"
