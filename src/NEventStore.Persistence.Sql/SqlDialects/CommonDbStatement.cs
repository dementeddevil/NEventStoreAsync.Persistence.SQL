using System.Data.Common;

namespace NEventStore.Persistence.Sql.SqlDialects
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Transactions;
    using NEventStore.Logging;
    using NEventStore.Persistence.Sql;

    public class CommonDbStatement : IDbStatement
    {
        private const int InfinitePageSize = 0;
        private static readonly ILog Logger = LogFactory.BuildLogger(typeof(CommonDbStatement));
        private readonly IDbConnection _connection;
        private readonly ISqlDialect _dialect;
        private readonly TransactionScope _scope;
        private readonly IDbTransaction _transaction;

        public CommonDbStatement(
            ISqlDialect dialect,
            TransactionScope scope,
            IDbConnection connection,
            IDbTransaction transaction)
        {
            Parameters = new Dictionary<string, Tuple<object, DbType?>>();

            _dialect = dialect;
            _scope = scope;
            _connection = connection;
            _transaction = transaction;
        }

        protected IDictionary<string, Tuple<object, DbType?>> Parameters { get; }

        protected ISqlDialect Dialect => _dialect;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public virtual int PageSize { get; set; }

        public virtual void AddParameter(string name, object value, DbType? parameterType = null)
        {
            Logger.Debug(Messages.AddingParameter, name);
            Parameters[name] = Tuple.Create(_dialect.CoalesceParameterValue(value), parameterType);
        }

        public virtual Task<int> ExecuteWithoutExceptionsAsync(string commandText, CancellationToken cancellationToken)
        {
            try
            {
                return ExecuteNonQueryAsync(commandText, cancellationToken);
            }
            catch (Exception)
            {
                Logger.Debug(Messages.ExceptionSuppressed);
                return Task.FromResult(0);
            }
        }

        public virtual Task<int> ExecuteNonQueryAsync(string commandText, CancellationToken cancellationToken)
        {
            try
            {
                using (var command = BuildCommand(commandText))
                {
                    if (command is DbCommand asyncCommand)
                    {
                        return asyncCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    return Task.FromResult(command.ExecuteNonQuery());
                }
            }
            catch (Exception e)
            {
                if (Dialect.IsDuplicate(e))
                {
                    throw new UniqueKeyViolationException(e.Message, e);
                }

                throw;
            }
        }

        public virtual async Task<T> ExecuteScalarAsync<T>(string commandText, CancellationToken cancellationToken)
        {
            try
            {
                using (var command = BuildCommand(commandText))
                {
                    if (command is DbCommand asyncCommand)
                    {
                        return (T)(await asyncCommand.ExecuteScalarAsync(cancellationToken)
                            .ConfigureAwait(false));
                    }

                    return (T)command.ExecuteScalar();
                }
            }
            catch (Exception e)
            {
                if (Dialect.IsDuplicate(e))
                {
                    throw new UniqueKeyViolationException(e.Message, e);
                }

                throw;
            }
        }

        public virtual Task<IEnumerable<IDataRecord>> ExecuteWithQueryAsync(
            string queryText, CancellationToken cancellationToken)
        {
            return ExecuteQueryAsync(queryText, (query, latest) => { }, InfinitePageSize, cancellationToken);
        }

        public virtual Task<IEnumerable<IDataRecord>> ExecutePagedQueryAsync(
            string queryText,
            NextPageDelegate nextPage,
            CancellationToken cancellationToken)
        {
            var pageSize = Dialect.CanPage ? PageSize : InfinitePageSize;
            if (pageSize > 0)
            {
                Logger.Verbose(Messages.MaxPageSize, pageSize);
                Parameters.Add(Dialect.Limit, Tuple.Create((object)pageSize, (DbType?)null));
            }

            return ExecuteQueryAsync(queryText, nextPage, pageSize, cancellationToken);
        }

        protected virtual void Dispose(bool disposing)
        {
            Logger.Verbose(Messages.DisposingStatement);

            if (_transaction != null)
            {
                _transaction.Dispose();
            }

            if (_connection != null)
            {
                _connection.Dispose();
            }

            if (_scope != null)
            {
                _scope.Dispose();
            }
        }

        protected virtual Task<IEnumerable<IDataRecord>> ExecuteQueryAsync(
            string queryText,
            NextPageDelegate nextPage,
            int pageSize,
            CancellationToken cancellationToken)
        {
            Parameters.Add(Dialect.Skip, Tuple.Create((object)0, (DbType?)null));
            var command = BuildCommand(queryText);

            try
            {
                return Task.FromResult<IEnumerable<IDataRecord>>(
                    new PagedEnumerationCollection(_scope, Dialect, command, nextPage, pageSize, this));
            }
            catch (Exception)
            {
                command.Dispose();
                throw;
            }
        }

        protected virtual IDbCommand BuildCommand(string statement)
        {
            Logger.Verbose(Messages.CreatingCommand);
            var command = _connection.CreateCommand();

            if (Settings.CommandTimeout > 0)
            {
                command.CommandTimeout = Settings.CommandTimeout;
            }

            command.Transaction = _transaction;
            command.CommandText = statement;

            Logger.Verbose(Messages.ClientControlledTransaction, _transaction != null);
            Logger.Verbose(Messages.CommandTextToExecute, statement);

            BuildParameters(command);

            return command;
        }

        protected virtual void BuildParameters(IDbCommand command)
        {
            foreach (var item in Parameters)
            {
                BuildParameter(command, item.Key, item.Value.Item1, item.Value.Item2);
            }
        }

        protected virtual void BuildParameter(IDbCommand command, string name, object value, DbType? dbType)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            SetParameterValue(parameter, value, dbType);

            Logger.Verbose(Messages.BindingParameter, name, parameter.Value);
            command.Parameters.Add(parameter);
        }

        protected virtual void SetParameterValue(IDataParameter param, object value, DbType? type)
        {
            param.Value = value ?? DBNull.Value;
            param.DbType = type ?? (value == null ? DbType.Binary : param.DbType);
        }
    }
}