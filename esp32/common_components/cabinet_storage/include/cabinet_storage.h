#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define CAB_STORAGE_PERMISSION_MAX 200
#define CAB_STORAGE_USER_ID_MAX 24
#define CAB_STORAGE_NAME_MAX 32

typedef enum {
    CAB_ROLE_ADMIN = 0,
    CAB_ROLE_TEACHER = 1,
    CAB_ROLE_STUDENT = 2,
} cab_role_t;

typedef struct {
    int16_t fingerprint_id;
    int16_t local_fingerprint_id;
    bool is_backup;
    uint32_t user_id_number;
    char user_id[CAB_STORAGE_USER_ID_MAX + 1];
    char name[CAB_STORAGE_NAME_MAX + 1];
    cab_role_t role;
    uint8_t lock_mask;
    uint32_t expire_days;
} cab_permission_t;

typedef struct {
    char device_id[25];
    char device_name[33];
    uint8_t work_mode;
    uint8_t mesh_channel;
    uint8_t fingerprint_count;
    uint32_t permission_version;
} cab_device_config_t;

bool cab_storage_init(const char *default_device_id, bool is_root);
bool cab_storage_load_config(cab_device_config_t *config);
bool cab_storage_save_config(const cab_device_config_t *config);

size_t cab_storage_permission_count(void);
uint32_t cab_storage_permission_version(void);
bool cab_storage_permissions_lost(void);
const cab_permission_t *cab_storage_permission_at(size_t index);
bool cab_storage_find_by_local_fingerprint(int fingerprint_id,
                                           cab_permission_t *permission);
bool cab_storage_find_primary_by_user(const char *user_id,
                                      cab_permission_t *permission);
bool cab_storage_replace_permissions(const cab_permission_t *permissions,
                                     size_t count, uint32_t version);
bool cab_storage_save_permission(const cab_permission_t *permission,
                                 uint32_t version);
bool cab_storage_delete_permission(int fingerprint_id);
bool cab_storage_delete_user(const char *user_id, uint32_t version);
bool cab_storage_clear_permissions(void);
int cab_storage_allocate_fingerprint_id(void);

uint32_t cab_storage_user_id_to_number(const char *user_id);
void cab_storage_number_to_user_id(uint32_t number, char *output,
                                   size_t output_size);
uint32_t cab_storage_date_to_days(const char *date);
uint32_t cab_storage_unix_time(void);
void cab_storage_set_unix_time(uint32_t timestamp);
bool cab_storage_time_is_synced(void);

#ifdef __cplusplus
}
#endif
