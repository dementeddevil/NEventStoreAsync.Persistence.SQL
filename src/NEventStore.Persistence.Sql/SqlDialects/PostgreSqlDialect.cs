namespace NEventStore.Persistence.Sql.SqlDialects
{
    using System;

    public class PostgreSqlDialect : CommonSqlDialect
    {
        public override string InitializeStorage => PostgreSqlStatements.InitializeStorage;

        public override string PersistCommit => PostgreSqlStatements.PersistCommits;

        public override bool IsDuplicate(Exception exception)
        {
            var message = exception.Message.ToUpperInvariant();
            return message.Contains("23505") || message.Contains("IX_COMMITS_COMMITSEQUENCE");
        }
    }
}