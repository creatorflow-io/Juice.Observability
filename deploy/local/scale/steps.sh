#!/usr/bin/env bash
# The scale steps (docs/phase-10-plan.md P3, milestone 3), one after another on the scale stack: 1×, 3×, 5×, 10×, then upward while every
# line still holds (15×, 20×, 30×, 40×). It stops after the first step that crosses a line (the limit; --through carries on past it,
# to see what runs out next), after a 1× miss (the reference must hold first, P7), or after a harness problem (the step says nothing
# about the platform: the generator itself fell short or wrote no summary, the sampler or the query mix failed, samples are missing,
# or there's no report). A refused send or a slow breach run is the platform's result, judged by the report's targets, not a harness
# problem. Then it takes the scale stack down and, with --restore, starts the working stack again, also when interrupted or failed.
#   scale/steps.sh [--restore] [--through] [multipliers...]      default: 1 3 5 10 15 20 30 40
#   CEILING=ingest|search|workers scale/steps.sh ...   a component ceiling (docs/phase-11-plan.md R19, see run.sh); for search the
#                                              steps are searches per second, for workers copies of the rules and SLOs. A 1× miss
#                                              doesn't stop a ceiling.
# Needs the working stack stopped (scale/stack.sh up refuses otherwise). Each step is about 35 minutes.
# Exit 0: every step held, or a limit was found. 1: a 1× miss, a harness problem, or the stacks not brought back as asked.
# 2: a wrong option. 130: interrupted (INT, TERM or HUP; the running step is stopped first).
set -uo pipefail
cd "$(dirname "$0")/.."
restore=false; through=false
while [[ "${1:-}" == --* ]]; do
  case "$1" in --restore) restore=true ;; --through) through=true ;; *) echo "unknown option $1" >&2; exit 2 ;; esac; shift
done
steps=("$@"); (( ${#steps[@]} )) || steps=(1 3 5 10 15 20 30 40)
source scale/lib.sh
root=$(cd ../.. && { pwd -W 2>/dev/null || pwd; })
stamp=$(date -u +%Y%m%dT%H%M)
log="$root/build/scale/steps-$stamp.log"
mkdir -p "$root/build/scale"
say() { echo "$(date -u +%H:%M:%S) $*" | tee -a "$log"; }
status=0; step_pid=""; started=false; starting=false

cleanup() {
  trap '' INT TERM HUP          # a second interrupt mustn't cut the clean-up short
  trap - EXIT
  # A running step is stopped first. A stack start is let finish (killing it could leave compose still starting containers),
  # so it can then be taken down whole.
  if [[ -n "$step_pid" ]]; then $starting || kill -TERM "$step_pid" 2>/dev/null; wait "$step_pid" 2>/dev/null; fi
  if $started; then
    bash scale/stack.sh down >> "$log" 2>&1 || { say "the scale stack did not come down: see $log"; (( status )) || status=1; }
  fi
  # Restored only when the start wasn't refused (exit 3): the working stack was then stopped, so it is started again.
  if $restore && $started; then
    docker compose --profile platform --profile test --profile grafana --profile agent up -d --wait --no-build >> "$log" 2>&1 \
      && say "working stack restarted" || { say "working stack did not restart: see $log"; (( status )) || status=1; }
  fi
  say "done (exit $status)"
  exit "$status"
}
trap cleanup EXIT
trap 'say "interrupted"; status=130; exit 130' INT TERM HUP

# Counted as started unless it refused (exit 3: the working stack is running), so a failed or interrupted start is still taken down.
started=true
starting=true
bash scale/stack.sh up >> "$log" 2>&1 &
step_pid=$!; wait "$step_pid"; up=$?; step_pid=""; starting=false
if (( up == 3 )); then started=false; say "the working stack is running: stop it first (see $log)"; status=1; exit 1; fi
(( up == 0 )) || { say "the scale stack didn't start: see $log"; status=1; exit 1; }
for m in "${steps[@]}"; do
  say "step ×$m"
  steplog="$root/build/scale/steps-$stamp-${m}x.log"
  # In the background, so a TERM or HUP to this script is handled at once and passed on to the step (run.sh traps it).
  bash scale/run.sh "$m" > "$steplog" 2>&1 &
  step_pid=$!
  wait "$step_pid"; result=$?; step_pid=""
  # The run's own directory, as run.sh named it (never an older run of the same multiplier).
  dir=$(sed -n 's/^scale step .* → //p' "$steplog" | head -1)
  kind=$(node -e '
    const [dir, steplog] = process.argv.slice(1);
    const { existsSync } = require("fs");
    try {
      if (!dir) throw 0;
      const s = require(dir + "/summary.json");
      const parts = ["sampler", "query-mix"].filter(k => !((s.meta || {}).skipped || []).includes(k) && (s.exits || {})[k] !== 0).map(k => k + " exited " + ((s.exits || {})[k] ?? "-"));
      const why = [s.harnessLimit && "the generator itself fell short", !existsSync(dir + "/generator.json") && "no generator summary",
        ...parts, s.lagCovered === false && "lag samples missing", s.acceptedCovered === false && "Collector samples missing"].filter(Boolean);
      console.log(why.length ? "harness problem: " + why.join(", ") : "");
    } catch { console.log("harness problem: no report (see " + steplog + ")"); }' "$dir" "$steplog" 2>/dev/null)
  say "step ×$m done: report exit $result${kind:+ ($kind)}${dir:+; $dir/report.md}"
  if [[ -n "$kind" ]]; then say "stopping on a $kind"; status=1; break; fi
  if (( result != 0 )); then
    if [[ "$m" == 1 && -z "${CEILING:-}" ]]; then say "stopping: 1× missed a target (P7: fix or ADR before going on)"; status=1; break; fi
    say "×$m crossed a line"
    $through || { say "stopping: the limit"; break; }
  fi
done
