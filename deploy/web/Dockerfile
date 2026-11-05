# The web app's static site behind Caddy, plain HTTP on 8080 inside the compose network; TLS ends at the edge proxy.
# PublishImages builds it from a staged context: this file, the Caddyfile and BuildWebApp's site as site/.
FROM caddy:2.10-alpine

# The base image gives caddy the capability to bind ports below 1024, which this image never does. A binary with a
# file capability cannot start once a container drops every capability (cap_drop: ALL), so it is removed
RUN setcap -r /usr/bin/caddy

COPY Caddyfile /etc/caddy/Caddyfile
COPY site/ /srv/

# Any user but root: the site is read only, and nothing is written at runtime
USER 1000:1000

EXPOSE 8080

LABEL org.opencontainers.image.source="https://github.com/DecSmith42/passel" \
      org.opencontainers.image.url="https://github.com/DecSmith42/passel" \
      org.opencontainers.image.documentation="https://github.com/DecSmith42/passel/blob/main/docs/components/web-app.md" \
      org.opencontainers.image.vendor="Invex Games" \
      org.opencontainers.image.licenses="MIT AND Apache-2.0" \
      org.opencontainers.image.title="passel-web" \
      org.opencontainers.image.description="The Passel web app and its landing, privacy and terms pages, served by Caddy"
