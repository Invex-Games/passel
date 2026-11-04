#!/usr/bin/env bash
# The only command the deploy key may run on the server (ADR 0004, docs/guides/deploying.md). It is installed by
# hand as /opt/passel/deploy-gate.sh and named in the key's authorized_keys line:
#
#   command="/opt/passel/deploy-gate.sh",restrict ssh-ed25519 AAAA... passel-deploy
#
# SSH runs it whatever the client asked for and passes the request in SSH_ORIGINAL_COMMAND. The only request it
# accepts is
#
#   deploy <tag> [--no-migrate]
#
# with a registry token on stdin. It signs in to the registry in a temporary Docker configuration that is removed
# when it exits, so no credential stays on the server, pulls passel-deploy:<tag>, extracts its bundle into
# releases/<tag>/ and runs that release's deploy.sh. An older tag therefore deploys with its own compose files.
set -euo pipefail

umask 077

root=${PASSEL_ROOT:-/opt/passel}
registry=${PASSEL_REGISTRY:-ghcr.io}
image_namespace=${PASSEL_IMAGE_NAMESPACE:-invex-games}
registry_user=${PASSEL_REGISTRY_USER:-passel-deploy}

refuse() {
    echo "deploy-gate: $*" >&2
    exit 2
}

request=${SSH_ORIGINAL_COMMAND:-}

# The whole request is checked against one pattern before anything runs, so nothing in it can reach a shell
request_pattern='^deploy [A-Za-z0-9][A-Za-z0-9._-]{0,127}( --no-migrate)?$'
[[ $request =~ $request_pattern ]] || refuse "refused; the only accepted command is: deploy <tag> [--no-migrate]"

read -r -a words <<<"$request"
tag=${words[1]}
deploy_args=("$tag")

if [ "${#words[@]}" -eq 3 ]; then
    deploy_args+=("--no-migrate")
fi

[ -d "$root" ] || refuse "$root does not exist"

# One deploy at a time, taken before releases/<tag> is replaced: a second deploy of the same tag would otherwise
# delete the directory the first one's deploy.sh is running from. deploy.sh inherits the lock on descriptor 9
if command -v flock >/dev/null; then
    exec 9>"$root/.deploy.lock"
    flock -n 9 || refuse "another deploy is running"
fi

DOCKER_CONFIG=$(mktemp -d)
export DOCKER_CONFIG
staging=""

# shellcheck disable=SC2329 # run by the EXIT trap
cleanup() {
    rm -rf "$DOCKER_CONFIG"
    if [ -n "$staging" ]; then
        rm -rf "$staging"
    fi
}

trap cleanup EXIT

# The token is the workflow's own, valid only while its job runs. Without one, only images already on the server or
# public ones can be used
token=""
if [ ! -t 0 ]; then
    IFS= read -r token || true
fi

if [ -n "$token" ]; then
    printf '%s' "$token" | docker login "$registry" --username "$registry_user" --password-stdin >/dev/null
fi

token=""

bundle_image="$registry/$image_namespace/passel-deploy:$tag"

# A version tag never moves, so a bundle already on the server is the same one
if ! docker image inspect "$bundle_image" >/dev/null 2>&1; then
    docker pull --quiet "$bundle_image" >/dev/null
fi

mkdir -p "$root/releases"
staging=$(mktemp -d "$root/releases/.incoming.XXXXXX")

container=$(docker create "$bundle_image" bundle)
if ! docker cp "$container:/bundle/." "$staging/"; then
    docker rm "$container" >/dev/null
    refuse "$bundle_image has no /bundle"
fi
docker rm "$container" >/dev/null

chmod 755 "$staging"
release_dir="$root/releases/$tag"
rm -rf "$release_dir"
mv "$staging" "$release_dir"
staging=""

status=0
bash "$release_dir/deploy.sh" "${deploy_args[@]}" </dev/null || status=$?
exit "$status"
