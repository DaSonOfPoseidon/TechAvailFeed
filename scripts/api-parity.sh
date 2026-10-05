#!/bin/sh
# Compares every API response of the Python service and the .NET port on a copy of the production
# database (read-only there), at the same fixed clock. Output stays in the gitignored corpus/.
set -e
cd "$(dirname "$0")/.."
scripts/test-db.sh up >/dev/null
docker exec techavail-testdb psql -U postgres -q -c "DROP DATABASE IF EXISTS api_copy" -c "CREATE DATABASE api_copy" 2>/dev/null
docker compose exec -T postgres pg_dump -U techavail --no-owner --no-acl techavail |
	docker exec -i techavail-testdb psql -U postgres -q -d api_copy >/dev/null
echo "python: $(uv run python -m tools.api_golden postgresql://postgres:test@127.0.0.1:5439/api_copy corpus/api_py.json)"
echo ".net:   $(scripts/dotnet.sh run --project tools/TechAvail.Parity -- api \
	"Host=techavail-testdb;Username=postgres;Password=test;Database=api_copy" corpus/api_py.json corpus/api_net.json)"
status=0
uv run python -m tools.replay diff corpus/api_py.json corpus/api_net.json || status=1
for n in $(ls corpus | sed -n 's/^api_py_export\([0-9]*\)\.xlsx$/\1/p'); do
	# The export time is the wall clock in Python, not its injectable clock.
	echo "export $n: $(uv run python -m tools.compare_xlsx corpus/api_py_export$n.xlsx corpus/api_net_export$n.xlsx 'About!B1' || echo DIFFERENT)"
done
exit $status
