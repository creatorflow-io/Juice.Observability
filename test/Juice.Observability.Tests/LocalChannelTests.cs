using Juice.Messaging.Local;
using Juice.Observability.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The local channel hands each message to a handler of its own, with no bound unless one is set. A burst of alert transitions
    /// planned its notifications all at once, past PostgreSQL's connections (docs/scale-tests.md, the workers ceiling): the host bounds it.
    /// </summary>
    public class LocalChannelTests
    {
        [Theory]
        [InlineData(null, PlatformExtensions.DefaultLocalConcurrency)]
        [InlineData("3", 3)]
        [InlineData("0", PlatformExtensions.DefaultLocalConcurrency)]
        public async Task The_host_bounds_how_many_messages_are_handled_at_once_Async(string? setting, int expected)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var settings = setting is null ? null : new Dictionary<string, string> { [$"{PlatformExtensions.LocalChannelSection}:MaxConcurrency"] = setting };
            await using var factory = new ApiTests.ApiFactory(database, settings: settings);

            Assert.Equal(expected, factory.Services.GetRequiredService<IOptions<LocalChannelOptions>>().Value.MaxConcurrency);
        }
    }
}
