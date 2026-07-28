#include "serial_uplink.h"
#include <Arduino.h>
#include <string.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
static SemaphoreHandle_t s_serialMux = nullptr;
void SerialUplink::begin() {
  if (s_serialMux == nullptr) s_serialMux = xSemaphoreCreateMutex();
}
bool SerialUplink::write(const uint8_t *data, size_t len) {
  if (data == nullptr || len == 0) return true;
  begin();
  if (s_serialMux) xSemaphoreTake(s_serialMux, portMAX_DELAY);
  size_t sent = Serial.write(data, len);
  if (s_serialMux) xSemaphoreGive(s_serialMux);
  return sent == len;
}
bool SerialUplink::writeText(const char *text) {
  if (text == nullptr) return true;
  return write(reinterpret_cast<const uint8_t *>(text), strlen(text));
}
