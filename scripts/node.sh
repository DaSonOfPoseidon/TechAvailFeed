#!/bin/sh
# Runs Node in a container (the dashboard in web/ needs a newer Node than the host has).
# npm's cache is kept in a named volume. Usage: scripts/node.sh npm test
# The labels keep these throwaway containers out of monitoring alerts (ContainerGone) and WUD.
# ng build needs more than mem-guard's 1 GiB default, so the limit is set explicitly.
set -e
cd "$(dirname "$0")/.."
tty=""
[ -t 0 ] && tty="-t"
exec docker run --rm -i $tty \
	--label monitoring.ignore=true --label wud.watch=false \
	--memory 2g --memory-swap 2g \
	-v "$PWD:/src" -w /src/web \
	-v techavail-npm:/root/.npm \
	-e NG_CLI_ANALYTICS=false $NODE_DOCKER_ARGS \
	node:24-alpine "$@"
