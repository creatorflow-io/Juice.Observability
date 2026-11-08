using System.Collections.Concurrent;

namespace Juice.Observability.Agents
{
    /// <summary>
    /// When to look again for the discovered host an agent belongs to. The look can't use an index (host names compare case-insensitively), so an
    /// agent on a host discovery doesn't know looks at most every <see cref="Interval"/>, not at every poll. A changed host name looks at once.
    /// </summary>
    public sealed class HostLinks(TimeProvider clock)
    {
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
        private readonly ConcurrentDictionary<(Guid Agent, string Hostname), DateTimeOffset> _nextLook = new();

        /// <summary>True when it's time to look for this agent and host name, and then not again for <see cref="Interval"/>.</summary>
        public bool ShouldLook(Guid agentId, string hostname)
        {
            var now = clock.GetUtcNow();
            var key = (agentId, hostname);
            if (_nextLook.TryGetValue(key, out var next) && now < next)
            {
                return false;
            }
            _nextLook[key] = now + Interval;
            return true;
        }
    }
}
