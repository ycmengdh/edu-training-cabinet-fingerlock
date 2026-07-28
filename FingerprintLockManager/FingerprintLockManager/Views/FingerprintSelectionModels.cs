namespace FingerprintLockManager
{
    internal sealed class FingerprintRoleOption
    {
        public string Role { get; init; } = "";
        public string DisplayText { get; init; } = "";
    }

    internal sealed class FingerprintClassOption
    {
        public string ClassId { get; init; } = "";
        public string DisplayText { get; init; } = "";
    }

    internal sealed class FingerprintUserOption
    {
        public User User { get; init; } = new();
        public string UserId => User.UserId;
        public string DisplayText => User.FingerprintId.HasValue
            ? $"{User.Name} ({User.UserId}) · 指纹 #{User.FingerprintId.Value}"
            : $"{User.Name} ({User.UserId}) · 未录入";
    }

    internal sealed class FingerprintTemplateOption
    {
        public int FingerprintId { get; init; }
        public int FingerIndex { get; init; } = 1;
        public string DisplayText { get; init; } = "";
    }

    internal sealed class FingerprintDeviceOption
    {
        public string DeviceId { get; init; } = "";
        public string DisplayText { get; init; } = "";
    }

    internal static class FingerprintSelectionData
    {
        public static string RoleText(string role) => role.ToLowerInvariant() switch
        {
            "admin" => "管理员",
            "teacher" => "老师",
            "student" => "学生",
            _ => role
        };

        public static List<FingerprintRoleOption> BuildRoles(IEnumerable<User> users) =>
            users.Select(user => user.Role)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(role => role switch { "student" => 0, "teacher" => 1, "admin" => 2, _ => 3 })
                .Select(role => new FingerprintRoleOption
                {
                    Role = role,
                    DisplayText = RoleText(role)
                })
                .ToList();

        public static List<FingerprintDeviceOption> LoadOnlineCabinets()
        {
            List<Device> saved;
            try { saved = App.DeviceService.GetAllDevices(); }
            catch { saved = new List<Device>(); }
            return App.MeshBridge.GetOnlineDevices()
                .Where(client => client.IsOnline && !client.IsRoot &&
                    !string.IsNullOrWhiteSpace(client.DeviceId))
                .Select(client =>
                {
                    Device? device = saved.FirstOrDefault(item =>
                        (!string.IsNullOrWhiteSpace(client.MeshMac) &&
                         string.Equals(item.MeshMac, client.MeshMac, StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(item.DeviceId, client.DeviceId, StringComparison.OrdinalIgnoreCase));
                    string number = device?.DeviceNumber ?? "";
                    string name = string.IsNullOrWhiteSpace(device?.DeviceName)
                        ? (string.IsNullOrWhiteSpace(client.DeviceName) ? client.DeviceId : client.DeviceName)
                        : device.DeviceName;
                    string label = string.IsNullOrWhiteSpace(number)
                        ? $"未编号 · {name} ({client.DeviceId})"
                        : $"{number} · {name} ({client.DeviceId})";
                    return new FingerprintDeviceOption
                    {
                        DeviceId = client.DeviceId,
                        DisplayText = label
                    };
                })
                .OrderBy(option => option.DisplayText)
                .ToList();
        }
    }
}
