idf_component_register(
    SRCS "root_ota.c"
    INCLUDE_DIRS "include"
    REQUIRES storage cabinet_mesh cabinet_protocol mesh_lite esp_event esp_timer
             esp_app_format bootloader_support
             espressif__cjson mbedtls freertos
)
