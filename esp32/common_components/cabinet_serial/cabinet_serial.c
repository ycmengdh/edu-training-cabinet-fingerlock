#include "cabinet_serial.h"

#include "driver/uart.h"
#include "driver/usb_serial_jtag.h"
#include "freertos/FreeRTOS.h"

static cab_serial_mode_t s_mode;
static bool s_initialized;

esp_err_t cab_serial_init(cab_serial_mode_t mode) {
    if (s_initialized) return s_mode == mode ? ESP_OK : ESP_ERR_INVALID_STATE;
    s_mode = mode;
    esp_err_t error;
    if (mode == CAB_SERIAL_USB_JTAG) {
        usb_serial_jtag_driver_config_t config = {
            .tx_buffer_size = 8192,
            .rx_buffer_size = 8192,
        };
        error = usb_serial_jtag_driver_install(&config);
    } else {
        uart_config_t config = {
            .baud_rate = 921600,
            .data_bits = UART_DATA_8_BITS,
            .parity = UART_PARITY_DISABLE,
            .stop_bits = UART_STOP_BITS_1,
            .flow_ctrl = UART_HW_FLOWCTRL_DISABLE,
            .source_clk = UART_SCLK_DEFAULT,
        };
        error = uart_driver_install(UART_NUM_0, 8192, 8192, 0, NULL, 0);
        if (error == ESP_OK) error = uart_param_config(UART_NUM_0, &config);
        if (error == ESP_OK) {
            error = uart_set_pin(UART_NUM_0, 43, 44,
                                 UART_PIN_NO_CHANGE, UART_PIN_NO_CHANGE);
        }
    }
    s_initialized = error == ESP_OK;
    return error;
}

int cab_serial_read(uint8_t *output, size_t output_size, uint32_t timeout_ms) {
    if (!s_initialized || output == NULL || output_size == 0) return -1;
    TickType_t timeout = pdMS_TO_TICKS(timeout_ms);
    if (s_mode == CAB_SERIAL_USB_JTAG) {
        return usb_serial_jtag_read_bytes(output, output_size, timeout);
    }
    return uart_read_bytes(UART_NUM_0, output, output_size, timeout);
}

int cab_serial_write(const uint8_t *data, size_t length, uint32_t timeout_ms) {
    if (!s_initialized || data == NULL) return -1;
    const TickType_t timeout = pdMS_TO_TICKS(timeout_ms);
    size_t written = 0;
    while (written < length) {
        int result;
        if (s_mode == CAB_SERIAL_USB_JTAG) {
            result = usb_serial_jtag_write_bytes(data + written,
                                                 length - written, timeout);
        } else {
            result = uart_write_bytes(UART_NUM_0, data + written,
                                      length - written);
        }
        if (result <= 0) return -1;
        written += (size_t)result;
    }
    if (s_mode == CAB_SERIAL_UART0) {
        uart_wait_tx_done(UART_NUM_0, timeout);
    }
    return (int)written;
}

int cab_serial_frame_writer(const uint8_t *data, size_t length, void *context) {
    (void)context;
    return cab_serial_write(data, length, 1000);
}
