idf_component_register(
    SRCS "cabinet_controller.c"
    INCLUDE_DIRS "include"
    REQUIRES fingerprint hardware cabinet_mesh cabinet_protocol ota
             cabinet_storage espressif__cjson
             esp_app_format esp_system heap
)
