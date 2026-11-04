#!/usr/bin/env bash
# Creates or updates Passel's two database logins. Runs as the superuser (POSTGRES_USER) on every deploy, before
# db-migrate, and changes nothing that is already right, so it may run any number of times:
#
#   passel_owner  owns the database, so it can create the schema's objects, and has CREATEROLE, so it can create
#                 passel_app and grant it. db-migrate always signs in as passel_owner. It is not a superuser.
#   passel_api    the API's login. db-migrate makes it a member of passel_app, which may only read and write data.
#
# The passwords come from the server's .env, so changing one there and deploying changes the login's password.
set -euo pipefail

: "${PGDATABASE:?PGDATABASE is not set}"
: "${PASSEL_OWNER_DB_PASSWORD:?PASSEL_OWNER_DB_PASSWORD is not set}"
: "${PASSEL_API_DB_PASSWORD:?PASSEL_API_DB_PASSWORD is not set}"

psql --no-psqlrc --quiet \
    --set ON_ERROR_STOP=1 \
    --set db="$PGDATABASE" \
    --set owner_password="$PASSEL_OWNER_DB_PASSWORD" \
    --set api_password="$PASSEL_API_DB_PASSWORD" <<'SQL'
SELECT 'CREATE ROLE passel_owner'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'passel_owner') \gexec

SELECT format('ALTER ROLE passel_owner LOGIN CREATEROLE NOSUPERUSER PASSWORD %L', :'owner_password') \gexec

SELECT format('ALTER DATABASE %I OWNER TO passel_owner', :'db') \gexec

SELECT 'CREATE ROLE passel_api'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'passel_api') \gexec

SELECT format('ALTER ROLE passel_api LOGIN NOSUPERUSER NOCREATEROLE NOCREATEDB PASSWORD %L', :'api_password') \gexec
SQL

echo "db-roles: passel_owner and passel_api are up to date"
