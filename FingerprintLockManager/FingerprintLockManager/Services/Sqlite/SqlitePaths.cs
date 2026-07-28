using System.IO;

namespace FingerprintLockManager
{
    /// <summary>
    /// 本机 SQLite 数据库路径：%APPDATA%\FingerprintLockManager\data\
    /// </summary>
    public static class SqlitePaths
    {
        private const string AppDataFolderName = "FingerprintLockManager";
        private const string DataFolderName = "data";

        public const string BusinessFileName = "business.db";
        public const string LogsFileName = "logs.db";

        public static string GetDataDirectory()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, AppDataFolderName, DataFolderName);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        public static string BusinessDbPath => Path.Combine(GetDataDirectory(), BusinessFileName);
        public static string LogsDbPath => Path.Combine(GetDataDirectory(), LogsFileName);
    }
}
