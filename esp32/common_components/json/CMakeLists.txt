# ESP-IDF 6 moved the built-in json component to espressif/cjson. Mesh-Lite
# 1.0.2 still requires the former component name.
idf_component_register(REQUIRES espressif__cjson)
