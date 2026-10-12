using AtmCashManagement.Api.Application;
using AtmCashManagement.Api.Domain;
using AtmCashManagement.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AtmCashManagement.Api.BackgroundServices;

/// <summary>
/// Releases the reservations of quotes that were neither confirmed nor cancelled within their
/// lifetime. Correctness does not depend on how often this runs: quote creation releases overdue
/// reservations of its ATM itself and confirmation checks the expiry time. The sweeper makes sure
/// the stored figures (and therefore reports) catch up even when an ATM sees no further traffic.
/// </summary>
public sealed class QuoteExpiryService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<CashOptions> options,
    ILogger<QuoteExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.QuoteSweepIntervalSeconds)));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Quote expiry sweep failed; it will be retried on the next tick.");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        List<Guid> atmIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow();
            atmIds = await db.WithdrawalQuotes.AsNoTracking()
                .Where(q => q.Status == QuoteStatus.Active && q.ExpiresAt <= now)
                .Select(q => q.AtmId)
                .Distinct()
                .ToListAsync(ct);
        }

        // One short transaction per ATM, so a busy ATM never holds up the others.
        foreach (var atmId in atmIds)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var withdrawals = scope.ServiceProvider.GetRequiredService<WithdrawalService>();
            var expired = await withdrawals.ExpireOverdueQuotesAsync(atmId, ct);
            if (expired > 0)
            {
                logger.LogInformation("Expired {Count} quote(s) on ATM {AtmId} and released their reservations.",
                    expired, atmId);
            }
        }
    }
}
