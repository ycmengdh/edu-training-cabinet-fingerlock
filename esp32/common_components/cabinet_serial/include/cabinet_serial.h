#pragma once

#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CAB_SERIAL_USB_JTAG,
    CAB_SERIAL_UART0,
} cab_serial_mode_t;

esp_err_t cab_serial_init(cab_serial_mode_t mode);
int cab_serial_read(uint8_t *output, size_t output_size, uint32_t timeout_ms);
int cab_serial_write(const uint8_t *data, size_t length, uint32_t timeout_ms);
int cab_serial_frame_writer(const uint8_t *data, size_t length, void *context);

#ifdef __cplusplus
}
#endif
