using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Juice.Observability.Api.Integrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Juice.Observability.Tests.Integrations.Instances
{
    /// <summary>The shipped idempotent script is what the module's migrations generate: a migration added without regenerating it fails here.</summary>
    [Trait("Milestone", "INT-I1")]
    public sealed class InstanceScriptDriftTests
    {
        private static string Shipped([CallerFilePath] string here = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", "deploy", "migrations", "integrations.sql"));

        private static string Normalize(string sql) => Regex.Replace(sql.TrimStart('﻿'), @"\s+", " ").Trim();

        [Fact]
        public void Shipped_script_matches_the_migrations()
        {
            using var db = new IntegrationsDesignTimeFactory().CreateDbContext([]);
            var generated = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

            Assert.Equal(Normalize(generated), Normalize(File.ReadAllText(Shipped())));
        }
    }
}
