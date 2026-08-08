using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Acm.Judge.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubmissionsAndCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AcCount",
                table: "problems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SubmitCount",
                table: "problems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "submissions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ProblemId = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    CodeLength = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    TimeMs = table.Column<int>(type: "integer", nullable: true),
                    MemoryKb = table.Column<int>(type: "integer", nullable: true),
                    Detail = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_submissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_submissions_problems_ProblemId",
                        column: x => x.ProblemId,
                        principalTable: "problems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_submissions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_submissions_ProblemId_Id",
                table: "submissions",
                columns: new[] { "ProblemId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_submissions_UserId_Id",
                table: "submissions",
                columns: new[] { "UserId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "submissions");

            migrationBuilder.DropColumn(
                name: "AcCount",
                table: "problems");

            migrationBuilder.DropColumn(
                name: "SubmitCount",
                table: "problems");
        }
    }
}
