using System.Reflection;
using System.Text.Json;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The manifest.json the build writes beside the agent (msbuild/Juice.Observability.Agent.targets): the launcher seeds and runs a version
    /// only from a manifest with a version and an entry point. Other tests write their own manifests; this one reads the build's.
    /// </summary>
    public sealed class AgentManifestTests
    {
        [Fact]
        public void The_build_writes_the_agents_version_and_entry_point()
        {
            var agent = typeof(Juice.Observability.Agent.JuiceAgent).Assembly;
            var root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "Juice.Observability.slnx"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("no repository root");
            var configuration = typeof(AgentManifestTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            var file = Path.Combine(root, "build", "bin", "Juice.Observability.Agent", configuration, "net10.0", AgentPackages.ManifestEntry);

            var manifest = JsonSerializer.Deserialize<AgentPackages.Manifest>(File.ReadAllBytes(file), CommandSignature.Tolerant)!;

            var version = agent.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
            Assert.NotEmpty(version);
            Assert.Equal(version, manifest.Version);
            Assert.Equal($"{agent.GetName().Name}.dll", manifest.EntryPoint);
        }
    }
}
