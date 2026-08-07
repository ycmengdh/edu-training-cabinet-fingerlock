idf_component_register(
    SRCS "app_main.c"
    INCLUDE_DIRS "."
    REQUIRES controller ota cabinet_mesh cabinet_protocol cabinet_serial
             esp_app_format esp_system heap
)
