using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prospecta.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase4Collection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OsmFilter",
                table: "BusinessCategories",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CollectionJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ConnectorKey = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UserId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ParametersJson = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Found = table.Column<int>(type: "int", nullable: false),
                    NewCount = table.Column<int>(type: "int", nullable: false),
                    DuplicateCount = table.Column<int>(type: "int", nullable: false),
                    ImportedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RetryOfJobId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionJobs", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "ConnectorUsage",
                columns: table => new
                {
                    ConnectorKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Calls = table.Column<int>(type: "int", nullable: false),
                    LastCallAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorUsage", x => new { x.ConnectorKey, x.Day });
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "CollectionResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    JobId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ExternalId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PayloadJson = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MatchBusinessId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    MatchReason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BusinessId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollectionResults_CollectionJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "CollectionJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionJobs_CreatedAt",
                table: "CollectionJobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionJobs_UserId_CreatedAt",
                table: "CollectionJobs",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionResults_ExternalId",
                table: "CollectionResults",
                column: "ExternalId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionResults_JobId_Status",
                table: "CollectionResults",
                columns: new[] { "JobId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectionResults");

            migrationBuilder.DropTable(
                name: "ConnectorUsage");

            migrationBuilder.DropTable(
                name: "CollectionJobs");

            migrationBuilder.DropColumn(
                name: "OsmFilter",
                table: "BusinessCategories");
        }
    }
}
