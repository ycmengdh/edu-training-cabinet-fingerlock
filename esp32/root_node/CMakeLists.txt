cmake_minimum_required(VERSION 3.16)

# ESP-Mesh-Lite 1.0.2 pins IoT Bridge 1.0.1, which still uses the
# pre-IDF-6 Wi-Fi interface names.
add_compile_definitions(
    ESP_IF_WIFI_STA=WIFI_IF_STA
    ESP_IF_WIFI_AP=WIFI_IF_AP
)

set(EXTRA_COMPONENT_DIRS "${CMAKE_CURRENT_LIST_DIR}/../common_components")
# Mesh-Lite rejects a firmware distribution when the source node's running
# version equals the target image version. Root never installs cabinet images,
# so keep its application version in a separate namespace.
set(PROJECT_VER "26081501-root")
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(cabinet_root_idf)
