using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using NEventStore.Persistence.Sql.SqlDialects;

namespace NEventStore.Persistence.Sql
{
    public interface IDbStatement : IDisposable
    {
        int PageSize { get; set; }

        void AddParameter(string name, object value, DbType? parameterType = null);

        Task<int> ExecuteNonQueryAsync(string commandText, CancellationToken cancellationToken);

        Task<int> ExecuteWithoutExceptionsAsync(string commandText, CancellationToken cancellationToken);

        Task<T> ExecuteScalarAsync<T>(string commandText, CancellationToken cancellationToken);

        Task<IEnumerable<IDataRecord>> ExecuteWithQueryAsync(string queryText, CancellationToken cancellationToken);

        Task<IEnumerable<IDataRecord>> ExecutePagedQueryAsync(string queryText, NextPageDelegate nextPage, CancellationToken cancellationToken);
    }
}