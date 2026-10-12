using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AtmCashManagement.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "atms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_atms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "exchange_rates",
                columns: table => new
                {
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    rate_to_try = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_rates", x => x.currency);
                    table.CheckConstraint("ck_exchange_rates_rate", "rate_to_try > 0");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    response_body = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cassettes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    atm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    denomination = table.Column<int>(type: "integer", nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cassettes", x => x.id);
                    table.CheckConstraint("ck_cassettes_capacity", "capacity > 0");
                    table.CheckConstraint("ck_cassettes_slot", "slot BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_cassettes_atms_atm_id",
                        column: x => x.atm_id,
                        principalTable: "atms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deposit_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    atm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unrecognised_count = table.Column<int>(type: "integer", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deposit_transactions", x => x.id);
                    table.ForeignKey(
                        name: "fk_deposit_transactions_atms_atm_id",
                        column: x => x.atm_id,
                        principalTable: "atms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reconciliations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    atm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    total_difference_try = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lines = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliations", x => x.id);
                    table.ForeignKey(
                        name: "fk_reconciliations_atms_atm_id",
                        column: x => x.atm_id,
                        principalTable: "atms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "withdrawal_quotes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    atm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    card_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    requested_amount = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    held_amount = table.Column<int>(type: "integer", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_option = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    confirmed_amount = table.Column<int>(type: "integer", nullable: true),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    reservations = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_withdrawal_quotes", x => x.id);
                    table.ForeignKey(
                        name: "fk_withdrawal_quotes_atms_atm_id",
                        column: x => x.atm_id,
                        principalTable: "atms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cassette_movements",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    atm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cassette_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    denomination = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    count_delta = table.Column<int>(type: "integer", nullable: false),
                    reserved_delta = table.Column<int>(type: "integer", nullable: false),
                    reference_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cassette_movements", x => x.id);
                    table.ForeignKey(
                        name: "fk_cassette_movements_atms_atm_id",
                        column: x => x.atm_id,
                        principalTable: "atms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cassette_movements_cassettes_cassette_id",
                        column: x => x.cassette_id,
                        principalTable: "cassettes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cassette_stocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cassette_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    denomination = table.Column<int>(type: "integer", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    reserved = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cassette_stocks", x => x.id);
                    table.CheckConstraint("ck_cassette_stocks_count", "count >= 0");
                    table.CheckConstraint("ck_cassette_stocks_reserved", "reserved >= 0 AND reserved <= count");
                    table.ForeignKey(
                        name: "fk_cassette_stocks_cassettes_cassette_id",
                        column: x => x.cassette_id,
                        principalTable: "cassettes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "exchange_rates",
                columns: new[] { "currency", "rate_to_try", "updated_at" },
                values: new object[,]
                {
                    { "EUR", 49.00m, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { "TRY", 1m, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { "USD", 42.50m, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });

            migrationBuilder.CreateIndex(
                name: "ix_atms_code",
                table: "atms",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cassette_movements_atm_id_id",
                table: "cassette_movements",
                columns: new[] { "atm_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_cassette_movements_cassette_id_currency_denomination",
                table: "cassette_movements",
                columns: new[] { "cassette_id", "currency", "denomination" });

            migrationBuilder.CreateIndex(
                name: "ix_cassette_movements_reference_id",
                table: "cassette_movements",
                column: "reference_id");

            migrationBuilder.CreateIndex(
                name: "ix_cassette_stocks_cassette_id_currency_denomination",
                table: "cassette_stocks",
                columns: new[] { "cassette_id", "currency", "denomination" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cassettes_atm_id_slot",
                table: "cassettes",
                columns: new[] { "atm_id", "slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deposit_transactions_atm_id",
                table: "deposit_transactions",
                column: "atm_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unsent",
                table: "outbox_messages",
                column: "id",
                filter: "sent_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_reconciliations_atm_id_created_at",
                table: "reconciliations",
                columns: new[] { "atm_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_withdrawal_quotes_atm_id_status_expires_at",
                table: "withdrawal_quotes",
                columns: new[] { "atm_id", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_withdrawal_quotes_card_hash_currency_business_date",
                table: "withdrawal_quotes",
                columns: new[] { "card_hash", "currency", "business_date" });

            // Added by hand: the ledger is append-only at database level too, so that not even
            // a manual SQL statement can rewrite history.
            migrationBuilder.Sql("""
                CREATE FUNCTION prevent_cassette_movement_change() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'cassette_movements is append-only: % is not allowed', TG_OP;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_cassette_movements_append_only
                    BEFORE UPDATE OR DELETE ON cassette_movements
                    FOR EACH ROW EXECUTE FUNCTION prevent_cassette_movement_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cassette_movements");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS prevent_cassette_movement_change();");

            migrationBuilder.DropTable(
                name: "cassette_stocks");

            migrationBuilder.DropTable(
                name: "deposit_transactions");

            migrationBuilder.DropTable(
                name: "exchange_rates");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "reconciliations");

            migrationBuilder.DropTable(
                name: "withdrawal_quotes");

            migrationBuilder.DropTable(
                name: "cassettes");

            migrationBuilder.DropTable(
                name: "atms");
        }
    }
}
