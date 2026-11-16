# Shared by the drills (tests/upgrade-drill.sh, tests/dr-drill.sh, tests/retention-drill.sh; docs/drills.md): logging and checks, the
# stack's ports, the drill's project and worktree, JSON answers, the viewer's token. Sourced after the drill sets root, tree and log.

failures=()
say() { echo "$(date -u +%H:%M:%S) $*" | tee -a "$log"; }
# check "<what>" '<test>': the test is evaluated; a failure is recorded, and the drill exits 1 at its end.
check() { if eval "$2"; then say "ok: $1"; else say "FAILED: $1"; failures+=("$1"); fi; }
# A setting from the .env in the current directory (the stack's).
env_of() { sed -n "s/^$1=//p" .env; }
# A count read from a store: a number, or the drill can't judge it (a failed query prints nothing, which must never read as 0).
is_count() { [[ "$1" =~ ^[0-9]+$ ]]; }
# A tab-separated file sorted on its first column, as join wants it, whatever the locale.
keyed() { LC_ALL=C sort -t $'\t' -k1,1 "$1"; }

# json '<JavaScript function of the parsed input>': what it returns, from JSON on stdin; nothing when the input isn't JSON or it throws.
json() {
  node -e 'let j=""; process.stdin.on("data", d => j += d).on("end", () => {
    try { const v = eval(process.argv[1])(JSON.parse(j)); console.log(v ?? ""); } catch { console.log(""); } })' "$1"
}

# The host ports the stack publishes, from the compose file in the current directory (with the profiles given).
stack_ports() {
  docker compose "$@" config --format json 2>/dev/null \
    | json 'c => [...new Set(Object.values(c.services).flatMap(s => (s.ports || []).map(p => p.published)).filter(Boolean))].join(" ")'
}

# require_free_ports "<ports>" [project...]: every port free, but those held by the named compose projects' containers (the drill stops or
# removes those next). Exits 2 when something else holds one, before the drill changes anything.
require_free_ports() {
  local ports=$1 port p held; shift
  [[ -n "$ports" ]] || { say "can't read the stack's ports from docker-compose.yml"; exit 1; }
  for port in $ports; do
    held=false
    for p in "$@"; do [[ -n "$(docker ps -q --filter "label=com.docker.compose.project=$p" --filter "publish=$port")" ]] && held=true; done
    $held && continue
    if timeout 1 bash -c "exec 3<>/dev/tcp/127.0.0.1/$port" 2>/dev/null; then say "port $port is in use by something else: stop it first"; exit 2; fi
  done
}

# The drill's project and its volumes gone (an earlier run's, interrupted or kept): every profile's containers, then any volume left.
remove_project() {
  docker compose -p "$1" --profile platform --profile test --profile grafana --profile agent down -v --remove-orphans >> "$log" 2>&1
  docker volume ls -q --filter "label=com.docker.compose.project=$1" | xargs -r docker volume rm >> "$log" 2>&1
}

# The worktree removed from outside it (a directory in use can't be removed on Windows); says so only once it is gone.
remove_tree() {
  cd "$root" || return
  git -C "$root" worktree remove --force "$tree" >> "$log" 2>&1
  if [[ -e "$tree" ]]; then say "the worktree $tree is left: remove it, then git worktree prune"; else say "the drill's project, volumes and worktree removed"; fi
}

# The demo viewer's token from the stack's IdP (the password given), and the id of the tenant with the key given (the deployment's).
viewer_token() {
  curl -s http://localhost:8080/realms/juice/protocol/openid-connect/token -d grant_type=password -d client_id=juice-platform-cli \
    -d username=victor --data-urlencode "password=$1" | json 'r => r.access_token'
}
tenant_id() {
  curl -s -H "Authorization: Bearer $1" http://localhost:5180/api/v1/tenants | KEY=$2 json 'p => p.items.find(t => t.key === process.env.KEY)?.id'
}
# How many items an API page holds (0 when it isn't one).
page_items() { local n; n=$(json 'p => p.items.length'); echo "${n:-0}"; }
