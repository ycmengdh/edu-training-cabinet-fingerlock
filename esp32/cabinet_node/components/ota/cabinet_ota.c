#include "cabinet_ota.h"

#include <stdio.h>
#include <string.h>

#include "cabinet_mesh.h"
#include "esp_app_desc.h"
#include "esp_app_format.h"
#include "esp_event.h"
#include "esp_log.h"
#include "esp_mesh_lite.h"
#include "esp_mesh_lite_core.h"
#include "esp_ota_ops.h"
#include "esp_partition.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

#define OTA_HEALTH_TIMEOUT_MS 90000U
#define OTA_HEALTH_TASK_STACK 4096

static const char *TAG = "cabinet_ota";
static SemaphoreHandle_t s_mutex;
static esp_ota_handle_t s_update_handle;
static const esp_partition_t *s_update_partition;
static bool s_update_active;
static size_t s_written;
static size_t s_expected_size;
static char s_target_version[32];
static size_t s_requested_size;
static char s_requested_version[32];
static uint32_t s_request_started_ms;
static cabinet_ota_status_t s_status;

static void set_status_locked(const char *phase, uint8_t progress,
                              const char *version, const char *error,
                              bool active) {
    const char *next_phase = phase == NULL ? "idle" : phase;
    const char *next_version = version == NULL ? "" : version;
    const char *next_error = error == NULL ? "" : error;
    bool changed = strcmp(s_status.phase, next_phase) != 0 ||
                   strcmp(s_status.version, next_version) != 0 ||
                   strcmp(s_status.error, next_error) != 0 ||
                   s_status.progress != progress ||
                   s_status.active != active;
    snprintf(s_status.phase, sizeof(s_status.phase), "%s", next_phase);
    snprintf(s_status.version, sizeof(s_status.version), "%s", next_version);
    snprintf(s_status.error, sizeof(s_status.error), "%s", next_error);
    s_status.progress = progress;
    s_status.active = active;
    if (changed) ++s_status.generation;
}

static esp_err_t begin_update_locked(void) {
    if (s_update_active) return ESP_OK;
    if (s_requested_size == 0 || s_requested_version[0] == '\0') {
        return ESP_ERR_INVALID_STATE;
    }
    s_update_partition = esp_ota_get_next_update_partition(NULL);
    if (s_update_partition == NULL ||
        s_requested_size > s_update_partition->size) {
        return ESP_ERR_INVALID_SIZE;
    }
    esp_err_t result = esp_ota_begin(s_update_partition,
                                     OTA_WITH_SEQUENTIAL_WRITES,
                                     &s_update_handle);
    if (result != ESP_OK) return result;
    s_update_active = true;
    s_written = 0;
    s_expected_size = s_requested_size;
    s_target_version[0] = '\0';
    set_status_locked("downloading", 0, s_requested_version, "", true);
    return ESP_OK;
}

static void abort_update_locked(void) {
    if (s_update_active) esp_ota_abort(s_update_handle);
    s_update_active = false;
    s_update_handle = 0;
    s_update_partition = NULL;
    s_written = 0;
    s_expected_size = 0;
    s_target_version[0] = '\0';
    s_requested_size = 0;
    s_requested_version[0] = '\0';
    s_request_started_ms = 0;
}

static esp_err_t validate_first_chunk(
    const esp_mesh_lite_lan_ota_file_transfer_param_t *param) {
    const size_t descriptor_offset = sizeof(esp_image_header_t) +
                                     sizeof(esp_image_segment_header_t);
    if (param->offset != 0 || param->data_size < descriptor_offset +
                                              sizeof(esp_app_desc_t)) {
        return ESP_ERR_INVALID_SIZE;
    }
    const esp_image_header_t *header = (const esp_image_header_t *)param->data;
    const esp_app_desc_t *descriptor =
        (const esp_app_desc_t *)(param->data + descriptor_offset);
    const esp_app_desc_t *running = esp_app_get_description();
    if (header->magic != ESP_IMAGE_HEADER_MAGIC ||
        header->chip_id != ESP_CHIP_ID_ESP32S3 ||
        descriptor->magic_word != ESP_APP_DESC_MAGIC_WORD ||
        strncmp(descriptor->project_name, "cabinet_node_idf",
                sizeof(descriptor->project_name)) != 0 ||
        param->fw_version == NULL ||
        strncmp(descriptor->version, param->fw_version,
                sizeof(descriptor->version)) != 0 ||
        strncmp(descriptor->version, s_requested_version,
                sizeof(descriptor->version)) != 0 ||
        strncmp(descriptor->version, running->version,
                sizeof(descriptor->version)) == 0) {
        return ESP_ERR_INVALID_VERSION;
    }
    snprintf(s_target_version, sizeof(s_target_version), "%.*s",
             (int)sizeof(descriptor->version), descriptor->version);
    return ESP_OK;
}

static esp_err_t provide_file(
    esp_mesh_lite_lan_ota_file_transfer_param_t *param) {
    if (param == NULL || param->data == NULL || param->data_size == 0) {
        return ESP_ERR_INVALID_ARG;
    }
    const esp_partition_t *running = esp_ota_get_running_partition();
    const esp_app_desc_t *description = esp_app_get_description();
    if (running == NULL || param->offset + param->data_size > running->size ||
        (param->fw_version != NULL &&
         strncmp(description->version, param->fw_version,
                 sizeof(description->version)) != 0)) {
        return ESP_ERR_INVALID_STATE;
    }
    return esp_partition_read(running, param->offset, param->data,
                              param->data_size);
}

static esp_err_t receive_file(
    esp_mesh_lite_lan_ota_file_transfer_param_t *param) {
    if (param == NULL || param->data == NULL || param->data_size == 0 ||
        s_mutex == NULL) return ESP_ERR_INVALID_ARG;
    if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(5000)) != pdTRUE) {
        return ESP_ERR_TIMEOUT;
    }
    esp_err_t result = begin_update_locked();
    if (result == ESP_OK &&
        (param->filesize <= 0 ||
         (size_t)param->filesize > s_update_partition->size ||
         (size_t)param->filesize < s_expected_size ||
         param->offset > (size_t)param->filesize)) {
        result = ESP_ERR_INVALID_SIZE;
    }
    /* Mesh-Lite rounds param->filesize up to 64 KB. Only the real image size
       from the root notification belongs in the OTA partition. */
    size_t write_size = 0;
    if (result == ESP_OK && param->offset < s_expected_size) {
        size_t remaining = s_expected_size - param->offset;
        write_size = param->data_size < remaining
            ? param->data_size : remaining;
    }
    if (result == ESP_OK && param->offset < s_written) {
        result = param->offset + write_size <= s_written
            ? ESP_OK : ESP_ERR_INVALID_STATE;
        xSemaphoreGive(s_mutex);
        return result;
    }
    if (result == ESP_OK && param->offset > s_written &&
        param->offset < s_expected_size) {
        result = ESP_ERR_INVALID_STATE;
    }
    if (result == ESP_OK && s_written == 0) {
        result = validate_first_chunk(param);
    }
    if (result == ESP_OK && write_size > 0) {
        result = esp_ota_write(s_update_handle, param->data,
                               write_size);
        if (result == ESP_OK) {
            s_written += write_size;
            uint8_t progress = s_expected_size == 0 ? 0 :
                (uint8_t)((s_written * 100U) / s_expected_size);
            if (progress >= s_status.progress + 5U || progress == 100U) {
                set_status_locked("downloading", progress,
                                  s_requested_version, "", true);
            }
        }
    }
    if (result != ESP_OK) {
        ESP_LOGE(TAG, "OTA write failed at %u: %s",
                 (unsigned)param->offset, esp_err_to_name(result));
        set_status_locked("failed", s_status.progress,
                          s_requested_version, esp_err_to_name(result), false);
    }
    xSemaphoreGive(s_mutex);
    return result;
}

static esp_err_t receive_file_done(void) {
    if (s_mutex == NULL ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(10000)) != pdTRUE) {
        return ESP_ERR_TIMEOUT;
    }
    if (!s_update_active || s_written == 0 ||
        s_written != s_expected_size || s_target_version[0] == '\0') {
        set_status_locked("failed", s_status.progress,
                          s_requested_version, "incomplete image", false);
        xSemaphoreGive(s_mutex);
        return ESP_ERR_INVALID_STATE;
    }
    set_status_locked("verifying", 100, s_target_version, "", true);
    esp_err_t result = esp_ota_end(s_update_handle);
    if (result == ESP_OK) {
        result = esp_ota_set_boot_partition(s_update_partition);
    }
    if (result != ESP_OK) {
        ESP_LOGE(TAG, "OTA finalize failed: %s", esp_err_to_name(result));
        set_status_locked("failed", 100, s_target_version,
                          esp_err_to_name(result), false);
        abort_update_locked();
    } else {
        ESP_LOGW(TAG, "OTA image %s ready, reboot pending", s_target_version);
        set_status_locked("rebooting", 100, s_target_version, "", true);
        s_update_active = false;
        s_update_handle = 0;
        s_expected_size = 0;
    }
    xSemaphoreGive(s_mutex);
    return result;
}

static void ota_event(void *argument, esp_event_base_t base,
                      int32_t event_id, void *event_data) {
    (void)argument;
    (void)base;
    if (event_id == ESP_MESH_LITE_EVENT_OTA_START) {
        if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
            esp_err_t result = begin_update_locked();
            if (result != ESP_OK) {
                ESP_LOGE(TAG, "OTA begin failed: %s", esp_err_to_name(result));
                set_status_locked("failed", s_status.progress,
                                  s_requested_version,
                                  esp_err_to_name(result), false);
            }
            xSemaphoreGive(s_mutex);
        }
    } else if (event_id == ESP_MESH_LITE_EVENT_OTA_PROGRESS &&
               event_data != NULL) {
        const esp_mesh_lite_event_ota_progress_t *progress = event_data;
        if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) == pdTRUE) {
            if (progress->percentage >= s_status.progress + 5U ||
                progress->percentage == 100U) {
                set_status_locked("downloading", progress->percentage,
                                  s_requested_version, "", true);
            }
            xSemaphoreGive(s_mutex);
        }
    } else if (event_id == ESP_MESH_LITE_EVENT_OTA_FINISH &&
               event_data != NULL) {
        const esp_mesh_lite_event_ota_finish_t *finish = event_data;
        if (finish->reason != ESP_MESH_LITE_EVENT_OTA_SUCCESS &&
            xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
            char reason[32];
            snprintf(reason, sizeof(reason), "mesh reason %d",
                     (int)finish->reason);
            set_status_locked("failed", s_status.progress,
                              s_requested_version, reason, false);
            abort_update_locked();
            xSemaphoreGive(s_mutex);
        }
    }
}

static void health_validation_task(void *argument) {
    (void)argument;
    const esp_partition_t *running = esp_ota_get_running_partition();
    esp_ota_img_states_t state = ESP_OTA_IMG_UNDEFINED;
    if (running == NULL ||
        esp_ota_get_state_partition(running, &state) != ESP_OK ||
        state != ESP_OTA_IMG_PENDING_VERIFY) {
        vTaskDelete(NULL);
        return;
    }
    if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
        set_status_locked("validating", 100,
                          esp_app_get_description()->version, "", true);
        xSemaphoreGive(s_mutex);
    }
    uint32_t elapsed = 0;
    while (elapsed < OTA_HEALTH_TIMEOUT_MS) {
        cab_mesh_stats_t stats = cab_mesh_stats();
        if (cab_mesh_is_connected() && stats.heartbeat_acks > 0) {
            esp_err_t result = esp_ota_mark_app_valid_cancel_rollback();
            ESP_LOGW(TAG, "OTA health validation: %s",
                     esp_err_to_name(result));
            if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
                set_status_locked(result == ESP_OK ? "complete" : "validating",
                                  100, esp_app_get_description()->version,
                                  result == ESP_OK ? "" : esp_err_to_name(result),
                                  result != ESP_OK);
                xSemaphoreGive(s_mutex);
            }
            if (result == ESP_OK) {
                vTaskDelete(NULL);
                return;
            }
        }
        vTaskDelay(pdMS_TO_TICKS(1000));
        elapsed += 1000;
    }
    ESP_LOGE(TAG, "OTA health validation timed out, rolling back");
    esp_ota_mark_app_invalid_rollback_and_reboot();
    esp_restart();
}

bool cabinet_ota_init(void) {
    if (s_mutex != NULL) return true;
    s_mutex = xSemaphoreCreateMutex();
    if (s_mutex == NULL) return false;
    memset(&s_status, 0, sizeof(s_status));
    snprintf(s_status.phase, sizeof(s_status.phase), "idle");
    s_status.generation = 1;
    static esp_mesh_lite_lan_ota_file_transfer_cb_t callbacks = {
        .provide_file_cb = provide_file,
        .get_file_cb = receive_file,
        .get_file_done = receive_file_done,
    };
    esp_mesh_lite_ota_register_file_transfer_cb(&callbacks);
    return esp_event_handler_register(ESP_MESH_LITE_EVENT,
                                      ESP_EVENT_ANY_ID, ota_event, NULL) ==
           ESP_OK;
}

bool cabinet_ota_start_health_validation(void) {
    return xTaskCreate(health_validation_task, "ota_health",
                       OTA_HEALTH_TASK_STACK, NULL, 6, NULL) == pdPASS;
}

bool cabinet_ota_running_image_validated(void) {
    const esp_partition_t *running = esp_ota_get_running_partition();
    esp_ota_img_states_t state = ESP_OTA_IMG_UNDEFINED;
    return running == NULL ||
           esp_ota_get_state_partition(running, &state) != ESP_OK ||
           state != ESP_OTA_IMG_PENDING_VERIFY;
}

esp_err_t cabinet_ota_request(const char *version, size_t image_size) {
    if (s_mutex == NULL || version == NULL || version[0] == '\0' ||
        strnlen(version, sizeof(s_requested_version)) >=
            sizeof(s_requested_version) || image_size == 0) {
        return ESP_ERR_INVALID_ARG;
    }
    const esp_app_desc_t *running = esp_app_get_description();
    if (running != NULL &&
        strncmp(running->version, version, sizeof(running->version)) == 0) {
        return ESP_ERR_INVALID_VERSION;
    }
    const esp_partition_t *partition = esp_ota_get_next_update_partition(NULL);
    if (partition == NULL || image_size > partition->size) {
        return ESP_ERR_INVALID_SIZE;
    }
    if (!cab_mesh_is_connected() || cab_mesh_layer() <= ROOT) {
        return ESP_ERR_INVALID_STATE;
    }
    if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) != pdTRUE) {
        return ESP_ERR_TIMEOUT;
    }
    uint32_t now = (uint32_t)(xTaskGetTickCount() * portTICK_PERIOD_MS);
    bool duplicate = strcmp(s_requested_version, version) == 0 &&
                     s_requested_size == image_size &&
                     s_status.active &&
                     now - s_request_started_ms < 60000U;
    if (s_update_active || duplicate) {
        xSemaphoreGive(s_mutex);
        return duplicate ? ESP_OK : ESP_ERR_INVALID_STATE;
    }
    snprintf(s_requested_version, sizeof(s_requested_version), "%s", version);
    s_requested_size = image_size;
    s_request_started_ms = now;
    set_status_locked("starting", 0, version, "", true);
    xSemaphoreGive(s_mutex);

    esp_mesh_lite_file_transmit_config_t config = {
        .type = ESP_MESH_LITE_OTA_TRANSMIT_FIRMWARE,
        .size = image_size,
        .extern_url_ota_cb = NULL,
    };
    snprintf(config.fw_version, sizeof(config.fw_version), "%s", version);
    esp_err_t result = esp_mesh_lite_transmit_file_start(&config);
    if (result != ESP_OK) {
        /* Mesh-Lite can still deliver a delayed OTA event after reporting an
           immediate start failure. Keep the request for event validation and
           for the root's next notification retry. */
        ESP_LOGE(TAG, "OTA pull start for %s failed: %s", version,
                 esp_err_to_name(result));
        if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
            set_status_locked("failed", 0, version,
                              esp_err_to_name(result), false);
            xSemaphoreGive(s_mutex);
        }
    }
    return result;
}

bool cabinet_ota_get_status(cabinet_ota_status_t *status) {
    if (status == NULL || s_mutex == NULL ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) != pdTRUE) return false;
    *status = s_status;
    xSemaphoreGive(s_mutex);
    return true;
}
