using System.Text.Json;
using AtmCashManagement.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCashManagement.Api.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Atm> Atms => Set<Atm>();
    public DbSet<Cassette> Cassettes => Set<Cassette>();
    public DbSet<CassetteStock> CassetteStocks => Set<CassetteStock>();
    public DbSet<CassetteMovement> CassetteMovements => Set<CassetteMovement>();
    public DbSet<WithdrawalQuote> WithdrawalQuotes => Set<WithdrawalQuote>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<DepositTransaction> DepositTransactions => Set<DepositTransaction>();
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Loads an ATM with its cassettes and takes a row lock on it (SELECT ... FOR UPDATE).
    /// Every operation that changes an ATM's cash starts with this call, so such operations run
    /// one at a time per ATM while different ATMs stay independent. Must be called inside a
    /// transaction; the lock is released on commit or rollback.
    /// </summary>
    public async Task<Atm?> LockAtmAsync(Guid atmId, CancellationToken ct)
    {
        var atm = (await Atms
                .FromSqlInterpolated($"SELECT * FROM atms WHERE id = {atmId} FOR UPDATE")
                .ToListAsync(ct))
            .SingleOrDefault();

        if (atm is null)
        {
            return null;
        }

        // Loaded after the lock is held, so the stock figures cannot be stale.
        await Cassettes
            .Include(c => c.Stocks)
            .Where(c => c.AtmId == atmId)
            .OrderBy(c => c.Slot)
            .ToListAsync(ct);

        atm.Cassettes.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        return atm;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardLedger();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        GuardLedger();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    /// <summary>Movements are append-only. A database trigger enforces the same rule for raw SQL.</summary>
    private void GuardLedger()
    {
        var tampered = ChangeTracker.Entries<CassetteMovement>()
            .Any(e => e.State is EntityState.Modified or EntityState.Deleted);

        if (tampered)
        {
            throw new InvalidOperationException("Cassette movements are immutable and cannot be updated or deleted.");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Atm>(b =>
        {
            b.ToTable("atms");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(32).IsRequired();
            b.HasIndex(x => x.Code).IsUnique();
            b.Property(x => x.Location).HasMaxLength(200);
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            b.HasMany(x => x.Cassettes).WithOne().HasForeignKey(c => c.AtmId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Cassette>(b =>
        {
            b.ToTable("cassettes", t =>
            {
                t.HasCheckConstraint("ck_cassettes_slot", "slot BETWEEN 1 AND 5");
                t.HasCheckConstraint("ck_cassettes_capacity", "capacity > 0");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
            b.HasIndex(x => new { x.AtmId, x.Slot }).IsUnique();
            b.HasMany(x => x.Stocks).WithOne().HasForeignKey(s => s.CassetteId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CassetteStock>(b =>
        {
            // Last line of defence: even a bug in the application cannot oversell a cassette.
            b.ToTable("cassette_stocks", t =>
            {
                t.HasCheckConstraint("ck_cassette_stocks_count", "count >= 0");
                t.HasCheckConstraint("ck_cassette_stocks_reserved", "reserved >= 0 AND reserved <= count");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
            b.HasIndex(x => new { x.CassetteId, x.Currency, x.Denomination }).IsUnique();
        });

        modelBuilder.Entity<CassetteMovement>(b =>
        {
            b.ToTable("cassette_movements");
            b.HasKey(x => x.Id);
            b.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.ReferenceType).HasMaxLength(50);
            b.Property(x => x.PerformedBy).HasMaxLength(100);
            b.HasIndex(x => new { x.CassetteId, x.Currency, x.Denomination });
            b.HasIndex(x => new { x.AtmId, x.Id });
            b.HasIndex(x => x.ReferenceId);
            b.HasOne<Cassette>().WithMany().HasForeignKey(x => x.CassetteId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Atm>().WithMany().HasForeignKey(x => x.AtmId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WithdrawalQuote>(b =>
        {
            b.ToTable("withdrawal_quotes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.CardHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.CardLast4).HasMaxLength(4).IsRequired();
            b.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ConfirmedOption).HasConversion<string>().HasMaxLength(10);
            Json(b.Property(x => x.Options));
            Json(b.Property(x => x.Reservations));
            b.HasIndex(x => new { x.AtmId, x.Status, x.ExpiresAt });
            b.HasIndex(x => new { x.CardHash, x.Currency, x.BusinessDate });
            b.HasOne<Atm>().WithMany().HasForeignKey(x => x.AtmId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.ToTable("idempotency_records");
            b.HasKey(x => x.Key);
            b.Property(x => x.Key).HasMaxLength(128);
            b.Property(x => x.RequestFingerprint).HasMaxLength(100).IsRequired();
            b.Property(x => x.ResponseBody).HasColumnType("jsonb").IsRequired();
        });

        modelBuilder.Entity<DepositTransaction>(b =>
        {
            b.ToTable("deposit_transactions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Result).HasColumnType("jsonb").IsRequired();
            b.HasIndex(x => x.AtmId);
            b.HasOne<Atm>().WithMany().HasForeignKey(x => x.AtmId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Reconciliation>(b =>
        {
            b.ToTable("reconciliations");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.TotalDifferenceTry).HasPrecision(18, 2);
            b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
            b.Property(x => x.DecidedBy).HasMaxLength(100);
            Json(b.Property(x => x.Lines));
            b.HasIndex(x => new { x.AtmId, x.CreatedAt });
            b.HasOne<Atm>().WithMany().HasForeignKey(x => x.AtmId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExchangeRate>(b =>
        {
            b.ToTable("exchange_rates", t => t.HasCheckConstraint("ck_exchange_rates_rate", "rate_to_try > 0"));
            b.HasKey(x => x.Currency);
            b.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
            b.Property(x => x.RateToTry).HasPrecision(18, 6);

            // Sample rates so the system works out of the box; maintained through the API afterwards.
            var seededAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            b.HasData(
                new ExchangeRate { Currency = Currency.TRY, RateToTry = 1m, UpdatedAt = seededAt },
                new ExchangeRate { Currency = Currency.USD, RateToTry = 42.50m, UpdatedAt = seededAt },
                new ExchangeRate { Currency = Currency.EUR, RateToTry = 49.00m, UpdatedAt = seededAt });
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasMaxLength(100).IsRequired();
            b.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            b.HasIndex(x => x.Id).HasFilter("sent_at IS NULL").HasDatabaseName("ix_outbox_messages_unsent");
        });
    }

    /// <summary>Stores a list of plain objects in a jsonb column.</summary>
    private static void Json<T>(PropertyBuilder<List<T>> property)
    {
        property
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, JsonDefaults.Options),
                json => JsonSerializer.Deserialize<List<T>>(json, JsonDefaults.Options) ?? new List<T>(),
                new ValueComparer<List<T>>(
                    (a, b) => JsonSerializer.Serialize(a, JsonDefaults.Options) ==
                              JsonSerializer.Serialize(b, JsonDefaults.Options),
                    value => JsonSerializer.Serialize(value, JsonDefaults.Options).GetHashCode(),
                    value => JsonSerializer.Deserialize<List<T>>(
                        JsonSerializer.Serialize(value, JsonDefaults.Options), JsonDefaults.Options)!));
    }
}
