namespace NEventStore.Persistence.Sql
{
    using System;

    public class DelegateStreamIdHasher : IStreamIdHasher
    {
        private readonly Func<string, string> _getHash;

        public DelegateStreamIdHasher(Func<string, string> getHash)
        {
            _getHash = getHash ?? throw new ArgumentNullException(nameof(getHash));
        }

        public string GetHash(string streamId)
        {
            return _getHash(streamId);
        }
    }
}