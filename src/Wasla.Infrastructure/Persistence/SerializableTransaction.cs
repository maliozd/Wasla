using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Wasla.Infrastructure.Persistence;

/// <summary>
/// A serializable transaction that hands its SQL Server session back at read committed (WAS-206).
/// </summary>
/// <remarks>
/// SQL Server keeps a session's isolation level after the transaction that set it, and the connection pool's reset
/// (<c>sp_reset_connection</c>) does not put it back. Without this, the pooled connection would give the next request's
/// ordinary reads serializable isolation and key-range locks until that session's next explicit transaction.
/// <para>
/// The level is set back inside the transaction, right before it commits or rolls back. SQL Server protects what was read
/// before such a change by the earlier level until the transaction ends, so the transaction keeps every serializable lock
/// it took. A failure to set it back on commit fails before the commit, with the server's error. When the transaction
/// ends without a commit on a session the server or the network already closed, nothing is set back and nothing is
/// raised: the client discards a broken connection instead of pooling it. Other providers get the plain transaction.
/// </para>
/// </remarks>
internal sealed class SerializableTransaction : IDbContextTransaction, IInfrastructure<DbTransaction>
{
    private const string ReadCommitted = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED";

    private readonly DatabaseFacade _database;
    private readonly IDbContextTransaction _inner;
    private bool _restored;

    private SerializableTransaction(DatabaseFacade database, IDbContextTransaction inner)
    {
        _database = database;
        _inner = inner;
    }

    public static async Task<IDbContextTransaction> BeginAsync(DatabaseFacade database, CancellationToken ct)
    {
        var inner = await database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        return database.IsSqlServer() ? new SerializableTransaction(database, inner) : inner;
    }

    public Guid TransactionId => _inner.TransactionId;

    public bool SupportsSavepoints => _inner.SupportsSavepoints;

    DbTransaction IInfrastructure<DbTransaction>.Instance => _inner.GetDbTransaction();

    // On commit a failed reset is never ignored: it fails before the commit, with the server's own error.
    public void Commit()
    {
        Restore(endingWithoutCommit: false);
        _inner.Commit();
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await RestoreAsync(endingWithoutCommit: false, cancellationToken).ConfigureAwait(false);
        await _inner.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Rollback()
    {
        try
        {
            Restore(endingWithoutCommit: true);
        }
        finally
        {
            _inner.Rollback();
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RestoreAsync(endingWithoutCommit: true, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _inner.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Disposing without a commit rolls the transaction back, as EF does; the session is set back first.
    public void Dispose()
    {
        try
        {
            Restore(endingWithoutCommit: true);
        }
        finally
        {
            _inner.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RestoreAsync(endingWithoutCommit: true, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void CreateSavepoint(string name) => _inner.CreateSavepoint(name);

    public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default) =>
        _inner.CreateSavepointAsync(name, cancellationToken);

    public void RollbackToSavepoint(string name) => _inner.RollbackToSavepoint(name);

    public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default) =>
        _inner.RollbackToSavepointAsync(name, cancellationToken);

    public void ReleaseSavepoint(string name) => _inner.ReleaseSavepoint(name);

    public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default) =>
        _inner.ReleaseSavepointAsync(name, cancellationToken);

    private void Restore(bool endingWithoutCommit)
    {
        if (CreateRestoreCommand() is not { } command)
            return;
        using (command)
        {
            try
            {
                command.ExecuteNonQuery();
                _restored = true;
            }
            catch (Exception) when (endingWithoutCommit && !IsOpen)
            {
                // The server or the network ended the session while the transaction ends without a commit: the
                // connection is broken and is not pooled again, and the error that ended it is the caller's to report.
            }
        }
    }

    private async Task RestoreAsync(bool endingWithoutCommit, CancellationToken ct)
    {
        if (CreateRestoreCommand() is not { } command)
            return;
        await using (command.ConfigureAwait(false))
        {
            try
            {
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _restored = true;
            }
            catch (Exception) when (endingWithoutCommit && !IsOpen)
            {
                // The server or the network ended the session while the transaction ends without a commit: the
                // connection is broken and is not pooled again, and the error that ended it is the caller's to report.
            }
        }
    }

    private bool IsOpen => _database.GetDbConnection().State == ConnectionState.Open;

    private DbCommand? CreateRestoreCommand()
    {
        if (_restored || !IsOpen)
            return null;

        var command = _database.GetDbConnection().CreateCommand();
        command.CommandText = ReadCommitted;
        // A transaction the server already ended (a deadlock victim, XACT_ABORT) is no longer usable on a command; the
        // session itself still has the serializable level and is set back outside it.
        var transaction = _inner.GetDbTransaction();
        command.Transaction = transaction.Connection is null ? null : transaction;
        return command;
    }
}
