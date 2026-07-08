using System.IO;
using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>
    /// 应用配置数据结构（对应 app_config.json）
    /// </summary>
    public class AppConfig
    {
        /// <summary>TCP 监听端口（STA 模式下上位机作为 TCP 服务端的监听端口）</summary>
        public int TcpPort { get; set; } = 8888;

        /// <summary>SQLite 数据库文件路径</summary>
        public string DatabasePath { get; set; } = "./Data/fingerprint_lock.db";

        /// <summary>默认 AP 模式下 ESP32 设备的 IP 地址</summary>
        public string ApDeviceIp { get; set; } = "192.168.4.1";

        /// <summary>默认 AP 模式下 ESP32 设备的 TCP 端口</summary>
        public int ApDevicePort { get; set; } = 8888;

        /// <summary>设备离线判定阈值（秒），超过该时间未收到心跳视为离线</summary>
        public int OfflineTimeoutSeconds { get; set; } = 60;
    }

    /// <summary>
    /// 应用配置助手
    /// 负责读取/保存 app_config.json 配置文件，并提供全局访问入口
    /// </summary>
    public static class ConfigHelper
    {
        /// <summary>配置文件名</summary>
        private const string ConfigFileName = "app_config.json";

        /// <summary>当前配置（懒加载，首次访问时从磁盘读取）</summary>
        private static AppConfig _current;

        /// <summary>
        /// 获取当前配置实例
        /// 首次访问时自动从 app_config.json 加载；文件不存在或读取失败时返回默认配置
        /// </summary>
        public static AppConfig Current
        {
            get
            {
                if (_current == null)
                {
                    _current = Load();
                }
                return _current;
            }
        }

        /// <summary>
        /// 从 app_config.json 加载配置
        /// </summary>
        /// <returns>反序列化后的 AppConfig；文件不存在或异常时返回默认配置</returns>
        public static AppConfig Load()
        {
            try
            {
                string path = GetConfigPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var cfg = JsonConvert.DeserializeObject<AppConfig>(json);
                    if (cfg != null)
                    {
                        return cfg;
                    }
                }
            }
            catch
            {
                // 读取失败时忽略，返回默认配置
            }
            return new AppConfig();
        }

        /// <summary>
        /// 保存配置到 app_config.json
        /// </summary>
        /// <param name="config">待保存的配置</param>
        public static void Save(AppConfig config)
        {
            try
            {
                if (config == null) return;

                string path = GetConfigPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(path, json);
                _current = config;
            }
            catch
            {
                // 保存失败时忽略，避免配置写入异常影响主流程
            }
        }

        /// <summary>
        /// 重置当前缓存的配置实例，强制下次访问时重新从磁盘加载
        /// </summary>
        public static void Reset()
        {
            _current = null;
        }

        /// <summary>
        /// 获取配置文件完整路径（位于程序运行目录下）
        /// </summary>
        private static string GetConfigPath()
        {
            return Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        }
    }
}
