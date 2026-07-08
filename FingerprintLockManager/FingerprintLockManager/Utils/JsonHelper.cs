using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>
    /// JSON 序列化/反序列化工具
    /// 基于 Newtonsoft.Json，统一序列化设置，并提供安全反序列化（异常时返回默认值）
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>统一的序列化设置：忽略 null 值，使用 ISO 日期格式</summary>
        private static readonly JsonSerializerSettings _settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Local
        };

        /// <summary>
        /// 将对象序列化为 JSON 字符串
        /// </summary>
        /// <param name="obj">待序列化的对象</param>
        /// <returns>JSON 字符串；对象为 null 时返回 null</returns>
        public static string Serialize(object obj)
        {
            if (obj == null) return null;
            return JsonConvert.SerializeObject(obj, _settings);
        }

        /// <summary>
        /// 将对象序列化为带缩进的 JSON 字符串（便于人读，常用于配置文件保存）
        /// </summary>
        /// <param name="obj">待序列化的对象</param>
        /// <returns>带缩进的 JSON 字符串</returns>
        public static string SerializeIndented(object obj)
        {
            if (obj == null) return null;
            return JsonConvert.SerializeObject(obj, Formatting.Indented, _settings);
        }

        /// <summary>
        /// 将 JSON 字符串反序列化为指定类型的对象
        /// 异常时安全返回 default(T)，不抛出异常
        /// </summary>
        /// <typeparam name="T">目标类型</typeparam>
        /// <param name="json">JSON 字符串</param>
        /// <returns>反序列化后的对象；失败或输入为空时返回 default(T)</returns>
        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrEmpty(json)) return default;
            try
            {
                return JsonConvert.DeserializeObject<T>(json, _settings);
            }
            catch
            {
                // 安全反序列化：任何异常均返回默认值，避免通信异常导致程序崩溃
                return default;
            }
        }
    }
}
