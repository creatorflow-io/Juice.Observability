using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <summary>
    /// Juice 10.13.1 (I73) quotes the column names in the outbox delivery indexes' filters (<c>"state" = 0</c> for <c>state = 0</c>). PostgreSQL
    /// stores both as the same predicate, so the indexes are unchanged: only the model snapshot moves, and no index is rebuilt.
    /// </summary>
    public partial class JuiceOutbox1013 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
