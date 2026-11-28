# Host Collector

One OpenTelemetry Collector per machine you want the HostMemory, HostCpu and HostDisk rules to cover. It scrapes the machine's memory, CPU and
filesystems (the `hostmetrics` receiver) and forwards them over OTLP/gRPC to the platform's Collector, which stamps the tenant. The config is
`config.yaml`; the pinned image is `otel/opentelemetry-collector-contrib:0.163.1`.

## Run it

1. On the platform Collector, add a token for this machine as a line in `secrets/otlp-tokens` (`<token> <comment>`) and restart that Collector.
2. On the machine, put only that token in a file, and copy the platform's CA (`secrets/tls/ca.crt`) next to it.
3. Start the container, with the machine's root filesystem mounted **read-only**:

```bash
docker run -d --name juice-host-collector --restart unless-stopped \
  -e JUICE_OTLP_ENDPOINT=https://<platform-collector>:4317 \
  -e JUICE_OTLP_TOKEN_FILE=/etc/juice/otlp-token \
  -e JUICE_OTLP_CA_FILE=/etc/juice/ca.crt \
  -e JUICE_HOST_NAME="$(hostname)" \
  -v "$PWD/config.yaml:/etc/otelcol-contrib/config.yaml:ro" \
  -v "$PWD/otlp-token:/etc/juice/otlp-token:ro" \
  -v "$PWD/ca.crt:/etc/juice/ca.crt:ro" \
  -v /:/hostfs:ro \
  otel/opentelemetry-collector-contrib:0.163.1 --config=/etc/otelcol-contrib/config.yaml
```

It runs as the image's non-root user and writes nothing to the host. `-v /:/hostfs:ro` makes the mount read-only all the way down (each
mount under `/`, not just the top one) only on Docker 25 or later with Linux kernel 5.12 or later; on older ones the submounts stay writable
(to the container's user, who owns nothing there), so check `docker version` and `uname -r`. On Docker Desktop the root includes the Windows
drives, as the platform Collector's own `/:/hostfs:ro` mount does; their usage is reported like any disk. Without Docker, run `otelcol-contrib --config config.yaml` with
`JUICE_HOST_ROOT=/` instead of the mount.

| Variable | |
|---|---|
| `JUICE_OTLP_ENDPOINT` | the platform Collector's OTLP/gRPC address |
| `JUICE_OTLP_TOKEN_FILE` | the file with this machine's bearer token |
| `JUICE_OTLP_CA_FILE` | the CA that signed the platform Collector's certificate |
| `JUICE_HOST_ROOT` | the root filesystem as the Collector sees it (default `/hostfs`) |
| `JUICE_HOST_NAME` | the `host.name` to report. Set it: inside a container the operating system's name is the container id. It should be the name a Juice Agent on that machine reports, so the two link. Instead of setting it, `--uts=host` on `docker run` gives the container the host's own name |

## What it sends

| Scraper | Metrics the platform reads |
|---|---|
| `memory` | `system.memory.usage` |
| `cpu` | `system.cpu.time`, `system.cpu.utilization` |
| `filesystem` | `system.filesystem.usage`, `system.filesystem.utilization` |

The filesystem scraper leaves out the pseudo and virtual types (`tmpfs`, `overlay`, `proc`, `sysfs`, `squashfs`, `devtmpfs`, `cgroup`, and the
like) and the mounts `/proc`, `/sys`, `/dev`, `/run`, `/snap` and `/var/lib/docker`, so the HostDisk rule, which watches the fullest read-write
mount, is not tripped by a mount that is always full. The rule itself ignores read-only mounts and those types too, whatever sends them.
To leave out another mount, add it to `exclude_mount_points`.

Run one per host, never on a host the platform Collector already scrapes (it would be counted twice). The local stack (`deploy/local`) has one in the opt-in profile `host` (`docker compose --profile host up -d host-collector`), sending to its own Collector with a token of its own (`secrets/host-collector-otlp-token`, written by `scripts/init.sh`, which also adds it to `otlp-tokens`).
