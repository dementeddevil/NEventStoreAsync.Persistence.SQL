namespace NEventStore.Persistence.Sql
{
    using System;
    using System.Data;

    public class ConnectionScope : ThreadScope<IDbConnection>, IDbConnection
    {
        public ConnectionScope(string connectionName, Func<IDbConnection> factory)
            : base(connectionName, factory)
        {}

        IDbTransaction IDbConnection.BeginTransaction()
        {
            return Current.BeginTransaction();
        }

        IDbTransaction IDbConnection.BeginTransaction(IsolationLevel il)
        {
            return Current.BeginTransaction(il);
        }

        void IDbConnection.Close()
        {
            // no-op--let Dispose do the real work.
        }

        void IDbConnection.ChangeDatabase(string databaseName)
        {
            Current.ChangeDatabase(databaseName);
        }

        IDbCommand IDbConnection.CreateCommand()
        {
            return Current.CreateCommand();
        }

        void IDbConnection.Open()
        {
            Current.Open();
        }

        string IDbConnection.ConnectionString
        {
            get => Current.ConnectionString;
            set => Current.ConnectionString = value;
        }

        int IDbConnection.ConnectionTimeout => Current.ConnectionTimeout;

        string IDbConnection.Database => Current.Database;

        ConnectionState IDbConnection.State => Current.State;
    }
}