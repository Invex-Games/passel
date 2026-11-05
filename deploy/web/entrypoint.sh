#!/bin/sh
# Configures the environment-neutral web image for the environment it runs in, then starts Caddy (the image's CMD).
# It takes two values from the container's environment:
#
#   API_ORIGIN        the API's origin, such as https://api.passel.link (http only for localhost or 127.0.0.1)
#   GOOGLE_CLIENT_ID  the Google OAuth web client id, <id>.apps.googleusercontent.com
#
# and writes /run/passel-web/config.json, which Caddy serves as /app/config.json and the app reads when it starts.
# Caddy also puts API_ORIGIN into the app's Content-Security-Policy. A value that is missing or not valid stops the
# container here, before anything is served. The checks admit no quote, backslash, space or control character, so
# the values can go into the JSON and the Caddyfile as they are. See docs/components/web-app.md.
set -eu

config_dir=/run/passel-web

fail() {
    echo "passel-web: $*; the container does not start" >&2
    exit 64
}

api_origin=${API_ORIGIN:-}
client_id=${GOOGLE_CLIENT_ID:-}

[ -n "$api_origin" ] || fail "API_ORIGIN is not set (the API's origin, such as https://api.passel.link)"
[ -n "$client_id" ] || fail "GOOGLE_CLIENT_ID is not set (the Google OAuth web client id)"

# One trailing slash is tolerated; the origin is used without it
api_origin=${api_origin%/}

# The allowed characters first: grep below matches line by line, and this rejects a newline among others
case $api_origin in
    *[!A-Za-z0-9.:/-]*) fail "API_ORIGIN has a character no origin has ($API_ORIGIN)" ;;
esac
case $client_id in
    *[!A-Za-z0-9._-]*) fail "GOOGLE_CLIENT_ID has a character no client id has ($GOOGLE_CLIENT_ID)" ;;
esac

https_origin='^https://[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?(:[0-9]{1,5})?$'
loopback_origin='^http://(localhost|127\.0\.0\.1)(:[0-9]{1,5})?$'
if ! printf '%s\n' "$api_origin" | grep -Eq -e "$https_origin" -e "$loopback_origin"; then
    fail "API_ORIGIN must be an https origin with no path, such as https://api.passel.link ($API_ORIGIN)"
fi

if ! printf '%s\n' "$client_id" | grep -Eq '^[A-Za-z0-9._-]+\.apps\.googleusercontent\.com$'; then
    fail "GOOGLE_CLIENT_ID is not a Google OAuth client id, <id>.apps.googleusercontent.com ($GOOGLE_CLIENT_ID)"
fi

[ -d "$config_dir" ] && [ -w "$config_dir" ] || fail "$config_dir is missing or not writable by $(id -u)"

# Written beside the final name and renamed, so Caddy never serves half a file
umask 022
printf '{"API_URL":"%s/api/packets","GOOGLE_CLIENT_ID":"%s"}\n' "$api_origin" "$client_id" >"$config_dir/config.json.tmp"
mv -f "$config_dir/config.json.tmp" "$config_dir/config.json"

# The Caddyfile reads the checked origin, never the raw value
API_ORIGIN=$api_origin
export API_ORIGIN

echo "passel-web: serving the app for the API at $api_origin" >&2
exec "$@"
