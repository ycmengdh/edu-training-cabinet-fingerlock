//                            USER DEFINED SETTINGS
//   Set driver type, fonts to be loaded, pins used and SPI control method etc
//
//   Configuration for 0.96" ST7735 80x160 TFT display on ESP32-S3 Root Node
//   Pins: MOSI=11, SCLK=10, CS=12, DC=13, RST=14

#define USER_SETUP_INFO "Root_Node_0.96LCD"

// ##################################################################################
//
// Section 1. Call up the right driver file and any options for it
//
// ##################################################################################

#define ST7735_DRIVER

// ##################################################################################
//
// Section 2. Define the display dimensions
//
// ##################################################################################

#define TFT_WIDTH  80
#define TFT_HEIGHT 160

// ##################################################################################
//
// Section 3. ST7735-specific panel configuration
//
// ##################################################################################

#define ST7735_GREENTAB160x80

// ##################################################################################
//
// Section 4. Colour order
//
// ##################################################################################

#define TFT_RGB_ORDER TFT_BGR

// ##################################################################################
//
// Section 5. Inversion
//
// ##################################################################################

#define TFT_INVERSION_ON

// Built-in 6x8 font used by the root status screen.
#define LOAD_GLCD

// ##################################################################################
//
// Section 6. ESP32 pins used for SPI interface
//
// ##################################################################################

#define TFT_MOSI 11
#define TFT_SCLK 10
#define TFT_CS   12
#define TFT_DC   13
#define TFT_RST  14

// ESP32-S3: force SPI3 (HSPI) host. Default FSPI register mapping in this
// TFT_eSPI revision can yield SPI_USER_REG -> 0x10 and StoreProhibited.
#define USE_HSPI_PORT

// ##################################################################################
//
// Section 7. SPI frequency
//
// ##################################################################################

// 27 MHz is more reliable than 40 MHz on short jumper wiring.
#define SPI_FREQUENCY  27000000
