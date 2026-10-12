using AtmCashManagement.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AtmCashManagement.Api.BackgroundServices;

/// <summary>
/// Publishes outbox events. "Publishing" is a log line here; in production it would be a message
/// broker or a notification service. Delivery is at-least-once: if the process stops between the
/// log line and the commit, the event is sent again on the next run.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<CashOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.OutboxPollIntervalSeconds)));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                while (await DispatchBatchAsync(stoppingToken) == BatchSize)
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox dispatch failed; it will be retried on the next tick.");
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // SKIP LOCKED lets several application instances share the work without sending an event twice.
        var messages = await db.OutboxMessages
            .FromSqlRaw(
                "SELECT * FROM outbox_messages WHERE sent_at IS NULL ORDER BY id LIMIT {0} FOR UPDATE SKIP LOCKED",
                BatchSize)
            .ToListAsync(ct);

        foreach (var message in messages)
        {
            logger.LogWarning("Outbox event {EventId} {EventType}: {Payload}", message.Id, message.Type, message.Payload);
            message.SentAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return messages.Count;
    }
}
