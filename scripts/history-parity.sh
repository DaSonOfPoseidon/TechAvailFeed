#!/bin/sh
# Compares the outcome history of the Python code and the .NET port on a copy of the production
# database (read-only there): both finalize every final day from scratch into their own copy, then
# compute every day from the first snapshot to today. Output stays in the gitignored corpus/.
set -e
cd "$(dirname "$0")/.."
scripts/test-db.sh up >/dev/null
for db in hist_py hist_net; do
	docker exec techavail-testdb psql -U postgres -q -c "DROP DATABASE IF EXISTS $db" -c "CREATE DATABASE $db" 2>/dev/null
	docker compose exec -T postgres pg_dump -U techavail --no-owner --no-acl techavail |
		docker exec -i techavail-testdb psql -U postgres -q -d $db >/dev/null
	docker exec techavail-testdb psql -U postgres -q -d $db -c "TRUNCATE outcome_days CASCADE"
done
python="docker run --rm --label monitoring.ignore=true --label wud.watch=false --network techavail-test
	-v $PWD/tools:/app/tools -v $PWD/feed:/app/feed -v $PWD/corpus:/app/corpus
	techavailfeed-ingest python -m tools.replay"
dotnet="scripts/dotnet.sh run --project tools/TechAvail.Parity --"
server=postgresql://postgres:test@techavail-testdb
net="Host=techavail-testdb;Username=postgres;Password=test;Database=hist_net"
start=2026-01-01
end=$(date +%F)
echo "python: $($python finalize $server/hist_py)"
echo ".net:   $($dotnet finalize "$net")"
$python compare-outcomes $server/hist_py $server/hist_net
echo "python: $($python history $server/hist_py $start $end corpus/history_py.json)"
echo ".net:   $($dotnet history "$net" $start $end corpus/history_net.json)"
$python diff corpus/history_py.json corpus/history_net.json
