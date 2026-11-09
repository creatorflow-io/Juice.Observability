using System.Text.Json;
using System.Text.RegularExpressions;

namespace Juice.Observability.Agent.Contracts
{
    /// <summary>The types a parameter can have. Nothing else: no objects, lists or free-form JSON, so every value can be bounded.</summary>
    public static class ParameterTypes
    {
        public const string String = "string";
        public const string Integer = "integer";
        public const string Boolean = "boolean";

        public static readonly IReadOnlyList<string> All = [String, Integer, Boolean];
    }

    /// <summary>
    /// One parameter of an action, with its bounds. A string has a <see cref="MaxLength"/> always, and may have a <see cref="Pattern"/>; an integer
    /// has a <see cref="Minimum"/> and <see cref="Maximum"/>; any may be limited to <see cref="Allowed"/> values.
    /// </summary>
    /// <param name="Name">camelCase, as it appears in the command's parameters.</param>
    /// <param name="Type">One of <see cref="ParameterTypes.All"/>.</param>
    /// <param name="Required">Must be given; otherwise it may be left out or null.</param>
    /// <param name="Description">What it is, for a form.</param>
    /// <param name="Minimum">An integer's lowest value.</param>
    /// <param name="Maximum">An integer's highest value.</param>
    /// <param name="MaxLength">A string's most characters.</param>
    /// <param name="Pattern">A .NET regular expression a string must match.</param>
    /// <param name="Allowed">The only values it may take, as their JSON reads.</param>
    /// <param name="Default">What a form fills in, as text. Not applied: a command carries every value it means.</param>
    public sealed record ActionParameter(string Name, string Type, bool Required, string? Description = null, long? Minimum = null, long? Maximum = null,
        int? MaxLength = null, string? Pattern = null, IReadOnlyList<string>? Allowed = null, string? Default = null);

    /// <summary>How much an action can hurt if it's wrong: the platform's approval policy is set per risk (roadmap Phase 8).</summary>
    public static class ActionRisks
    {
        public const string Low = "Low";
        public const string Medium = "Medium";
        public const string High = "High";

        public static readonly IReadOnlyList<string> All = [Low, Medium, High];
    }

    /// <summary>
    /// What an action is: its name, what it does, whether it changes the host or only reads it, its parameters, and its risk. The built-in actions
    /// have one each (<see cref="AgentActions.Descriptors"/>); an agent describes its own custom ones the same way.
    /// </summary>
    /// <param name="Name">Lowercase words joined by dots (<see cref="ActionSchema.IsValidName"/>).</param>
    /// <param name="Description">What it does, in a sentence.</param>
    /// <param name="Changing">True when it changes the host rather than reading it.</param>
    /// <param name="Parameters">What it takes, each bounded.</param>
    /// <param name="Custom">An agent build's own action, not a built-in.</param>
    /// <param name="Risk">One of <see cref="ActionRisks.All"/>; when null, Medium for a changing action and Low otherwise (<see cref="RiskOf"/>).</param>
    public sealed record ActionDescriptor(string Name, string Description, bool Changing, IReadOnlyList<ActionParameter> Parameters, bool Custom = false, string? Risk = null)
    {
        public string RiskOf() => Risk ?? (Changing ? ActionRisks.Medium : ActionRisks.Low);
    }

    /// <summary>
    /// One action as an agent reports it: what it is, with its parameters narrowed to what this host's allowlist accepts, whether the allowlist
    /// permits it at all, and the host's other limits in words. Information for people and the platform's early checks: the agent checks every
    /// command itself whatever this says.
    /// </summary>
    public sealed record ReportedAction(ActionDescriptor Action, bool Permitted, IReadOnlyList<string> Limits);

    /// <summary>Every action an agent can run, built-in and custom, as it reports them to the platform when it starts.</summary>
    public sealed record ActionsReport(IReadOnlyList<ReportedAction> Actions)
    {
        public const int MaxActions = 50;
        public const int MaxParameters = 20;
        public const int MaxLimits = 10;
        public const int MaxText = 300;
        public const int MaxAllowed = 100;

        /// <summary>The reasons this report can't be taken as it is; empty when it can. The platform refuses a report with any.</summary>
        public IEnumerable<string> Validate()
        {
            if (Actions is null)
            {
                yield return "actions is required";
                yield break;
            }
            if (Actions.Count > MaxActions) yield return $"at most {MaxActions} actions";
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reported in Actions.Take(MaxActions))
            {
                if (reported?.Action is not { } action)
                {
                    yield return "an action is empty";
                    continue;
                }
                foreach (var problem in ActionSchema.ValidateDescriptor(action)) yield return $"{action.Name}: {problem}";
                if (!names.Add(action.Name ?? "")) yield return $"{action.Name}: reported twice";
                if (action.Custom && AgentActions.All.Contains(action.Name ?? "")) yield return $"{action.Name}: a custom action can't take a built-in action's name";
                if (reported.Limits is null || reported.Limits.Count > MaxLimits || reported.Limits.Any(l => l is null || l.Length > MaxText))
                {
                    yield return $"{action.Name}: at most {MaxLimits} limits of {MaxText} characters";
                }
            }
        }
    }

    /// <summary>Checks descriptors, and parameters against a descriptor, the same way on the platform and on the agent.</summary>
    public static partial class ActionSchema
    {
        /// <summary>Patterns are .NET regular expressions, run with this timeout so a bad one can't hang the caller.</summary>
        public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

        /// <summary><c>vendor.action</c>: lowercase words joined by dots, two to four of them, at most 64 characters.</summary>
        public static bool IsValidName(string? name) => name is { Length: > 0 and <= 64 } && Name().IsMatch(name);

        public static IEnumerable<string> ValidateDescriptor(ActionDescriptor action)
        {
            if (!IsValidName(action.Name)) yield return "name is lowercase words joined by dots, like vendor.check, at most 64 characters";
            if (string.IsNullOrWhiteSpace(action.Description) || action.Description.Length > ActionsReport.MaxText) yield return $"description is 1 to {ActionsReport.MaxText} characters";
            if (action.Risk is { } risk && !ActionRisks.All.Contains(risk)) yield return $"risk is one of {string.Join(", ", ActionRisks.All)}";
            // A changing action can't call itself harmless: Low is for reading only.
            if (action.Changing && action.Risk == ActionRisks.Low) yield return "a changing action's risk is Medium or High";
            if (action.Parameters is null)
            {
                yield return "parameters is required";
                yield break;
            }
            if (action.Parameters.Count > ActionsReport.MaxParameters) yield return $"at most {ActionsReport.MaxParameters} parameters";
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in action.Parameters)
            {
                if (p is null)
                {
                    yield return "a parameter is empty";
                    continue;
                }
                if (p.Name is null || !ParameterName().IsMatch(p.Name)) yield return $"parameter name '{p.Name}' is camelCase, at most 64 characters";
                else if (!names.Add(p.Name)) yield return $"parameter {p.Name} appears twice";
                if (!ParameterTypes.All.Contains(p.Type)) yield return $"{p.Name}: type is one of {string.Join(", ", ParameterTypes.All)}";
                if (p.Description is { Length: > ActionsReport.MaxText }) yield return $"{p.Name}: description is at most {ActionsReport.MaxText} characters";
                if (p.Type == ParameterTypes.String && p.MaxLength is not (> 0 and <= 4096)) yield return $"{p.Name}: a string needs a maxLength of 1 to 4096";
                if (p.Type == ParameterTypes.Integer && (p.Minimum is null || p.Maximum is null || p.Minimum > p.Maximum)) yield return $"{p.Name}: an integer needs a minimum and a maximum";
                if (p.Pattern is { } pattern && (pattern.Length > ActionsReport.MaxText || !IsValidPattern(pattern))) yield return $"{p.Name}: pattern isn't a valid regular expression";
                if (p.Allowed is { } allowed && (allowed.Count > ActionsReport.MaxAllowed || allowed.Any(a => a is null || a.Length > ActionsReport.MaxText)))
                {
                    yield return $"{p.Name}: at most {ActionsReport.MaxAllowed} allowed values of {ActionsReport.MaxText} characters";
                }
                if (p.Default is { Length: > ActionsReport.MaxText }) yield return $"{p.Name}: default is at most {ActionsReport.MaxText} characters";
            }
        }

        private static bool IsValidPattern(string pattern)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.None, PatternTimeout);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Checks a command's parameters: an object, with no member the action doesn't name, every required one present, each of its type and within
        /// its bounds. Returns null when they hold, otherwise the first reason they don't.
        /// </summary>
        public static string? Validate(IReadOnlyList<ActionParameter> parameters, JsonElement values)
        {
            if (values.ValueKind != JsonValueKind.Object) return "parameters must be an object";
            var known = parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
            foreach (var member in values.EnumerateObject())
            {
                if (!known.ContainsKey(member.Name)) return $"unknown parameter '{member.Name}'";
            }
            foreach (var p in parameters)
            {
                if (!values.TryGetProperty(p.Name, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    if (p.Required) return $"{p.Name} is required";
                    continue;
                }
                if (Check(p, value) is { } problem) return problem;
            }
            return null;
        }

        private static string? Check(ActionParameter p, JsonElement value)
        {
            string text;
            switch (p.Type)
            {
                case ParameterTypes.String:
                    if (value.ValueKind != JsonValueKind.String) return $"{p.Name} is a string";
                    text = value.GetString()!;
                    if (text.Length > (p.MaxLength ?? 0)) return $"{p.Name} is at most {p.MaxLength} characters";
                    if (p.Pattern is { } pattern)
                    {
                        try
                        {
                            if (!Regex.IsMatch(text, pattern, RegexOptions.None, PatternTimeout)) return $"{p.Name} doesn't have the expected form";
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            return $"{p.Name} doesn't have the expected form";
                        }
                    }
                    break;
                case ParameterTypes.Integer:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) return $"{p.Name} is a whole number";
                    if (number < p.Minimum || number > p.Maximum) return $"{p.Name} is {p.Minimum} to {p.Maximum}";
                    text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case ParameterTypes.Boolean:
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return $"{p.Name} is true or false";
                    text = value.GetBoolean() ? "true" : "false";
                    break;
                default:
                    return $"{p.Name} has an unknown type";
            }
            if (p.Allowed is { Count: > 0 } allowed && !allowed.Contains(text, StringComparer.Ordinal)) return $"{p.Name} is one of: {string.Join(", ", allowed)}";
            return null;
        }

        [GeneratedRegex(@"^[a-z][a-z0-9_]{0,30}(\.[a-z][a-z0-9_]{0,30}){1,3}$")]
        private static partial Regex Name();

        [GeneratedRegex("^[a-z][A-Za-z0-9]{0,63}$")]
        private static partial Regex ParameterName();
    }
}
