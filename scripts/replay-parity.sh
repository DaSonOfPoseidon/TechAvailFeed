#!/bin/sh
# Replays the local corpus through the Python ingest and the .NET port into two fresh databases
# on the test Postgres (scripts/test-db.sh up), then compares the tables row by row.
# Prints counts only. MAIL_FROM is read from .env unless set.
set -e
cd "$(dirname "$0")/.."
root="${1:-corpus}"
: "${MAIL_FROM:=$(grep '^MAIL_FROM=' .env | cut -d= -f2-)}"
export MAIL_FROM
scripts/test-db.sh up >/dev/null
for db in replay_py replay_net; do
	docker exec techavail-testdb psql -U postgres -q -c "DROP DATABASE IF EXISTS $db" -c "CREATE DATABASE $db" 2>/dev/null
done
python="docker run --rm --label monitoring.ignore=true --label wud.watch=false --network techavail-test
	-e MAIL_FROM -v $PWD/tools:/app/tools -v $PWD/feed:/app/feed -v $PWD/$root:/app/$root
	techavailfeed-ingest python -m tools.replay"
server=postgresql://postgres:test@techavail-testdb
echo "python: $($python replay "$root" $server/replay_py)"
echo ".net:   $(scripts/dotnet.sh run --project tools/TechAvail.Parity -- replay "$root" \
	"Host=techavail-testdb;Username=postgres;Password=test;Database=replay_net")"
$python compare $server/replay_py $server/replay_net
