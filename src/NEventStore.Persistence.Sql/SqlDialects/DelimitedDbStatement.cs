
namespace NEventStore.Persistence.Sql.SqlDialects
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Transactions;
    using NEventStore.Persistence.Sql;

    public class DelimitedDbStatement : CommonDbStatement
    {
        private const string Delimiter = ";";

        public DelimitedDbStatement(
            ISqlDialect dialect,
            TransactionScope scope,
            IDbConnection connection,
            IDbTransaction transaction)
            : base(dialect, scope, connection, transaction)
        {}

        public override async Task<int> ExecuteNonQueryAsync(string commandText, CancellationToken cancellationToken)
        {
            var rowsAffected = 0;
            foreach (var command in SplitCommandText(commandText))
            {
                rowsAffected += await base
                    .ExecuteNonQueryAsync(command, cancellationToken)
                    .ConfigureAwait(false);
            }

            return rowsAffected;
        }

        private static IEnumerable<string> SplitCommandText(string delimited)
        {
            if (string.IsNullOrEmpty(delimited))
            {
                return new string[] {};
            }

            return delimited.Split(Delimiter.ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
                            .AsEnumerable().Select(x => x + Delimiter)
                            .ToArray();
        }
    }
}