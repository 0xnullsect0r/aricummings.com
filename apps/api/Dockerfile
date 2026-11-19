# API image. Build from the repository root:
#   docker build -f apps/api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore with only the project files first, so source changes don't re-restore.
COPY apps/api/AriApi.csproj apps/api/
COPY apps/api.tests/api.tests.csproj apps/api.tests/
COPY apps/Ari.slnx apps/
RUN dotnet restore apps/Ari.slnx

COPY apps/api apps/api
COPY apps/api.tests apps/api.tests
RUN dotnet publish apps/api/AriApi.csproj -c Release -o /app /p:UseAppHost=false

# Seed photos, keyed on the manifest so a code change is the only thing that re-encodes.
COPY apps/api/Seed /seed-input
RUN if [ -f /seed-input/media-manifest.json ]; then \
      cd /app && dotnet Ari.dll build-media --source /seed-input --data /seed-out; \
    else mkdir -p /seed-out; \
    fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl tzdata sqlite3 \
    && rm -rf /var/lib/apt/lists/*

ARG SITE_RELEASE=dev
LABEL site.release=$SITE_RELEASE
LABEL org.opencontainers.image.source=https://github.com/0xnullsect0r/aricummings.com

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS= \
    ARI_ENV=production \
    ARI_PUBLIC_PORT=8080 \
    ARI_INTERNAL_PORT=8082 \
    ARI_DATA_DIR=/data \
    ARI_BACKUP_DIR=/backups \
    DOTNET_EnableDiagnostics=0 \
    HOME=/tmp

WORKDIR /app
COPY --from=build /app .
COPY --from=build /seed-out /app/Seed/media

RUN mkdir -p /data/media /backups && chown -R 1000:1000 /data /backups /app

USER 1000:1000
EXPOSE 8080
# 8081 (admin) and 8082 (internal) are exposed only when ADMIN_PORT is set; compose decides.

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS "http://127.0.0.1:${ARI_PUBLIC_PORT:-8080}/api/health" || exit 1

ENTRYPOINT ["dotnet", "Ari.dll"]
