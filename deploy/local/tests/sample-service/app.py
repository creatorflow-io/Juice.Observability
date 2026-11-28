"""A non-.NET sample service for Juice Observability (Phase 12 R8).

Plain Flask and the stock OpenTelemetry SDK, started under `opentelemetry-instrument`: traces for each request, and four metrics of
its own, one of each type the platform can alert on. It generates its own orders, so there is data without anyone calling it.

    sample.queue.depth       gauge                      orders waiting
    sample.orders.processed  counter (monotonic sum)    orders finished, by result
    sample.inflight          up-down counter            orders being processed now
    sample.order.duration    histogram, seconds         time to process one order
"""
import os
import random
import threading
import time

from flask import Flask, jsonify
from opentelemetry import metrics, trace

app = Flask(__name__)
meter = metrics.get_meter("sample.orders")
tracer = trace.get_tracer("sample.orders")

_queue_depth = 0
_lock = threading.Lock()


def _observe_queue_depth(options):
    yield metrics.Observation(_queue_depth)


meter.create_observable_gauge("sample.queue.depth", callbacks=[_observe_queue_depth], unit="{order}", description="Orders waiting")
processed = meter.create_counter("sample.orders.processed", unit="{order}", description="Orders finished")
inflight = meter.create_up_down_counter("sample.inflight", unit="{order}", description="Orders being processed now")
duration = meter.create_histogram(
    "sample.order.duration", unit="s", description="Time to process one order",
    explicit_bucket_boundaries_advisory=[0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5],
)


def process_order():
    """One order through the queue: waits, is processed, finishes (rarely failing)."""
    global _queue_depth
    with _lock:
        _queue_depth += 1
    with tracer.start_as_current_span("process-order"):
        with _lock:
            _queue_depth -= 1
        inflight.add(1)
        started = time.monotonic()
        try:
            time.sleep(random.uniform(0.02, 0.4))
            result = "failed" if random.random() < 0.05 else "ok"
        finally:
            inflight.add(-1)
        duration.record(time.monotonic() - started, {"result": result})
        processed.add(1, {"result": result})
    return result


def generate():
    """A steady trickle of orders, with a burst now and then so the queue depth moves."""
    while True:
        burst = random.randint(8, 25) if random.random() < 0.1 else 1
        for _ in range(burst):
            threading.Thread(target=process_order, daemon=True).start()
        time.sleep(random.uniform(0.5, 2.0))


@app.get("/orders")
def orders():
    return jsonify({"queued": _queue_depth})


@app.post("/orders")
def create_order():
    return jsonify({"result": process_order()}), 201


@app.get("/healthz")
def healthz():
    return "ok"


if __name__ == "__main__":
    threading.Thread(target=generate, daemon=True).start()
    app.run(host="0.0.0.0", port=int(os.environ.get("PORT", "8080")))
