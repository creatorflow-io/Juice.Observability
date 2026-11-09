using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Juice.Observability.Agent;

namespace Juice.Observability.Agent.Sample
{
    /// <param name="Path">The file to look at. The host's allowlist names the ones it may: <c>"Values": { "path": [ ... ] }</c>.</param>
    public sealed record FileInfoParameters(
        [property: MaxLength(256), RegularExpression(@"^(/|[A-Za-z]:\\)[A-Za-z0-9._\-/\\:]*$"), Description("An absolute path")] string Path);

    /// <summary>
    /// A custom action, as a customer would write one: read-only, typed, bounded. Whether a file exists, its size and when it last changed; never what's
    /// in it. The parameter's bounds come from its attributes, and the platform and the agent both check them before this runs.
    /// </summary>
    public sealed class FileInfoAction : AgentAction<FileInfoParameters>
    {
        public override string Name => "sample.file_info";

        public override string Description => "Whether a file exists, its size and when it last changed. Never its contents.";

        protected override Task<object?> RunAsync(FileInfoParameters parameters, CancellationToken cancellationToken)
        {
            if (parameters.Path.Split('/', '\\').Contains(".."))
            {
                throw new ActionFailedException("invalid_path");
            }
            var file = new FileInfo(parameters.Path);
            return Task.FromResult<object?>(file.Exists
                ? new { path = parameters.Path, exists = true, sizeBytes = file.Length, modifiedAt = file.LastWriteTimeUtc }
                : new { path = parameters.Path, exists = false, sizeBytes = (long?)null, modifiedAt = (DateTime?)null });
        }
    }

    internal static class Program
    {
        private static Task<int> Main(string[] args) => JuiceAgent.RunAsync(args, actions => actions.Add<FileInfoAction>());
    }
}
