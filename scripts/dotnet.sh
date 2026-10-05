#!/bin/sh
# Runs the .NET SDK in a container so the host needs only Docker. NuGet packages are cached in
# a named volume. MAIL_FROM / AUTHSERV_ID are passed through for the parity tool when set.
set -e
cd "$(dirname "$0")/.."
exec docker run --rm -i \
	-v "$PWD:/src" -w /src \
	-v techavail-nuget:/root/.nuget/packages \
	-e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
	-e MAIL_FROM -e AUTHSERV_ID \
	mcr.microsoft.com/dotnet/sdk:10.0 dotnet "$@"
