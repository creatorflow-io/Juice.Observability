namespace Juice.Observability.Tests
{
    public class LocalStackResolutionTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "juice-ls-" + Guid.NewGuid().ToString("N"));

        public void Dispose() => System.IO.Directory.Delete(_root, true);

        private string Deploy(string checkout)
        {
            var dir = Path.Combine(_root, checkout, "deploy", "local");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Env(string dir) => File.WriteAllText(Path.Combine(dir, ".env"), "A=b");

        private string Checkout(string name)
        {
            var bin = Path.Combine(_root, name, "test", "bin");
            System.IO.Directory.CreateDirectory(bin);
            System.IO.Directory.CreateDirectory(Path.Combine(_root, name, ".git"));
            return bin;
        }

        [Fact]
        public void The_checkouts_own_deploy_wins_over_the_variable_and_the_main_worktree()
        {
            var bin = Checkout("wt");
            var own = Deploy("wt"); Env(own);
            var configured = Deploy("cfg"); Env(configured);
            var main = Deploy("main"); Env(main);

            Assert.Equal(own, LocalStack.ResolveDeployDirectory(bin, _ => configured, _ => Path.Combine(_root, "main")));
        }

        [Fact]
        public void The_variable_wins_over_the_main_worktree_when_the_checkout_has_no_env()
        {
            var bin = Checkout("wt");
            Deploy("wt"); // folder without .env
            var configured = Deploy("cfg"); Env(configured);
            var main = Deploy("main"); Env(main);

            Assert.Equal(configured, LocalStack.ResolveDeployDirectory(bin,
                k => k == "JUICE_LOCAL_DEPLOY" ? configured : null, _ => Path.Combine(_root, "main")));
        }

        [Fact]
        public void The_main_worktree_is_used_last()
        {
            var bin = Checkout("wt");
            var main = Deploy("main"); Env(main);

            Assert.Equal(main, LocalStack.ResolveDeployDirectory(bin, _ => null, _ => Path.Combine(_root, "main")));
            // A variable pointing at a folder without .env is ignored.
            Assert.Equal(main, LocalStack.ResolveDeployDirectory(bin, _ => Path.Combine(_root, "nothing"), _ => Path.Combine(_root, "main")));
        }

        [Fact]
        public void Nothing_found_is_null_so_ci_skips()
        {
            var bin = Checkout("wt");
            Deploy("main");

            Assert.Null(LocalStack.ResolveDeployDirectory(bin, _ => null, _ => Path.Combine(_root, "main")));
            Assert.Null(LocalStack.ResolveDeployDirectory(bin, _ => null, _ => null));
        }
    }
}
