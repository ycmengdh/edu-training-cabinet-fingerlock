namespace CabinetLock
{
    public sealed class SingleInstanceGuard : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _disposed;

        private SingleInstanceGuard(Mutex mutex, bool isPrimaryInstance)
        {
            _mutex = mutex;
            IsPrimaryInstance = isPrimaryInstance;
        }

        public bool IsPrimaryInstance { get; }

        public static SingleInstanceGuard Acquire(string mutexName)
        {
            if (string.IsNullOrWhiteSpace(mutexName))
                throw new ArgumentException("Mutex name is required.", nameof(mutexName));

            var mutex = new Mutex(true, mutexName, out bool createdNew);
            return new SingleInstanceGuard(mutex, createdNew);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (IsPrimaryInstance)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }

            _mutex.Dispose();
        }
    }
}
