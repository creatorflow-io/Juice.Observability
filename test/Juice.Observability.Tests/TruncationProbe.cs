using System.Diagnostics.Metrics;

namespace Juice.Observability.Tests
{
    /// <summary>Counts <c>juice.observability.rules.truncated</c> by template while it is alive (other tests in the process may add to it too, so assert at least).</summary>
    public sealed class TruncationProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _counts = [];

        public TruncationProbe()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "juice.observability.rules.truncated") listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key != "template") continue;
                    var key = tag.Value?.ToString() ?? "";
                    lock (_counts) _counts[key] = _counts.GetValueOrDefault(key) + value;
                }
            });
            _listener.Start();
        }

        public long Count(string template)
        {
            lock (_counts) return _counts.GetValueOrDefault(template);
        }

        public void Dispose() => _listener.Dispose();
    }
}
