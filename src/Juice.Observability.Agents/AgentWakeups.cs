using System.Collections.Concurrent;

namespace Juice.Observability.Agents
{
    /// <summary>
    /// Wakes an agent's waiting poll when a command for it is issued in this process, so it gets the command at once instead of on the poll's next
    /// look at the database. A poll takes its waiter before it looks, so a command issued between the look and the wait isn't missed. Commands issued
    /// on another replica are found by the poll's regular look (<see cref="AgentApi.Recheck"/>).
    /// </summary>
    public sealed class AgentWakeups
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _waiters = new();

        /// <summary>A task that completes when a command for this agent is issued here.</summary>
        public Task Waiter(Guid agentId) => _waiters.GetOrAdd(agentId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public void Wake(Guid agentId)
        {
            if (_waiters.TryRemove(agentId, out var waiter))
            {
                waiter.TrySetResult();
            }
        }
    }
}
