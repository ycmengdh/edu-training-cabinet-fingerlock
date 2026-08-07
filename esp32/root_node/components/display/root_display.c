#include "root_display.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "driver/gpio.h"
#include "driver/spi_master.h"
#include "esp_err.h"
#include "esp_heap_caps.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#define TFT_HOST SPI3_HOST
#define TFT_MOSI_GPIO 11
#define TFT_SCLK_GPIO 10
#define TFT_CS_GPIO 12
#define TFT_DC_GPIO 13
#define TFT_RST_GPIO 14
#define TFT_CLOCK_HZ 27000000

#define TFT_WIDTH 160
#define TFT_HEIGHT 80
#define TFT_X_GAP 1
#define TFT_Y_GAP 26
#define FRAME_BYTES (TFT_WIDTH * TFT_HEIGHT * 2)

#define COLOR_BLACK 0x0000
#define COLOR_NAVY 0x000F
#define COLOR_DARK_GREEN 0x0320
#define COLOR_DARK_RED 0x8000
#define COLOR_GREY 0x7BEF
#define COLOR_WHITE 0xFFFF
#define COLOR_CYAN 0x07FF
#define COLOR_GREEN 0x07E0
#define COLOR_RED 0xF800

typedef struct {
    char character;
    uint8_t columns[5];
} glyph_t;

// Five-column GLCD glyphs for the uppercase status console character set.
static const glyph_t s_font[] = {
    {' ', {0x00, 0x00, 0x00, 0x00, 0x00}},
    {'!', {0x00, 0x00, 0x5F, 0x00, 0x00}},
    {'-', {0x08, 0x08, 0x08, 0x08, 0x08}},
    {'.', {0x00, 0x60, 0x60, 0x00, 0x00}},
    {'/', {0x20, 0x10, 0x08, 0x04, 0x02}},
    {'0', {0x3E, 0x51, 0x49, 0x45, 0x3E}},
    {'1', {0x00, 0x42, 0x7F, 0x40, 0x00}},
    {'2', {0x42, 0x61, 0x51, 0x49, 0x46}},
    {'3', {0x21, 0x41, 0x45, 0x4B, 0x31}},
    {'4', {0x18, 0x14, 0x12, 0x7F, 0x10}},
    {'5', {0x27, 0x45, 0x45, 0x45, 0x39}},
    {'6', {0x3C, 0x4A, 0x49, 0x49, 0x30}},
    {'7', {0x01, 0x71, 0x09, 0x05, 0x03}},
    {'8', {0x36, 0x49, 0x49, 0x49, 0x36}},
    {'9', {0x06, 0x49, 0x49, 0x29, 0x1E}},
    {':', {0x00, 0x36, 0x36, 0x00, 0x00}},
    {'A', {0x7E, 0x11, 0x11, 0x11, 0x7E}},
    {'B', {0x7F, 0x49, 0x49, 0x49, 0x36}},
    {'C', {0x3E, 0x41, 0x41, 0x41, 0x22}},
    {'D', {0x7F, 0x41, 0x41, 0x22, 0x1C}},
    {'E', {0x7F, 0x49, 0x49, 0x49, 0x41}},
    {'F', {0x7F, 0x09, 0x09, 0x09, 0x01}},
    {'G', {0x3E, 0x41, 0x49, 0x49, 0x7A}},
    {'H', {0x7F, 0x08, 0x08, 0x08, 0x7F}},
    {'I', {0x00, 0x41, 0x7F, 0x41, 0x00}},
    {'J', {0x20, 0x40, 0x41, 0x3F, 0x01}},
    {'K', {0x7F, 0x08, 0x14, 0x22, 0x41}},
    {'L', {0x7F, 0x40, 0x40, 0x40, 0x40}},
    {'M', {0x7F, 0x02, 0x0C, 0x02, 0x7F}},
    {'N', {0x7F, 0x04, 0x08, 0x10, 0x7F}},
    {'O', {0x3E, 0x41, 0x41, 0x41, 0x3E}},
    {'P', {0x7F, 0x09, 0x09, 0x09, 0x06}},
    {'Q', {0x3E, 0x41, 0x51, 0x21, 0x5E}},
    {'R', {0x7F, 0x09, 0x19, 0x29, 0x46}},
    {'S', {0x46, 0x49, 0x49, 0x49, 0x31}},
    {'T', {0x01, 0x01, 0x7F, 0x01, 0x01}},
    {'U', {0x3F, 0x40, 0x40, 0x40, 0x3F}},
    {'V', {0x1F, 0x20, 0x40, 0x20, 0x1F}},
    {'W', {0x3F, 0x40, 0x38, 0x40, 0x3F}},
    {'X', {0x63, 0x14, 0x08, 0x14, 0x63}},
    {'Y', {0x07, 0x08, 0x70, 0x08, 0x07}},
    {'Z', {0x61, 0x51, 0x49, 0x45, 0x43}},
};

static spi_device_handle_t s_spi;
static uint8_t *s_frame;
static bool s_bus_initialized;
static bool s_ready;
static char s_root_suffix[5] = "----";
static char s_error[80] = "not initialized";

static void set_error(const char *operation, esp_err_t error) {
    snprintf(s_error, sizeof(s_error), "%s: %s", operation,
             esp_err_to_name(error));
    s_ready = false;
}

static esp_err_t transmit(bool data_mode, const void *data, size_t length) {
    if (s_spi == NULL || data == NULL || length == 0) return ESP_ERR_INVALID_ARG;
    gpio_set_level(TFT_DC_GPIO, data_mode ? 1 : 0);
    spi_transaction_t transaction = {
        .length = length * 8,
        .tx_buffer = data,
    };
    return spi_device_transmit(s_spi, &transaction);
}

static esp_err_t command(uint8_t value, const uint8_t *parameters,
                         size_t parameter_count) {
    esp_err_t error = transmit(false, &value, 1);
    if (error == ESP_OK && parameter_count > 0) {
        error = transmit(true, parameters, parameter_count);
    }
    return error;
}

static esp_err_t reset_and_initialize(void) {
    gpio_set_level(TFT_RST_GPIO, 1);
    vTaskDelay(pdMS_TO_TICKS(10));
    gpio_set_level(TFT_RST_GPIO, 0);
    vTaskDelay(pdMS_TO_TICKS(20));
    gpio_set_level(TFT_RST_GPIO, 1);
    vTaskDelay(pdMS_TO_TICKS(120));

    esp_err_t error = command(0x01, NULL, 0);
    vTaskDelay(pdMS_TO_TICKS(150));
    if (error != ESP_OK) return error;
    error = command(0x11, NULL, 0);
    vTaskDelay(pdMS_TO_TICKS(500));
    if (error != ESP_OK) return error;

    static const uint8_t frame_rate[] = {0x01, 0x2C, 0x2D};
    static const uint8_t partial_rate[] = {
        0x01, 0x2C, 0x2D, 0x01, 0x2C, 0x2D};
    static const uint8_t inv_control[] = {0x07};
    static const uint8_t power_1[] = {0xA2, 0x02, 0x84};
    static const uint8_t power_2[] = {0xC5};
    static const uint8_t power_3[] = {0x0A, 0x00};
    static const uint8_t power_4[] = {0x8A, 0x2A};
    static const uint8_t power_5[] = {0x8A, 0xEE};
    static const uint8_t vcom[] = {0x0E};
    static const uint8_t color_mode[] = {0x05};
    static const uint8_t positive_gamma[] = {
        0x02, 0x1C, 0x07, 0x12, 0x37, 0x32, 0x29, 0x2D,
        0x29, 0x25, 0x2B, 0x39, 0x00, 0x01, 0x03, 0x10};
    static const uint8_t negative_gamma[] = {
        0x03, 0x1D, 0x07, 0x06, 0x2E, 0x2C, 0x29, 0x2D,
        0x2E, 0x2E, 0x37, 0x3F, 0x00, 0x00, 0x02, 0x10};
    static const uint8_t rotation[] = {0xA8};

    const struct {
        uint8_t value;
        const uint8_t *parameters;
        size_t count;
    } sequence[] = {
        {0xB1, frame_rate, sizeof(frame_rate)},
        {0xB2, frame_rate, sizeof(frame_rate)},
        {0xB3, partial_rate, sizeof(partial_rate)},
        {0xB4, inv_control, sizeof(inv_control)},
        {0xC0, power_1, sizeof(power_1)},
        {0xC1, power_2, sizeof(power_2)},
        {0xC2, power_3, sizeof(power_3)},
        {0xC3, power_4, sizeof(power_4)},
        {0xC4, power_5, sizeof(power_5)},
        {0xC5, vcom, sizeof(vcom)},
        {0x3A, color_mode, sizeof(color_mode)},
        {0xE0, positive_gamma, sizeof(positive_gamma)},
        {0xE1, negative_gamma, sizeof(negative_gamma)},
        {0x36, rotation, sizeof(rotation)},
        {0x21, NULL, 0},
        {0x13, NULL, 0},
        {0x29, NULL, 0},
    };
    for (size_t index = 0; index < sizeof(sequence) / sizeof(sequence[0]);
         ++index) {
        error = command(sequence[index].value, sequence[index].parameters,
                        sequence[index].count);
        if (error != ESP_OK) return error;
    }
    vTaskDelay(pdMS_TO_TICKS(100));
    return ESP_OK;
}

static void set_pixel(int x, int y, uint16_t color) {
    if (x < 0 || x >= TFT_WIDTH || y < 0 || y >= TFT_HEIGHT) return;
    size_t offset = ((size_t)y * TFT_WIDTH + (size_t)x) * 2;
    s_frame[offset] = (uint8_t)(color >> 8);
    s_frame[offset + 1] = (uint8_t)color;
}

static void fill(uint16_t color) {
    for (size_t pixel = 0; pixel < TFT_WIDTH * TFT_HEIGHT; ++pixel) {
        s_frame[pixel * 2] = (uint8_t)(color >> 8);
        s_frame[pixel * 2 + 1] = (uint8_t)color;
    }
}

static void fill_rect(int x, int y, int width, int height, uint16_t color) {
    for (int row = y; row < y + height; ++row) {
        for (int column = x; column < x + width; ++column) {
            set_pixel(column, row, color);
        }
    }
}

static const uint8_t *find_glyph(char character) {
    if (character >= 'a' && character <= 'z') character -= 'a' - 'A';
    for (size_t index = 0; index < sizeof(s_font) / sizeof(s_font[0]);
         ++index) {
        if (s_font[index].character == character) return s_font[index].columns;
    }
    return s_font[0].columns;
}

static void draw_text(int x, int y, const char *text, uint16_t color) {
    if (text == NULL) return;
    while (*text != '\0' && x + 5 <= TFT_WIDTH) {
        const uint8_t *glyph = find_glyph(*text++);
        for (int column = 0; column < 5; ++column) {
            for (int row = 0; row < 7; ++row) {
                if ((glyph[column] & (1U << row)) != 0) {
                    set_pixel(x + column, y + row, color);
                }
            }
        }
        x += 6;
    }
}

static esp_err_t flush_frame(void) {
    uint16_t x_start = TFT_X_GAP;
    uint16_t x_end = TFT_X_GAP + TFT_WIDTH - 1;
    uint16_t y_start = TFT_Y_GAP;
    uint16_t y_end = TFT_Y_GAP + TFT_HEIGHT - 1;
    uint8_t columns[] = {
        (uint8_t)(x_start >> 8), (uint8_t)x_start,
        (uint8_t)(x_end >> 8), (uint8_t)x_end};
    uint8_t rows[] = {
        (uint8_t)(y_start >> 8), (uint8_t)y_start,
        (uint8_t)(y_end >> 8), (uint8_t)y_end};
    esp_err_t error = command(0x2A, columns, sizeof(columns));
    if (error == ESP_OK) error = command(0x2B, rows, sizeof(rows));
    if (error == ESP_OK) error = command(0x2C, NULL, 0);
    if (error == ESP_OK) error = transmit(true, s_frame, FRAME_BYTES);
    return error;
}

static void cleanup(void) {
    if (s_spi != NULL) {
        spi_bus_remove_device(s_spi);
        s_spi = NULL;
    }
    if (s_bus_initialized) {
        spi_bus_free(TFT_HOST);
        s_bus_initialized = false;
    }
    free(s_frame);
    s_frame = NULL;
}

bool root_display_init(const char *root_id) {
    if (s_ready) return true;
    if (root_id != NULL) {
        size_t length = strlen(root_id);
        const char *suffix = length > 4 ? root_id + length - 4 : root_id;
        snprintf(s_root_suffix, sizeof(s_root_suffix), "%s", suffix);
    }
    s_frame = heap_caps_malloc(FRAME_BYTES,
                               MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL);
    if (s_frame == NULL) {
        snprintf(s_error, sizeof(s_error), "frame buffer allocation failed");
        return false;
    }

    gpio_config_t gpio = {
        .pin_bit_mask = (1ULL << TFT_DC_GPIO) | (1ULL << TFT_RST_GPIO),
        .mode = GPIO_MODE_OUTPUT,
        .pull_up_en = GPIO_PULLUP_DISABLE,
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
        .intr_type = GPIO_INTR_DISABLE,
    };
    esp_err_t error = gpio_config(&gpio);
    if (error != ESP_OK) {
        set_error("gpio_config", error);
        cleanup();
        return false;
    }

    spi_bus_config_t bus = {
        .mosi_io_num = TFT_MOSI_GPIO,
        .miso_io_num = -1,
        .sclk_io_num = TFT_SCLK_GPIO,
        .quadwp_io_num = -1,
        .quadhd_io_num = -1,
        .max_transfer_sz = FRAME_BYTES,
    };
    error = spi_bus_initialize(TFT_HOST, &bus, SPI_DMA_CH_AUTO);
    if (error != ESP_OK) {
        set_error("spi_bus_initialize", error);
        cleanup();
        return false;
    }
    s_bus_initialized = true;

    spi_device_interface_config_t device = {
        .clock_speed_hz = TFT_CLOCK_HZ,
        .mode = 0,
        .spics_io_num = TFT_CS_GPIO,
        .queue_size = 1,
    };
    error = spi_bus_add_device(TFT_HOST, &device, &s_spi);
    if (error != ESP_OK) {
        set_error("spi_bus_add_device", error);
        cleanup();
        return false;
    }
    error = reset_and_initialize();
    if (error != ESP_OK) {
        set_error("panel_init", error);
        cleanup();
        return false;
    }

    fill(COLOR_BLACK);
    fill_rect(0, 0, TFT_WIDTH, 12, COLOR_NAVY);
    draw_text(4, 2, "ROOT BOOT", COLOR_CYAN);
    char identity[24];
    snprintf(identity, sizeof(identity), "ID %s  FW 3.1.0", s_root_suffix);
    draw_text(4, 20, identity, COLOR_WHITE);
    draw_text(4, 36, "MESH START", COLOR_GREEN);
    error = flush_frame();
    if (error != ESP_OK) {
        set_error("initial_flush", error);
        cleanup();
        return false;
    }
    s_ready = true;
    s_error[0] = '\0';
    return true;
}

bool root_display_ready(void) {
    return s_ready;
}

const char *root_display_last_error(void) {
    return s_error;
}

void root_display_update(const root_display_status_t *status) {
    if (!s_ready || status == NULL) return;
    fill(COLOR_BLACK);
    fill_rect(0, 0, TFT_WIDTH, 12, COLOR_NAVY);

    uint32_t hours = status->uptime_seconds / 3600U;
    uint32_t minutes = (status->uptime_seconds / 60U) % 60U;
    uint32_t seconds = status->uptime_seconds % 60U;
    char line[32];
    snprintf(line, sizeof(line), "ROOT %02lu:%02lu:%02lu",
             (unsigned long)(hours % 100U), (unsigned long)minutes,
             (unsigned long)seconds);
    draw_text(3, 2, line, COLOR_CYAN);

    uint16_t host_background = status->host_connected
        ? COLOR_DARK_GREEN : COLOR_DARK_RED;
    fill_rect(124, 1, 35, 10, host_background);
    draw_text(127, 2, status->host_connected ? "HOST" : "USB!",
              status->host_connected ? COLOR_GREEN : COLOR_RED);

    snprintf(line, sizeof(line), "CAB %d  MESH %s L%d",
             status->route_count, status->mesh_connected ? "OK" : "NG",
             status->mesh_layer);
    draw_text(3, 17, line,
              status->mesh_connected ? COLOR_GREEN : COLOR_RED);

    snprintf(line, sizeof(line), "SD %s  CHILD %d",
             status->sd_ready ? "OK" : "NG", status->child_count);
    draw_text(3, 29, line, status->sd_ready ? COLOR_GREEN : COLOR_RED);

    snprintf(line, sizeof(line), "TXF %lu  RXD %lu",
             (unsigned long)status->send_failures,
             (unsigned long)status->receive_drops);
    draw_text(3, 41, line,
              status->send_failures == 0 && status->receive_drops == 0
                  ? COLOR_WHITE : COLOR_RED);

    snprintf(line, sizeof(line), "HB %lu  TO %lu",
             (unsigned long)status->heartbeat_acks,
             (unsigned long)status->heartbeat_timeouts);
    draw_text(3, 53, line,
              status->heartbeat_timeouts == 0 ? COLOR_WHITE : COLOR_RED);

    snprintf(line, sizeof(line), "ID %s  FW 3.1.0-IDF", s_root_suffix);
    draw_text(3, 67, line, COLOR_GREY);

    esp_err_t error = flush_frame();
    if (error != ESP_OK) set_error("frame_flush", error);
}
