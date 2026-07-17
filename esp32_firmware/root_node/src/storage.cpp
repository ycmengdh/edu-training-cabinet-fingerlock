/**
 * storage.cpp - Minimal NVS storage for Root Node
 */
#include "storage.h"
#include "debug.h"
#include <Preferences.h>
#include <sys/time.h>

static Preferences prefs;
bool Storage::initialized = false;

void Storage::begin() {
    if (!initialized) {
        prefs.begin("esp32_cfg", false);
        initialized = true;
        Debug::println(F("[STORAGE] Storage init done (NVS)"));
    }
}

bool Storage::loadDeviceConfig(DeviceConfig &cfg) {
    if (!initialized) begin();

    cfg.device_id    = prefs.getString("device_id", DEVICE_ID_DEFAULT);
    cfg.device_name  = prefs.getString("device_name", "Root Node");
    cfg.work_mode    = (WorkMode)prefs.getUChar("work_mode", MODE_MESH);
    cfg.is_root      = prefs.getBool("is_root", true);
    cfg.uplink_mode  = (UplinkMode)prefs.getUChar("uplink_mode", UPLINK_USB);
    cfg.mesh_channel = prefs.getUChar("mesh_channel", MESH_CHANNEL);
    cfg.mesh_password = prefs.getString("mesh_password", MESH_PASSWORD);
    cfg.wifi_ssid    = prefs.getString("wifi_ssid", "");
    cfg.wifi_password = prefs.getString("wifi_password", "");
    cfg.server_ip    = prefs.getString("server_ip", UPLINK_SERVER_IP_DEFAULT);
    cfg.server_port  = prefs.getUShort("server_port", UPLINK_TCP_PORT);
    cfg.fingerprint_count = prefs.getUShort("fp_count", 0);
    cfg.perm_version = prefs.getUInt("perm_version", 0);

    return true;
}

bool Storage::saveDeviceConfig(const DeviceConfig &cfg) {
    if (!initialized) begin();

    prefs.putString("device_id", cfg.device_id);
    prefs.putString("device_name", cfg.device_name);
    prefs.putUChar("work_mode", (uint8_t)cfg.work_mode);
    prefs.putBool("is_root", cfg.is_root);
    prefs.putUChar("uplink_mode", (uint8_t)cfg.uplink_mode);
    prefs.putUChar("mesh_channel", cfg.mesh_channel);
    prefs.putString("mesh_password", cfg.mesh_password);
    prefs.putString("wifi_ssid", cfg.wifi_ssid);
    prefs.putString("wifi_password", cfg.wifi_password);
    prefs.putString("server_ip", cfg.server_ip);
    prefs.putUShort("server_port", cfg.server_port);
    prefs.putUShort("fp_count", cfg.fingerprint_count);
    prefs.putUInt("perm_version", cfg.perm_version);

    return true;
}

WorkMode Storage::loadWorkMode() {
    if (!initialized) begin();
    return (WorkMode)prefs.getUChar("work_mode", MODE_MESH);
}

bool Storage::saveWorkMode(WorkMode mode) {
    if (!initialized) begin();
    return prefs.putUChar("work_mode", (uint8_t)mode);
}

void Storage::setUnixTime(uint32_t unixTime) {
    if (!initialized) begin();
    prefs.putUInt("unix_time", unixTime);

    struct timeval tv;
    tv.tv_sec = unixTime;
    tv.tv_usec = 0;
    settimeofday(&tv, nullptr);
}

uint32_t Storage::getUnixTime() {
    return (uint32_t)time(nullptr);
}

bool Storage::isTimeSynced() {
    if (!initialized) begin();
    uint32_t saved = prefs.getUInt("unix_time", 0);
    return (saved > 1700000000) || (time(nullptr) > 1700000000);
}
