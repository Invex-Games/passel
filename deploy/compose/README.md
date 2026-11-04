# Docker Compose deployment

The compose files and scripts of the self-hosted deployment (ADR 0004). Each build of `main` packs them into the
`passel-deploy` image, tagged like the API and web images, and the `DeployCompose` workflow deploys a tag through
`deploy-gate.sh` on the server.

See [Releasing and Deploying](../../docs/guides/deploying.md) for the server setup, deploying, rolling back,
backups and troubleshooting.
