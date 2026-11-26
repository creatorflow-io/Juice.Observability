namespace Juice.Observability.Health.DeadMan
{
    /// <summary>
    /// Configuration section <c>Notifications:DeadMan</c> (phase 5, M49): the one endpoint the dead-man alarm posts to when the notification
    /// pipeline stops keeping up, and when its causes arise. It sends through its own path (<see cref="Delivery.SafeHttpClient"/>, signed), never
    /// through the pipeline it watches.
    /// </summary>
    public sealed class DeadManOptions
    {
        public const string Section = "Notifications:DeadMan";

        /// <summary>Where the alarm posts (https, or http to an allowlisted destination). Unset: no dead-man.</summary>
        public string? Url { get; set; }

        /// <summary>The signing secret, as a reference (<c>env:NAME</c> or <c>file:/path</c>).</summary>
        public string? SecretRef { get; set; }

        /// <summary>Destinations the alarm may reach that aren't public (a CIDR or a host name, with its ports), as for webhooks.</summary>
        public List<DeadManDestination> Allowlist { get; set; } = [];

        /// <summary>Plain http to an allowlisted destination (never to a public one).</summary>
        public bool AllowHttpToAllowlisted { get; set; }

        /// <summary>
        /// <b>stalled:</b> some stage has had work due and not taken for longer than this. At least 2 minutes, and at least twice the longest
        /// single step (an SMTP send, about 27 s plus recording) and twice the stage poll interval, or a slow but working pipeline would trip it.
        /// </summary>
        public TimeSpan DueThreshold { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary><b>failing:</b> some stage has this many rows whose latest step failed and that are still to be retried...</summary>
        public int FailingThreshold { get; set; } = 10;

        /// <summary>...on two reads at least this far apart.</summary>
        public TimeSpan FailingFor { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// <b>no success:</b> at least this many failed attempts and none that got out, in the pipeline's send window, on two reads at least
        /// <see cref="FailingFor"/> apart. A single broken endpoint with no other traffic trips it too: nothing is getting out.
        /// </summary>
        public int MinFailedAttempts { get; set; } = 5;

        /// <summary><b>stuck in retry:</b> a delivery still in flight older than this (above a normal retry run, whose longest wait is 10 min).</summary>
        public TimeSpan UndeliveredThreshold { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary><b>unreadable:</b> reading the pipeline's health takes longer than this (or throws).</summary>
        public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>A cause still standing is sent again after this; a cause is sent at most once in it.</summary>
        public TimeSpan ResendAfter { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>How often the pipeline's health is read.</summary>
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>The documented floor of <see cref="DueThreshold"/>.</summary>
        public static readonly TimeSpan MinimumDueThreshold = TimeSpan.FromMinutes(2);

        /// <summary>The longest single step a stage takes (an SMTP send with its timeouts, plus recording), for the threshold's check.</summary>
        public static readonly TimeSpan LongestStep = TimeSpan.FromSeconds(30);

        /// <summary>What is wrong with these settings, given the stages' longest poll interval; empty when nothing is.</summary>
        public IReadOnlyList<string> Problems(TimeSpan longestPoll)
        {
            var problems = new List<string>();
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
            {
                problems.Add("Notifications:DeadMan:Url is an absolute https (or allowlisted http) URL.");
            }
            if (string.IsNullOrWhiteSpace(SecretRef) || !Delivery.SecretReference.IsValid(SecretRef))
            {
                problems.Add("Notifications:DeadMan:SecretRef is env:NAME or file:/path: the alarm is signed.");
            }
            var floor = new[] { MinimumDueThreshold, 2 * LongestStep, 2 * longestPoll }.Max();
            if (DueThreshold < floor)
            {
                problems.Add($"Notifications:DeadMan:DueThreshold is at least {floor} (2 minutes, twice the longest step and twice the stages' poll): "
                    + "below it a slow but working pipeline would raise the alarm.");
            }
            if (FailingThreshold < 1) problems.Add("Notifications:DeadMan:FailingThreshold is at least 1.");
            if (MinFailedAttempts < 1) problems.Add("Notifications:DeadMan:MinFailedAttempts is at least 1.");
            if (UndeliveredThreshold < DueThreshold) problems.Add("Notifications:DeadMan:UndeliveredThreshold is at least the DueThreshold.");
            if (FailingFor <= TimeSpan.Zero) problems.Add("Notifications:DeadMan:FailingFor is positive.");
            if (ReadTimeout <= TimeSpan.Zero) problems.Add("Notifications:DeadMan:ReadTimeout is positive.");
            if (Interval <= TimeSpan.Zero) problems.Add("Notifications:DeadMan:Interval is positive.");
            if (ResendAfter < Interval) problems.Add("Notifications:DeadMan:ResendAfter is at least the Interval.");
            return problems;
        }
    }

    /// <summary>An allowlist entry for the alarm's endpoint: a CIDR or a host name, and its ports (443 when none).</summary>
    public sealed class DeadManDestination
    {
        public string Destination { get; set; } = "";
        public List<int> Ports { get; set; } = [];
    }

    /// <summary>
    /// What the shadow comparison says (M47): whether the new pipeline, in shadow, keeps failing to match what legacy sent. A stub until M47
    /// registers one; without one, <c>shadow</c> is never a cause.
    /// </summary>
    public interface IShadowReport
    {
        /// <summary>Null while there is nothing to say; otherwise why the shadow comparison is failing.</summary>
        ValueTask<string?> FailingAsync(CancellationToken cancellationToken);
    }
}
