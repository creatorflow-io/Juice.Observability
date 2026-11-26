namespace Juice.Observability.Health.DeadMan
{
    /// <summary>
    /// The shadow run's state for the dead-man (M49): null when it has nothing to report, otherwise a short reason, the alarm's detail for cause
    /// "shadow". Platform-wide, one answer per pass. (Copied from M49 exactly; whichever of M47 and M49 merges second drops the duplicate.)
    /// </summary>
    public interface IShadowReport
    {
        ValueTask<string?> FailingAsync(CancellationToken cancellationToken);
    }
}
