using System.Text.Json;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent
{
    public enum Recall
    {
        /// <summary>Never seen: it may run.</summary>
        New,
        /// <summary>This very command again, as a redelivery: send its result again instead of running it twice.</summary>
        Redelivered,
        /// <summary>Its idempotency key was handled by another command: send that result instead of running again.</summary>
        AlreadyDone,
        /// <summary>Its nonce was used by a different command: a replay, refused.</summary>
        Replayed,
    }

    /// <summary>
    /// The nonces and idempotency keys this agent has seen, with the results it reported, kept on disk until the command could no longer be valid
    /// (its expiry plus the clock skew allowed), so a restart doesn't forget them. A command is recorded before it runs.
    /// </summary>
    public sealed class CommandMemory
    {
        private sealed record Entry(string Nonce, Guid CommandId, string IdempotencyKey, DateTimeOffset KeepUntil, CommandResult? Result);

        private readonly string? _file;
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _byNonce;

        /// <param name="file">Where to keep it; null keeps it in memory only (tests).</param>
        public CommandMemory(string? file)
        {
            _file = file;
            _byNonce = file is not null && File.Exists(file)
                ? (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllBytes(file), CommandSignature.Json) ?? []).ToDictionary(e => e.Nonce)
                : [];
        }

        /// <summary>
        /// The memory on disk. A file that doesn't parse (cut short, or edited) is moved aside, named in <paramref name="setAside"/>, and the agent
        /// starts with an empty memory rather than not at all: a replay would then still need the platform's own mTLS channel, and only until the
        /// commands it remembered expire. A file that can't be read for now (locked, a disk error) throws, and the caller tries again.
        /// </summary>
        public static CommandMemory Open(string file, out string? setAside)
        {
            setAside = null;
            try
            {
                return new CommandMemory(file);
            }
            catch (JsonException)
            {
                setAside = $"{file}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(file, setAside, overwrite: true);
                return new CommandMemory(file);
            }
        }

        public (Recall Recall, CommandResult? Result) Recall(CommandEnvelope envelope, DateTimeOffset now)
        {
            lock (_gate)
            {
                Prune(now);
                if (_byNonce.TryGetValue(envelope.Nonce, out var seen))
                {
                    return seen.CommandId == envelope.CommandId ? (Agent.Recall.Redelivered, seen.Result) : (Agent.Recall.Replayed, null);
                }
                var done = _byNonce.Values.FirstOrDefault(e => e.IdempotencyKey == envelope.IdempotencyKey && e.Result is not null);
                return done is not null ? (Agent.Recall.AlreadyDone, done.Result! with { CommandId = envelope.CommandId }) : (Agent.Recall.New, null);
            }
        }

        /// <summary>Before running: from now on its nonce can't be used again.</summary>
        public void Record(CommandEnvelope envelope)
        {
            lock (_gate)
            {
                _byNonce[envelope.Nonce] = new Entry(envelope.Nonce, envelope.CommandId, envelope.IdempotencyKey, envelope.ExpiresAt + CommandLimits.ClockSkew, null);
                Save();
            }
        }

        public void Complete(CommandEnvelope envelope, CommandResult result)
        {
            lock (_gate)
            {
                if (_byNonce.TryGetValue(envelope.Nonce, out var entry))
                {
                    _byNonce[envelope.Nonce] = entry with { Result = result };
                    Save();
                }
            }
        }

        private void Prune(DateTimeOffset now)
        {
            foreach (var expired in _byNonce.Values.Where(e => e.KeepUntil <= now).Select(e => e.Nonce).ToList())
            {
                _byNonce.Remove(expired);
            }
        }

        private void Save()
        {
            if (_file is null)
            {
                return;
            }
            var temporary = _file + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(_byNonce.Values.ToList(), CommandSignature.Json));
            File.Move(temporary, _file, overwrite: true);
        }
    }
}
