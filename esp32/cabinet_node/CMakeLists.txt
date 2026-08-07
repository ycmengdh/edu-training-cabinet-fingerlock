cmake_minimum_required(VERSION 3.16)

# ESP-Mesh-Lite 1.0.2 pins IoT Bridge 1.0.1, which still uses the
# pre-IDF-6 Wi-Fi interface names.
add_compile_definitions(
    ESP_IF_WIFI_STA=WIFI_IF_STA
    ESP_IF_WIFI_AP=WIFI_IF_AP
    CABINET_HARDWARE_VERSION="cabinet-v1"
)

set(EXTRA_COMPONENT_DIRS "${CMAKE_CURRENT_LIST_DIR}/../common_components")
set(PROJECT_VER "26080701-cab")
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(cabinet_node_idf)
