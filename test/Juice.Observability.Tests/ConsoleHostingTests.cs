using System.Net;
using static Juice.Observability.Tests.ApiTests;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The API serves Juice Console from its web root (UI architecture §3.3): files as files, any other path outside the
    /// API as the Console's index page, and API paths never as the Console.
    /// </summary>
    public class ConsoleHostingTests
    {
        [Fact]
        public async Task Console_files_routes_and_api_paths_are_told_apart_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var root = Directory.CreateTempSubdirectory("juice-console-").FullName;
            try
            {
                await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><title>console</title>", Ct);
                await File.WriteAllTextAsync(Path.Combine(root, "main-ABC.js"), "export const x = 1;", Ct);
                await using var factory = new ApiFactory(database, settings: new Dictionary<string, string> { ["webroot"] = root });
                using var client = factory.CreateClient();

                using var script = await client.GetAsync("/main-ABC.js", Ct);
                Assert.Equal(HttpStatusCode.OK, script.StatusCode);
                Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);

                using var route = await client.GetAsync("/services/0b9e?env=production", Ct);
                Assert.Equal("text/html", route.Content.Headers.ContentType?.MediaType);
                Assert.Equal("DENY", route.Headers.GetValues("X-Frame-Options").Single());

                // A missing file is never answered with the index page (a stale chunk must fail, not load HTML as script).
                using var missingFile = await client.GetAsync("/chunk-GONE.js", Ct);
                Assert.NotEqual(HttpStatusCode.OK, missingFile.StatusCode);
                Assert.NotEqual("text/html", missingFile.Content.Headers.ContentType?.MediaType);

                using var api = await client.GetAsync("/api/v1/nope", Ct);
                Assert.NotEqual("text/html", api.Content.Headers.ContentType?.MediaType);
                Assert.True(api.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, api.StatusCode.ToString());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
