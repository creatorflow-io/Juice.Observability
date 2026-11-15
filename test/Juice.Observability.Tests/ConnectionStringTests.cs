using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// GSS encryption is off unless the deployment asks for it: Npgsql's probe for it, on every new connection, crashed the process in a burst of
    /// new connections (docs/scale-tests.md, the workers ceiling at 4×).
    /// </summary>
    public class ConnectionStringTests
    {
        [Theory]
        [InlineData("Host=db;Database=juice;Username=juice;Password=secret", GssEncryptionMode.Disable)]
        [InlineData("Host=db;Database=juice;GSS Encryption Mode=Prefer", GssEncryptionMode.Prefer)]
        [InlineData("Host=db;Database=juice;gssencryptionmode=require", GssEncryptionMode.Require)]
        public void Gss_encryption_is_off_unless_the_connection_string_sets_it(string given, GssEncryptionMode expected)
        {
            var result = new NpgsqlConnectionStringBuilder(PlatformDbContext.WithoutGssProbe(given));

            Assert.Equal(expected, result.GssEncryptionMode);
            Assert.Equal("db", result.Host);
            Assert.Equal("juice", result.Database);
        }

        [Fact]
        public void The_context_and_so_the_worker_leases_connect_without_the_probe()
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>();
            PlatformDbContext.Configure(options, "Host=db;Database=juice;Username=juice;Password=secret");
            using var db = new PlatformDbContext(options.Options);

            // WorkerLease builds its own unpooled connections from this.
            var used = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
            Assert.Equal(GssEncryptionMode.Disable, used.GssEncryptionMode);
            Assert.Equal("secret", used.Password);
        }
    }
}
