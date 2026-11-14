using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CinemaWatch.Server.Data.Migrations
{
    /// <summary>
    /// SaaS 0.7.0 (docs/SAAS.md stage 6.4.2): check_log tells the wizard's requests from the poller's; poller_settings
    /// holds the operator's overrides of the wizard's budget, the account limit and the pause between requests.
    /// </summary>
    public partial class AddWizard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "wizard_account_requests_per_hour",
                table: "poller_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "wizard_burst",
                table: "poller_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "wizard_gap_seconds",
                table: "poller_settings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "wizard_requests_per_hour",
                table: "poller_settings",
                type: "integer",
                nullable: true);

            // Every request logged before SaaS 0.7.0 came from the poller (RequestSource.Poller).
            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "check_log",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Poller");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "wizard_account_requests_per_hour",
                table: "poller_settings");

            migrationBuilder.DropColumn(
                name: "wizard_burst",
                table: "poller_settings");

            migrationBuilder.DropColumn(
                name: "wizard_gap_seconds",
                table: "poller_settings");

            migrationBuilder.DropColumn(
                name: "wizard_requests_per_hour",
                table: "poller_settings");

            migrationBuilder.DropColumn(
                name: "source",
                table: "check_log");
        }
    }
}
