using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CashOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClosedByName",
                table: "CashShifts",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClosedByUserId",
                table: "CashShifts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DifferenceOverrideId",
                table: "CashShifts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FloatLeftAmount",
                table: "CashShifts",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "OpenedByUserId",
                table: "CashShifts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasBlindCount",
                table: "CashShifts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Billers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    CommissionFixed = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    CommissionPercent = table.Column<decimal>(type: "TEXT", precision: 6, scale: 4, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Billers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CashShiftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BillerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BillerName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AccountReference = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    CustomerName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Commission = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    TenderedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ChangeAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    ReceiptNumber = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VoidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VoidOverrideId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VoidReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CashMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CashShiftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ManagerOverrideId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BillPaymentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashMovements_CashShifts_CashShiftId",
                        column: x => x.CashShiftId,
                        principalTable: "CashShifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PartialCashCounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CashShiftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpectedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    CountedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    LinesJson = table.Column<string>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CountedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartialCashCounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Billers_Name",
                table: "Billers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_CashShiftId",
                table: "BillPayments",
                column: "CashShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_ReceiptNumber",
                table: "BillPayments",
                column: "ReceiptNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_CashShiftId",
                table: "CashMovements",
                column: "CashShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_PartialCashCounts_CashShiftId",
                table: "PartialCashCounts",
                column: "CashShiftId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Billers");

            migrationBuilder.DropTable(
                name: "BillPayments");

            migrationBuilder.DropTable(
                name: "CashMovements");

            migrationBuilder.DropTable(
                name: "PartialCashCounts");

            migrationBuilder.DropColumn(
                name: "ClosedByName",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "ClosedByUserId",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "DifferenceOverrideId",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "FloatLeftAmount",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "OpenedByUserId",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "WasBlindCount",
                table: "CashShifts");
        }
    }
}
