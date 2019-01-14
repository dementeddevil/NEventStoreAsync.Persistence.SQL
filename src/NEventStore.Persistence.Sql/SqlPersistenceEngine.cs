
namespace NEventStore.Persistence.Sql
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Transactions;
    using NEventStore.Logging;
    using NEventStore.Serialization;

    public class SqlPersistenceEngine : IPersistStreams
    {
        private static readonly ILog Logger = LogFactory.BuildLogger(typeof(SqlPersistenceEngine));
        private static readonly DateTime EpochTime = new DateTime(1970, 1, 1);
        private readonly IConnectionFactory _connectionFactory;
        private readonly ISqlDialect _dialect;
        private readonly int _pageSize;
        private readonly TransactionScopeOption _scopeOption;
        private readonly ISerialize _serializer;
        private bool _disposed;
        private int _initialized;
        private readonly IStreamIdHasher _streamIdHasher;

        public SqlPersistenceEngine(
            IConnectionFactory connectionFactory,
            ISqlDialect dialect,
            ISerialize serializer,
            TransactionScopeOption scopeOption,
            int pageSize)
            : this(connectionFactory, dialect, serializer, scopeOption, pageSize, new Sha1StreamIdHasher())
        { }

        public SqlPersistenceEngine(
            IConnectionFactory connectionFactory,
            ISqlDialect dialect,
            ISerialize serializer,
            TransactionScopeOption scopeOption,
            int pageSize,
            IStreamIdHasher streamIdHasher)
        {
            if (pageSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            }

            if (streamIdHasher == null)
            {
                throw new ArgumentNullException(nameof(streamIdHasher));
            }

            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _scopeOption = scopeOption;
            _pageSize = pageSize;
            _streamIdHasher = new StreamIdHasherValidator(streamIdHasher);

            Logger.Debug(Messages.UsingScope, _scopeOption.ToString());
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public virtual Task InitializeAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _initialized) > 1)
            {
                return Task.CompletedTask;
            }

            Logger.Debug(Messages.InitializingStorage);
            return ExecuteCommandAsync(
                (statement, token) => statement.ExecuteWithoutExceptionsAsync(_dialect.InitializeStorage, token),
                cancellationToken);
        }

        public virtual Task<IEnumerable<ICommit>> GetFromAsync(string bucketId, string streamId, int minRevision, int maxRevision, CancellationToken cancellationToken)
        {
            Logger.Debug(Messages.GettingAllCommitsBetween, streamId, minRevision, maxRevision);
            streamId = _streamIdHasher.GetHash(streamId);
            return ExecuteQueryAsync(
                async (query, token) =>
                {
                    var statement = _dialect.GetCommitsFromStartingRevision;
                    query.AddParameter(_dialect.BucketId, bucketId, DbType.AnsiString);
                    query.AddParameter(_dialect.StreamId, streamId, DbType.AnsiString);
                    query.AddParameter(_dialect.StreamRevision, minRevision);
                    query.AddParameter(_dialect.MaxStreamRevision, maxRevision);
                    query.AddParameter(_dialect.CommitSequence, 0);
                    return (await query
                            .ExecutePagedQueryAsync(statement, _dialect.NextPageDelegate, token)
                            .ConfigureAwait(false))
                        .Select(x => x.GetCommit(_serializer, _dialect));
                },
                cancellationToken);
        }

        public virtual Task<IEnumerable<ICommit>> GetFromAsync(string bucketId, DateTime start, CancellationToken cancellationToken)
        {
            start = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerSecond)); // Rounds down to the nearest second.
            start = start < EpochTime ? EpochTime : start;

            Logger.Debug(Messages.GettingAllCommitsFrom, start, bucketId);
            return ExecuteQueryAsync(
                async (query, token) =>
                {
                    var statement = _dialect.GetCommitsFromInstant;
                    query.AddParameter(_dialect.BucketId, bucketId, DbType.AnsiString);
                    query.AddParameter(_dialect.CommitStamp, start);
                    return (await query
                            .ExecutePagedQueryAsync(statement, (q, r) => { }, token)
                            .ConfigureAwait(false))
                        .Select(x => x.GetCommit(_serializer, _dialect));
                },
                cancellationToken);
        }

        public virtual Task<IEnumerable<ICommit>> GetFromToAsync(string bucketId, DateTime start, DateTime end, CancellationToken cancellationToken)
        {
            start = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerSecond)); // Rounds down to the nearest second.
            start = start < EpochTime ? EpochTime : start;
            end = end < EpochTime ? EpochTime : end;

            Logger.Debug(Messages.GettingAllCommitsFromTo, start, end);
            return ExecuteQueryAsync(
                async (query, token) =>
                {
                    var statement = _dialect.GetCommitsFromToInstant;
                    query.AddParameter(_dialect.BucketId, bucketId, DbType.AnsiString);
                    query.AddParameter(_dialect.CommitStampStart, start);
                    query.AddParameter(_dialect.CommitStampEnd, end);
                    return (await query.ExecutePagedQueryAsync(statement, (q, r) => { }, token).ConfigureAwait(false))
                        .Select(x => x.GetCommit(_serializer, _dialect));
                },
                cancellationToken);
        }

        public virtual async Task<ICommit> CommitAsync(CommitAttempt attempt, CancellationToken cancellationToken)
        {
            ICommit commit;
            try
            {
                commit = await PersistCommitAsync(attempt, cancellationToken)
                    .ConfigureAwait(false);
                Logger.Debug(Messages.CommitPersisted, attempt.CommitId);
            }
            catch (Exception e)
            {
                if (!(e is UniqueKeyViolationException))
                {
                    throw;
                }

                if (await DetectDuplicateAsync(attempt, cancellationToken)
                    .ConfigureAwait(false))
                {
                    Logger.Info(Messages.DuplicateCommit);
                    throw new DuplicateCommitException(e.Message, e);
                }

                Logger.Info(Messages.ConcurrentWriteDetected);
                throw new ConcurrencyException(e.Message, e);
            }
            return commit;
        }

        public virtual Task<IEnumerable<IStreamHead>> GetStreamsToSnapshotAsync(string bucketId, int maxThreshold, CancellationToken cancellationToken)
        {
            Logger.Debug(Messages.GettingStreamsToSnapshot);
            return ExecuteQueryAsync(
                async (query, token) =>
                {
                    var statement = _dialect.GetStreamsRequiringSnapshots;
                    query.AddParameter(_dialect.BucketId, bucketId, DbType.AnsiString);
                    query.AddParameter(_dialect.Threshold, maxThreshold);
                    return (await query
                        .ExecutePagedQueryAsync(
                            statement,
                            (q, s) => q.SetParameter(_dialect.StreamId, _dialect.CoalesceParameterValue(s.StreamId()), DbType.AnsiString),
                            cancellationToken).ConfigureAwait(false))
                        .Select(x => (IStreamHead)x.GetStreamToSnapshot());
                },
                cancellationToken);
        }

        public virtual async Task<ISnapshot> GetSnapshotAsync(string bucketId, string streamId, int maxRevision, CancellationToken cancellationToken)
        {
            Logger.Debug(Messages.GettingRevision, streamId, maxRevision);
            var streamIdHash = _streamIdHasher.GetHash(streamId);
            return (await ExecuteQueryAsync(
                async (query, token) =>
                {
                    var statement = _dialect.GetSnapshot;
                    query.AddParameter(_dialect.BucketId, bucketId, DbType.AnsiString);
                    query.AddParameter(_dialect.StreamId, streamIdHash, DbType.AnsiString);
                    query.AddParameter(_dialect.StreamRevision, maxRevision);
                    return (await query
                            .ExecuteWithQueryAsync(statement, cancellationToken)
                            .ConfigureAwait(false))
                        .Select(x => (ISnapshot)x.GetSnapshot(_serializer, streamId));
                },
                cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        }

        public virtual Task<bool> AddSnapshotAsync(ISnapshot snapshot, CancellationToken cancellationToken)
        {
            Logger.Debug(Messages.AddingSnapshot, snapshot.StreamId, snapshot.StreamRevision);
            var streamId = _streamIdHasher.GetHash(snapshot.StreamId);
            return ExecuteCommandAsync(
                async (connection, cmd, token) =>
                {
                    cmd.AddParameter(_dialect.BucketId, snapshot.BucketId, DbType.AnsiString);
                    cmd.AddParameter(_dialect.StreamId, streamId, DbType.AnsiString);
                    cmd.AddParameter(_dialect.StreamRevision, snapshot.StreamRevision);
                    _dialect.AddPayloadParamater(_connectionFactory, connection, cmd, _serializer.Serialize(snapshot.Payload));
                    return (await cmd.ExecuteWithoutExceptionsAsync(_dialect.AppendSnapshotToCommit, token).ConfigureAwait(false)) > 0;
                },
                cancellationToken);
        }

        public virtual Task PurgeAsync(CancellationToken cancellationToken)
        {
            Logger.Warn(Messages.PurgingStorage);
            return ExecuteCommandAsync(
                (cmd, token) => cmd.ExecuteNonQueryAsync(_dialect.PurgeStorage, token),
                cancellationToken);
        }

        public Task PurgeAsync(string bucketId, CancellationToken cancellationToken)
        {
            Logger.Warn(Messages.PurgingBucket, bucketId);
            return ExecuteCommandAsync(
                (cmd, token) =>
                {
                    cmd.AddParameter(_dialect.BucketId, bucketId, DbType.AnsiString);
                    return cmd.ExecuteNonQueryAsync(_dialect.PurgeBucket, token);
                },
                cancellationToken);
        }

        public Task DropAsync(CancellationToken cancellationToken)
        {
            Logger.Warn(Messages.DroppingTables);
            return ExecuteCommandAsync(
                (cmd, token) => cmd.ExecuteNonQueryAsync(_dialect.Drop, token),
                cancellationToken);
        }

        public Task DeleteStreamAsync(string bucketId, string streamId, CancellationToken cancellationToken)
        {
            Logger.Warn(Messages.DeletingStream, streamId, bucketId);
            streamId = _streamIdHasher.GetHash(streamId);
            return ExecuteCommandAsync(
                (cmd, token) =>
                {
                    cmd.AddParameter(_dialect.BucketId, bucketId, DbType.AnsiString);
                    cmd.AddParameter(_dialect.StreamId, streamId, DbType.AnsiString);
                    return cmd.ExecuteNonQueryAsync(_dialect.DeleteStream, token);
                },
                cancellationToken);
        }

        public Task<IEnumerable<ICommit>> GetFromAsync(string bucketId, long checkpointToken, CancellationToken cancellationToken)
        {
            Logger.Debug(Messages.GettingAllCommitsFromBucketAndCheckpoint, bucketId, checkpointToken);
            return ExecuteQueryAsync(
                async (query, token) =>
                {
                    var statement = _dialect.GetCommitsFromBucketAndCheckpoint;
                    query.AddParameter(_dialect.BucketId, bucketId, DbType.AnsiString);
                    query.AddParameter(_dialect.CheckpointNumber, checkpointToken);
                    return (await query
                            .ExecutePagedQueryAsync(statement, (q, r) => { }, token).ConfigureAwait(false))
                        .Select(x => x.GetCommit(_serializer, _dialect));
                },
                cancellationToken);
        }

        public Task<IEnumerable<ICommit>> GetFromAsync(long checkpointToken, CancellationToken cancellationToken)
        {
            Logger.Debug(Messages.GettingAllCommitsFromCheckpoint, checkpointToken);
            return ExecuteQueryAsync(
                async (query, token) =>
                {
                    var statement = _dialect.GetCommitsFromCheckpoint;
                    query.AddParameter(_dialect.CheckpointNumber, checkpointToken);
                    return (await query
                            .ExecutePagedQueryAsync(statement, (q, r) => { }, token).ConfigureAwait(false))
                        .Select(x => x.GetCommit(_serializer, _dialect));
                },
                cancellationToken);
        }

        public bool IsDisposed => _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing || _disposed)
            {
                return;
            }

            Logger.Debug(Messages.ShuttingDownPersistence);
            _disposed = true;
        }

        protected virtual void OnPersistCommit(IDbStatement cmd, CommitAttempt attempt)
        {
        }

        private Task<ICommit> PersistCommitAsync(CommitAttempt attempt, CancellationToken cancellationToken)
        {
            Logger.Debug(Messages.AttemptingToCommit, attempt.Events.Count, attempt.StreamId, attempt.CommitSequence, attempt.BucketId);
            var streamId = _streamIdHasher.GetHash(attempt.StreamId);
            return ExecuteCommandAsync<ICommit>(
                async (connection, cmd, token) =>
                {
                    cmd.AddParameter(_dialect.BucketId, attempt.BucketId, DbType.AnsiString);
                    cmd.AddParameter(_dialect.StreamId, streamId, DbType.AnsiString);
                    cmd.AddParameter(_dialect.StreamIdOriginal, attempt.StreamId);
                    cmd.AddParameter(_dialect.StreamRevision, attempt.StreamRevision);
                    cmd.AddParameter(_dialect.Items, attempt.Events.Count);
                    cmd.AddParameter(_dialect.CommitId, attempt.CommitId);
                    cmd.AddParameter(_dialect.CommitSequence, attempt.CommitSequence);
                    cmd.AddParameter(_dialect.CommitStamp, attempt.CommitStamp);
                    cmd.AddParameter(_dialect.Headers, _serializer.Serialize(attempt.Headers));
                    _dialect.AddPayloadParamater(_connectionFactory, connection, cmd, _serializer.Serialize(attempt.Events.ToList()));
                    OnPersistCommit(cmd, attempt);

                    var checkpointNumber = (await cmd
                        .ExecuteScalarAsync<object>(_dialect.PersistCommit, token).ConfigureAwait(false))
                        .ToLong();

                    return new Commit(
                        attempt.BucketId,
                        attempt.StreamId,
                        attempt.StreamRevision,
                        attempt.CommitId,
                        attempt.CommitSequence,
                        attempt.CommitStamp,
                        checkpointNumber,
                        attempt.Headers,
                        attempt.Events);
                },
                cancellationToken);
        }

        private Task<bool> DetectDuplicateAsync(CommitAttempt attempt, CancellationToken cancellationToken)
        {
            var streamId = _streamIdHasher.GetHash(attempt.StreamId);
            return ExecuteCommandAsync(
                async (cmd, token) =>
                {
                    cmd.AddParameter(_dialect.BucketId, attempt.BucketId, DbType.AnsiString);
                    cmd.AddParameter(_dialect.StreamId, streamId, DbType.AnsiString);
                    cmd.AddParameter(_dialect.CommitId, attempt.CommitId);
                    cmd.AddParameter(_dialect.CommitSequence, attempt.CommitSequence);
                    var value = await cmd
                        .ExecuteScalarAsync<object>(_dialect.DuplicateCommit, token)
                        .ConfigureAwait(false);
                    return (value is long longValue
                        ? longValue
                        : (int)value) > 0;
                }, cancellationToken);
        }

        protected virtual async Task<IEnumerable<T>> ExecuteQueryAsync<T>(Func<IDbStatement, CancellationToken, Task<IEnumerable<T>>> query, CancellationToken cancellationToken)
        {
            ThrowWhenDisposed();

            var scope = OpenQueryScope();
            IDbConnection connection = null;
            IDbTransaction transaction = null;
            IDbStatement statement = null;

            try
            {
                connection = _connectionFactory.Open();
                transaction = _dialect.OpenTransaction(connection);
                statement = _dialect.BuildStatement(scope, connection, transaction);
                statement.PageSize = _pageSize;

                Logger.Verbose(Messages.ExecutingQuery);
                return await query(statement, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception e)
            {
                statement?.Dispose();
                transaction?.Dispose();
                connection?.Dispose();
                scope?.Dispose();

                Logger.Debug(Messages.StorageThrewException, e.GetType());
                if (e is StorageUnavailableException)
                {
                    throw;
                }

                throw new StorageException(e.Message, e);
            }
        }

        protected virtual TransactionScope OpenQueryScope()
        {
            return OpenCommandScope() ??
                   new TransactionScope(TransactionScopeOption.Suppress);
        }

        private void ThrowWhenDisposed()
        {
            if (!_disposed)
            {
                return;
            }

            Logger.Warn(Messages.AlreadyDisposed);
            throw new ObjectDisposedException(Messages.AlreadyDisposed);
        }

        private Task<T> ExecuteCommandAsync<T>(Func<IDbStatement, CancellationToken, Task<T>> command, CancellationToken cancellationToken)
        {
            return ExecuteCommandAsync((_, statement, token) => command(statement, token), cancellationToken);
        }

        protected virtual async Task<T> ExecuteCommandAsync<T>(Func<IDbConnection, IDbStatement, CancellationToken, Task<T>> command, CancellationToken cancellationToken)
        {
            ThrowWhenDisposed();

            using (var scope = OpenCommandScope())
            using (var connection = _connectionFactory.Open())
            using (var transaction = _dialect.OpenTransaction(connection))
            using (var statement = _dialect.BuildStatement(scope, connection, transaction))
            {
                try
                {
                    Logger.Verbose(Messages.ExecutingCommand);
                    var rowsAffected = await command(connection, statement, cancellationToken)
                        .ConfigureAwait(true);
                    Logger.Verbose(Messages.CommandExecuted, rowsAffected);

                    transaction?.Commit();
                    scope?.Complete();

                    return rowsAffected;
                }
                catch (Exception e)
                {
                    Logger.Debug(Messages.StorageThrewException, e.GetType());
                    if (!RecoverableException(e))
                    {
                        throw new StorageException(e.Message, e);
                    }

                    Logger.Info(Messages.RecoverableExceptionCompletesScope);
                    scope?.Complete();

                    throw;
                }
            }
        }

        protected virtual TransactionScope OpenCommandScope()
        {
            if (Transaction.Current == null)
            {
                return new TransactionScope(
                    _scopeOption,
                    new TransactionOptions
                    {
                        IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted
                    });
            }
            // todo: maybe add a warning for the isolation level
            /*
            if (Transaction.Current.IsolationLevel == System.Transactions.IsolationLevel.Serializable)
            {
                Logger.Warn("Serializable can be troublesome");
            }
            */
            return new TransactionScope(_scopeOption);
        }

        private static bool RecoverableException(Exception e)
        {
            return e is UniqueKeyViolationException || e is StorageUnavailableException;
        }

        private class StreamIdHasherValidator : IStreamIdHasher
        {
            private readonly IStreamIdHasher _streamIdHasher;
            private const int MaxStreamIdHashLength = 40;

            public StreamIdHasherValidator(IStreamIdHasher streamIdHasher)
            {
                _streamIdHasher = streamIdHasher ?? throw new ArgumentNullException(nameof(streamIdHasher));
            }
            public string GetHash(string streamId)
            {
                if (string.IsNullOrWhiteSpace(streamId))
                {
                    throw new ArgumentException(Messages.StreamIdIsNullEmptyOrWhiteSpace);
                }
                var streamIdHash = _streamIdHasher.GetHash(streamId);
                if (string.IsNullOrWhiteSpace(streamIdHash))
                {
                    throw new InvalidOperationException(Messages.StreamIdHashIsNullEmptyOrWhiteSpace);
                }
                if (streamIdHash.Length > MaxStreamIdHashLength)
                {
                    throw new InvalidOperationException(Messages.StreamIdHashTooLong.FormatWith(streamId, streamIdHash, streamIdHash.Length, MaxStreamIdHashLength));
                }
                return streamIdHash;
            }
        }
    }
}