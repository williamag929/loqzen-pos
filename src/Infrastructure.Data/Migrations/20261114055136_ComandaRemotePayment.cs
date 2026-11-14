using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ComandaRemotePayment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaidByTerminalCode",
                table: "Comandas",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentLockedAt",
                table: "Comandas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentLockedByTerminalCode",
                table: "Comandas",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RemoteOrderId",
                table: "Comandas",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteOrderNumber",
                table: "Comandas",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Comandas_PaidByTerminalCode",
                table: "Comandas",
                column: "PaidByTerminalCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Comandas_PaidByTerminalCode",
                table: "Comandas");

            migrationBuilder.DropColumn(
                name: "PaidByTerminalCode",
                table: "Comandas");

            migrationBuilder.DropColumn(
                name: "PaymentLockedAt",
                table: "Comandas");

            migrationBuilder.DropColumn(
                name: "PaymentLockedByTerminalCode",
                table: "Comandas");

            migrationBuilder.DropColumn(
                name: "RemoteOrderId",
                table: "Comandas");

            migrationBuilder.DropColumn(
                name: "RemoteOrderNumber",
                table: "Comandas");
        }
    }
}
