idf_component_register(
    SRCS "app_main.c"
    INCLUDE_DIRS "."
    REQUIRES cabinet_mesh cabinet_protocol cabinet_serial controller display
             ota storage esp_driver_usb_serial_jtag esp_system
             heap
)
