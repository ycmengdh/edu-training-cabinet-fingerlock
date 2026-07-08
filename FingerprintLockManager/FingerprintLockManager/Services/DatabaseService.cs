using System.IO;
using FreeSql;

namespace FingerprintLockManager
{
    /// <summary>
    /// 数据库初始化服务
    /// 负责初始化全局 FreeSql 实例、自动建表，并创建默认管理员账号
    /// </summary>
    public class DatabaseService
    {
        /// <summary>全局 FreeSql 实例（所有 Service 共用）</summary>
        public static IFreeSql Fsql;

        /// <summary>默认管理员账号</summary>
        private const string DefaultAdminId = "admin";

        /// <summary>默认管理员密码（明文，存储前会进行 SHA256 哈希）</summary>
        private const string DefaultAdminPassword = "admin123";

        /// <summary>
        /// 初始化数据库连接并自动建表
        /// </summary>
        /// <param name="dbPath">SQLite 数据库文件路径，默认 ./Data/fingerprint_lock.db</param>
        public void Init(string dbPath = "./Data/fingerprint_lock.db")
        {
            // 若已初始化则直接返回，避免重复构建
            if (Fsql != null) return;

            // 解析为绝对路径（相对路径基于程序运行目录）
            dbPath = ResolveFullPath(dbPath);

            // 确保数据库所在目录存在
            string dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 使用 FreeSqlBuilder 构建 SQLite 数据库实例
            Fsql = new FreeSqlBuilder()
                .UseConnectionString(DataType.Sqlite, $"Data Source={dbPath}")
                .UseAutoSyncStructure(true)   // 自动同步表结构（建表）
                .Build();

            // 初始化默认管理员账号
            InitDefaultAdmin();
        }

        /// <summary>
        /// 检查并创建默认管理员账号（admin / admin123）
        /// 若数据库中已存在任意管理员账号则跳过
        /// </summary>
        private void InitDefaultAdmin()
        {
            try
            {
                // 查询是否已存在管理员账号
                bool hasAdmin = Fsql.Select<User>()
                    .Where(u => u.Role == "admin")
                    .Any();

                if (hasAdmin) return;

                // 创建默认管理员
                var admin = new User
                {
                    UserId = DefaultAdminId,
                    Name = "系统管理员",
                    Role = "admin",
                    FingerprintId = null,
                    PasswordHash = PasswordHelper.HashPassword(DefaultAdminPassword),
                    CreateTime = DateTime.Now,
                    UpdateTime = DateTime.Now
                };

                Fsql.Insert(admin).ExecuteAffrows();
            }
            catch
            {
                // 初始化默认管理员失败时忽略，避免影响程序启动
            }
        }

        /// <summary>
        /// 将相对路径解析为基于程序运行目录的绝对路径
        /// </summary>
        /// <param name="path">原始路径</param>
        /// <returns>绝对路径</returns>
        private static string ResolveFullPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            // 已是绝对路径则直接返回
            if (Path.IsPathRooted(path)) return path;

            // 相对路径基于程序运行目录解析
            return Path.Combine(AppContext.BaseDirectory, path);
        }
    }
}
