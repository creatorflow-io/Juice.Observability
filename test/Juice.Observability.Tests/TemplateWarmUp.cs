using System.Text.Json;
using Juice.Notifications;
using Juice.Notifications.Rendering;
using Juice.Observability.Notifications;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The ported templates rendered once per test process before any test times one (QA): the renderer's own warm-up doesn't cover what they use,
    /// so the first render of the webhook template costs about 100 ms cold, and under a full suite's load it can pass the 200 ms budget. The
    /// pipeline retries such a timeout; a test that compares bytes would fail instead.
    /// </summary>
    internal static class TemplateWarmUp
    {
        private static readonly Lazy<Task> Once = new(WarmAsync);

        public static Task EnsureAsync() => Once.Value;

        private static async Task WarmAsync()
        {
            var model = new RenderModel(ObservabilityTypes.AlertFiring, 1, "Alert firing", NotificationPriority.High,
                JsonSerializer.SerializeToElement(new { type = "alert.firing", alert = new { id = "x", value = 0.5 } }), new Dictionary<string, string>(), new Dictionary<string, string>());
            foreach (var (_, body) in DefaultTemplates.ByChannel)
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        await TemplateRenderer.RenderAsync(body, model, CancellationToken.None);
                        break;
                    }
                    catch (RenderException)
                    {
                        // Cold, or the data doesn't fit this channel's template: either way it is warm now.
                    }
                }
            }
        }
    }
}
