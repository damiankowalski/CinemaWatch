using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CinemaWatch.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPoller : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "check_pending",
                table: "profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_manual_check_at",
                table: "profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "check_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cycle = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    screening_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    manual = table.Column<bool>(type: "boolean", nullable: false),
                    method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    host = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    target = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    cf_mitigated = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    cf_ray = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    retry_after = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_check_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_state",
                columns: table => new
                {
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    screening_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    available_seat_keys = table.Column<List<string>>(type: "text[]", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_state", x => new { x.profile_id, x.screening_id });
                    table.ForeignKey(
                        name: "fk_notification_state_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered = table.Column<bool>(type: "boolean", nullable: false),
                    subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "poller_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    blocked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    block_count = table.Column<int>(type: "integer", nullable: false),
                    blocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    block_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_cycle_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_cleanup_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_poller_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "seat_plans",
                columns: table => new
                {
                    venue_id = table.Column<int>(type: "integer", nullable: false),
                    seatplan_id = table.Column<int>(type: "integer", nullable: false),
                    json = table.Column<string>(type: "text", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seat_plans", x => new { x.venue_id, x.seatplan_id });
                });

            migrationBuilder.CreateTable(
                name: "service_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    data = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "watched_screenings",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cinema_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    interval_minutes = table.Column<int>(type: "integer", nullable: false),
                    next_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    manual_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_succeeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_http_status = table.Column<int>(type: "integer", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    free_seat_keys = table.Column<List<string>>(type: "text[]", nullable: true),
                    free_count = table.Column<int>(type: "integer", nullable: true),
                    total_count = table.Column<int>(type: "integer", nullable: true),
                    venue_id = table.Column<int>(type: "integer", nullable: true),
                    seatplan_id = table.Column<int>(type: "integer", nullable: true),
                    venue_type_id = table.Column<int>(type: "integer", nullable: true),
                    is_reserved = table.Column<int>(type: "integer", nullable: true),
                    details_fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watched_screenings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_check_log_at",
                table: "check_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_created_at",
                table: "notifications",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_profile_id_created_at",
                table: "notifications",
                columns: new[] { "profile_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_service_events_at",
                table: "service_events",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_watched_screenings_next_check_at",
                table: "watched_screenings",
                column: "next_check_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "check_log");

            migrationBuilder.DropTable(
                name: "notification_state");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "poller_state");

            migrationBuilder.DropTable(
                name: "seat_plans");

            migrationBuilder.DropTable(
                name: "service_events");

            migrationBuilder.DropTable(
                name: "watched_screenings");

            migrationBuilder.DropColumn(
                name: "check_pending",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "last_manual_check_at",
                table: "profiles");
        }
    }
}
