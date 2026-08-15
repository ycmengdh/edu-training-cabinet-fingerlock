#pragma once

#include <stdbool.h>
#include <stddef.h>

#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    char phase[20];
    char version[32];
    char error[64];
    uint32_t generation;
    uint8_t progress;
    bool active;
} cabinet_ota_status_t;

bool cabinet_ota_init(void);
bool cabinet_ota_start_health_validation(void);
bool cabinet_ota_running_image_validated(void);
esp_err_t cabinet_ota_request(const char *version, size_t image_size);
esp_err_t cabinet_ota_pause(void);
bool cabinet_ota_get_status(cabinet_ota_status_t *status);

#ifdef __cplusplus
}
#endif
