using Newtonsoft.Json;

namespace CabinetLock
{
    /// <summary>
    /// 用户模型（对应 users 表）
    /// 管理员和教师可登录；学生仅作为业务用户，不登录上位机且不需要密码。
    /// </summary>
    public class User
    {
        /// <summary>用户唯一标识（主键，非自增）</summary>
        [JsonProperty("user_id")]
        public string UserId { get; set; } = "";

        /// <summary>可编辑的业务编号：学生为学号，教师/管理员为登录账号 ID。</summary>
        [JsonProperty("user_code")]
        public string UserCode { get; set; } = "";

        [JsonIgnore]
        public string DisplayId => string.IsNullOrWhiteSpace(UserCode) ? UserId : UserCode;

        [JsonIgnore]
        public string IdentityLabel => Role switch
        {
            "student" => "学号",
            "teacher" => "教师 ID",
            _ => "账号 ID"
        };

        /// <summary>用户姓名</summary>
        [JsonProperty("name")]
        public string Name { get; set; } = "";

        /// <summary>学生性别（male / female / other，可为空）</summary>
        [JsonProperty("gender")]
        public string Gender { get; set; } = "";

        /// <summary>角色：admin / teacher / student</summary>
        [JsonProperty("role")]
        public string Role { get; set; } = "";

        /// <summary>所属班级 ID（可空）</summary>
        [JsonProperty("class_id")]
        public string? ClassId { get; set; }

        /// <summary>教师负责的班级 ID 集合；旧数据为空时回退到 ClassId。</summary>
        [JsonProperty("class_ids")]
        public List<string>? ClassIds { get; set; }

        public IReadOnlyList<string> GetResponsibleClassIds()
        {
            if (!string.Equals(Role, "teacher", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(ClassId) ? Array.Empty<string>() : new[] { ClassId };

            return (ClassIds ?? new List<string>())
                .Append(ClassId ?? "")
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool IsResponsibleForClass(string? classId) =>
            !string.IsNullOrWhiteSpace(classId) && GetResponsibleClassIds().Contains(
                classId, StringComparer.OrdinalIgnoreCase);

        public void SetResponsibleClassIds(IEnumerable<string>? classIds)
        {
            ClassIds = (classIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            ClassId = ClassIds.FirstOrDefault();
        }

        [JsonIgnore]
        public string ResponsibleClassText { get; set; } = "未分配";

        [JsonIgnore]
        public int ResponsibleClassCount => GetResponsibleClassIds().Count;

        /// <summary>列表多选勾选（不入库）。</summary>
        [JsonIgnore]
        public bool IsSelected { get; set; }

        [JsonIgnore]
        public bool IsSystemAdministrator =>
            string.Equals(UserId, SystemAdministratorPolicy.UserId,
                StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool CanDeleteAccount => !SystemAdministratorPolicy.IsReserved(this);

        /// <summary>
        /// 学生被分配到的柜机通讯 ID。null 表示兼容旧数据（默认全部柜机），
        /// 空数组表示尚未分配；教师和管理员不受此字段限制。
        /// </summary>
        [JsonProperty("assigned_device_ids")]
        public List<string>? AssignedDeviceIds { get; set; }

        /// <summary>
        /// 柜机绑定明细。null 表示尚未迁移的旧数据；空数组表示未分配柜机。
        /// AssignedDeviceIds 保留用于兼容旧版本，新的业务逻辑以此字段为准。
        /// </summary>
        [JsonProperty("cabinet_assignments")]
        public List<CabinetAssignment>? CabinetAssignments { get; set; }

        /// <summary>兼容旧版本的默认指纹 ID；每柜实际使用 CabinetAssignments 中的选择。</summary>
        [JsonProperty("fingerprint_id")]
        public int? FingerprintId { get; set; }

        /// <summary>密码盐值（管理员和教师使用；学生为空）</summary>
        [JsonProperty("password_salt")]
        public string PasswordSalt { get; set; } = "";

        /// <summary>登录密码哈希（管理员和教师使用；学生为空）</summary>
        [JsonProperty("password_hash")]
        public string PasswordHash { get; set; } = "";

        /// <summary>停用后不能登录，也不会下发本地开锁权限。</summary>
        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonIgnore]
        public string StatusText => Enabled ? "启用" : "停用";

        /// <summary>创建时间</summary>
        [JsonProperty("create_time")]
        public DateTime CreateTime { get; set; }

        /// <summary>更新时间</summary>
        [JsonProperty("update_time")]
        public DateTime? UpdateTime { get; set; }
    }
}
