using System.Globalization;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace CabinetLock
{
    public static partial class BusinessDatabase
    {
        public static MaintenanceSettings GetMaintenanceSettings()
        {
            lock (Sync)
            {
                Initialize();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"SELECT setting_value,config_version,update_time
FROM system_settings WHERE setting_key='maintenance_pin'";
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return new MaintenanceSettings();
                string pin = reader.GetString(0);
                return new MaintenanceSettings
                {
                    Pin = MaintenanceSettings.IsValidPin(pin)
                        ? pin : MaintenanceSettings.DefaultPin,
                    Version = Convert.ToUInt32(reader.GetInt64(1), CultureInfo.InvariantCulture),
                    UpdateTime = DateTime.TryParse(reader.GetString(2), out DateTime updated)
                        ? updated : DateTime.Now
                };
            }
        }

        public static MaintenanceSettings SetMaintenancePin(string pin)
        {
            if (!MaintenanceSettings.IsValidPin(pin))
                throw new ArgumentException("维护密码必须是由按键 1-4 组成的 6 位密码", nameof(pin));

            lock (Sync)
            {
                Initialize();
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                uint version;
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = @"SELECT config_version FROM system_settings
WHERE setting_key='maintenance_pin'";
                    object? current = read.ExecuteScalar();
                    version = current == null || current is DBNull
                        ? 1 : unchecked(Convert.ToUInt32(current, CultureInfo.InvariantCulture) + 1);
                    if (version == 0) version = 1;
                }

                DateTime updated = DateTime.Now;
                using (var write = connection.CreateCommand())
                {
                    write.Transaction = transaction;
                    write.CommandText = @"INSERT INTO system_settings(
setting_key,setting_value,config_version,update_time)
VALUES('maintenance_pin',$value,$version,$updated)
ON CONFLICT(setting_key) DO UPDATE SET setting_value=$value,
config_version=$version,update_time=$updated;";
                    write.Parameters.AddWithValue("$value", pin);
                    write.Parameters.AddWithValue("$version", (long)version);
                    write.Parameters.AddWithValue("$updated", updated.ToString("o"));
                    write.ExecuteNonQuery();
                }
                using (var meta = connection.CreateCommand())
                {
                    meta.Transaction = transaction;
                    meta.CommandText = @"INSERT INTO table_meta(table_name,version,updated_at)
VALUES('system_settings',1,$updated)
ON CONFLICT(table_name) DO UPDATE SET version=version+1,updated_at=$updated;";
                    meta.Parameters.AddWithValue("$updated", updated.ToString("o"));
                    meta.ExecuteNonQuery();
                }
                transaction.Commit();
                return new MaintenanceSettings { Pin = pin, Version = version, UpdateTime = updated };
            }
        }

        private static JArray ReadSystemSettings(SqliteConnection connection)
        {
            var output = new JArray();
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT setting_key,setting_value,config_version,update_time
FROM system_settings ORDER BY setting_key";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                output.Add(new JObject
                {
                    ["setting_key"] = reader.GetString(0),
                    ["setting_value"] = reader.GetString(1),
                    ["config_version"] = reader.GetInt64(2),
                    ["update_time"] = reader.GetString(3)
                });
            }
            return output;
        }

        private static void WriteSystemSettings(
            SqliteConnection connection, SqliteTransaction transaction, JArray array)
        {
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM system_settings";
                clear.ExecuteNonQuery();
            }
            foreach (JObject row in array.OfType<JObject>())
            {
                string key = row.Value<string>("setting_key") ?? "";
                string value = row.Value<string>("setting_value") ?? "";
                if (string.IsNullOrWhiteSpace(key)) continue;
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = @"INSERT INTO system_settings(
setting_key,setting_value,config_version,update_time)
VALUES($key,$value,$version,$updated)";
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.AddWithValue("$value", value);
                insert.Parameters.AddWithValue("$version", row.Value<long?>("config_version") ?? 1);
                insert.Parameters.AddWithValue("$updated",
                    row.Value<string>("update_time") ?? DateTime.Now.ToString("o"));
                insert.ExecuteNonQuery();
            }
            using var ensure = connection.CreateCommand();
            ensure.Transaction = transaction;
            ensure.CommandText = @"INSERT OR IGNORE INTO system_settings(
setting_key,setting_value,config_version,update_time)
VALUES('maintenance_pin','112233',1,$updated)";
            ensure.Parameters.AddWithValue("$updated", DateTime.Now.ToString("o"));
            ensure.ExecuteNonQuery();
        }
    }
}
