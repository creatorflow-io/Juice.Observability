using Juice.Observability.Api;
using Juice.Observability.Api.Evidence;
using Juice.Observability.Extensibility;
using Juice.Observability.Tests.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests.EvidenceContributors
{
    /// <summary>Evidence contributors (core 2.0 M3, E9): composition rules, the core's caps on what they return and the order. No database.</summary>
    [Trait("Milestone", "C2-M3")]
    public sealed class EvidenceUnitTests
    {
        private static EvidenceSection Section(params EvidenceItem[] items) => new("Title", items);

        private static bool Never(string route) => false;

        [Fact]
        public void Contributor_key_prefix_unique_and_cap()
        {
            var prefix = Assert.Throws<ModuleCompositionException>(() => CoreHost.Build(b => b.AddObservabilityModule<OtherPrefixModule>()));
            Assert.Contains("someone-else.x", prefix.Message);
            var duplicate = Assert.Throws<ModuleCompositionException>(() => CoreHost.Build(b => b.AddObservabilityModule<DuplicateKeyModule>()));
            Assert.Contains("dup-module.a", duplicate.Message);
            // The sixth is refused as the module composes, and the module leaves nothing behind.
            var cap = Assert.Throws<ModuleCompositionException>(() => CoreHost.Build(b => b.AddObservabilityModule<CapModule>()));
            Assert.Contains("more than 5", cap.Message);
            var builder = CoreHost.CreateBuilder();
            builder.Services.AddJuiceObservability(builder.Configuration);
            Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<CapModule>());
            Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(Cap1));

            // Five are the most, and a valid module composes.
            using var app = CoreHost.Build(b => b.AddObservabilityModule<EvidenceSampleModule>());
            Assert.Equal(5, app.Services.GetServices<EvidenceRegistration>().Count(r => r.Module == "evidence-sample"));
        }

        [Fact]
        public void Unknown_contributor_policy_refuses_start()
        {
            var unknown = Assert.Throws<ModuleCompositionException>(() => CoreHost.Build(b => b.AddObservabilityModule<UnknownPolicyModule>()));
            Assert.Contains("policy-module.nothing", unknown.Message);
            // Another module's policy is no more the contributor's than an unknown one.
            var foreign = Assert.Throws<ModuleCompositionException>(() => CoreHost.Build(b =>
            {
                b.AddObservabilityModule<EvidenceSampleModule>();
                b.AddObservabilityModule<ForeignPolicyModule>();
            }));
            Assert.Contains("evidence-sample.admin", foreign.Message);
        }

        [Fact]
        public void Order_core_then_registration()
        {
            using var app = CoreHost.Build(b => b.AddObservabilityModule<EvidenceSampleModule>());
            var catalog = app.Services.GetRequiredService<EvidenceCatalog>();
            Assert.Equal(["core.changes", "evidence-sample.one", "evidence-sample.two", "evidence-sample.three", "evidence-sample.admin"],
                catalog.For(EvidenceTarget.Alert).Select(i => i.Key));
            Assert.Equal(["evidence-sample.service"], catalog.For(EvidenceTarget.Service).Select(i => i.Key));
        }

        [Fact]
        public void Section_caps_truncate_and_flag()
        {
            var items = Enumerable.Range(0, 60).Select(i => new EvidenceItem(new string('l', 80), new string('v', 600))).ToArray();
            var result = EvidenceSanitizer.Check(new EvidenceSection(new string('t', 100), items, new string('n', 600)), Never);
            Assert.True(result.Truncated);
            var section = result.Section!;
            Assert.Equal(64, section.Title.Length);
            Assert.Equal(512, section.Note!.Length);
            Assert.Equal(50, section.Items.Count);
            Assert.All(section.Items, i => Assert.Equal((64, 512), (i.Label.Length, i.Value!.Length)));

            // Within bounds: nothing is cut, nothing is flagged.
            var small = EvidenceSanitizer.Check(Section(new EvidenceItem(new string('l', 64), new string('v', 512))), Never);
            Assert.False(small.Truncated);
            Assert.Equal(64, small.Section!.Items[0].Label.Length);

            // A cut never splits a surrogate pair.
            var emoji = EvidenceSanitizer.Check(Section(new EvidenceItem(new string('a', 63) + "\U0001F600", null)), Never);
            Assert.Equal(63, emoji.Section!.Items[0].Label.Length);
        }

        [Theory]
        [InlineData("line\nbreak")]
        [InlineData("tab\there")]
        [InlineData("bell\a")]
        [InlineData("right\u202Eleft")]
        [InlineData("isolate\u2066x")]
        [InlineData("mark\u200Fx")]
        [InlineData("zero\u200Bwidth")]
        [InlineData("sep\u2028x")]
        public void Control_or_bidi_text_section_invalid(string text)
        {
            Assert.Null(EvidenceSanitizer.Check(new EvidenceSection(text, []), Never).Section);
            Assert.Null(EvidenceSanitizer.Check(Section(new EvidenceItem(text, null)), Never).Section);
            Assert.Null(EvidenceSanitizer.Check(Section(new EvidenceItem("label", text)), Never).Section);
            Assert.Null(EvidenceSanitizer.Check(new EvidenceSection("Title", [], text), Never).Section);
            // An empty title or label, and a severity outside the three, are as invalid.
            Assert.Null(EvidenceSanitizer.Check(new EvidenceSection(" ", []), Never).Section);
            Assert.Null(EvidenceSanitizer.Check(Section(new EvidenceItem("", null)), Never).Section);
            Assert.Null(EvidenceSanitizer.Check(Section(new EvidenceItem("x", null, Severity: "fatal")), Never).Section);
            Assert.NotNull(EvidenceSanitizer.Check(Section(new EvidenceItem("x", "plain, 'quoted' <b>text</b> and ünïcode", Severity: "warning")), Never).Section);
        }

        [Fact]
        public void Control_or_bidi_text_unpaired_surrogate_invalid()
        {
            // Built here: a test case's data can't carry a lone surrogate through discovery.
            foreach (var text in new[] { "lone" + '\uD800' + "x", "lone" + '\uDC00' + "x", "end" + '\uD800' })
            {
                Assert.Null(EvidenceSanitizer.Check(Section(new EvidenceItem("label", text)), Never).Section);
            }
        }

        [Fact]
        public void Link_must_match_manifest_route_else_dropped_item_kept()
        {
            using var app = CoreHost.Build(b => b.AddObservabilityModule<EvidenceSampleModule>());
            var catalog = app.Services.GetRequiredService<EvidenceCatalog>();
            EvidenceItem Item(EvidenceLink link) => new("x", "y", Link: link);
            var matching = new EvidenceLink("evidence-sample/abc-1", new Dictionary<string, string> { ["tab"] = "changes" });
            var section = EvidenceSanitizer.Check(Section(
                Item(matching),
                Item(new EvidenceLink("evidence-sample")),              // fewer segments than the pattern
                Item(new EvidenceLink("evidence-sample/a/b")),          // more
                Item(new EvidenceLink("unknown/abc")),                  // no manifest route
                Item(new EvidenceLink("https://evil.example/x")),       // not relative
                Item(new EvidenceLink("/evidence-sample/abc")),         // leading slash
                Item(new EvidenceLink("evidence-sample/..")),           // traversal
                Item(new EvidenceLink("evidence-sample/:id")),          // a pattern, not a route
                Item(new EvidenceLink("evidence-sample/abc", new Dictionary<string, string> { ["a b"] = "x" })),        // bad query key
                Item(new EvidenceLink("evidence-sample/abc", new Dictionary<string, string> { ["q"] = "x\ny" }))),      // control in a value
                catalog.RouteMatches).Section!;

            Assert.Equal(10, section.Items.Count);
            Assert.Equal(matching.Route, section.Items[0].Link!.Route);
            Assert.Equal("changes", section.Items[0].Link!.Query!["tab"]);
            Assert.All(section.Items.Skip(1), i => Assert.Null(i.Link));
            Assert.All(section.Items, i => Assert.Equal(("x", "y"), (i.Label, i.Value)));
        }

        [Fact]
        public void Options_out_of_range_refuse_start()
        {
            foreach (var (key, value) in new[] { ("Evidence:ContributorTimeout", "00:00:00.1"), ("Evidence:ContributorTimeout", "00:00:30"), ("Evidence:Budget", "00:00:00.5"), ("Evidence:Budget", "00:05:00") })
            {
                using var app = CoreHost.Build(settings: new Dictionary<string, string?> { [key] = value });
                Assert.Throws<OptionsValidationException>(() => app.Services.GetRequiredService<IOptions<EvidenceOptions>>().Value);
            }
            using var defaults = CoreHost.Build();
            var options = defaults.Services.GetRequiredService<IOptions<EvidenceOptions>>().Value;
            Assert.Equal((TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)), (options.ContributorTimeout, options.Budget));
        }
    }
}
