using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Prospecta.Application.Common;

/// <summary>Starts a transaction, or joins the one already open (services call each other inside critical operations).</summary>
public sealed class UnitOfWork : IAsyncDisposable
{
    private readonly IDbContextTransaction? _tx;

    private UnitOfWork(IDbContextTransaction? tx) => _tx = tx;

    public static async Task<UnitOfWork> BeginAsync(DatabaseFacade db, CancellationToken ct) =>
        new(db.CurrentTransaction is null ? await db.BeginTransactionAsync(ct) : null);

    public Task CommitAsync(CancellationToken ct = default) => _tx?.CommitAsync(ct) ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => _tx?.DisposeAsync() ?? ValueTask.CompletedTask;
}
