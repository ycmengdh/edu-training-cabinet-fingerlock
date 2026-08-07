#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "cabinet_protocol.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*root_controller_tx_t)(const uint8_t *data, size_t length,
                                     void *context);

bool root_controller_init(const char *root_id, root_controller_tx_t transmit,
                          void *context);
void root_controller_handle(const cab_app_view_t *request);
void root_controller_report_status(void);

#ifdef __cplusplus
}
#endif
