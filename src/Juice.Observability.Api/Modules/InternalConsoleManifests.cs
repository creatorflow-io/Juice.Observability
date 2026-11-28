using Juice.Observability.Extensibility;

namespace Juice.Observability.Api.Modules
{
    /// <summary>
    /// The Console manifests of the platform's own modules (core 2.0 M3, E7): the menu and the routes of the pages the <c>@juice-js/observability</c>
    /// library holds. They go through the same validator as an external module's, with the internal-only exemptions (the library's own package and the
    /// empty route of the Overview). The menu is what the Console showed before manifests existed, entry for entry: keys are the old menu ids, and
    /// <see cref="ConsoleMenuEntry.Order"/> follows the old sequence (top level first, then Explore, Automation and Configure).
    /// <c>builtin-manifests.json</c> in the Console library is these manifests as JSON, pinned to the served answer by a test.
    /// </summary>
    internal static class InternalConsoleManifests
    {
        public const string Package = ModuleValidation.CorePackage;

        private static ConsoleMenuEntry Entry(string key, string label, string route, string icon, int order, string? group = null)
            => new(key, label, route, icon, null, order) { Group = group };

        private static ConsoleRoute Route(string path) => new(path, null);

        /// <summary>Overview, the investigation pages, alerts and rules: the pages that need no other module.</summary>
        public static ConsoleManifest Core { get; } = new(Package,
        [
            Route(""), Route("services"), Route("services/:id"), Route("traces"), Route("traces/:id"), Route("topology"), Route("logs"), Route("metrics"),
            Route("resources"), Route("resources/:id"), Route("alerts"), Route("alerts/:id"), Route("rules"), Route("escalation"), Route("silences"),
            Route("incidents"), Route("incidents/:id"), Route("timeline"), Route("load-runs"),
        ],
        [
            Entry("overview", "Overview", "", "dashboard", 10),
            Entry("services", "Services", "services", "hub", 20),
            Entry("traces", "Traces", "traces", "timeline", 30),
            Entry("logs", "Logs", "logs", "notes", 40),
            Entry("metrics", "Metrics", "metrics", "show_chart", 50),
            Entry("alerts", "Alerts", "alerts", "notifications", 60),
            Entry("incidents", "Incidents", "incidents", "emergency", 70),
            Entry("topology", "Dependencies", "topology", "account_tree", 100, ConsoleMenuGroups.Explore),
            Entry("resources", "Resources", "resources", "dns", 110, ConsoleMenuGroups.Explore),
            Entry("timeline", "Timeline", "timeline", "history", 130, ConsoleMenuGroups.Explore),
            Entry("load-runs", "Load runs", "load-runs", "speed", 140, ConsoleMenuGroups.Explore),
            Entry("rules", "Rules", "rules", "rule", 300, ConsoleMenuGroups.Configure),
            Entry("silences", "Silences", "silences", "notifications_paused", 310, ConsoleMenuGroups.Configure),
            Entry("escalation", "Escalation selection", "escalation", "campaign", 320, ConsoleMenuGroups.Configure),
        ]);

        /// <summary>Service level objectives.</summary>
        public static ConsoleManifest Slos { get; } = new(Package,
            [Route("slos"), Route("slos/:key")],
            [Entry("slos", "SLOs", "slos", "track_changes", 120, ConsoleMenuGroups.Explore)]);

        /// <summary>Agents and the approvals of their action requests.</summary>
        public static ConsoleManifest Agents { get; } = new(Package,
            [Route("agents"), Route("agents/:id"), Route("approvals")],
            [
                Entry("agents", "Agents", "agents", "smart_toy", 200, ConsoleMenuGroups.Automation),
                Entry("approvals", "Approvals", "approvals", "approval", 210, ConsoleMenuGroups.Automation),
            ]);

        /// <summary>
        /// First segments of Console routes that belong to the platform: every segment the three manifests use, the sign-in callback (<c>auth</c>) and the
        /// old notification pages' redirect (<c>notifications</c>). An external module's route can't start with one: the Console would otherwise hand the
        /// page to the module.
        /// </summary>
        public static IReadOnlySet<string> ReservedFirstSegments { get; } = new HashSet<string>(
            new[] { Core, Slos, Agents }.SelectMany(m => m.Routes).Select(r => FirstSegment(r.Path)).Where(s => s.Length > 0).Concat(["auth", "notifications"]),
            StringComparer.Ordinal);

        /// <summary>The text before the first <c>/</c>: <c>alerts</c> for <c>alerts/:id</c>, empty for the Overview.</summary>
        public static string FirstSegment(string path)
        {
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? path : path[..slash];
        }
    }
}
