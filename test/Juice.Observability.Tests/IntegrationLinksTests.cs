using Juice.Observability.Api.Endpoints;

namespace Juice.Observability.Tests
{
    /// <summary>Grafana is optional (UI architecture §5): without it the API returns no links, never a broken one.</summary>
    public class IntegrationLinksTests
    {
        private static readonly DateTimeOffset At = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

        [Theory]
        [InlineData("")]
        [InlineData("not a url")]
        [InlineData("javascript:alert(1)")]
        [InlineData("file:///etc/passwd")]
        public void No_or_unsafe_Grafana_url_gives_no_links(string baseUrl)
            => Assert.Empty(IntegrationLinks.ForService(new IntegrationOptions { Grafana = { BaseUrl = baseUrl } }, "orders", At));

        [Fact]
        public void Grafana_link_encodes_the_name_and_drops_the_configured_path()
        {
            var link = Assert.Single(IntegrationLinks.ForService(
                new IntegrationOptions { Grafana = { BaseUrl = "https://grafana.example/some/path" } }, "a&b c", At));
            Assert.Equal("grafana", link.Integration);
            Assert.Equal($"https://grafana.example/d/juice-overview?var-service=a%26b%20c&from={At.AddHours(-1).ToUnixTimeMilliseconds()}&to={At.ToUnixTimeMilliseconds()}", link.Url);
        }
    }
}
