using Juice.Observability.Api.Endpoints;

namespace Juice.Observability.Tests
{
    /// <summary>What a pipeline reports is untrusted: secrets must not reach a change event (the Phase 5 gate).</summary>
    public class ChangeSanitizerTests
    {
        [Theory]
        [InlineData("deploy --password hunter2 now", "hunter2")]
        [InlineData("deploy -p --api-key=abc123xyz", "abc123xyz")]
        [InlineData("password=hunter2", "hunter2")]
        [InlineData("PWD: hunter2", "hunter2")]
        [InlineData("Server=db;User Id=sa;Password=hunter2;Encrypt=true", "hunter2")]
        [InlineData("Pwd=hunter2", "hunter2")]
        [InlineData("{\"client_secret\": \"s3cr3t value\"}", "s3cr3t value")]
        [InlineData("api_key = 'abc 123'", "abc 123")]
        [InlineData("AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI", "wJalrXUtnFEMI")]
        [InlineData("export ACCESS_TOKEN=abc.def", "abc.def")]
        [InlineData("pulled https://bot:hunter2@git.example.com/repo.git", "hunter2")]
        [InlineData("Authorization: Bearer abcdef123456", "abcdef123456")]
        [InlineData("curl -H 'authorization: basic dXNlcjpwYXNz'", "dXNlcjpwYXNz")]
        [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl sent", "eyJhbGciOiJIUzI1NiJ9")]
        [InlineData("used ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_abcdefghij")]
        [InlineData("used glpat-abcdefghij123456", "glpat-abcdefghij")]
        [InlineData("used xoxb-1234567890-abcdefg", "xoxb-1234567890")]
        [InlineData("key AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE")]
        public void A_secret_is_masked(string text, string secret)
        {
            var result = ChangeSanitizer.Text(text, ChangeSanitizer.MaxSummary);

            Assert.DoesNotContain(secret, result);
            Assert.Contains(ChangeSanitizer.Mask, result);
        }

        [Theory]
        [InlineData("Deployed orders 1.4.2 to production")]
        [InlineData("Rotated the database password")]
        [InlineData("Fixed the token refresh bug in checkout")]
        [InlineData("See https://git.example.com/repo/pull/12")]
        public void An_ordinary_summary_is_left_alone(string text)
            => Assert.Equal(text, ChangeSanitizer.Text(text, ChangeSanitizer.MaxSummary));

        [Fact]
        public void Text_is_trimmed_bounded_and_kept_on_one_line()
        {
            Assert.Equal("a b", ChangeSanitizer.Text("  a\n\tb \r\n", 100));
            Assert.Equal("abc", ChangeSanitizer.Text("abcdef", 3));
            Assert.Equal("", ChangeSanitizer.Text(null, 3));
        }

        [Fact]
        public void Only_allowed_attributes_are_kept_and_their_values_masked()
        {
            var kept = ChangeSanitizer.Attributes(new Dictionary<string, string?>
            {
                ["commit"] = "abc123",
                ["url"] = "https://bot:hunter2@ci.example.com/42",
                ["ticket"] = "",
                ["DB_PASSWORD"] = "hunter2",
                ["customer"] = "someone@example.com",
                ["branch"] = null,
            }, out var dropped);

            Assert.Equal(["commit", "url"], kept.Keys.Order());
            Assert.Equal("abc123", kept["commit"]);
            Assert.DoesNotContain("hunter2", kept["url"]);
            Assert.Equal(4, dropped);
        }

        [Fact]
        public void No_attributes_is_fine()
        {
            Assert.Empty(ChangeSanitizer.Attributes(null, out var dropped));
            Assert.Equal(0, dropped);
        }
    }
}
