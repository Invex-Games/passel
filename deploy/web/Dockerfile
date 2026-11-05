# The web app's static site behind Caddy, plain HTTP on 8080 inside the compose network; TLS ends at the edge proxy.
# PublishImages builds it from a staged context: this file, the Caddyfile, entrypoint.sh and BuildWebApp's site as
# site/. The site holds no environment's values: entrypoint.sh writes the app's configuration when the container
# starts, from API_ORIGIN and GOOGLE_CLIENT_ID, so one image serves every environment.
FROM caddy:2.10-alpine

# The base image gives caddy the capability to bind ports below 1024, which this image never does. A binary with a
# file capability cannot start once a container drops every capability (cap_drop: ALL), so it is removed.
# /run/passel-web is the one directory the container writes to: the app's config.json, owned by the runtime user so
# that no capability is needed to write it. A read-only container needs a tmpfs there (uid=1000)
RUN setcap -r /usr/bin/caddy \
    && mkdir -p /run/passel-web \
    && chown 1000:1000 /run/passel-web \
    && chmod 755 /run/passel-web

COPY Caddyfile /etc/caddy/Caddyfile
COPY --chmod=755 entrypoint.sh /usr/local/bin/passel-web-entrypoint
COPY site/ /srv/

# Any user but root: the site stays root's and read only to it
USER 1000:1000

EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/passel-web-entrypoint"]
CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]

LABEL org.opencontainers.image.source="https://github.com/Invex-Games/passel" \
      org.opencontainers.image.url="https://github.com/Invex-Games/passel" \
      org.opencontainers.image.documentation="https://github.com/Invex-Games/passel/blob/main/docs/components/web-app.md" \
      org.opencontainers.image.vendor="Invex Games" \
      org.opencontainers.image.licenses="MIT AND Apache-2.0" \
      org.opencontainers.image.title="passel-web" \
      org.opencontainers.image.description="The Passel web app and its landing, privacy and terms pages, served by Caddy"
