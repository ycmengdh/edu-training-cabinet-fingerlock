#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "cabinet_protocol.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*cab_controller_tx_t)(const uint8_t *data, size_t length,
                                    bool mesh_ingress, void *context);

bool cab_controller_init(const char *device_id, cab_controller_tx_t transmit,
                         void *context);
void cab_controller_set_mesh_connected(bool connected);
void cab_controller_handle(const cab_app_view_t *request, bool mesh_ingress);
void cab_controller_update(void);
void cab_controller_report_status(bool mesh_ingress);

#ifdef __cplusplus
}
#endif
