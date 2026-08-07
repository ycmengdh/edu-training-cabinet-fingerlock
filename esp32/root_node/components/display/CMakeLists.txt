idf_component_register(
    SRCS "root_display.c"
    INCLUDE_DIRS "include"
    REQUIRES esp_driver_gpio esp_driver_spi freertos heap
)
