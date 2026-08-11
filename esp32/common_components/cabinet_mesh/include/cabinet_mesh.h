#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CAB_MESH_ROOT,
    CAB_MESH_CABINET,
} cab_mesh_role_t;

typedef void (*cab_mesh_receive_t)(const uint8_t from[6], const uint8_t *data,
                                   size_t length, void *context);
typedef void (*cab_mesh_state_t)(bool connected, int layer, void *context);

typedef struct {
    uint32_t sends;
    uint32_t send_failures;
    uint32_t receives;
    uint32_t receive_drops;
    uint32_t reconnects;
    uint32_t scan_cycles;
    uint32_t root_responses;
    uint32_t heartbeat_acks;
    uint32_t heartbeat_timeouts;
} cab_mesh_stats_t;

esp_err_t cab_mesh_init(cab_mesh_role_t role, cab_mesh_receive_t receive,
                        cab_mesh_state_t state, void *context);
bool cab_mesh_is_connected(void);
bool cab_mesh_is_root(void);
int cab_mesh_layer(void);
int cab_mesh_child_count(void);
int cab_mesh_route_count(void);
int cab_mesh_link_rssi(void);
void cab_mesh_self_mac(uint8_t output[6]);
void cab_mesh_ap_mac(uint8_t output[6]);
bool cab_mesh_parent_bssid(uint8_t output[6]);
void cab_mesh_request_parent_search(void);
esp_err_t cab_mesh_send_root(const uint8_t *data, size_t length);
esp_err_t cab_mesh_send_root_best_effort(const uint8_t *data, size_t length);
esp_err_t cab_mesh_send_node(const uint8_t destination[6], const uint8_t *data,
                             size_t length);
esp_err_t cab_mesh_send_all(const uint8_t *data, size_t length);
int cab_mesh_routes(uint8_t (*output)[6], size_t capacity);
cab_mesh_stats_t cab_mesh_stats(void);
void cab_mesh_note_root_response(bool heartbeat_ack);
void cab_mesh_note_heartbeat_timeout(void);
void cab_mesh_note_receive_drop(void);

#ifdef __cplusplus
}
#endif
