idf_component_register(
    SRCS "cabinet_fingerprint.c"
    INCLUDE_DIRS "include"
    REQUIRES esp_driver_gpio esp_driver_uart freertos esp_timer
)
