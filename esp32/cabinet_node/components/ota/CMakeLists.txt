idf_component_register(
    SRCS "cabinet_ota.c"
    INCLUDE_DIRS "include"
    REQUIRES cabinet_mesh mesh_lite app_update esp_event esp_app_format
             bootloader_support freertos
)
