#!/bin/sh
# Runs the .NET SDK in a container so the host needs only Docker. NuGet packages are cached in
# a named volume. UPDATE_SNAPSHOTS=1 rewrites the fixture snapshots (tests/fixtures/*.json).
# The labels keep these throwaway containers out of monitoring alerts (ContainerGone) and WUD.
# When scripts/test-db.sh is up, the container joins its network and the data tests use it.
set -e
cd "$(dirname "$0")/.."
network=""
if docker inspect techavail-testdb >/dev/null 2>&1; then
	network="--network techavail-test"
	: "${TEST_DATABASE_URL:=Host=techavail-testdb;Username=postgres;Password=test}"
	export TEST_DATABASE_URL
fi
exec docker run --rm -i \
	--label monitoring.ignore=true --label wud.watch=false \
	-v "$PWD:/src" -w /src \
	-v techavail-nuget:/root/.nuget/packages \
	-e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
	-e TEST_DATABASE_URL -e UPDATE_SNAPSHOTS $network \
	mcr.microsoft.com/dotnet/sdk:10.0 dotnet "$@"
