using System.Threading;
using System.Threading.Tasks;
using Core.Application.Sync;
using Core.Domain.Entities;

namespace Infrastructure.Data.Outbox;

/// <summary>
/// Sink that accepts everything and sends nothing. Registered when <see cref="SyncSettings.Enabled"/> is false
/// (the dispatcher short-circuits before calling it anyway) and handy as a default in tests.
/// </summary>
public sealed class NullOutboxSink : IOutboxSink
{
    public Task DeliverAsync(OutboxMessage message, CancellationToken ct = default) => Task.CompletedTask;
}
