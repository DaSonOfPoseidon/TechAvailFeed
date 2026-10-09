#!/bin/sh
# Read-only check of the .NET mailbox code against the live mailbox (EXAMINE and BODY.PEEK only):
# every processed mail must match the copy the ingest archived in corpus/mail.
set -e
cd "$(dirname "$0")/.."
scripts/dotnet.sh build tools/TechAvail.ImapCheck >/dev/null
exec docker run --rm -i --label monitoring.ignore=true --label wud.watch=false \
	--env-file .env -e DATABASE_URL=unused \
	-v "$PWD:/src" -w /src -v techavail-nuget:/root/.nuget/packages \
	-e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
	mcr.microsoft.com/dotnet/sdk:10.0 dotnet run --no-build --project tools/TechAvail.ImapCheck -- corpus
