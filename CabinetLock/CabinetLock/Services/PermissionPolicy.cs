namespace CabinetLock
{
    /// <summary>
    /// Security invariants that must be applied independently of the UI.
    /// Internal lock index 0 is displayed as Lock 1 and is reserved for admins.
    /// </summary>
    public static class PermissionPolicy
    {
        public const int SystemLockId = 0;

        public static bool IsAdmin(string? role) =>
            string.Equals(role?.Trim(), "admin", StringComparison.OrdinalIgnoreCase);

        public static bool CanGrant(string? role, int lockId) =>
            lockId != SystemLockId || IsAdmin(role);

        public static RolePermission Normalize(RolePermission permission)
        {
            ArgumentNullException.ThrowIfNull(permission);

            string role = permission.Role.Trim().ToLowerInvariant();
            return new RolePermission
            {
                Role = role,
                Lock0 = IsAdmin(role) && permission.Lock0,
                Lock1 = permission.Lock1,
                Lock2 = permission.Lock2,
                Lock3 = permission.Lock3,
                UpdateTime = permission.UpdateTime
            };
        }

        public static void Enforce(string? role, bool[] permissions)
        {
            ArgumentNullException.ThrowIfNull(permissions);
            if (permissions.Length > SystemLockId && !IsAdmin(role))
            {
                permissions[SystemLockId] = false;
            }
        }
    }
}
