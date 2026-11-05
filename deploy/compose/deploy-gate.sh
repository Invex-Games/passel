#!/usr/bin/env bash
# The only command the deploy key may run on the server (ADR 0004, docs/guides/deploying.md). It is installed by
# hand in the root of the stack it deploys, /opt/passel/deploy-gate.sh for production, and named in the key's
# authorized_keys line:
#
#   command="/opt/passel/deploy-gate.sh",restrict ssh-ed25519 AAAA... passel-deploy
#
# The stack's root is the directory the gate is installed in, and nothing else: no variable, from the forced command
# or from the client, can point it at another stack. A second stack on the same server, such as pre-prod in
# /opt/passel-preprod, gets its own copy of the gate there and its own key line naming that copy.
#
# SSH runs it whatever the client asked for and passes the request in SSH_ORIGINAL_COMMAND. The only request it
# accepts is
#
#   deploy <tag> [--no-migrate]
#
# with a registry token on stdin. It signs in to the registry in a temporary Docker configuration that is removed
# when it exits, so no credential stays on the server, pulls passel-deploy:<tag>, extracts its bundle into
# releases/<tag>/ and runs that release's deploy.sh. An older tag therefore deploys with its own compose files, and a
# stack other than production refuses one older than the per-stack settings.
#
# The bundle's deploy.sh runs as the deploy user, who has the Docker daemon and so the whole server: the gate limits
# what a key can ask for, not what a release does once it runs. Whoever can deploy a tag to any stack on a server can
# reach every stack on it.
set -euo pipefail

umask 077

# The gate's own directory, with links resolved
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
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

# An earlier gate took its root from PASSEL_ROOT; this one never does, so a value that disagrees is a mistake to stop at
if [ -n "${PASSEL_ROOT:-}" ] && [ "${PASSEL_ROOT%/}" != "$root" ]; then
    refuse "PASSEL_ROOT is not used: this gate deploys $root, the directory it is installed in"
fi

[ -f "$root/.env" ] || refuse "$root/.env does not exist; the gate deploys the stack in its own directory"

# One deploy at a time, taken before releases/<tag> is replaced: a second deploy of the same tag would otherwise
# delete the directory the first one's deploy.sh is running from. deploy.sh inherits the lock on descriptor 9
if command -v flock >/dev/null; then
    exec 9>"$root/.deploy.lock"
    flock -n 9 || refuse "another deploy is running"
fi

# The CLI also looks for plugins in its configuration directory, so a Compose plugin installed for this user alone
# (~/.docker/cli-plugins) would vanish with the empty one. Its plugins are linked in; nothing else of the user's
# configuration, credentials least of all, is
user_plugins="${DOCKER_CONFIG:-${HOME:-}/.docker}/cli-plugins"
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

if [ -d "$user_plugins" ]; then
    ln -s "$user_plugins" "$DOCKER_CONFIG/cli-plugins"
fi

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

# A release from before stacks could share a server fixes production's aliases, subnets and edge address in its
# compose files, so a second stack deploying it would answer to production's names on a shared proxy network or
# clash with its networks. A stack other than passel only takes a release whose compose.yaml reads those settings.
# The shell's COMPOSE_PROJECT_NAME, if any, wins over the .env's, as it does for compose
project=$(sed -n -E 's/^[[:space:]]*COMPOSE_PROJECT_NAME[[:space:]]*=[[:space:]]*//p' "$root/.env" | tail -n 1 | tr -d "\r\"' ")
project=${COMPOSE_PROJECT_NAME:-${project:-passel}}
# The project names the volumes, so a second stack that forgot to name its own would deploy over production's
[ "$project" != "passel" ] || [ "$root" = /opt/passel ] ||
    refuse "$root/.env must set COMPOSE_PROJECT_NAME: the project passel belongs to the stack in /opt/passel"
if [ "$project" != "passel" ]; then
    for setting in PASSEL_API_ALIAS PASSEL_BACKEND_SUBNET; do
        grep -qs -- "$setting" "$staging/compose.yaml" ||
            refuse "$tag predates stacks beside production: its compose.yaml ignores $setting, so stack $project can't deploy it"
    done
fi

chmod 755 "$staging"
release_dir="$root/releases/$tag"
rm -rf "$release_dir"
mv "$staging" "$release_dir"
staging=""

status=0
bash "$release_dir/deploy.sh" "${deploy_args[@]}" </dev/null || status=$?
exit "$status"
