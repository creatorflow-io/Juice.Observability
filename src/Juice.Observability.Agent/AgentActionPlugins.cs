using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent
{
    /// <summary>
    /// An action a customer adds to their own agent build. It's code compiled into that build and shipped in a package signed with their release key:
    /// nothing a command carries can choose what runs, only the bounded parameters this action declares. Derive from <see cref="AgentAction{TParameters}"/>.
    /// </summary>
    public interface IAgentAction
    {
        ActionDescriptor Descriptor { get; }

        /// <summary>How long one run may take, beyond which it's cancelled and reported failed (<c>timeout</c>).</summary>
        TimeSpan Timeout { get; }

        /// <summary>The parameters as the action's own type, once they've passed <see cref="ActionSchema.Validate"/>.</summary>
        object Bind(JsonElement parameters);

        Task<object?> RunAsync(object parameters, CancellationToken cancellationToken);
    }

    /// <summary>For an action that takes no parameters.</summary>
    public sealed record NoParameters;

    /// <summary>
    /// A custom action. Its parameters are a record of <c>string</c>, <c>int</c>, <c>long</c> and <c>bool</c> members, bounded with attributes:
    /// <see cref="MaxLengthAttribute"/> (required on every string), <see cref="RangeAttribute"/>, <see cref="RegularExpressionAttribute"/>,
    /// <see cref="AllowedValuesAttribute"/>, with <see cref="DescriptionAttribute"/> and <see cref="DefaultValueAttribute"/> for forms. A member is
    /// required unless it's nullable. The platform and the agent both check a command's parameters against these before <see cref="RunAsync(TParameters, CancellationToken)"/>
    /// sees them, and this host's allowlist must list the action (<see cref="LocalAllowlist"/>).
    /// </summary>
    /// <example>
    /// <code>
    /// public sealed record QueueParameters([property: MaxLength(64), AllowedValues("orders", "billing")] string Queue);
    /// public sealed class QueueDepth : AgentAction&lt;QueueParameters&gt;
    /// {
    ///     public override string Name =&gt; "acme.queue_depth";
    ///     public override string Description =&gt; "Messages waiting in one of the shop's queues.";
    ///     protected override Task&lt;object?&gt; RunAsync(QueueParameters p, CancellationToken ct) =&gt; ...;
    /// }
    /// </code>
    /// </example>
    public abstract class AgentAction<TParameters> : IAgentAction where TParameters : class
    {
        private ActionDescriptor? _descriptor;

        /// <summary>Lowercase words joined by dots, starting with your own prefix, like <c>acme.queue_depth</c>.</summary>
        public abstract string Name { get; }

        /// <summary>What it does, in a sentence, as people will see it in the Console.</summary>
        public abstract string Description { get; }

        /// <summary>True when it changes the host: then only admins can issue it. Read-only by default.</summary>
        public virtual bool Changing => false;

        /// <summary>10 seconds unless overridden; at most <see cref="AgentActionCatalog.MaxTimeout"/>.</summary>
        public virtual TimeSpan Timeout => TimeSpan.FromSeconds(10);

        /// <summary>
        /// <see cref="ActionRisks"/>: what the platform's approval policy goes by. Low for a read-only action and Medium for a changing one unless
        /// overridden; a changing action can't be Low.
        /// </summary>
        public virtual string Risk => Changing ? ActionRisks.Medium : ActionRisks.Low;

        public ActionDescriptor Descriptor => _descriptor ??= new(Name, Description, Changing, ParameterSchema.Describe(typeof(TParameters)), Custom: true, Risk: Risk);

        /// <summary>
        /// Does the work and returns what to report: anything that serializes to JSON, kept under 64 KB. Throw <see cref="ActionFailedException"/>
        /// to fail with a reason of your own; any other exception fails with its type's name. Honour the cancellation token: the agent stops waiting
        /// at the timeout either way.
        /// </summary>
        protected abstract Task<object?> RunAsync(TParameters parameters, CancellationToken cancellationToken);

        object IAgentAction.Bind(JsonElement parameters) => JsonSerializer.Deserialize<TParameters>(parameters, CommandSignature.Json)
            ?? throw new JsonException("empty parameters");

        Task<object?> IAgentAction.RunAsync(object parameters, CancellationToken cancellationToken) => RunAsync((TParameters)parameters, cancellationToken);
    }

    /// <summary>A custom action failed for a reason it names: reported as the command's reason, so keep it a short code like <c>queue_unreachable</c>.</summary>
    public sealed class ActionFailedException(string reason) : Exception(reason);

    /// <summary>A custom action to run: which one, its parameters typed, and as they came.</summary>
    public sealed record CustomInvocation(IAgentAction Action, object Parameters, JsonElement Raw);

    /// <summary>Reads a parameter record's members and attributes into <see cref="ActionParameter"/>s.</summary>
    public static class ParameterSchema
    {
        public static IReadOnlyList<ActionParameter> Describe(Type type)
        {
            if (type == typeof(NoParameters)) return [];
            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            var nullability = new NullabilityInfoContext();
            var result = new List<ActionParameter>();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.Name != "EqualityContract"))
            {
                // A positional record's attributes may sit on its constructor parameter instead of the property.
                var parameter = constructor?.GetParameters().FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase));
                T? Attribute<T>() where T : Attribute => property.GetCustomAttribute<T>() ?? parameter?.GetCustomAttribute<T>();

                var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                var underlying = Nullable.GetUnderlyingType(property.PropertyType);
                var clr = underlying ?? property.PropertyType;
                var nullable = underlying is not null || (!clr.IsValueType && nullability.Create(property).ReadState != NullabilityState.NotNull);
                var required = !nullable || Attribute<RequiredAttribute>() is not null;
                var description = Attribute<DescriptionAttribute>()?.Description;
                var allowed = Attribute<AllowedValuesAttribute>()?.Values.Select(Text).ToList();
                var fallback = Attribute<DefaultValueAttribute>()?.Value is { } d ? Text(d) : null;

                if (clr == typeof(string))
                {
                    var max = Attribute<MaxLengthAttribute>()?.Length ?? Attribute<StringLengthAttribute>()?.MaximumLength
                        ?? throw new InvalidOperationException($"{type.Name}.{property.Name}: a string parameter needs [MaxLength]");
                    result.Add(new(name, ParameterTypes.String, required, description, MaxLength: max, Pattern: Attribute<RegularExpressionAttribute>()?.Pattern, Allowed: allowed, Default: fallback));
                }
                else if (clr == typeof(int) || clr == typeof(long))
                {
                    var range = Attribute<RangeAttribute>();
                    long min = range is null ? (clr == typeof(int) ? int.MinValue : long.MinValue) : Convert.ToInt64(range.Minimum, CultureInfo.InvariantCulture);
                    long max = range is null ? (clr == typeof(int) ? int.MaxValue : long.MaxValue) : Convert.ToInt64(range.Maximum, CultureInfo.InvariantCulture);
                    result.Add(new(name, ParameterTypes.Integer, required, description, min, max, Allowed: allowed, Default: fallback));
                }
                else if (clr == typeof(bool))
                {
                    result.Add(new(name, ParameterTypes.Boolean, required, description, Allowed: allowed, Default: fallback));
                }
                else
                {
                    throw new InvalidOperationException($"{type.Name}.{property.Name}: parameters are string, int, long or bool, not {clr.Name}");
                }
            }
            return result;
        }

        /// <summary>A value as the JSON for it reads: <c>true</c>, not <c>True</c>.</summary>
        private static string Text(object? value) => value is bool b ? (b ? "true" : "false") : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>
    /// Every action this agent can run: the built-in ones, then the custom ones its build added. Checked when the agent starts: a custom action with
    /// an invalid name or schema, a name used twice or a built-in's name, stops it with <see cref="AgentService.ConfigurationExitCode"/>.
    /// </summary>
    public sealed class AgentActionCatalog
    {
        public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);

        /// <summary>The built-in actions only, as an agent without custom ones has.</summary>
        public static readonly AgentActionCatalog BuiltIn = new([]);

        private readonly Dictionary<string, IAgentAction> _custom;

        public AgentActionCatalog(IEnumerable<IAgentAction> custom)
        {
            _custom = new(StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (var action in custom)
            {
                ActionDescriptor descriptor;
                try
                {
                    descriptor = action.Descriptor;
                }
                catch (InvalidOperationException e)
                {
                    problems.Add($"{action.GetType().Name}: {e.Message}");
                    continue;
                }
                problems.AddRange(ActionSchema.ValidateDescriptor(descriptor).Select(p => $"{action.GetType().Name} ({descriptor.Name}): {p}"));
                if (AgentActions.All.Contains(descriptor.Name)) problems.Add($"{action.GetType().Name}: {descriptor.Name} is a built-in action's name");
                else if (!_custom.TryAdd(descriptor.Name, action)) problems.Add($"{action.GetType().Name}: {descriptor.Name} is added twice");
                if (action.Timeout <= TimeSpan.Zero || action.Timeout > MaxTimeout) problems.Add($"{descriptor.Name}: timeout is more than 0 and at most {MaxTimeout.TotalMinutes} minutes");
            }
            if (problems.Count > 0) throw new InvalidOperationException("Invalid custom agent actions:\n" + string.Join("\n", problems));
        }

        public IReadOnlyList<ActionDescriptor> Descriptors => [.. AgentActions.Descriptors, .. _custom.Values.Select(a => a.Descriptor)];

        public bool Knows(string action) => AgentActions.All.Contains(action) || _custom.ContainsKey(action);

        /// <summary>The typed parameters (a built-in's record, or a <see cref="CustomInvocation"/>), or null with the reason.</summary>
        public object? Parse(string action, JsonElement parameters, out string? error)
        {
            if (!_custom.TryGetValue(action, out var custom)) return AgentActions.Parse(action, parameters, out error);
            if ((error = ActionSchema.Validate(custom.Descriptor.Parameters, parameters)) is not null) return null;
            try
            {
                return new CustomInvocation(custom, custom.Bind(parameters), parameters.Clone());
            }
            catch (JsonException e)
            {
                error = $"unreadable parameters: {e.Message}";
                return null;
            }
        }
    }

    /// <summary>Adds custom actions to an agent build: <c>JuiceAgent.RunAsync(args, actions =&gt; actions.Add&lt;QueueDepth&gt;())</c>.</summary>
    public sealed class AgentActionsBuilder
    {
        internal List<IAgentAction> Actions { get; } = [];

        public AgentActionsBuilder Add<T>() where T : IAgentAction, new() => Add(new T());

        public AgentActionsBuilder Add(IAgentAction action)
        {
            Actions.Add(action);
            return this;
        }
    }
}
