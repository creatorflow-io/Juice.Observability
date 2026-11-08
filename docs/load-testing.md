# Load testing with the platform

A guide for teams that want to know how much traffic their services can take and what breaks first. The
[README](../README.md#load-test-runs) has the API and the exact limit conditions; this page explains the idea and how to use it well.

## The idea in one paragraph

The platform generates no load. You run a load test (k6, or any tool that can make HTTP calls) against your services, and the test
tells the platform when it started and when it stopped. That window is a **load run**. The Console's **Load runs** page then reads the
telemetry every service sent during the window and shows, step by step, how each one behaved and where it started to struggle.
Everyday traffic rarely pushes a service that far, so this is the only place those limits show up.

## Who does what

| Part | Responsibility | Lives in |
|---|---|---|
| **Load test** | Drives traffic at an entry point, and marks the run's start and end through the API | A load-test repo or the owning team's test folder, not the service's code |
| **Services under test** | Handle the traffic as usual and send telemetry, as every onboarded service already does | Nothing to add |
| **Platform** | Reads the window's telemetry, compares runs and finds each service's limit | `/load-runs` API and the Console's Load runs page |

A service doesn't implement anything for load testing. If it is instrumented, it shows up in the results of any run that sends it
traffic, including runs that only call it indirectly.

### What the results need from a service's telemetry

| Figure on the page | Needs | If it's missing |
|---|---|---|
| Load/s, p95, errors | Server and consumer spans (ASP.NET Core and messaging instrumentation) | The service doesn't appear |
| Consumer busy | Consumer spans | Shows 0% |
| Outbox backlog | Messaging spans carrying `messaging.message.id` (Juice outbox and messaging instrumentation) | Shows 0 |
| Thread pool queue | The `dotnet.thread_pool.queue.length` runtime metric | Shows "—" |

Figures are counted from spans. If traces are sampled, load and errors read low, so turn sampling off (or up) in the environment you
test.

## Test a flow, not a service

One run captures every service the traffic touches. A test that calls `POST /orders` on the gateway also shows the order service,
the payment consumer behind the outbox, and whatever they call. The page lists first the services that hit a limit, and that's often
not the one the test called.

So write tests per **flow that matters**: checkout, search, a bulk import, a login storm. Each answers: how much of this traffic can
we take, and what gives way first? Write a test aimed at a single service only for a question about that service alone, such as how
many messages per second one consumer can handle.

## Writing a test that gives useful results

- **Ramp up the load.** The page finds a limit by comparing the quiet part of a run with the busy part, and the quietest quarter
  never counts as a limit. A flat load gives it little to compare. Use k6 `stages` that climb past where you expect trouble.
- **Run long enough.** Results are read in whole time steps of 15 s up to 1 h, depending on the run's length. A run needs at least 4
  steps with traffic before a limit can be found, and a run shorter than one step shows nothing. Several minutes per stage is a good
  start.
- **Use a dedicated environment.** Pass `environment` when starting the run, so traffic from elsewhere isn't mixed in. Don't load test
  production.
- **Keep a stable name.** Reusing a name such as `checkout-ramp` lets you tick runs from before and after a change and compare them
  side by side (up to 4 at a time).
- **Always end the run.** End it in k6's `teardown`. A run that's never ended is read up to now, which mixes in whatever came after.
  Ending it twice is harmless.
- **Send an `Idempotency-Key` when starting**, so a retried start returns the same run instead of creating a second one.

Starting and ending a run needs a token with the **operator** or **admin** role. Both are audited.

### Example (k6)

```javascript
import http from 'k6/http';

const runs = `${__ENV.PLATFORM_URL}/api/v1/tenants/${__ENV.TENANT_ID}/load-runs`;
const headers = { Authorization: `Bearer ${__ENV.PLATFORM_TOKEN}`, 'Content-Type': 'application/json' };

export const options = {
  stages: [
    { duration: '3m', target: 20 },   // quiet: the baseline the rest is compared with
    { duration: '5m', target: 100 },
    { duration: '5m', target: 300 },  // past where we expect trouble
  ],
};

export function setup() {
  const body = JSON.stringify({ name: 'checkout-ramp', environment: 'staging' });
  const key = `checkout-ramp-${Date.now()}`;
  return http.post(runs, body, { headers: { ...headers, 'Idempotency-Key': key } }).json();
}

export default function () {
  http.post(`${__ENV.TARGET_URL}/orders`, JSON.stringify({ sku: 'demo', quantity: 1 }), { headers: { 'Content-Type': 'application/json' } });
}

export function teardown(run) {
  http.post(`${runs}/${run.id}/end`, null, { headers });
}
```

## Reading the results

Tick one or more runs on the **Load runs** page. For each service you get:

- **Peak load/s, with p95 and error rate at the peak.**
- **The limit**, if one was reached: why (latency, consumer busy, thread pool queue, errors or outbox backlog), from what load, and
  the value at that point. The load quoted is the highest reached so far, since a struggling service's own rate may already be falling.
- **Step-by-step figures** under each run, with the limit step highlighted.
- **Open the window**, which opens the service's pages for exactly that run's time range, to look at traces and logs from the moment
  it started to struggle.

What each limit usually means:

| Limit | Usually points to |
|---|---|
| `latency` | A slow dependency, lock contention or CPU running out |
| `consumer-busy` | The consumer can't keep up. Messages wait in the broker, which isn't in any span, so latency looks fine. Add consumers or make handling cheaper |
| `queue` | Thread pool starvation, often blocking calls (`.Result`, `.Wait()`) or synchronous I/O |
| `errors` | Timeouts, pool exhaustion or a dependency refusing work |
| `backlog` | The outbox is written faster than it's delivered |

"No limit reached" means none of these held for two steps in a row. It doesn't mean there's no limit, only that this run didn't find
it. Push harder.

## What it doesn't measure

- The broker's own queue depth and the database aren't measured directly. You see their effect on the services, not their state.
- The outbox backlog includes messages from rolled-back transactions.
- Figures from one machine only roughly carry over to another. Compare runs from the same environment.
