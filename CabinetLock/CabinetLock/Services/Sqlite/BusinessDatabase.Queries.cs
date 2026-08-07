using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace CabinetLock
{
    public enum UserPageSort
    {
        RoleThenId,
        RoleThenName
    }

    public sealed class UserPageQuery
    {
        public int PageIndex { get; init; }
        public int PageSize { get; init; } = 50;
        public string? Role { get; init; }
        public string? Keyword { get; init; }
        public string? ClassId { get; init; }
        public string? ClassName { get; init; }
        public string ScopeRole { get; init; } = "";
        public string ScopeUserId { get; init; } = "";
        public IReadOnlyCollection<string>? ScopeClassIds { get; init; }
        public UserPageSort Sort { get; init; } = UserPageSort.RoleThenId;
    }

    public sealed class ClassPageQuery
    {
        public int PageIndex { get; init; }
        public int PageSize { get; init; } = 20;
        public string? Keyword { get; init; }
        public IReadOnlyCollection<string>? VisibleClassIds { get; init; }
    }

    public sealed class PagedResult<T>
    {
        public PagedResult(IReadOnlyList<T> items, int totalCount, int pageIndex, int pageSize)
        {
            Items = items;
            TotalCount = totalCount;
            PageIndex = pageIndex;
            PageSize = pageSize;
        }

        public IReadOnlyList<T> Items { get; }
        public int TotalCount { get; }
        public int PageIndex { get; }
        public int PageSize { get; }
    }

    public readonly record struct StudentBindingStatistics(
        int BoundStudents, int TotalStudents);

    public static partial class BusinessDatabase
    {
        public static PagedResult<User> QueryUsers(UserPageQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                var parameters = new List<(string Name, object Value)>();
                string where = BuildUserWhere(query, parameters);

                using SqliteCommand countCommand = connection.CreateCommand();
                countCommand.CommandText = $"SELECT COUNT(1) FROM users u {where}";
                AddParameters(countCommand, parameters);
                int totalCount = Convert.ToInt32(countCommand.ExecuteScalar());
                int pageSize = Math.Clamp(query.PageSize, 1, 500);
                int totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
                int pageIndex = Math.Clamp(query.PageIndex, 0, totalPages - 1);

                string orderBy = query.Sort == UserPageSort.RoleThenName
                    ? "u.role COLLATE NOCASE, u.name COLLATE NOCASE, u.user_id COLLATE NOCASE"
                    : "u.role COLLATE NOCASE, COALESCE(NULLIF(u.user_code,''),u.user_id) COLLATE NOCASE";
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = $@"
SELECT u.user_id,u.user_code,u.name,u.gender,u.role,u.class_id,u.class_ids_json,
u.assigned_device_ids_json,u.cabinet_assignments_json,u.fingerprint_id,
u.password_salt,u.password_hash,u.enabled,u.create_time,u.update_time
FROM users u {where}
ORDER BY {orderBy}
LIMIT $limit OFFSET $offset";
                AddParameters(command, parameters);
                command.Parameters.AddWithValue("$limit", pageSize);
                command.Parameters.AddWithValue("$offset", pageIndex * pageSize);
                using SqliteDataReader reader = command.ExecuteReader();
                var users = new List<User>(Math.Min(pageSize, totalCount));
                while (reader.Read()) users.Add(MapUser(reader));
                return new PagedResult<User>(users, totalCount, pageIndex, pageSize);
            }
        }

        public static User? ReadUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;
            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = @"
SELECT user_id,user_code,name,gender,role,class_id,class_ids_json,
assigned_device_ids_json,cabinet_assignments_json,fingerprint_id,
password_salt,password_hash,enabled,create_time,update_time
FROM users WHERE user_id=$id COLLATE NOCASE LIMIT 1";
                command.Parameters.AddWithValue("$id", userId.Trim());
                using SqliteDataReader reader = command.ExecuteReader();
                return reader.Read() ? MapUser(reader) : null;
            }
        }

        public static IReadOnlyList<string> ReadTeacherNamesForClass(string classId)
        {
            if (string.IsNullOrWhiteSpace(classId)) return Array.Empty<string>();
            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = @"
SELECT COALESCE(NULLIF(TRIM(u.name),''),NULLIF(TRIM(u.user_code),''),u.user_id)
FROM users u
WHERE u.role='teacher' COLLATE NOCASE
AND (u.class_id=$class_id COLLATE NOCASE OR EXISTS (
    SELECT 1 FROM json_each(
        CASE WHEN json_valid(u.class_ids_json) THEN u.class_ids_json ELSE '[]' END)
    WHERE value=$class_id COLLATE NOCASE))
ORDER BY u.name COLLATE NOCASE,u.user_id COLLATE NOCASE";
                command.Parameters.AddWithValue("$class_id", classId.Trim());
                using SqliteDataReader reader = command.ExecuteReader();
                var result = new List<string>();
                while (reader.Read())
                {
                    string name = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
                }
                return result;
            }
        }

        public static List<FingerprintTemplate> ReadFpTemplateMetasForUsers(
            IEnumerable<string>? userIds)
        {
            List<string> ids = (userIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var result = new List<FingerprintTemplate>();
            if (ids.Count == 0) return result;

            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                foreach (string[] batch in ids.Chunk(500))
                {
                    using SqliteCommand command = connection.CreateCommand();
                    List<string> names = AddInParameters(command, "$fp_user", batch);
                    command.CommandText = $@"
SELECT fingerprint_id,user_id,user_name,finger_index,finger_name,
quality,enabled,enroll_time,template_size,source_device,backup_status,note
FROM fingerprints
WHERE user_id COLLATE NOCASE IN ({string.Join(',', names)})
ORDER BY enroll_time DESC,fingerprint_id DESC";
                    using SqliteDataReader reader = command.ExecuteReader();
                    while (reader.Read()) result.Add(MapFpMeta(reader));
                }
            }
            return result;
        }

        public static HashSet<int> ReadUsedFingerprintIds()
        {
            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = @"
SELECT fingerprint_id FROM fingerprints WHERE fingerprint_id > 0
UNION
SELECT fingerprint_id FROM users WHERE fingerprint_id > 0";
                using SqliteDataReader reader = command.ExecuteReader();
                var result = new HashSet<int>();
                while (reader.Read() && !reader.IsDBNull(0)) result.Add(reader.GetInt32(0));
                return result;
            }
        }

        public static Dictionary<string, string> ReadUserCodes(
            IEnumerable<string>? userIds)
        {
            List<string> ids = (userIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (ids.Count == 0) return result;

            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                foreach (string[] batch in ids.Chunk(500))
                {
                    using SqliteCommand command = connection.CreateCommand();
                    List<string> names = AddInParameters(command, "$user", batch);
                    command.CommandText = $@"
SELECT user_id,COALESCE(NULLIF(user_code,''),user_id)
FROM users WHERE user_id COLLATE NOCASE IN ({string.Join(',', names)})";
                    using SqliteDataReader reader = command.ExecuteReader();
                    while (reader.Read())
                        result[reader.GetString(0)] = reader.GetString(1);
                }
            }
            return result;
        }

        public static StudentBindingStatistics QueryStudentBindingStatistics(
            string scopeRole, string scopeUserId,
            IReadOnlyCollection<string>? scopeClassIds)
        {
            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                var parameters = new List<(string Name, object Value)>();
                string where = BuildUserWhere(new UserPageQuery
                {
                    Role = "student",
                    ScopeRole = scopeRole,
                    ScopeUserId = scopeUserId,
                    ScopeClassIds = scopeClassIds
                }, parameters);
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = $@"
SELECT COUNT(1),COALESCE(SUM(CASE WHEN bound.user_id IS NULL THEN 0 ELSE 1 END),0)
FROM users u
LEFT JOIN (
    SELECT user_id FROM permissions GROUP BY user_id
) bound ON bound.user_id=u.user_id
{where}";
                AddParameters(command, parameters);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read()) return new StudentBindingStatistics(0, 0);
                int total = reader.GetInt32(0);
                int bound = reader.GetInt32(1);
                return new StudentBindingStatistics(bound, total);
            }
        }

        public static PagedResult<ClassInfo> QueryClasses(ClassPageQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                List<TeacherClassAssignment> teachers = ReadTeacherAssignments(connection);
                var parameters = new List<(string Name, object Value)>();
                string where = BuildClassWhere(query, teachers, parameters);

                using SqliteCommand countCommand = connection.CreateCommand();
                countCommand.CommandText = $"SELECT COUNT(1) FROM classes c {where}";
                AddParameters(countCommand, parameters);
                int totalCount = Convert.ToInt32(countCommand.ExecuteScalar());
                int pageSize = Math.Clamp(query.PageSize, 1, 500);
                int totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
                int pageIndex = Math.Clamp(query.PageIndex, 0, totalPages - 1);

                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = $@"
SELECT c.class_id,c.name,c.enabled,c.create_time
FROM classes c {where}
ORDER BY c.class_id COLLATE NOCASE
LIMIT $limit OFFSET $offset";
                AddParameters(command, parameters);
                command.Parameters.AddWithValue("$limit", pageSize);
                command.Parameters.AddWithValue("$offset", pageIndex * pageSize);
                var classes = new List<ClassInfo>(Math.Min(pageSize, totalCount));
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        classes.Add(new ClassInfo
                        {
                            ClassId = reader.GetString(0),
                            Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            Enabled = !reader.IsDBNull(2) && reader.GetInt64(2) != 0,
                            CreateTime = ParseTime(reader.IsDBNull(3) ? null : reader.GetString(3)) ?? DateTime.MinValue
                        });
                    }
                }

                PopulateClassStatistics(connection, classes, teachers);
                return new PagedResult<ClassInfo>(classes, totalCount, pageIndex, pageSize);
            }
        }

        public static Dictionary<string, string> ReadClassNames(
            IReadOnlyCollection<string>? visibleClassIds = null)
        {
            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                if (visibleClassIds == null)
                {
                    command.CommandText = "SELECT class_id,name FROM classes ORDER BY class_id COLLATE NOCASE";
                }
                else if (visibleClassIds.Count == 0)
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    var names = AddInParameters(command, "$class", visibleClassIds);
                    command.CommandText = $"SELECT class_id,name FROM classes WHERE class_id COLLATE NOCASE IN ({string.Join(',', names)}) ORDER BY class_id COLLATE NOCASE";
                }
                using SqliteDataReader reader = command.ExecuteReader();
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read()) result[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
                return result;
            }
        }

        public static Dictionary<string, DateTime> ReadLatestPermissionUpdateTimes(
            IReadOnlyCollection<string>? userIds = null)
        {
            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                string where = "";
                if (userIds != null)
                {
                    if (userIds.Count == 0)
                        return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                    var names = AddInParameters(command, "$user", userIds);
                    where = $"WHERE user_id COLLATE NOCASE IN ({string.Join(',', names)})";
                }
                command.CommandText = $@"
SELECT user_id,MAX(update_time) FROM permissions {where}
GROUP BY user_id";
                using SqliteDataReader reader = command.ExecuteReader();
                var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                {
                    DateTime? value = ParseTime(reader.IsDBNull(1) ? null : reader.GetString(1));
                    if (value.HasValue && !reader.IsDBNull(0)) result[reader.GetString(0)] = value.Value;
                }
                return result;
            }
        }

        public static List<UserPermission> ReadUserPermissions(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return new List<UserPermission>();
            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = @"
SELECT id,user_id,lock_id,has_access,update_time FROM permissions
WHERE user_id=$user COLLATE NOCASE ORDER BY lock_id";
                command.Parameters.AddWithValue("$user", userId.Trim());
                using SqliteDataReader reader = command.ExecuteReader();
                var result = new List<UserPermission>(4);
                while (reader.Read())
                {
                    result.Add(new UserPermission
                    {
                        Id = Convert.ToInt32(reader.GetInt64(0)),
                        UserId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        LockId = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        HasAccess = !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
                        UpdateTime = ParseTime(reader.IsDBNull(4) ? null : reader.GetString(4)) ?? DateTime.MinValue
                    });
                }
                return result;
            }
        }

        public static Dictionary<string, List<UserPermission>> ReadUserPermissions(
            IEnumerable<string>? userIds)
        {
            List<string> ids = (userIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var result = new Dictionary<string, List<UserPermission>>(
                StringComparer.OrdinalIgnoreCase);
            if (ids.Count == 0) return result;

            lock (Sync)
            {
                Initialize();
                using SqliteConnection connection = Open();
                foreach (string[] batch in ids.Chunk(500))
                {
                    using SqliteCommand command = connection.CreateCommand();
                    List<string> names = AddInParameters(command, "$permission_user", batch);
                    command.CommandText = $@"
SELECT id,user_id,lock_id,has_access,update_time FROM permissions
WHERE user_id COLLATE NOCASE IN ({string.Join(',', names)})
ORDER BY user_id COLLATE NOCASE,lock_id";
                    using SqliteDataReader reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var permission = new UserPermission
                        {
                            Id = Convert.ToInt32(reader.GetInt64(0)),
                            UserId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            LockId = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                            HasAccess = !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
                            UpdateTime = ParseTime(reader.IsDBNull(4) ? null : reader.GetString(4)) ?? DateTime.MinValue
                        };
                        if (!result.TryGetValue(permission.UserId, out List<UserPermission>? items))
                        {
                            items = new List<UserPermission>(4);
                            result[permission.UserId] = items;
                        }
                        items.Add(permission);
                    }
                }
            }
            return result;
        }

        private static string BuildUserWhere(
            UserPageQuery query, List<(string Name, object Value)> parameters)
        {
            var conditions = new List<string>();
            string scopeRole = query.ScopeRole.Trim().ToLowerInvariant();
            if (scopeRole == "teacher")
            {
                var visible = (query.ScopeClassIds ?? Array.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var scopeParts = new List<string>
                {
                    "u.user_id=$scope_user COLLATE NOCASE",
                    "u.role='admin'"
                };
                parameters.Add(("$scope_user", query.ScopeUserId));
                if (visible.Count > 0)
                {
                    var names = AddParameterNames(parameters, "$scope_class", visible);
                    scopeParts.Add($"(u.role='student' AND u.class_id COLLATE NOCASE IN ({string.Join(',', names)}))");
                }
                conditions.Add($"({string.Join(" OR ", scopeParts)})");
            }
            else if (scopeRole == "student")
            {
                conditions.Add("u.user_id=$scope_user COLLATE NOCASE");
                parameters.Add(("$scope_user", query.ScopeUserId));
            }
            else if (scopeRole != "admin")
            {
                conditions.Add("1=0");
            }

            if (!string.IsNullOrWhiteSpace(query.Role))
            {
                conditions.Add("u.role=$role COLLATE NOCASE");
                parameters.Add(("$role", query.Role.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(query.ClassId))
            {
                conditions.Add("u.class_id=$class_id COLLATE NOCASE");
                parameters.Add(("$class_id", query.ClassId.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(query.ClassName))
            {
                conditions.Add(ClassMembershipCondition("= $class_name COLLATE NOCASE"));
                parameters.Add(("$class_name", query.ClassName.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                conditions.Add($@"(
u.name LIKE $keyword COLLATE NOCASE OR
COALESCE(NULLIF(u.user_code,''),u.user_id) LIKE $keyword COLLATE NOCASE OR
u.user_id LIKE $keyword COLLATE NOCASE OR
CASE u.role WHEN 'admin' THEN '管理员' WHEN 'teacher' THEN '教师' ELSE '学生' END LIKE $keyword COLLATE NOCASE OR
COALESCE(u.class_id,'') LIKE $keyword COLLATE NOCASE OR
{ClassMembershipCondition("LIKE $keyword COLLATE NOCASE")})");
                parameters.Add(("$keyword", $"%{query.Keyword.Trim()}%"));
            }
            return conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        }

        private static string ClassMembershipCondition(string nameComparison) => $@"EXISTS (
SELECT 1 FROM classes visible_class
WHERE visible_class.name {nameComparison}
AND (visible_class.class_id=u.class_id COLLATE NOCASE OR EXISTS (
SELECT 1 FROM json_each(CASE WHEN json_valid(u.class_ids_json) THEN u.class_ids_json ELSE '[]' END) membership
WHERE membership.value=visible_class.class_id COLLATE NOCASE)))";

        private static string BuildClassWhere(ClassPageQuery query,
            IReadOnlyCollection<TeacherClassAssignment> teachers,
            List<(string Name, object Value)> parameters)
        {
            var conditions = new List<string>();
            if (query.VisibleClassIds != null)
            {
                List<string> visible = query.VisibleClassIds.Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (visible.Count == 0) conditions.Add("1=0");
                else
                {
                    var names = AddParameterNames(parameters, "$visible_class", visible);
                    conditions.Add($"c.class_id COLLATE NOCASE IN ({string.Join(',', names)})");
                }
            }
            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                string keyword = query.Keyword.Trim();
                List<string> teacherClassIds = teachers
                    .Where(teacher => teacher.SearchText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(teacher => teacher.ClassIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var keywordParts = new List<string>
                {
                    "c.class_id LIKE $keyword COLLATE NOCASE",
                    "c.name LIKE $keyword COLLATE NOCASE"
                };
                parameters.Add(("$keyword", $"%{keyword}%"));
                if (teacherClassIds.Count > 0)
                {
                    var names = AddParameterNames(parameters, "$teacher_class", teacherClassIds);
                    keywordParts.Add($"c.class_id COLLATE NOCASE IN ({string.Join(',', names)})");
                }
                conditions.Add($"({string.Join(" OR ", keywordParts)})");
            }
            return conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        }

        private static void PopulateClassStatistics(SqliteConnection connection,
            IReadOnlyCollection<ClassInfo> classes, IReadOnlyCollection<TeacherClassAssignment> teachers)
        {
            if (classes.Count == 0) return;
            var byId = classes.ToDictionary(item => item.ClassId, StringComparer.OrdinalIgnoreCase);
            using (SqliteCommand command = connection.CreateCommand())
            {
                var names = AddInParameters(command, "$page_class", byId.Keys);
                command.CommandText = $@"
SELECT class_id,COUNT(1) FROM users
WHERE role='student' AND class_id COLLATE NOCASE IN ({string.Join(',', names)})
GROUP BY class_id";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0) && byId.TryGetValue(reader.GetString(0), out ClassInfo? item))
                        item.StudentCount = reader.GetInt32(1);
                }
            }
            foreach (ClassInfo item in classes)
            {
                string[] assigned = teachers.Where(teacher => teacher.ClassIds.Contains(item.ClassId))
                    .Select(teacher => teacher.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                item.TeacherCount = assigned.Length;
                item.TeacherText = assigned.Length == 0 ? "未分配" : string.Join("、", assigned);
            }
        }

        private static List<TeacherClassAssignment> ReadTeacherAssignments(SqliteConnection connection)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
SELECT user_id,user_code,name,class_id,class_ids_json FROM users
WHERE role='teacher' ORDER BY user_id COLLATE NOCASE";
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<TeacherClassAssignment>();
            while (reader.Read())
            {
                string userId = reader.GetString(0);
                string userCode = reader.IsDBNull(1) ? userId : reader.GetString(1);
                string name = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var ids = ReadJsonList<string>(reader, 4) ?? new List<string>();
                if (!reader.IsDBNull(3)) ids.Add(reader.GetString(3));
                result.Add(new TeacherClassAssignment(
                    string.IsNullOrWhiteSpace(name) ? userCode : name,
                    $"{name} {userCode} {userId}",
                    ids.Where(id => !string.IsNullOrWhiteSpace(id))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)));
            }
            return result;
        }

        private static User MapUser(SqliteDataReader reader) => new()
        {
            UserId = reader.GetString(0),
            UserCode = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
            Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
            Gender = reader.IsDBNull(3) ? "" : reader.GetString(3),
            Role = reader.IsDBNull(4) ? "" : reader.GetString(4),
            ClassId = reader.IsDBNull(5) ? null : reader.GetString(5),
            ClassIds = ReadJsonList<string>(reader, 6),
            AssignedDeviceIds = ReadJsonList<string>(reader, 7),
            CabinetAssignments = ReadJsonList<CabinetAssignment>(reader, 8),
            FingerprintId = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            PasswordSalt = reader.IsDBNull(10) ? "" : reader.GetString(10),
            PasswordHash = reader.IsDBNull(11) ? "" : reader.GetString(11),
            Enabled = !reader.IsDBNull(12) && reader.GetInt64(12) != 0,
            CreateTime = ParseTime(reader.IsDBNull(13) ? null : reader.GetString(13)) ?? DateTime.MinValue,
            UpdateTime = ParseTime(reader.IsDBNull(14) ? null : reader.GetString(14))
        };

        private static List<T>? ReadJsonList<T>(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return null;
            try { return JsonConvert.DeserializeObject<List<T>>(reader.GetString(ordinal)); }
            catch { return new List<T>(); }
        }

        private static void AddParameters(SqliteCommand command,
            IEnumerable<(string Name, object Value)> parameters)
        {
            foreach ((string name, object value) in parameters)
                command.Parameters.AddWithValue(name, value);
        }

        private static List<string> AddParameterNames(List<(string Name, object Value)> parameters,
            string prefix, IEnumerable<string> values)
        {
            var names = new List<string>();
            foreach (string value in values)
            {
                string name = $"{prefix}_{names.Count}";
                names.Add(name);
                parameters.Add((name, value));
            }
            return names;
        }

        private static List<string> AddInParameters(SqliteCommand command,
            string prefix, IEnumerable<string> values)
        {
            var names = new List<string>();
            foreach (string value in values)
            {
                string name = $"{prefix}_{names.Count}";
                names.Add(name);
                command.Parameters.AddWithValue(name, value);
            }
            return names;
        }

        private sealed record TeacherClassAssignment(
            string DisplayName, string SearchText, HashSet<string> ClassIds);
    }
}
