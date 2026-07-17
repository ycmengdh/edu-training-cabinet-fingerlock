/**
 * message_handler.h - Root Node message handler
 * Routes messages between host and cabinet nodes.
 * Acts as SD card data center (user table, fingerprint templates, etc.).
 * Handles: SD_QUERY, SD_SAVE, SD_QUERY_VERSION, UPLOAD/DOWNLOAD/DELETE_FP_TEMPLATE,
 *          REGISTER, TIME_SYNC, READ_CONFIG, WRITE_CONFIG, READ_STATUS, REBOOT.
 */
#ifndef MESSAGE_HANDLER_H
#define MESSAGE_HANDLER_H

#include <Arduino.h>
#include <ArduinoJson.h>
#include "config.h"

class MessageHandler {
public:
    static void init();
    static void handleIncoming(const String &message);
    static void handleMeshMessage(const uint8_t *fromMac, const String &message);
    static void handleDeviceOffline(const String &deviceId);
    static void update();

    // Send message via MeshComm (to host via MeshBridge)
    static bool sendMessage(const String &cmd, const String &dataJson = "",
                            const String &msgId = "");

    static void sendAck(const String &msgId, const String &result = "ok");
    static void sendError(ErrorCode code, const String &message,
                          const String &msgId = "");

    static bool sendLargeResponse(const String &cmd, const String &dataJson,
                                  const String &msgId = "");

private:
    // Command handlers
    static void cmdRegister(const String &msgId);
    static void cmdTimeSync(const JsonObject &data, const String &msgId);
    static void cmdReadConfig(const String &msgId);
    static void cmdWriteConfig(const JsonObject &data, const String &msgId);
    static void cmdReadStatus(const String &msgId);
    static void cmdReboot(const JsonObject &data, const String &msgId);

#ifdef ENABLE_SD_CARD
    static void cmdSdQuery(const JsonObject &data, const String &msgId);
    static void cmdSdSave(const JsonObject &data, const String &msgId);
    static void cmdSdQueryVersion(const String &msgId);
    static void cmdUploadFpTemplate(const JsonObject &data, const String &msgId);
    static void cmdDownloadFpTemplate(const JsonObject &data, const String &msgId);
    static void cmdDeleteFpTemplate(const JsonObject &data, const String &msgId);
#endif

    static uint8_t hexCharToVal(char c);
};

#endif // MESSAGE_HANDLER_H
