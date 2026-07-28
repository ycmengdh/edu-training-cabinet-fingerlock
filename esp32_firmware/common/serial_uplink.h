#pragma once
#include <stddef.h>
#include <stdint.h>
namespace SerialUplink {
void begin();
bool write(const uint8_t *data, size_t len);
bool writeText(const char *text);
}
