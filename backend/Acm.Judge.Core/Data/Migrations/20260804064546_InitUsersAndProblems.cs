using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Acm.Judge.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitUsersAndProblems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HashedPassword = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "problems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    InputDesc = table.Column<string>(type: "text", nullable: true),
                    OutputDesc = table.Column<string>(type: "text", nullable: true),
                    TimeLimit = table.Column<int>(type: "integer", nullable: false),
                    MemoryLimit = table.Column<int>(type: "integer", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    Difficulty = table.Column<short>(type: "smallint", nullable: true),
                    SampleInputs = table.Column<string>(type: "text", nullable: false),
                    SampleOutputs = table.Column<string>(type: "text", nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_problems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_problems_users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_problems_AuthorId",
                table: "problems",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_problems_Slug",
                table: "problems",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Username",
                table: "users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "problems");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
