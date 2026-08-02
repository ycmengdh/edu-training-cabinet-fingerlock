using System.IO;

namespace CabinetLock
{
    /// <summary>
    /// 本机 SQLite 数据库路径：应用程序目录\data\
    /// </summary>
    public static class SqlitePaths
    {
        private const string DataFolderName = "data";

        public const string BusinessFileName = "business.db";
        public const string LogsFileName = "logs.db";

        public static string GetDataDirectory()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string dir = Path.Combine(appDir, DataFolderName);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        public static string BusinessDbPath => Path.Combine(GetDataDirectory(), BusinessFileName);
        public static string LogsDbPath => Path.Combine(GetDataDirectory(), LogsFileName);
    }
}
