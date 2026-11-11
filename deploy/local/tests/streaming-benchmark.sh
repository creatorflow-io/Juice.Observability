#!/usr/bin/env bash
# The Phase 9 streaming benchmark (docs/phase-9-plan.md): scheduled evaluation with 50 rules and 50 SLOs at the reference load.
#   tests/streaming-benchmark.sh            about 50 minutes; the report goes to build/benchmark/
#   BREAKS=2 tests/streaming-benchmark.sh   a smoke run of the harness (about 15 minutes, no 200-SLO read)
# Runs tests/load.sh (the reference load through the test Collector) and, beside it, StreamingBenchmark, which hosts the whole platform on
# a scratch database reading the same ClickHouse tenant. Needs docker compose --profile test.
set -uo pipefail
cd "$(dirname "$0")/.."
root=$(cd ../.. && pwd)
BREAKS=${BREAKS:-20}
LOAD=$([[ "$BREAKS" == 20 ]] && echo 55m || echo 20m)
out="$root/build/benchmark"
mkdir -p "$out"
stamp=$(date -u +%Y%m%dT%H%M)

label="streaming-benchmark-$stamp-$$"
LOAD_LABEL=$label tests/load.sh "$LOAD" > "$out/load-$stamp.log" 2>&1 &
load=$!
sleep 15   # the generators reach their rate
JUICE_BENCHMARK=1 JUICE_BENCHMARK_BREAKS=$BREAKS JUICE_BENCHMARK_OUT="$out/streaming-$stamp.md" \
  dotnet test "$root/test/Juice.Observability.Tests" --filter "FullyQualifiedName~StreamingBenchmark.Scheduled_evaluation" \
  --logger "console;verbosity=detailed" > "$out/benchmark-$stamp.log" 2>&1
result=$?
if (( result != 0 )); then
  # Failed: don't wait out the rest of the load run to say so.
  echo "benchmark failed ($result); stopping the load run"
  kill "$load" 2>/dev/null
  docker ps -q --filter "label=juice.load=$label" | xargs -r docker stop >/dev/null
else
  echo "benchmark finished; waiting for the load run to end"
fi
wait "$load"
echo
cat "$out/streaming-$stamp.md" 2>/dev/null || { echo "no report: see $out/benchmark-$stamp.log"; tail -40 "$out/benchmark-$stamp.log"; }
echo
echo "reference load:"
tail -4 "$out/load-$stamp.log"
exit $result
