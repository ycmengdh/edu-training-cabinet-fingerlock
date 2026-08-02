namespace CabinetLock
{
    public static class LockNaming
    {
        public const int LockCount = 4;

        public static string ToDisplayName(int lockId) =>
            lockId >= 0 && lockId < LockCount ? $"Lock {lockId + 1}" : "-";
    }
}
