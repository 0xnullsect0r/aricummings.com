# Web image. Build from the repository root:
#   docker build -f deploy/web-image/Dockerfile .

FROM node:24-bookworm-slim AS build
WORKDIR /src

COPY apps/web/package.json apps/web/
# npm ci when a lockfile is committed; otherwise a plain install.
COPY apps/web/package-lock.json* apps/web/bun.lock* apps/web/
RUN cd apps/web && \
    if [ -f package-lock.json ]; then npm ci --no-audit --no-fund; \
    else npm install --no-audit --no-fund; fi

COPY apps/web apps/web
RUN cd apps/web && npm run build

FROM node:24-bookworm-slim
RUN groupadd -g 1000 site && useradd -u 1000 -g 1000 site

ARG SITE_RELEASE=dev
LABEL site.release=$SITE_RELEASE
LABEL org.opencontainers.image.source=https://github.com/0xnullsect0r/aricummings.com

ENV NODE_ENV=production \
    PORT=3000 \
    HOSTNAME=0.0.0.0 \
    ARI_API_INTERNAL=http://api:8082 \
    NEXT_TELEMETRY_DISABLED=1

WORKDIR /app
COPY --from=build --chown=site:site /src/apps/web/.next/standalone ./
COPY --from=build --chown=site:site /src/apps/web/.next/static ./.next/static
COPY --from=build --chown=site:site /src/apps/web/public ./public

USER site
EXPOSE 3000

# No curl in this image; the health check uses Node's own fetch.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD node -e "fetch('http://127.0.0.1:3000/').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"

CMD ["node", "server.js"]
