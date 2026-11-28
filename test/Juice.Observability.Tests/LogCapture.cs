using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Tests
{
    /// <summary>A logger provider that keeps what a host logged, so a test can say what it was told: <c>services.AddSingleton&lt;ILoggerProvider&gt;(capture)</c>.</summary>
    [ProviderAlias("Capture")]
    internal sealed class LogCapture : ILoggerProvider
    {
        public sealed record Entry(string Category, LogLevel Level, string Message);

        public ConcurrentQueue<Entry> Entries { get; } = new();

        public IEnumerable<Entry> At(LogLevel level) => Entries.Where(e => e.Level == level);

        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Capturing(string category, ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new Entry(category, logLevel, formatter(state, exception)));
        }
    }
}
