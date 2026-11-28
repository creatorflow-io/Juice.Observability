using Juice.Observability.Api.Integrations;

namespace Juice.Observability.Tests.Integrations.Instances
{
    /// <summary>A <c>file:</c> reference stays inside the secrets folder however the path is linked.</summary>
    [Trait("Milestone", "INT-I1")]
    public sealed class SecretFolderTests : IDisposable
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "juice-secrets-" + Guid.NewGuid().ToString("N"));

        public SecretFolderTests() => Directory.CreateDirectory(_root);

        public void Dispose() => Directory.Delete(_root, true);

        private string Folder() => Directory.CreateDirectory(Path.Combine(_root, "secrets")).FullName;

        private string Outside(string content = "outside-value")
        {
            var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "token"), content);
            return outside;
        }

        private static bool TryLink(string link, string target)
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                // Windows without the symbolic link privilege: a junction is the same escape and needs none.
                if (!OperatingSystem.IsWindows())
                {
                    return false;
                }
                using var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true });
                junction!.WaitForExit();
                return junction.ExitCode == 0;
            }
        }

        [Fact]
        public async Task A_file_inside_the_folder_resolves_and_a_trailing_newline_is_dropped()
        {
            var folder = Folder();
            File.WriteAllText(Path.Combine(folder, "token"), "inside-value\n");
            var (value, reason) = await SecretReference.ResolveAsync("file:" + Path.Combine(folder, "token"), "JUICE_INTEGRATION_", folder, Ct);
            Assert.Equal("inside-value", value);
            Assert.Null(reason);
        }

        [Fact]
        public async Task A_linked_folder_inside_the_secret_folder_pointing_out_is_refused()
        {
            var folder = Folder();
            var outside = Outside();
            if (!TryLink(Path.Combine(folder, "sub"), outside))
            {
                Assert.Skip("Symbolic links can't be created here.");
            }
            var (value, reason) = await SecretReference.ResolveAsync("file:" + Path.Combine(folder, "sub", "token"), "JUICE_INTEGRATION_", folder, Ct);
            Assert.Null(value);
            Assert.Contains("outside", reason);
        }

        [Fact]
        public async Task A_link_further_up_the_path_is_followed_for_every_part()
        {
            var folder = Folder();
            var outside = Outside();
            Directory.CreateDirectory(Path.Combine(folder, "real", "deeper"));
            if (!TryLink(Path.Combine(folder, "real", "deeper", "escape"), outside))
            {
                Assert.Skip("Symbolic links can't be created here.");
            }
            var (value, _) = await SecretReference.ResolveAsync("file:" + Path.Combine(folder, "real", "deeper", "escape", "token"), "JUICE_INTEGRATION_", folder, Ct);
            Assert.Null(value);
        }

        [Fact]
        public async Task A_secret_folder_that_is_itself_a_link_and_a_link_staying_inside_both_resolve()
        {
            var folder = Folder();
            File.WriteAllText(Path.Combine(folder, "token"), "inside-value");
            Directory.CreateDirectory(Path.Combine(folder, "real"));
            File.WriteAllText(Path.Combine(folder, "real", "token"), "real-value");
            if (!TryLink(Path.Combine(folder, "alias"), Path.Combine(folder, "real")))
            {
                Assert.Skip("Symbolic links can't be created here.");
            }
            Assert.Equal("real-value", (await SecretReference.ResolveAsync("file:" + Path.Combine(folder, "alias", "token"), "JUICE_INTEGRATION_", folder, Ct)).Value);
            // the configured folder reached through a link is the same folder
            var viaLink = Path.Combine(_root, "folder-link");
            Assert.True(TryLink(viaLink, folder));
            Assert.Equal("inside-value", (await SecretReference.ResolveAsync("file:" + Path.Combine(viaLink, "token"), "JUICE_INTEGRATION_", viaLink, Ct)).Value);
        }
    }
}
