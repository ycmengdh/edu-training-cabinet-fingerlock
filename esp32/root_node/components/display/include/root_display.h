#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint32_t uptime_seconds;
    bool host_connected;
    bool mesh_connected;
    bool sd_ready;
    int mesh_layer;
    int child_count;
    int route_count;
    uint32_t send_failures;
    uint32_t receive_drops;
    uint32_t heartbeat_acks;
    uint32_t heartbeat_timeouts;
} root_display_status_t;

// The TFT is diagnostic-only. Failure is reported through these APIs and must
// never be promoted to a fatal application error.
bool root_display_init(const char *root_id);
bool root_display_ready(void);
const char *root_display_last_error(void);
void root_display_update(const root_display_status_t *status);

#ifdef __cplusplus
}
#endif
