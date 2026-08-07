idf_component_register(
    SRCS "root_storage.c"
    INCLUDE_DIRS "include"
    REQUIRES esp_driver_sdmmc fatfs sdmmc espressif__cjson mbedtls
)
