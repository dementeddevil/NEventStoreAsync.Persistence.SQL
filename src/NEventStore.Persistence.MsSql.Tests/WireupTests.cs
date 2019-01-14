using System.Threading;
using System.Threading.Tasks;
using NEventStore.Persistence.Sql.Tests;

namespace NEventStore.Persistence.AcceptanceTests
{
    using System;
    using NEventStore.Persistence.AcceptanceTests.BDD;
    using NEventStore.Persistence.Sql;
    using NEventStore.Persistence.Sql.SqlDialects;
    using FluentAssertions;
#if MSTEST
    using Microsoft.VisualStudio.TestTools.UnitTesting;
#endif
#if NUNIT
    using NUnit.Framework;
    using System.Data.SqlClient;
#endif
#if XUNIT
    using Xunit;
    using Xunit.Should;
#endif

#if MSTEST
    [TestClass]
#endif
    public class when_specifying_a_hasher : SpecificationBase
    {
        private bool _hasherInvoked;
        private IStoreEvents _eventStore;

        protected override Task Context()
        {
            _eventStore = Wireup
                .Init()
#if !NETSTANDARD2_0
                .UsingSqlPersistence(new EnviromentConnectionFactory("MsSql", "System.Data.SqlClient"))
#else
                .UsingSqlPersistence(new EnviromentConnectionFactory("MsSql", System.Data.SqlClient.SqlClientFactory.Instance))
#endif
                .WithDialect(new MsSqlDialect())
                .WithStreamIdHasher(streamId =>
                {
                    _hasherInvoked = true;
                    return new Sha1StreamIdHasher().GetHash(streamId);
                })
                .InitializeStorageEngine()
#if !NETSTANDARD2_0
                // enlist in ambient transaction throws in dotnet core, should be fixed on next verison of the driver
                .EnlistInAmbientTransaction()
#endif
                .UsingBinarySerialization()
                .Build();
            return Task.CompletedTask;
        }

        protected override async Task Cleanup()
        {
            if (_eventStore != null)
            {
                await _eventStore.Advanced.DropAsync(CancellationToken.None);
                _eventStore.Dispose();
            }
        }

        protected override async Task Because()
        {
            using (var stream = await _eventStore.OpenStreamAsync(Guid.NewGuid(), CancellationToken.None))
            {
                stream.Add(new EventMessage{ Body = "Message" });
                await stream.CommitChangesAsync(Guid.NewGuid(), CancellationToken.None);
            }
        }

        [Fact]
        public void should_invoke_hasher()
        {
            _hasherInvoked.Should().BeTrue();
        }
    }
}