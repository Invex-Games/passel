# ADR 0004: Hosting on Docker Compose on the owner's server

**Status:** accepted, November 2026. Supersedes [ADR 0003](0003-hosting-on-azure.md). This records the decision before it is all built: the filesystem parcel store and then the images land first, and the compose stack and the deploy workflow follow in the next changes, so nothing described here runs yet unless the [deploying guide](../guides/deploying.md) says so. **Decides:** where the API and the web app run, how they are built, deployed and rolled back, and where uploaded files are kept outside Azure.

## Context

ADR 0003 put the API on Azure Container Apps with a managed PostgreSQL server, Key Vault and Application Insights. The owner has an Ubuntu x64 server of their own, and for one small API with one replica the Azure services cost more each month than the server does in a year. ADR 0003 weighed a VM with Docker Compose and set it aside because patching, TLS, backups and deployment would all be by hand; this decision answers each of those below. [ADR 0002](0002-product-decisions.md) anticipated the move: should hosting leave Azure, the parcel store swaps behind its interface, and nothing in the clients depends on either.

## Options

| Option | For | Against |
|---|---|---|
| Azure Container Apps (ADR 0003) | Managed revisions, jobs, identities and TLS; nothing to patch | The running cost for one replica and a database server; Azure-only pieces in the AppHost |
| **Docker Compose on the owner's server** | One server already paid for; the same containers as anywhere else; a stack small enough to read in one file | One machine: no failover, and the owner patches and backs it up |
| k3s on the same server | Rolling deploys and probes as in Kubernetes | A cluster to run for two containers and a database, with nothing it does that Compose and a deploy script can't for one replica |
| A platform as a service (Fly.io, Render, Railway) | TLS, deploys and logs managed | Another bill and another vendor, managed PostgreSQL priced like Azure's, and nowhere obvious for the files |

## Decision

The API, the web app and PostgreSQL run as one Docker Compose project on the owner's server, deployed from GitHub Actions over SSH. Azure stays in the code but is no longer where Passel runs.

**Versions and images.** The build takes its version and build id from GitVersion, which `IBuild` uses as its provider. The version is GitVersion's SemVer (the same as its FullSemVer under `ContinuousDelivery`), which on `main` ends in the number of commits since the last version, as in `0.2.0-rc.12`, so every commit on `main` has a version of its own. Every image gets two tags: the version, with `+` and `/` replaced by `-`, which is immutable and is what gets deployed and rolled back to, and `sha-` with the first seven characters of the commit, for tracing an image to its source and as a backstop should two builds ever produce the same version, as two branches with the same name at different times could. There is no `latest`. The images live in GitHub Container Registry under `ghcr.io/invex-games/`: `passel-webapi`, the API, which also runs the `db-migrate` command; `passel-web`, Caddy serving the static web app; and `passel-deploy`, an image built from scratch that holds the compose files and scripts of the release under the same tag. Images are pushed only by builds of `main`; other builds build them without pushing. A version tag that exists already is never overwritten: before a push the build looks the tag up, skips the push when it is there with the same `org.opencontainers.image.revision`, and fails when it is there with another. The job that publishes depends on the API's tests, because the merge commit is what ships, whatever its pull request showed.

**Build-time values.** The web app compiles the API's address and the Google web client id into its script and its content security policy, so there is one web image per environment. The build reads both from repository variables (`API_BASE_URL`, `GOOGLE_WEB_CLIENT_ID`), and building the web app without a real client id fails instead of warning. The API image is built by the .NET SDK's container support; its registry, repository and tags are passed by the publishing target only, so the Azure path's image naming does not change.

**The web image.** Caddy serves the output of the one web app build, with the security headers in a snippet that both the site and its 404 page import, its admin endpoint and automatic HTTPS off, and running as an unprivileged user. The base image's capability to bind low ports is removed from the Caddy binary, since it listens on 8080 and a binary with a file capability will not start in a container that drops every capability. TLS ends in front of it.

**Files.** A new `FileSystemParcelStore` keeps the bytes of uploaded files in a directory on a local disk, chosen with `Parcels:Store=FileSystem` and `Parcels:FileSystemRoot`. The default, `Auto`, keeps the old behaviour: Azure when the `passel-blobs` connection string is set, no store otherwise. Naming `AzureBlob` without that connection string stops the API at startup rather than quietly leaving it with no store. The store writes each upload to a temp file, flushes it, renames it into place without overwriting and then flushes the directory, so a parcel recorded as uploaded has its bytes on disk even after a power cut; its own disk failures are answered with 503 like a storage outage, and are kept apart from other I/O errors such as a client that stops sending. It works on a local disk only: a network share weakens the rename and the flushes it relies on. See [Parcels](../components/web-api.md#parcels).

**The database and its roles.** PostgreSQL 18 runs in the stack with a data volume. A script run on every deploy, as the superuser the container creates, makes sure two logins exist with the passwords in the server's `.env`: `passel_owner`, which owns the database and may create roles but is not a superuser, and `passel_api`. The `db-migrate` command always runs as `passel_owner`; the default privileges it grants apply only to tables later created by the same login, so it must be the same login every time. Should `passel_owner` turn out to lack a privilege the migration needs, the migration runs as the superuser instead, under the same rule. The API connects as `passel_api`, which the migration makes a member of `passel_app` and which therefore has only DML.

**The proxy.** Neither the API nor the web app publishes a port. By default a bundled Caddy (the `edge` profile) takes ports 80 and 443, gets certificates with ACME, and is the only address the API trusts forwarded headers from: it has a fixed address on the stack's `edge` network, and the API sets `ForwardedHeaders:Enabled` with that address as its one known network, never `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. A server that already runs a proxy uses an override file instead: one joins the proxy's own Docker network, another binds the API and the web app to the loopback for a proxy on the host. The server's `.env` names the files and profiles, so every compose command sees the same stack. Compose 2.24 or later is required.

**Deploying.** A manual workflow, `DeployCompose`, runs in the `production` environment with its own concurrency group, `deploy-compose`, beside the Azure workflow's `deploy-webapi`. It checks that all three images exist at the tag, then connects over SSH with a key whose `authorized_keys` entry forces one command, a gate script that accepts only `deploy <tag>` and optionally `--no-migrate`, checks the tag against `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`, reads a registry token from standard input into a temporary Docker configuration that it removes afterwards, pulls the deploy image, copies its files to `releases/<tag>/` and runs that release's deploy script. The tag is checked in the build, in the gate and in the script. The script runs the one-off services in order (the roles, the parcels directory, the migration unless told not to), then starts the API and the web app and waits for them to be healthy, then checks `/health` through the proxy. Only after that does it record the release; a failed deploy prints the migration's and the API's logs and leaves the record as it was. The server keeps the current and previous release and an append-only log of releases.

**Rolling back.** A rollback is the same workflow with an older tag and the migration off, which brings back that release's images together with that release's compose files. That only works if the previous release runs against the newer schema, so a release may add columns only when they are nullable or have a default; the migration never drops one.

**Settings.** The API and the migration get an explicit environment in the compose file, never an `env_file`, with `ASPNETCORE_ENVIRONMENT` and `DOTNET_ENVIRONMENT` both `Production`. The Azure settings stay unset: `APPLICATIONINSIGHTS_CONNECTION_STRING`, every `AZURE_*` variable, `Database:ApiRoleObjectId`, `Authentication:Google:ClientSecret` and `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. Passwords are generated as hex, which needs no quoting in a connection string or in compose; the Sqids alphabet is quoted in `.env`, and must equal the one any earlier deployment used.

**Health and hardening.** The API's container is checked with a plain HTTP request to `/health` from inside it, with `localhost` as the host, which `AllowedHosts` lists; if the image has no shell to make it, the container goes without a health check and the deploy script's check through the proxy stands in. The API and the web app run without new privileges and with every capability dropped. Every service's logs rotate at 10 MB, five files.

**Backups.** A script run by cron on the server dumps the database first, in PostgreSQL's custom format, with its roles alongside, and then archives the parcels volume. Shipping the backups off the server is the owner's choice of tool.

**Azure.** The Azure code stays where it is: the AppHost's Azure hosting, the Azure blob store, the `Deploy` workflow and its concurrency group. Nothing removes it in this change.

## Answers to "all by hand"

- **TLS:** the bundled Caddy gets and renews certificates by itself, or the server's existing proxy keeps doing it.
- **Patching:** `unattended-upgrades` patches the server. Dependabot updates the dependencies, and every build of `main` rebuilds the images from fresh base images, so a deploy picks up patched images too. PostgreSQL minor versions arrive with the image; a major version is a dump and a restore.
- **Backups:** the backup script and the restore drill in the deploying guide; the database and the parcels volume are consistent only as a pair.
- **Deployment:** the manual workflow, with GitHub's run history as the audit trail and the `production` environment's protection rules in front of it.

## Consequences

- One machine is a single point of failure. That is accepted: the API already ran one replica, because its rate limits, stream caps and upload gates count per instance.
- The owner runs the server: its updates, its disk, its backups and its restore drills.
- The web app is built once per environment, since the API's address is compiled into it.
- The parcels volume and the database must be backed up and restored together.
- New columns must be nullable or have a default, so a rollback keeps working.
- The deploy key can run Docker, which is as good as root on the server. The forced command is what limits it to deploying a validated tag.

## Follow-ups

- Removing the Azure path once the server has run for a while.
- An S3-compatible parcel store, should the files outgrow one disk.
- How a staging environment would be named and built, given one web image per environment.
