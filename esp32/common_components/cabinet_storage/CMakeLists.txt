idf_component_register(
    SRCS "cabinet_storage.c"
    INCLUDE_DIRS "include"
    REQUIRES nvs_flash esp_timer
)
