using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Terminals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TerminalCode",
                table: "Orders",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TerminalId",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TerminalCode",
                table: "CashShifts",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TerminalId",
                table: "CashShifts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TerminalCode",
                table: "BillPayments",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TerminalId",
                table: "BillPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyncInboxReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TerminalCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AggregateType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AggregateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncInboxReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Terminals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCatalogPullAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AppVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    LastAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Terminals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TerminalId",
                table: "Orders",
                column: "TerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_CashShifts_TerminalCode_ShiftNumber",
                table: "CashShifts",
                columns: new[] { "TerminalCode", "ShiftNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashShifts_TerminalId",
                table: "CashShifts",
                column: "TerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_TerminalId",
                table: "BillPayments",
                column: "TerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncInboxReceipts_MessageId",
                table: "SyncInboxReceipts",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncInboxReceipts_TerminalCode_ReceivedAt",
                table: "SyncInboxReceipts",
                columns: new[] { "TerminalCode", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Terminals_Code",
                table: "Terminals",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncInboxReceipts");

            migrationBuilder.DropTable(
                name: "Terminals");

            migrationBuilder.DropIndex(
                name: "IX_Orders_TerminalId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_CashShifts_TerminalCode_ShiftNumber",
                table: "CashShifts");

            migrationBuilder.DropIndex(
                name: "IX_CashShifts_TerminalId",
                table: "CashShifts");

            migrationBuilder.DropIndex(
                name: "IX_BillPayments_TerminalId",
                table: "BillPayments");

            migrationBuilder.DropColumn(
                name: "TerminalCode",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TerminalId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TerminalCode",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "TerminalId",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "TerminalCode",
                table: "BillPayments");

            migrationBuilder.DropColumn(
                name: "TerminalId",
                table: "BillPayments");
        }
    }
}
