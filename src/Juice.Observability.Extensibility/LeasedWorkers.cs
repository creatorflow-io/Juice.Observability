namespace Juice.Observability.Extensibility;

public interface ILeasedWorker
{
    TimeSpan Interval { get; }

    Task<LeasedPassOutcome> RunOnceAsync(LeasedPass pass, CancellationToken ct);
}

/// <summary>Idle is a heartbeat only.</summary>
public enum LeasedPassOutcome { Succeeded, Partial, Idle }

public sealed record LeasedPass(string Worker, int Generation, bool Retaken, CancellationToken LeaseLost);

public static class LeasedWorkerModuleExtensions
{
    /// <summary>The name is the module name or <c>&lt;module&gt;-&lt;x&gt;</c>, never a core worker name.</summary>
    public static IModuleServicesBuilder AddLeasedWorker<T>(this IModuleServicesBuilder b, string name) where T : class, ILeasedWorker
        => throw new NotImplementedException();
}
