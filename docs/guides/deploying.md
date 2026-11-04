# Releasing and Deploying

Passel runs on the owner's Ubuntu server as one Docker Compose project: PostgreSQL, the API and the web app, with an optional Caddy in front for TLS ([ADR 0004](../decisions/0004-hosting-on-docker-compose.md)). Every build of `main` pushes the images to GitHub Container Registry, and a manual workflow deploys a tag over SSH. Nothing deploys on a merge.

The Azure deployment of [ADR 0003](../decisions/0003-hosting-on-azure.md) is still in the code and is described at the end, under [Azure (legacy, ADR 0003)](#azure-legacy-adr-0003). The extension and the mobile app are not deployed from here yet; their release builds are described in [Build Targets](../reference/build-targets.md#release-builds).

## Self-hosted (Docker Compose)

### Versions and tags

Versions come from GitVersion and the conventional commit prefixes described in `CONTRIBUTING.md`; on `main` every commit has its own, such as `0.2.0-rc.12`. The `Build` workflow tests every push to `main`, then builds three images and pushes them to `ghcr.io/invex-games/`, each tagged with the version (`+` and `/` become `-`) and with `sha-` and the first seven characters of the commit:

| Image | What it is |
|---|---|
| `passel-webapi` | The API, from the .NET SDK's container support. The same image runs `db-migrate` |
| `passel-web` | Caddy serving the web app's static site ([Web App](../components/web-app.md#hosting)) |
| `passel-deploy` | No program at all: the release's compose files and scripts from `deploy/compose`, which the server copies out of it |

The version tag is the one deployed and rolled back to. It never moves: a build that finds the tag in the registry for another commit fails rather than push over it. There is no `latest`. Builds of other branches build the images without pushing them.

### What gets deployed

`deploy/compose/compose.yaml` describes the stack. Its project is named `passel`, so its volumes are `passel_pgdata` and so on.

| Service | Image | Runs | Purpose |
|---|---|---|---|
| `postgres` | `postgres:18` | Always | The database, in the `pgdata` volume. Only on the internal `backend` network |
| `db-roles` | `postgres:18` | Once per deploy | `db/roles.sh` as the superuser: creates or updates the `passel_owner` and `passel_api` logins with the passwords in `.env`, and makes `passel_owner` the database's owner |
| `parcels-init` | `busybox` | Once per deploy | Creates the parcels directory in the `parcels` volume, owned by the API's user (1654) and readable by it alone |
| `db-migrate` | `passel-webapi` | Once per deploy, unless skipped | `db-migrate` as `passel_owner`: applies the schema and grants `passel_api` the API's DML-only privileges ([Schema in production](../components/web-api.md#schema-in-production)) |
| `api` | `passel-webapi` | Always | The API as `passel_api`, keeping uploaded files in the `parcels` volume (`Parcels:Store=FileSystem`). Healthy when `/health`, which checks the database too, answers 200 |
| `web` | `passel-web` | Always | The web app |
| `edge` | `caddy:2.10-alpine` | With the `edge` profile | TLS for both hosts on ports 80 and 443, with certificates from Let's Encrypt |
| `dashboard` | `aspire-dashboard` | With the `telemetry` profile | A telemetry viewer on the server's loopback |

`api` and `web` publish no port: a proxy reaches them on the `edge` network as `passel-api:8080` and `passel-web:8080`. Both run with every capability dropped and `no-new-privileges`, and every service's logs rotate at 10 MB, five files. The server's `.env` supplies every setting; [Configuration](../reference/configuration.md#compose-deployment) maps each one to the API's.

### Server requirements

- Ubuntu on x64 (the images are `linux/amd64`), with ports 80 and 443 free for the bundled edge.
- Docker Engine with the Compose plugin 2.24 or later (`docker compose version`); `deploy.sh` refuses an older one.
- `curl`, and `openssl` for generating passwords. Both come with Ubuntu.
- Disk for the images (about 1 GB a release), the database and the uploaded files, at most 500 MB per user by default.

### Server setup

Once, as an administrator on the server, then on GitHub.

1. **Updates.** `sudo apt install unattended-upgrades` and keep it on, so security updates install themselves.
2. **The deploy user.** It runs Docker, and a member of the `docker` group can do anything root can, so it gets no password and no other use:
   ```
   sudo adduser --disabled-password --gecos "" deploy
   sudo usermod -aG docker deploy
   ```
3. **The directory.** Everything lives in `/opt/passel`, owned by the deploy user:
   ```
   sudo install -d -o deploy -g deploy -m 750 /opt/passel
   ```
   Deploys add `releases/<tag>/` (each release's compose files), `current` (a link to the running release), `release.env` and `release.env.previous` (the running tag and the one before), `releases.log` (one line per successful deploy, appended to) and, with the default, `backups/`.
4. **The deploy key and the gate.** Install the gate from the repository, owned by root so the deploy user cannot edit it, and make a key for the workflow on your own machine:
   ```
   sudo install -o root -g root -m 755 deploy/compose/deploy-gate.sh /opt/passel/deploy-gate.sh
   ssh-keygen -t ed25519 -N "" -C passel-deploy -f passel-deploy
   ```
   Add the public key to `~deploy/.ssh/authorized_keys` (mode 600, the directory 700) behind a forced command, on one line:
   ```
   command="/opt/passel/deploy-gate.sh",restrict ssh-ed25519 AAAA... passel-deploy
   ```
   Whatever a client asks for, SSH runs the gate, which accepts only `deploy <tag>` and `deploy <tag> --no-migrate` and refuses anything else before running a thing; `restrict` turns off forwarding, terminals and agent use. The private key goes to GitHub in step 7; delete both files from your machine afterwards.
5. **The settings.** Copy `deploy/compose/.env.example` to `/opt/passel/.env`, readable by the deploy user only (`chmod 600`), and fill it in. Generate each password with `openssl rand -hex 32`: hex needs no quoting in a connection string or in the file. `ENCODING_ALPHABET` is the Sqids alphabet for public ids, single-quoted. Generate it once and never change it; if the Azure deployment ever held data, use its `ENCODING_ALPHABET` secret instead, or every id a client holds stops working. Keep a copy of the finished `.env` off the server, in a password manager for instance: the backups don't include it, and without the same alphabet a restored database's ids and shared links no longer decode. `POSTGRES_PASSWORD` only takes effect when the database is first created (see [Troubleshooting](#troubleshooting-the-server)).
6. **DNS and the proxy.** Point `API_HOST` and `WEB_HOST` at the server, then pick how requests reach the stack, with `COMPOSE_FILE` and `COMPOSE_PROFILES` in `.env`:

   | Setup | `COMPOSE_FILE` | `COMPOSE_PROFILES` | `PROXY_TRUSTED_CIDR` |
   |---|---|---|---|
   | The bundled edge: Caddy on 80 and 443 with Let's Encrypt (the default) | `compose.yaml` | `edge` | `172.30.0.2/32`, the edge's fixed address |
   | A proxy already running in a Docker network of its own | `compose.yaml:compose.external-proxy.yaml`, and `PROXY_NETWORK` naming the network | empty | The proxy container's address as a `/32`, or the network's CIDR |
   | A proxy running on the host, such as nginx | `compose.yaml:compose.ports.yaml` (the API on `127.0.0.1:18080`, the web app on `127.0.0.1:18081`) | empty | Not used: Docker's port forwarding connects from the host's address on one of the API's two networks, which one depending on the Docker version, so the override trusts both, `172.30.0.1` and `172.30.1.1` |

   The API believes `X-Forwarded-For` and `X-Forwarded-Proto` from `PROXY_TRUSTED_CIDR` only. The stack's two networks have fixed subnets, `172.30.0.0/24` (`edge`) and `172.30.1.0/24` (`backend`), so no other Docker network on the server may use them. For the bundled edge, open 80/tcp, 443/tcp and 443/udp. Another proxy must pass the client address and scheme in those two headers, stream the event stream at `/api/packets/events` without buffering, keep it open for up to an hour and accept 25 MB uploads; It must also pass the client's original `Host` header (`API_HOST` for the API), since the API answers `400 Invalid Hostname` to any other. For nginx that is `proxy_set_header Host $host`, which nginx doesn't do by default (it sends the `proxy_pass` address, such as `127.0.0.1:18080`), then `proxy_buffering off`, `proxy_read_timeout 1h` and `client_max_body_size 32m`, besides the usual `proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for` and `X-Forwarded-Proto $scheme`. Caddy does all of it by default.
7. **The `production` environment on GitHub** (Settings, Environments): required reviewers, and deployment branches limited to `main`. Add these to it:

   | Name | Kind | Value |
   |---|---|---|
   | `DEPLOY_HOST` | Variable | The server's host name or address |
   | `DEPLOY_USER` | Variable | `deploy` |
   | `DEPLOY_KNOWN_HOSTS` | Variable | The server's host key as a known_hosts line, from `ssh-keyscan -t ed25519 <host>`. Compare its fingerprint (`ssh-keygen -lf` on the line) with `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` run on the server, so the workflow never trusts a key it has only seen over the network |
   | `DEPLOY_SSH_KEY` | Secret | The private key from step 4 |

   The repository variables `GOOGLE_WEB_CLIENT_ID` and `API_BASE_URL` (`https://<API_HOST>`), which the web image is built with, are listed in [Continuous Integration](ci.md#what-the-repository-needs).
8. **The packages.** After the first build of `main` has pushed them, make `passel-webapi`, `passel-web` and `passel-deploy` private (the package's settings on GitHub). Leave this repository's role under Manage Actions access as the first push set it, Admin, or at least Write, or leave the package inheriting access from the repository: the `Build` workflow pushes every later release to the same packages with its own token, and a Read role makes those pushes fail. The deploy job only reads them, with its own token, and the server signs in with that token for the one deploy only.
9. **Google.** Add `https://<WEB_HOST>` to the Google web client's Authorized JavaScript origins ([Google OAuth Clients](../getting-started/google-oauth.md)).
10. **The first deploy.** Run the `DeployCompose` workflow from `main` with the tag left blank. Then `https://<API_HOST>/health` should answer `Healthy`, and signing in at `https://<WEB_HOST>/app/` should show an empty feed.

### The DeployCompose workflow

`DeployCompose` runs on manual dispatch only (Actions, DeployCompose, Run workflow), in the `production` environment, so its protection rules apply first. One run deploys at a time; a second waits rather than cancelling the first. It has two inputs: `image-tag`, blank for the version of the commit the run is on, and `migrate`, on by default.

The `DeployCompose` target checks the tag, checks that the registry has all three images at that tag, and connects to the server as `DEPLOY_USER`, checking its host key against `DEPLOY_KNOWN_HOSTS`. It sends `deploy <tag>` (with `--no-migrate` when `migrate` is off) and the job's token on standard input. The gate on the server signs in to the registry with the token in a temporary Docker configuration, which it deletes when it exits, so no credential stays on the server. It takes the server's deploy lock (`/opt/passel/.deploy.lock`), so a second deploy, from the workflow or by hand, stops with `another deploy is running` before it touches anything, and holds it until `deploy.sh` ends. It pulls `passel-deploy:<tag>`, copies its files to `/opt/passel/releases/<tag>/`, and runs that release's `deploy.sh`, which:

1. Pulls the images. Passel's own are pulled only when the server lacks them, since a version tag never moves. PostgreSQL's, busybox's, Caddy's and the dashboard's are pulled on every deploy, which brings their patches in, except with `--no-migrate`, when the server's own copies are used too. When Docker Hub or MCR can't be reached, the deploy goes on with the images the server has; only one it has never pulled fails it.
2. Starts PostgreSQL and waits for it to be healthy.
3. Runs `db-roles`, `parcels-init` and, unless `--no-migrate`, `db-migrate`, each to completion.
4. Starts `api`, `web` and the profiles' services, and waits for them to be healthy.
5. Fetches `SMOKE_URL` (`https://<API_HOST>/health` by default), retrying for about half a minute.

Only when every step has succeeded does it record the release: `release.env` becomes `release.env.previous`, the new `release.env` names the tag, `releases.log` gets a line, `current` points at the release, and dangling images are pruned (tagged ones stay for a rollback). If a step fails, it prints the last 200 lines of the API's and the web app's logs, leaves `release.env` as it was and fails the run. Whatever had already started keeps running: a failed migration leaves the previous API serving, but an API that started and then failed the smoke check is the new one, so roll back as below.

Each release deploys with its own compose files, because they come from the deploy image of the same tag.

### Rolling back to an earlier release

Run `DeployCompose` with `image-tag` set to the release to go back to and `migrate` off. `release.env.previous` and `releases.log` on the server name the earlier tags; any tag the registry still has works. That release's images start with that release's compose files, on the database as the newer migration left it.

That only works because of one rule for every change to the schema: a release may add tables and columns, but a new column must be nullable or have a default, so that the release before it, which doesn't know the column, can still insert rows. `db-migrate` never drops a column, an index or a foreign key, and the API's startup check accepts what a newer migration added. With `migrate` off, the older migration doesn't run either; run it (`migrate` on) only if the older release needs Marten functions put back, which its startup check would say.

### Deploying by hand

On the server, as the deploy user, the gate does the same as the workflow. A tag the server has deployed before, with `--no-migrate`, needs no registry, neither GHCR nor Docker Hub nor MCR:

```
SSH_ORIGINAL_COMMAND="deploy 0.2.0-rc.11 --no-migrate" /opt/passel/deploy-gate.sh </dev/null
```

For a tag it has never pulled, give the gate a GitHub token that can read the packages (a fine-grained or classic token with `read:packages`) on standard input; it is used for this deploy only and not stored:

```
read -rs TOKEN && printf '%s\n' "$TOKEN" | SSH_ORIGINAL_COMMAND="deploy 0.2.0-rc.12" /opt/passel/deploy-gate.sh
```

To run compose yourself, work from the running release and give it both settings files:

```
cd /opt/passel/current
docker compose --env-file ../../.env --env-file ../../release.env ps
```

### Logs and telemetry on the server

`docker compose --env-file ../../.env --env-file ../../release.env logs -f api` (from `/opt/passel/current`) follows the API's log; `docker compose ... logs db-migrate` has nothing, because the one-off services run with `--rm` and their output went to the deploy's log, which is the workflow run's. Docker keeps 50 MB of each service's log. The API logs a warning at startup that its data protection keys are not persisted: nothing in Production uses them, since the API takes bearer tokens only.

The API exports logs, traces and metrics over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, to any OTLP backend (`OTEL_EXPORTER_OTLP_PROTOCOL` and `OTEL_EXPORTER_OTLP_HEADERS` as the backend needs). For a look without one, add `telemetry` to `COMPOSE_PROFILES`, set the endpoint to `http://dashboard:18889` and deploy: the Aspire dashboard listens on the server's `127.0.0.1:18888`, reached with `ssh -L 18888:127.0.0.1:18888 <you>@<server>` from your own account (the deploy key can't forward), and its log has the login link. It keeps telemetry in memory only. The privacy rules in [Logs and telemetry](#logs-and-telemetry) apply to whatever backend receives it.

### Backups and the restore drill

`backup.sh` writes three files with the same UTC timestamp: `db-<time>.dump` (`pg_dump` in the custom format), `roles-<time>.sql` (`pg_dumpall --roles-only`, for reference: `db-roles` recreates the logins) and `parcels-<time>.tgz` (the parcels volume). It writes them under temporary names and renames them only once all three are complete, so a failed run leaves no dump without its archive. The API keeps running throughout. The database is dumped first and the files archived second: a file uploaded in between is in the archive without a record, and the orphan scan removes it after a restore; a parcel deleted in between is in the dump without its file, and after a restore its download answers 404 (the API logs `Parcel <id> has no blob`). Uploads in flight (`.uploading`) are left out of the archive, and a file the API moves or deletes while the archive is written is skipped and named in the log; any other error from `tar` fails the run. Run it from the deploy user's crontab:

```
15 3 * * * /opt/passel/current/backup.sh /var/backups/passel >>/var/log/passel/backup.log 2>&1
```

The directory and the log's directory must exist and belong to the deploy user. Shipping the files off the server and pruning old ones is for an offsite tool such as restic or rclone, run after it. The database and the parcels are consistent only as a pair from the same run. The backups leave out `/opt/passel/.env`, which holds the passwords and `ENCODING_ALPHABET`: keep a copy of it off the server, apart from the backup files (step 5 of [Server setup](#server-setup)). The passwords can be set anew, but every packet, parcel and device id a client holds, and every shared link, depends on the alphabet, and a restore with another one brings the data back with none of them decoding.

To restore, or to rehearse a restore on a spare server, from `/opt/passel/current` with `dc` standing for `docker compose --env-file ../../.env --env-file ../../release.env`:

0. On a new server, set it up as in [Server setup](#server-setup), but put the kept copy of `.env` at `/opt/passel/.env` instead of a new one, with the same `ENCODING_ALPHABET`, and deploy the release the backup came from once, so that `current` and `release.env` exist.
1. `dc down`, and for a full restore remove the old volumes too (`dc down -v`).
2. `dc up -d --wait postgres`, which creates an empty database.
3. `dc run --rm --no-deps -T db-roles` for the two logins.
4. `dc exec -T postgres sh -c 'pg_restore --username="$POSTGRES_USER" --dbname="$POSTGRES_DB" --no-owner --no-privileges --role=passel_owner' < db-<time>.dump`. The tables then belong to `passel_owner`; the dump's grants are left out because `passel_app` doesn't exist yet.
5. `dc run --rm --no-deps -T db-migrate`, which creates `passel_app` and puts back the grants and default privileges.
6. `dc run --rm --no-deps -T parcels-init`, then `dc run --rm --no-deps -T parcels-init sh -c 'cd /v && tar -xzf -' < parcels-<time>.tgz`.
7. Deploy the release again (`deploy <tag> --no-migrate`), which starts the rest and checks it.

### Disk space

`df -h /var/lib/docker` shows what is left for the images and the volumes, and `docker system df -v` what each volume takes. A full disk shows first as 503 on the file endpoints (the filesystem store's writes fail), then as a database that stops accepting writes. Each release keeps its images until removed: `docker image ls 'ghcr.io/invex-games/*'` lists them, and `docker image rm` removes those of releases you won't roll back to. Their directories under `releases/` are small and can go too, except the current and previous ones.

### Troubleshooting the server

| Symptom | Likely cause |
|---|---|
| `deploy-gate: refused` | The request was not exactly `deploy <tag>` or `deploy <tag> --no-migrate`, or the tag has a character outside letters, digits, `.`, `_` and `-` |
| The workflow fails before connecting, saying an image is not in the registry | The tag was never pushed: only builds of `main` push, and the build may still be running |
| `Host key verification failed` | `DEPLOY_KNOWN_HOSTS` doesn't hold the server's current host key |
| `db-roles` fails to sign in | `POSTGRES_PASSWORD` was changed in `.env` after the database was created; the image only uses it the first time. Put the old one back, or first run `ALTER ROLE postgres PASSWORD '<new>'` with `dc exec postgres psql -U postgres` |
| `db-migrate` fails with permission denied | It ran as another login than `passel_owner`, or the tables belong to another role (a restore without `--no-owner`) |
| The API never becomes healthy and its log names the schema | The schema lacks a table, column or function the API needs: `db-migrate` didn't run, or the deploy used `--no-migrate` for a release whose migration never ran |
| The API stops at startup over `Cors:AllowedOrigins` | `WEB_ORIGIN` isn't written as a browser sends it: scheme and host, a port only when it isn't the default, no path and no trailing slash |
| Every request gets `400 Invalid Hostname` | The proxy sends a `Host` other than `API_HOST` |
| Every request gets `401` | `GOOGLE_WEB_CLIENT_ID` or `CHROME_EXTENSION_CLIENT_ID` doesn't match the client the token was issued to |
| File requests get `503`, while packets work | The parcels directory can't be written: the volume is full, or the directory belongs to root (it was created without `parcels-init`; deploying again fixes it) |
| Rate limits hit everyone at once, or the API logs the proxy's address for every client | `PROXY_TRUSTED_CIDR` doesn't match the proxy, so the forwarded headers are ignored |
| Clients can set their own address | The forwarded headers are applied twice: `ASPNETCORE_FORWARDEDHEADERS_ENABLED` must stay unset, or the API is reachable other than through the proxy |
| `edge` fails with "address already in use" | Something else holds port 80 or 443 on the host |
| A network fails to be created, saying its pool overlaps | Another Docker network on the server uses `172.30.0.0/24` or `172.30.1.0/24` |
| A deploy fails because a network "has active endpoints" | The release changed a network's settings, which Docker can only apply to a new network. `dc down` (the volumes stay), then deploy the release again |

### Verified locally

Without the owner's server, the stack was run on a development machine as a separate project (`passel-verify`, on the loopback ports of `compose.ports.yaml`) from locally built images, through the gate as the workflow would:

- A first deploy, a second release and a rollback to the first with `--no-migrate`, each recorded in `releases.log` with `release.env.previous` naming the one before. `db-roles` and `db-migrate` ran every time; `db-migrate` as `passel_owner`, not a superuser, applied the schema to an empty database and again to an existing one.
- The API healthy through its own healthcheck, `/health` answering 200 with `Host: localhost` and 400 with any other, an unauthenticated request getting 401, the parcels directory owned by 1654 and writable, and `ASPNETCORE_FORWARDEDHEADERS_ENABLED` absent.
- A deploy whose smoke check failed: it printed the logs, left `release.env` as it was and exited non-zero.
- The gate refusing `deploy local; id`, a tag with `;`, extra words and a second line, and `deploy.sh` refusing `x;rm`.
- `backup.sh`, then the restore drill above onto empty volumes, with the data, the table owner, the grants and the files back.
- In `compose.ports.yaml` mode, the forwarded headers believed from a request through the published port (HSTS sent for `X-Forwarded-Proto: https`) and ignored from another container on the `edge` network.
- The bundled edge on a spare port, proxying both hosts, at its fixed address; and the `telemetry` profile, with the dashboard running without capabilities.

What only the server can show: the first pull from GHCR with the job's token, Let's Encrypt certificates for the real hosts, the deploy over SSH with the forced command, and the external-proxy setups.

## Azure (legacy, ADR 0003)

This deployment is no longer where Passel runs, but its code stays in place: the AppHost's Azure model (`src/webapi/Invex.Passel.AppHost/AzureHosting.cs`), the Azure blob parcel store and the manual `Deploy` workflow, with its concurrency group `deploy-webapi`. The API runs on Azure Container Apps with an Azure Database for PostgreSQL flexible server, deployed with `aspire deploy`.

### Versions

Versions come from GitVersion and the conventional commit prefixes described in `CONTRIBUTING.md`. Every push to `main` runs the `Build` workflow, which tests everything and attaches the release builds to the run. The Deploy workflow deploys the commit it was started on, and tags the API's image with that commit's version, so the image in the registry, the revision in Azure and the commit always match.

### What gets deployed

| Resource | Name in the AppHost | Purpose |
|---|---|---|
| Container Apps environment, with a container registry | `passel-env` | Runs the API and the migration job; the registry holds their images |
| Container app | `invex-passel-webapi` | The API: one replica, 0.5 vCPU, 1 GiB, HTTPS ingress, readiness on `/health` and liveness on `/alive` |
| Container Apps job | `passel-migrate` | The API's image started as `db-migrate`: applies the schema and grants the API's role its privileges |
| PostgreSQL flexible server and database | `passel-pg`, `passel-db` | PostgreSQL 18, Burstable B1ms, 32 GB with auto-grow, 14 days of point-in-time restore, Entra authentication only |
| Storage account, with the `parcels` blob container | `passel-storage`, `passel-blobs`, `passel-parcels` | Uploaded files. Standard LRS, no anonymous access, TLS 1.2, no shared keys (the API signs in as its identity, with Storage Blob Data Contributor and no other role on the account). Blob soft delete and versioning are off, so a deleted file is erased; deleted containers are kept for 7 days. |
| Key Vault | `passel-kv` | The Sqids alphabet |
| Log Analytics workspace and Application Insights | `passel-logs`, `passel-insights` | Container logs, and the API's logs, traces and metrics |
| Managed identities | `passel-api-identity` and one per job and environment | What each resource signs in as; no passwords |

Azure names are derived from these and made unique per resource group, so the server, the vault, the storage account and the environment get a suffix (`passelpg-…`, `passelkv-…`, `passelstorage…`, `passelenv…`). The container app and the job keep their names.

> [!WARNING]
> Leave blob soft delete and versioning off on the storage account. Deleting a file must erase it ([ADR 0001](../decisions/0001-packets-as-documents.md)); with either on, Azure keeps every deleted file for the retention period, out of reach of the API's sweep. Container soft delete is on, and keeps no deleted files: it only lets a container deleted by mistake be restored within 7 days.

### The Deploy workflow

`Deploy` runs on manual dispatch only (Actions, Deploy, Run workflow), in the `production` environment, so its protection rules apply before anything runs. One run deploys at a time; a second one waits rather than cancelling the first. The workflow signs in to Azure with `azure/login` and the deployment identity's federated credential, then runs `DeployWebApi`, which:

1. Checks that the Google client ids are real client ids and that the Sqids alphabet is set.
2. Writes the Bicep with `aspire publish` and attaches it to the run as `AzureInfrastructure`, a record of what was provisioned.
3. Runs `aspire deploy --environment Production`. Aspire provisions or updates every resource in the resource group, builds the two images with `dotnet publish` (the .NET SDK's container support) and pushes them with the version tag, updates the migration job and runs it, and only once the migration has succeeded updates the API's container app. Container Apps then starts the new revision and moves traffic to it when its readiness probe passes.

If the migration fails, the deployment stops before the API is touched, and the running revision keeps serving. If a new revision never becomes ready (a missing setting, for example), or the deployment fails after the migration, the previous revision keeps serving too, and can still restart: the API's startup check accepts a schema a newer migration has added to, and only refuses one that lacks something it needs.

To see the Bicep without deploying, run `dotnet atom PublishAzureInfrastructure`; it writes the files to `atom-publish/AzureInfrastructure` without signing in to anything. Every pull request runs it too, so a change that breaks the Azure model fails in review.

### One-off setup

These are the steps of the checklist in [ADR 0003](../decisions/0003-hosting-on-azure.md#one-off-setup-by-the-owner), in order. They need someone who can create resource groups and role assignments in the subscription; the deployment identity itself only ever gets rights in the API's resource group.

#### Azure

Register the resource providers the deployment uses, once per subscription:

```
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.ContainerRegistry
az provider register --namespace Microsoft.DBforPostgreSQL
az provider register --namespace Microsoft.Insights
az provider register --namespace Microsoft.KeyVault
az provider register --namespace Microsoft.ManagedIdentity
az provider register --namespace Microsoft.OperationalInsights
az provider register --namespace Microsoft.Storage
```

Create the API's resource group, and a separate one for the deployment identity. The identity lives apart so that its rights in the API's group don't include changing its own credentials:

```
az group create --name <rg> --location <region>
az group create --name <ops-rg> --location <region>
az identity create --name passel-github-deploy --resource-group <ops-rg>
```

Give the identity a federated credential that only a workflow job in the `production` environment of this repository can use:

```
az identity federated-credential create --name github-production --identity-name passel-github-deploy --resource-group <ops-rg> --issuer https://token.actions.githubusercontent.com --subject repo:Invex-Games/pushin:environment:production --audiences api://AzureADTokenExchange
```

Note the identity's `clientId` and `principalId` (`az identity show --name passel-github-deploy --resource-group <ops-rg>`), and the tenant id (`az account show --query tenantId`).

#### Role assignments

The deployment identity needs **Contributor** on the API's resource group to create and update the resources, and **Role Based Access Control Administrator** there for the three role assignments Aspire makes: AcrPull for the environment's identity on the registry, Key Vault Secrets User for the API's identity on the vault, and Storage Blob Data Contributor for the API's identity on the storage account. The condition limits it to those three roles:

```
az role assignment create --assignee-object-id <principalId> --assignee-principal-type ServicePrincipal --role Contributor --scope /subscriptions/<sub>/resourceGroups/<rg>

az role assignment create --assignee-object-id <principalId> --assignee-principal-type ServicePrincipal --role "Role Based Access Control Administrator" --scope /subscriptions/<sub>/resourceGroups/<rg> --condition-version 2.0 --condition "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {7f951dda-4ed3-4680-a7ca-43fe172d538d, 4633458b-17de-408a-b874-0445c86b69e6, ba92f5b4-2d11-453d-a403-e96b0029c9fe})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {7f951dda-4ed3-4680-a7ca-43fe172d538d, 4633458b-17de-408a-b874-0445c86b69e6, ba92f5b4-2d11-453d-a403-e96b0029c9fe}))"
```

If the identity was given the role with the two-role condition before files were added, replace its assignment with this one (`az role assignment delete` with the same assignee, role and scope, then the command above); otherwise the first deployment with the storage account fails with `AuthorizationFailed` on the storage role assignment.

The deployment identity gets no access to the database: the migration runs inside Azure as the job's own identity.

#### GitHub

Create the `production` environment (Settings, Environments), with required reviewers and deployment branches limited to `main`. Then add these variables to the environment, or to the repository if other workflows need them too:

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | The deployment identity's client id |
| `AZURE_TENANT_ID` | The tenant id |
| `AZURE_SUBSCRIPTION_ID` | The subscription id |
| `AZURE_RESOURCE_GROUP` | `<rg>` |
| `AZURE_LOCATION` | `<region>`, for example `australiaeast` |
| `GOOGLE_WEB_CLIENT_ID` | The Google web client id, the API's token audience ([Google OAuth Clients](../getting-started/google-oauth.md)) |
| `CHROME_EXTENSION_CLIENT_ID` | The extension's client id, already used by the `Build` workflow |
| `API_DOMAIN`, `API_CERTIFICATE_NAME` | Empty until the [custom domain](#custom-domain) is set up |

And one environment secret, `ENCODING_ALPHABET`: the Sqids alphabet for public ids, a shuffle of the 62 letters and digits. Generate it once and keep it: changing it later makes every id a client holds invalid.

```
pwsh -c "-join ([char[]]'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789' | Get-Random -Shuffle)"
```

#### First deployment

Run the Deploy workflow from `main`. The first run creates everything and takes several minutes, most of it the database server. The run's log ends with the API's URL (`https://invex-passel-webapi.<environment domain>`); `/health` there should answer `Healthy`.

#### Custom domain

The clients' production builds call `https://api.passel.link`, so the API needs that domain bound with a certificate. Container Apps validates the domain before it accepts it, and issues a managed certificate only once the domain is bound, so it takes two deployments:

1. Look up the app's host name and the environment's verification id:
   ```
   az containerapp show --name invex-passel-webapi --resource-group <rg> --query properties.configuration.ingress.fqdn --output tsv
   az containerapp env list --resource-group <rg> --query "[0].properties.customDomainConfiguration.customDomainVerificationId" --output tsv
   ```
2. Add a CNAME record `api` pointing at the host name, and a TXT record `asuid.api` holding the verification id.
3. Set `API_DOMAIN` to `api.passel.link` and deploy. The domain is added without a certificate, and the API's `AllowedHosts` now includes it.
4. Create the managed certificate, then set `API_CERTIFICATE_NAME` to its name and deploy again:
   ```
   az containerapp env certificate create --resource-group <rg> --name <environment name> --certificate-name api-passel-link --hostname api.passel.link --validation-method CNAME
   ```

Every later deployment keeps the binding, because both values come from the variables each time.

### The database

Three kinds of role exist on the server:

| Role | Who | Can |
|---|---|---|
| Microsoft Entra administrator | The migration job's identity, made one by the deployment | Change the schema; create roles. Owns the tables |
| `passel_app` | Nobody logs in as it | Select, insert, update and delete rows, use the sequences and run the functions in the store's schema |
| The API's role, named after `passel-api-identity` | The API, with an Entra token | What `passel_app` can, nothing more |

`db-migrate` ([Web API](../components/web-api.md#schema-in-production)) creates the last two the first time and is a no-op for them afterwards. The API checks at startup that the database has every table, column and function it needs and refuses to start otherwise, so a revision deployed without its migration never takes traffic. Anything more, such as a column or an index a newer release added, it accepts.

A schema change reaches production at the next deployment, applied by the migration job while the previous revision is still serving. So a change must work with the code before it as well as after it: add a column or a table in one release, and stop using the old one in a later one. `db-migrate` never drops a table, or a column, index or foreign key the configuration no longer declares; it logs each one it leaves in place. To remove one for good, once no release that could be rolled back to uses it, write the statement with `db-patch`, which lists the drops `db-migrate` leaves out, and run it yourself as described below.

To see what a migration would change without applying it, run `db-patch` against the database: it writes the statements to a file instead of running them. Locally it uses the connection string from `ConnectionStrings__passel-db` or the API's `appsettings.Development.json` ([Configuration](../reference/configuration.md#web-api)):

```
dotnet run --project src/webapi/Invex.Passel.WebApi -- db-patch patch.sql
```

To look at the database yourself, add your own account as a Microsoft Entra administrator of the server (portal, the server, Security, Authentication), then connect with a token as the password:

```
az account get-access-token --resource-type oss-rdbms --query accessToken --output tsv
psql "host=<server>.postgres.database.azure.com dbname=passel-db user=<you@example.com> sslmode=require"
```

#### Backups and restore

The server keeps automatic backups for 14 days, and can be restored to any point in that window. A restore creates a new server rather than rewinding the existing one:

```
az postgres flexible-server restore --resource-group <rg> --name <new-server> --source-server <server> --restore-time 2026-11-04T09:00:00Z
```

The deployment keeps pointing at the original server, so copy what you need back from the restored one (`pg_dump` and `pg_restore`), then delete it.

### Rolling back

The Deploy workflow deploys only from `main`: the `production` environment refuses any other branch, and the run dialog offers branches and tags, never a commit. So a rollback is a change on `main`:

1. Revert the commits to undo (`git revert`) in a pull request, and merge it once `Validate` passes.
2. Run the Deploy workflow from `main`. It deploys the reverted code under a new version, and its migration puts back the older release's Marten functions. Tables, columns, indexes and foreign keys the newer release added stay where they are, data included, and the older code ignores them.

While that goes through review, the API alone can go back to an earlier image straight away, without a deployment. The older code starts on the newer schema, because the startup check accepts what a newer migration added. List the revisions with their images, then point the container app at the one to go back to:

```
az containerapp revision list --name invex-passel-webapi --resource-group <rg> --query "[].{name:name, created:properties.createdTime, image:properties.template.containers[0].image}" --output table
az containerapp update --name invex-passel-webapi --resource-group <rg> --image <image>
```

The next Deploy run replaces that image with whatever `main` holds, so follow it with the revert.

### Logs and telemetry

The API's requests, dependencies, exceptions and logs are in Application Insights (`passel-insights`). Container output, including the migration job's, is in the Log Analytics workspace (`passel-logs`), table `ContainerAppConsoleLogs_CL`; each job run's status is also on the job's Execution history page in the portal. Both keep data for the workspace's default of 30 days.

None of it holds what users send: requests are recorded by path (packet ids, never content) with the query redacted, SQL without parameter values, and logs name packet ids. The page fetches for link previews go to the hosts users link, so they are left out of the dependencies, the outgoing HTTP and DNS metrics carry no host name, and the reason a link got no preview is logged at Debug, which Production doesn't keep. A new outgoing request to a URL a user supplied must call `ExcludeFromTelemetry` on its `HttpRequestMessage` in the same way.

Uploaded files stay out of it in the same way. The storage requests are recorded as dependencies by blob URI, and a blob is named after its parcel's Guid alone, never a user or a file name; requests to the API are recorded by path, which carries the public parcel id. File names are never logged, and the file sweep logs counts at Information and parcel ids at Warning. The Azure SDK's own event log, which logs every storage request at Information, is kept to warnings (`Azure.Core` in `appsettings.json`).

### Troubleshooting

| Symptom | Likely cause |
|---|---|
| The workflow fails at Azure login | The federated credential's subject doesn't match: the job must run in the `production` environment of this repository |
| `AuthorizationFailed` while provisioning | A missing role on the resource group, or an unregistered resource provider |
| The deployment fails at `run-database-migration` | The migration job failed; its output is in the workspace. The API was not changed |
| The new revision never becomes ready | The API refused to start: usually a missing setting, or a schema that lacks a table, column or function it needs (the migration didn't run). The revision's console log says which |
| Every request gets `400 Invalid Hostname` | The host isn't in `AllowedHosts`: set `API_DOMAIN` for a custom domain |
| Every request gets `401` | `GOOGLE_WEB_CLIENT_ID` or `CHROME_EXTENSION_CLIENT_ID` doesn't match the client the token was issued to |
| `AuthorizationFailed` on the `invex-passel-webapi-roles-passel-storage` assignment | The deployment identity's Role Based Access Control Administrator condition doesn't include Storage Blob Data Contributor; see [Role assignments](#role-assignments) |
| File requests get `503` "File storage unavailable", while packets work | Storage can't be reached or refuses the API. A `403` (`AuthorizationPermissionMismatch`) in the API's dependencies means the API identity lacks Storage Blob Data Contributor on the account; a missing `ConnectionStrings__passel-blobs` is logged as a warning when the API starts. `/health` stays healthy either way, by design |
| The first revision with files never becomes ready | Its schema check found no `mt_doc_parcel` table: the migration job didn't run, or failed. Its log says which |

### What was verified locally

Nothing has been deployed yet: the setup above needs the owner's subscription. Without it, these were checked offline:

- `aspire publish` writes the Bicep, `aspire do diagnostics` shows the migration ordered between the job and the API, and the Azure modelling leaves the run-mode model of the AppHost as it was.
- With the storage account added, the Bicep has one role assignment on the account, Storage Blob Data Contributor for `passel-api-identity`; blob soft delete and versioning off and container delete retention at 7 days; the `parcels` container; `ConnectionStrings__passel-blobs` on the API set to the blob endpoint; and nothing of storage on `passel-migrate`. The migration still runs between the job and the API.
- The image built with `dotnet publish -t:PublishContainer` runs `db-migrate` against PostgreSQL 18 and then serves `/health` as a role that only has DML, refusing other hosts with 400.
- The API in Production refuses to start on a database that hasn't been migrated, and starts with an Application Insights connection string set.
- The integration tests run the migration twice and the API on the DML-only role, and check that the role can't create or alter tables. They also run a newer release's migration, then this build's API on it, then this build's migration as a rollback, and check that the API serves throughout and the newer column survives.

What can only be checked in Azure: the first `aspire deploy`, the Entra mapping of the API's role (a security label set by `db-migrate`), the probes' `Host` header, the custom-domain steps, the API's managed-identity sign-in to storage, and how much ephemeral disk the container has for uploads buffered while they are checked (at most four 25 MB files at once).
