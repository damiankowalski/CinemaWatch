using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CinemaWatch.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOperator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paused_by_operator_at",
                table: "poller_state",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "component_status",
                columns: table => new
                {
                    component = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_ok_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_component_status", x => x.component);
                });

            migrationBuilder.CreateTable(
                name: "poller_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    min_interval_minutes = table.Column<int>(type: "integer", nullable: true),
                    requests_per_hour = table.Column<int>(type: "integer", nullable: true),
                    request_gap_min_seconds = table.Column<double>(type: "double precision", nullable: true),
                    request_gap_max_seconds = table.Column<double>(type: "double precision", nullable: true),
                    manual_gap_min_seconds = table.Column<double>(type: "double precision", nullable: true),
                    manual_gap_max_seconds = table.Column<double>(type: "double precision", nullable: true),
                    manual_fresh_minutes = table.Column<int>(type: "integer", nullable: true),
                    jitter_percent = table.Column<int>(type: "integer", nullable: true),
                    first_block_pause_minutes = table.Column<int>(type: "integer", nullable: true),
                    max_block_pause_minutes = table.Column<int>(type: "integer", nullable: true),
                    reuse_screening_details = table.Column<bool>(type: "boolean", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_poller_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "component_status");

            migrationBuilder.DropTable(
                name: "poller_settings");

            migrationBuilder.DropColumn(
                name: "paused_by_operator_at",
                table: "poller_state");
        }
    }
}
