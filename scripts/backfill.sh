#!/bin/sh
# Backfills feed runs ingest refused for arriving late (see tools/TechAvail.Backfill). A dry run unless
# --apply; take a pg_dump and stop ingest (docker compose stop ingest) before applying.
set -e
cd "$(dirname "$0")/.."
scripts/dotnet.sh build tools/TechAvail.Backfill >/dev/null
# DATABASE_URL in .env (an external Postgres) wins; otherwise the compose postgres service.
url=$(grep '^DATABASE_URL=' .env | cut -d= -f2-) || true
[ -n "$url" ] || url="postgresql://techavail:$(grep '^POSTGRES_PASSWORD=' .env | cut -d= -f2-)@postgres:5432/techavail"
exec docker run --rm -i --label monitoring.ignore=true --label wud.watch=false \
	--network techavailfeed_default --memory 1g --memory-swap 1g \
	--env-file .env -e DATABASE_URL="$url" \
	-v "$PWD:/src" -w /src -v techavail-nuget:/root/.nuget/packages \
	-e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
	mcr.microsoft.com/dotnet/sdk:10.0 dotnet run --no-build --project tools/TechAvail.Backfill -- "$@"
