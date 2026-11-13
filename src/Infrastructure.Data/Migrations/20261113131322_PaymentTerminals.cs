using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentTerminals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TerminalPaymentId",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentReference",
                table: "BillPayments",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TerminalPaymentId",
                table: "BillPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentTerminals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    SerialNumber = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AssignedUserName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPingOk = table.Column<bool>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTerminals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TerminalPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PaymentTerminalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TerminalName = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    InvoiceReference = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComandaId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BillPaymentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CashShiftId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AuthorizationCode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    CardBrand = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    CardLast4 = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    ProviderReference = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    VoucherText = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequestedByName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerminalPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TerminalPayments_PaymentTerminals_PaymentTerminalId",
                        column: x => x.PaymentTerminalId,
                        principalTable: "PaymentTerminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTerminals_Name",
                table: "PaymentTerminals",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalPayments_BillPaymentId",
                table: "TerminalPayments",
                column: "BillPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalPayments_CashShiftId",
                table: "TerminalPayments",
                column: "CashShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalPayments_ComandaId",
                table: "TerminalPayments",
                column: "ComandaId");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalPayments_OrderId",
                table: "TerminalPayments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalPayments_PaymentTerminalId_RequestedAt",
                table: "TerminalPayments",
                columns: new[] { "PaymentTerminalId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TerminalPayments_Status",
                table: "TerminalPayments",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TerminalPayments");

            migrationBuilder.DropTable(
                name: "PaymentTerminals");

            migrationBuilder.DropColumn(
                name: "TerminalPaymentId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PaymentReference",
                table: "BillPayments");

            migrationBuilder.DropColumn(
                name: "TerminalPaymentId",
                table: "BillPayments");
        }
    }
}
