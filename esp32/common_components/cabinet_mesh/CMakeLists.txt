idf_component_register(
    SRCS "cabinet_mesh.c"
    INCLUDE_DIRS "include"
    REQUIRES esp_wifi esp_event esp_netif nvs_flash mesh_lite iot_bridge
)
