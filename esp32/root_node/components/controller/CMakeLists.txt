idf_component_register(
    SRCS "root_controller.c"
    INCLUDE_DIRS "include"
    REQUIRES cabinet_mesh cabinet_protocol cabinet_storage display ota storage
             espressif__cjson mbedtls esp_app_format esp_system
)
