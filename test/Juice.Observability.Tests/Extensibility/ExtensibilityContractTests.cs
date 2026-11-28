using Juice.Observability.Api;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Extensibility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Juice.Observability.Tests.Extensibility;

public class ExtensibilityContractTests
{
    private static readonly ModuleInfo Sample = new("sample", "Sample", "1.0.0", "[2.0.0,3.0.0)");

    [Theory]
    [InlineData("[2.0.0,3.0.0)", "2.0.0", "3.0.0", true, false)]
    [InlineData("(1.9,2.1]", "1.9.0", "2.1.0", false, true)]
    [InlineData("[2.0.0,)", "2.0.0", null, true, false)]
    [InlineData("[2.0.0]", "2.0.0", "2.0.0", true, true)]
    public void Range_Parses_NuGet_intervals(string text, string min, string? max, bool minInc, bool maxInc)
    {
        Assert.True(CoreVersionRange.TryParse(text, out var range));
        Assert.Equal(Version.Parse(min), range.Min);
        Assert.Equal(max is null ? null : Version.Parse(max), range.Max);
        Assert.Equal(minInc, range.MinInclusive);
        Assert.Equal(maxInc, range.MaxInclusive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[3,2)")]
    [InlineData("2.0")]
    [InlineData("[a,b)")]
    [InlineData("[2.0.0,3.0.0")]
    [InlineData("[,)")]
    [InlineData("[2.0.0]]")]
    public void Range_refuses_bad_input(string text)
        => Assert.False(CoreVersionRange.TryParse(text, out _));

    [Fact]
    public void Range_refuses_more_than_64_characters()
    {
        var text = "[2.0.0," + new string('0', 60) + ".0.0)";
        Assert.True(text.Length > 64);
        Assert.False(CoreVersionRange.TryParse(text, out _));
        Assert.False(CoreVersionRange.TryParse(null!, out _));
    }

    [Fact]
    public void Range_includes_follows_the_brackets()
    {
        CoreVersionRange.TryParse("[2.0.0,3.0.0)", out var r);
        Assert.True(r.Includes(new Version(2, 0, 0)));
        Assert.True(r.Includes(new Version(2, 9, 9)));
        Assert.False(r.Includes(new Version(3, 0, 0)));
        Assert.False(r.Includes(new Version(1, 9, 9)));
        CoreVersionRange.TryParse("(2.0.0,]", out _);
        CoreVersionRange.TryParse("(2.0.0,)", out var open);
        Assert.False(open.Includes(new Version(2, 0, 0)));
        Assert.True(open.Includes(new Version(2, 0, 1)));
        CoreVersionRange.TryParse("[2.0.0]", out var exact);
        Assert.True(exact.Includes(new Version(2, 0, 0)));
        Assert.False(exact.Includes(new Version(2, 0, 1)));
    }

    [Theory]
    [InlineData("2.0.0-alpha.12+abc")]
    [InlineData("2.0.0-local.20261128.1")]
    [InlineData("2.0.0")]
    public void CoreVersion_ignores_prerelease_and_metadata(string informational)
    {
        Assert.Equal(new Version(2, 0, 0), CoreVersion.Parse(informational));
        CoreVersionRange.TryParse("[2.0.0,3.0.0)", out var range);
        Assert.True(range.Includes(CoreVersion.Parse(informational)));
    }

    [Fact]
    public void CoreVersion_current_is_a_three_part_version()
    {
        Assert.Equal(3, new[] { CoreVersion.Current.Major, CoreVersion.Current.Minor, CoreVersion.Current.Build }.Length);
        Assert.True(CoreVersion.Current.Build >= 0);
        Assert.True(CoreVersion.Current.Major >= 2);
    }

    [Theory]
    [InlineData("sample", true)]
    [InlineData("pro-slos", true)]
    [InlineData("ab", true)]
    [InlineData("a", false)]
    [InlineData("Sample_X", false)]
    [InlineData("-x", false)]
    [InlineData("x-", false)]
    [InlineData("SLOS", false)]
    [InlineData("1abc", false)]
    [InlineData("sаmple", false)] // Cyrillic a
    [InlineData("ｓample", false)] // full-width s
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Name_rules(string? name, bool ok)
        => Assert.Equal(ok, ModuleValidation.Name(name) is null);

    [Fact]
    public void Name_length_limit_is_32()
    {
        Assert.Null(ModuleValidation.Name(new string('a', 32)));
        Assert.NotNull(ModuleValidation.Name(new string('a', 33)));
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.2.3-alpha.1+build.5", true)]
    [InlineData("0.0.1-rc.1", true)]
    [InlineData("1.0", false)]
    [InlineData("01.0.0", false)]
    [InlineData("1.0.0-", false)]
    [InlineData("1.0.0-01", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Version_must_be_semver(string? version, bool ok)
        => Assert.Equal(ok, ModuleValidation.Version(version) is null);

    [Fact]
    public void DisplayName_is_1_to_64_plain_text()
    {
        Assert.Null(ModuleValidation.DisplayName("Sample module"));
        Assert.NotNull(ModuleValidation.DisplayName(""));
        Assert.NotNull(ModuleValidation.DisplayName(new string('x', 65)));
        Assert.NotNull(ModuleValidation.DisplayName("<b>x</b>"));
        Assert.NotNull(ModuleValidation.DisplayName("a\nb"));
    }

    [Fact]
    public void Info_checks_every_field()
    {
        Assert.Null(ModuleValidation.Info(Sample));
        Assert.NotNull(ModuleValidation.Info(Sample with { Name = "X" }));
        Assert.NotNull(ModuleValidation.Info(Sample with { Version = "1" }));
        Assert.NotNull(ModuleValidation.Info(Sample with { SupportedCore = "2.0" }));
        Assert.NotNull(ModuleValidation.Info(Sample with { DisplayName = "" }));
    }

    [Fact]
    public void Policy_name_must_carry_module_prefix_and_core_roles_only()
    {
        Assert.Null(ModuleValidation.Policy(Sample, "sample.write", [CoreRoles.Operator, CoreRoles.Admin]));
        Assert.NotNull(ModuleValidation.Policy(Sample, "platform.read", [CoreRoles.Viewer]));
        Assert.NotNull(ModuleValidation.Policy(Sample, "other.x", [CoreRoles.Viewer]));
        Assert.NotNull(ModuleValidation.Policy(Sample, "sample.", [CoreRoles.Viewer]));
        Assert.NotNull(ModuleValidation.Policy(Sample, "sample.x", ["root"]));
        Assert.NotNull(ModuleValidation.Policy(Sample, "sample.x", []));
        Assert.NotNull(ModuleValidation.Policy(Sample, "sample.x", null));
        Assert.NotNull(ModuleValidation.Policy(Sample, "sample.x", [CoreRoles.Viewer, "Admin"]));
    }

    [Fact]
    public void CoreRoles_and_policies_keep_their_values()
    {
        Assert.Equal(["admin", "operator", "viewer"], CoreRoles.All.Order());
        Assert.Equal("platform.read", CorePolicies.Read);
        Assert.Equal("platform.operate", CorePolicies.Operate);
        Assert.Equal(Roles.Viewer, CoreRoles.Viewer);
        Assert.Equal(Policies.Read, CorePolicies.Read);
    }

    private static ConsoleManifest Manifest(
        string package = "@juice-js/observability-sample", IEnumerable<ConsoleRoute>? routes = null, IEnumerable<ConsoleMenuEntry>? menu = null)
        => new(package, (routes ?? [new ConsoleRoute("items", CorePolicies.Read)]).ToList(),
            (menu ?? [new ConsoleMenuEntry("items", "Items", "items", "list", "sample.write", 10)]).ToList());

    private static readonly IReadOnlySet<string> OwnPolicies = new HashSet<string> { "sample.write" };

    [Fact]
    public void Manifest_accepts_a_valid_one()
    {
        Assert.Null(ModuleValidation.Manifest(Manifest(), OwnPolicies));
        Assert.Null(ModuleValidation.Manifest(Manifest("@juice-js/observability"), OwnPolicies));
        Assert.Null(ModuleValidation.Manifest(Manifest(routes: [new ConsoleRoute("a/b/:id", null)]), OwnPolicies));
    }

    [Theory]
    [InlineData("javascript:x")]
    [InlineData("//evil")]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    [InlineData("/abs")]
    [InlineData("https://evil.example/x")]
    [InlineData("..")]
    [InlineData("")]
    public void Manifest_refuses_scheme_protocol_relative_dotdot_paths(string path)
    {
        Assert.NotNull(ModuleValidation.Manifest(Manifest(routes: [new ConsoleRoute(path, null)]), OwnPolicies));
        Assert.NotNull(ModuleValidation.Manifest(
            Manifest(menu: [new ConsoleMenuEntry("k", "L", path, null, null, 0)]), OwnPolicies));
    }

    [Fact]
    public void Manifest_refuses_long_labels_and_too_many_entries()
    {
        Assert.NotNull(ModuleValidation.Manifest(
            Manifest(menu: [new ConsoleMenuEntry("k", new string('x', 65), "items", null, null, 0)]), OwnPolicies));
        Assert.Null(ModuleValidation.Manifest(
            Manifest(menu: [new ConsoleMenuEntry("k", new string('x', 64), "items", null, null, 0)]), OwnPolicies));
        Assert.NotNull(ModuleValidation.Manifest(Manifest(
            menu: Enumerable.Range(0, 51).Select(i => new ConsoleMenuEntry($"k{i}", "L", "items", null, null, i))), OwnPolicies));
        Assert.Null(ModuleValidation.Manifest(Manifest(
            menu: Enumerable.Range(0, 50).Select(i => new ConsoleMenuEntry($"k{i}", "L", "items", null, null, i))), OwnPolicies));
        Assert.NotNull(ModuleValidation.Manifest(Manifest(
            routes: Enumerable.Range(0, 51).Select(i => new ConsoleRoute($"r{i}", null))), OwnPolicies));
    }

    [Fact]
    public void Manifest_refuses_bad_package_and_unknown_policy()
    {
        Assert.NotNull(ModuleValidation.Manifest(Manifest("evil-package"), OwnPolicies));
        Assert.NotNull(ModuleValidation.Manifest(Manifest("@other/observability-x"), OwnPolicies));
        Assert.NotNull(ModuleValidation.Manifest(Manifest(routes: [new ConsoleRoute("items", "other.x")]), OwnPolicies));
        Assert.NotNull(ModuleValidation.Manifest(
            Manifest(menu: [new ConsoleMenuEntry("k", "L", "items", null, "other.x", 0)]), OwnPolicies));
        Assert.Null(ModuleValidation.Manifest(Manifest(routes: [new ConsoleRoute("items", CorePolicies.Operate)]), OwnPolicies));
        Assert.NotNull(ModuleValidation.Manifest(null, OwnPolicies));
    }

    [Fact]
    public void Paging_matches_Api_cursor_encoding()
    {
        var id = Guid.NewGuid();
        Assert.Equal(new Cursor("alerts", id).Encode(), new PageCursor("alerts", id).Encode());
        Assert.Equal(Paging.DefaultLimit, Cursor.DefaultLimit);
        Assert.Equal(Paging.MaxLimit, Cursor.MaxLimit);

        var encoded = new Cursor("a", id).Encode();
        Assert.True(PageCursor.TryDecode(encoded, out var back));
        Assert.Equal(new PageCursor("a", id), back);

        // an Api cursor is a valid module cursor and the reverse
        Assert.True(Cursor.TryDecode(new PageCursor("b", id).Encode(), out var api));
        Assert.Equal(new Cursor("b", id), api);

        string?[] bad = [new string('A', 129), "!!!garbage", "bm90LWEtY3Vyc29y", "AAAA"];
        foreach (var value in bad)
        {
            Assert.Equal(Cursor.TryDecode(value, out _), PageCursor.TryDecode(value, out _));
            Assert.False(PageCursor.TryDecode(value, out _));
        }
        Assert.True(PageCursor.TryDecode(null, out var none));
        Assert.Null(none);
        Assert.True(PageCursor.TryDecode("", out none));
        Assert.Null(none);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(201, null)]
    [InlineData(-1, null)]
    [InlineData(null, "garbage!")]
    [InlineData(10, "garbage!")]
    [InlineData(null, "129")]
    [InlineData(1, null)]
    [InlineData(200, null)]
    [InlineData(null, null)]
    public void Paging_Validate_gives_the_same_answer_as_Api(int? limit, string? cursor)
    {
        if (cursor == "129")
        {
            cursor = new string('A', 129);
        }
        var mine = Paging.Validate(limit, cursor, out var take, out var after);
        var theirs = Cursor.Validate(limit, cursor, out var apiTake, out var apiAfter);
        Assert.Equal(apiTake, take);
        Assert.Equal(theirs is null, mine is null);
        Assert.Equal(apiAfter?.Phase, after?.Phase);
        if (mine is not null)
        {
            var a = Assert.IsType<ProblemHttpResult>(mine).ProblemDetails;
            var b = Assert.IsType<ProblemHttpResult>(theirs).ProblemDetails;
            Assert.Equal(b.Status, a.Status);
            Assert.Equal(StatusCodes.Status400BadRequest, a.Status);
            Assert.Equal(b.Title, a.Title);
            Assert.Equal(b.Detail, a.Detail);
        }
    }
}
