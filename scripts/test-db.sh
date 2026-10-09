#!/bin/sh
# A throwaway Postgres for the .NET data tests, on a private network that scripts/dotnet.sh joins.
# Labelled so monitoring and WUD ignore it, and not published on the host. Each test creates and
# drops its own database in it.
#   scripts/test-db.sh up | down
set -e
case "$1" in
up)
	docker network inspect techavail-test >/dev/null 2>&1 || docker network create techavail-test >/dev/null
	docker inspect techavail-testdb >/dev/null 2>&1 || docker run -d --name techavail-testdb \
		--label monitoring.ignore=true --label wud.watch=false \
		--network techavail-test --memory 512m --memory-swap 512m \
		-e POSTGRES_PASSWORD=test postgres:16-alpine >/dev/null
	until docker exec techavail-testdb pg_isready -U postgres >/dev/null 2>&1; do sleep 1; done
	echo "TEST_DATABASE_URL=Host=techavail-testdb;Username=postgres;Password=test"
	;;
down)
	docker rm -f techavail-testdb >/dev/null 2>&1 || true
	docker network rm techavail-test >/dev/null 2>&1 || true
	;;
*)
	echo "usage: $0 up|down" >&2
	exit 2
	;;
esac
