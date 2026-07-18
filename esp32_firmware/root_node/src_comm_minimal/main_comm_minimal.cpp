/**
 * Minimal bring-up for root node over USB-Serial-JTAG (GPIO19/20).
 * No Mesh, no SD, no TFT.
 */
#include <Arduino.h>

void setup() {
    Serial.begin(115200);
    // USB CDC may enumerate after reset; wait a bit for the host port.
    unsigned long start = millis();
    while (!Serial && millis() - start < 3000) {
        delay(10);
    }
    delay(300);

    for (int i = 0; i < 8; i++) {
        Serial.println();
        Serial.println("========================================");
        Serial.println("  ROOT_USB_SERIAL_MINIMAL");
        Serial.println("  USB-Serial-JTAG (GPIO19/20)");
        Serial.println("  If you see this, app Serial is alive");
        Serial.println("========================================");
        Serial.printf("boot_print=%d  send PING for PONG\r\n", i);
        Serial.flush();
        delay(250);
    }
}

void loop() {
    static unsigned long last = 0;
    unsigned long now = millis();
    if (now - last >= 1000) {
        last = now;
        Serial.printf("ALIVE uptime_ms=%lu\r\n", now);
        Serial.flush();
    }

    while (Serial.available()) {
        static char line[32];
        static uint8_t pos = 0;
        char c = (char)Serial.read();
        if (c == '\r') continue;
        if (c == '\n') {
            line[pos < sizeof(line) ? pos : sizeof(line) - 1] = 0;
            if (strcasecmp(line, "PING") == 0 || strcasecmp(line, "AT") == 0) {
                Serial.print("PONG\r\n");
                Serial.flush();
            } else if (pos > 0) {
                Serial.printf("ECHO:%s\r\n", line);
                Serial.flush();
            }
            pos = 0;
        } else if (pos + 1 < sizeof(line)) {
            line[pos++] = c;
        } else {
            pos = 0;
        }
    }
}
