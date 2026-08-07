#include "cabinet_protocol.h"

#include <string.h>
#include <time.h>

#define FRAME_HEAD_1 0xA5
#define FRAME_HEAD_2 0x5A
#define FRAME_VERSION_NORMAL 0x01
#define FRAME_VERSION_FRAGMENT 0x02
#define APP_MAGIC_0 0xB1
#define APP_MAGIC_1 0x0F
#define APP_VERSION 0x01
#define APP_HEADER_SIZE 18
#define FRAGMENT_DATA_MAX (CAB_FRAME_MAX_PAYLOAD - 4)

static uint16_t s_message_id = 1;
static uint8_t s_fragment_id = 1;

static void write_u16_le(uint8_t *p, uint16_t value) {
    p[0] = (uint8_t)value;
    p[1] = (uint8_t)(value >> 8);
}

static void write_u32_le(uint8_t *p, uint32_t value) {
    p[0] = (uint8_t)value;
    p[1] = (uint8_t)(value >> 8);
    p[2] = (uint8_t)(value >> 16);
    p[3] = (uint8_t)(value >> 24);
}

static uint16_t read_u16_le(const uint8_t *p) {
    return (uint16_t)p[0] | ((uint16_t)p[1] << 8);
}

static uint32_t read_u32_le(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint16_t crc_step(uint16_t crc, const uint8_t *data, size_t length) {
    for (size_t i = 0; i < length; ++i) {
        crc ^= data[i];
        for (int bit = 0; bit < 8; ++bit) {
            crc = (crc & 1) ? (uint16_t)((crc >> 1) ^ 0xA001)
                            : (uint16_t)(crc >> 1);
        }
    }
    return crc;
}

uint16_t cab_crc16(const uint8_t *data, size_t length) {
    return crc_step(0xFFFF, data, length);
}

void cab_frame_parser_init(cab_frame_parser_t *parser,
                           cab_frame_callback_t callback, void *context) {
    memset(parser, 0, sizeof(*parser));
    parser->callback = callback;
    parser->callback_context = context;
}

static void parser_reset(cab_frame_parser_t *parser) {
    parser->state = 0;
    parser->length = 0;
    parser->position = 0;
    parser->received_crc = 0;
}

static void dispatch_payload(cab_frame_parser_t *parser) {
    if (parser->version == FRAME_VERSION_NORMAL) {
        parser->callback(parser->payload, parser->length,
                         parser->callback_context);
        return;
    }
    if (parser->length < 4) return;

    const uint8_t message_id = parser->payload[0];
    const uint8_t sequence = parser->payload[1];
    const uint8_t total = parser->payload[2];
    const uint16_t part_len = (uint16_t)(parser->length - 4);
    if (total == 0 || total > 16 || sequence >= total) return;

    if (parser->fragment_id != message_id || parser->fragment_total != total) {
        parser->fragment_id = message_id;
        parser->fragment_total = total;
        parser->fragment_mask = 0;
        memset(parser->fragment_lengths, 0, sizeof(parser->fragment_lengths));
    }
    const size_t offset = (size_t)sequence * FRAGMENT_DATA_MAX;
    if (offset + part_len > sizeof(parser->reassembly)) return;
    const uint16_t bit = (uint16_t)(1U << sequence);
    if ((parser->fragment_mask & bit) != 0) return;

    memcpy(parser->reassembly + offset, parser->payload + 4, part_len);
    parser->fragment_lengths[sequence] = part_len;
    parser->fragment_mask |= bit;
    const uint16_t complete_mask = total == 16
        ? 0xFFFF : (uint16_t)((1U << total) - 1U);
    if (parser->fragment_mask != complete_mask) return;

    size_t total_len = 0;
    for (uint8_t i = 0; i < total; ++i) total_len += parser->fragment_lengths[i];
    parser->callback(parser->reassembly, total_len, parser->callback_context);
    parser->fragment_mask = 0;
}

void cab_frame_parser_feed(cab_frame_parser_t *parser, const uint8_t *data,
                           size_t length) {
    if (parser == NULL || data == NULL || parser->callback == NULL) return;
    for (size_t i = 0; i < length; ++i) {
        const uint8_t byte = data[i];
        switch (parser->state) {
            case 0:
                if (byte == FRAME_HEAD_1) parser->state = 1;
                break;
            case 1:
                parser->state = byte == FRAME_HEAD_2 ? 2 :
                                (byte == FRAME_HEAD_1 ? 1 : 0);
                break;
            case 2:
                if (byte != FRAME_VERSION_NORMAL &&
                    byte != FRAME_VERSION_FRAGMENT) {
                    parser_reset(parser);
                    break;
                }
                parser->version = byte;
                parser->state = 3;
                break;
            case 3:
                parser->length = (uint16_t)byte << 8;
                parser->state = 4;
                break;
            case 4:
                parser->length |= byte;
                parser->position = 0;
                if (parser->length == 0 ||
                    parser->length > sizeof(parser->payload)) {
                    parser_reset(parser);
                } else {
                    parser->state = 5;
                }
                break;
            case 5:
                parser->payload[parser->position++] = byte;
                if (parser->position == parser->length) parser->state = 6;
                break;
            case 6:
                parser->received_crc = (uint16_t)byte << 8;
                parser->state = 7;
                break;
            case 7: {
                parser->received_crc |= byte;
                uint8_t header[3] = {parser->version,
                                     (uint8_t)(parser->length >> 8),
                                     (uint8_t)parser->length};
                uint16_t crc = crc_step(0xFFFF, header, sizeof(header));
                crc = crc_step(crc, parser->payload, parser->length);
                if (crc == parser->received_crc) {
                    dispatch_payload(parser);
                } else {
                    parser->crc_errors++;
                }
                parser_reset(parser);
                break;
            }
            default:
                parser_reset(parser);
                break;
        }
    }
}

static int send_one_frame(uint8_t version, const uint8_t *payload,
                          uint16_t length, cab_frame_write_t writer,
                          void *context) {
    uint8_t frame[CAB_FRAME_MAX_PAYLOAD + 7];
    frame[0] = FRAME_HEAD_1;
    frame[1] = FRAME_HEAD_2;
    frame[2] = version;
    frame[3] = (uint8_t)(length >> 8);
    frame[4] = (uint8_t)length;
    memcpy(frame + 5, payload, length);
    const uint16_t crc = cab_crc16(frame + 2, (size_t)length + 3);
    frame[5 + length] = (uint8_t)(crc >> 8);
    frame[6 + length] = (uint8_t)crc;
    return writer(frame, (size_t)length + 7, context);
}

int cab_frame_send(const uint8_t *payload, size_t length,
                   cab_frame_write_t writer, void *context) {
    if (payload == NULL || writer == NULL || length == 0 ||
        length > CAB_FRAME_REASSEMBLY_MAX) return -1;
    if (length <= CAB_FRAME_MAX_PAYLOAD) {
        return send_one_frame(FRAME_VERSION_NORMAL, payload, (uint16_t)length,
                              writer, context) < 0 ? -1 : (int)length;
    }

    uint8_t fragment[CAB_FRAME_MAX_PAYLOAD];
    uint8_t id = s_fragment_id++;
    if (s_fragment_id == 0) s_fragment_id = 1;
    const uint8_t total = (uint8_t)((length + FRAGMENT_DATA_MAX - 1) /
                                    FRAGMENT_DATA_MAX);
    for (uint8_t sequence = 0; sequence < total; ++sequence) {
        const size_t offset = (size_t)sequence * FRAGMENT_DATA_MAX;
        const size_t remaining = length - offset;
        const uint16_t part_len = (uint16_t)(remaining > FRAGMENT_DATA_MAX
            ? FRAGMENT_DATA_MAX : remaining);
        fragment[0] = id;
        fragment[1] = sequence;
        fragment[2] = total;
        fragment[3] = 0;
        memcpy(fragment + 4, payload + offset, part_len);
        if (send_one_frame(FRAME_VERSION_FRAGMENT, fragment,
                           (uint16_t)(part_len + 4), writer, context) < 0) {
            return -1;
        }
    }
    return (int)length;
}

uint16_t cab_next_message_id(void) {
    uint16_t result = s_message_id++;
    if (s_message_id == 0) s_message_id = 1;
    return result;
}

bool cab_app_decode(const uint8_t *data, size_t length, cab_app_view_t *view) {
    if (data == NULL || view == NULL || length < APP_HEADER_SIZE ||
        data[0] != APP_MAGIC_0 || data[1] != APP_MAGIC_1 ||
        data[2] != APP_VERSION) return false;
    memset(view, 0, sizeof(*view));
    view->flags = data[3];
    view->command = read_u16_le(data + 4);
    view->message_id = read_u16_le(data + 6);
    view->correlation_id = read_u16_le(data + 8);
    view->device_id_len = data[10];
    view->source_id_len = data[11];
    view->payload_len = read_u16_le(data + 12);
    view->timestamp_unix = read_u32_le(data + 14);
    if (view->device_id_len > CAB_APP_ID_MAX ||
        view->source_id_len > CAB_APP_ID_MAX ||
        view->payload_len > CAB_APP_MAX_PAYLOAD) return false;
    size_t position = APP_HEADER_SIZE;
    if ((view->flags & CAB_APP_FLAG_HAS_HMAC) != 0) position += 44;
    const size_t required = position + view->device_id_len +
                            view->source_id_len + view->payload_len;
    if (required > length) return false;
    view->device_id = data + position;
    position += view->device_id_len;
    view->source_id = data + position;
    position += view->source_id_len;
    view->payload = data + position;
    return true;
}

int cab_app_encode(uint8_t *output, size_t output_size, uint16_t command,
                   uint16_t message_id, uint16_t correlation_id, uint8_t flags,
                   const char *device_id, const char *source_id,
                   const uint8_t *payload, uint16_t payload_len,
                   uint32_t timestamp_unix) {
    if (output == NULL || (payload_len > 0 && payload == NULL) ||
        payload_len > CAB_APP_MAX_PAYLOAD) return -1;
    size_t device_len = device_id == NULL ? 0 : strnlen(device_id, CAB_APP_ID_MAX);
    size_t source_len = source_id == NULL ? 0 : strnlen(source_id, CAB_APP_ID_MAX);
    const size_t required = APP_HEADER_SIZE + device_len + source_len + payload_len;
    if (required > output_size) return -1;
    if (timestamp_unix == 0) timestamp_unix = (uint32_t)time(NULL);
    flags &= (uint8_t)~CAB_APP_FLAG_HAS_HMAC;
    output[0] = APP_MAGIC_0;
    output[1] = APP_MAGIC_1;
    output[2] = APP_VERSION;
    output[3] = flags;
    write_u16_le(output + 4, command);
    write_u16_le(output + 6, message_id);
    write_u16_le(output + 8, correlation_id);
    output[10] = (uint8_t)device_len;
    output[11] = (uint8_t)source_len;
    write_u16_le(output + 12, payload_len);
    write_u32_le(output + 14, timestamp_unix);
    size_t position = APP_HEADER_SIZE;
    if (device_len > 0) {
        memcpy(output + position, device_id, device_len);
        position += device_len;
    }
    if (source_len > 0) {
        memcpy(output + position, source_id, source_len);
        position += source_len;
    }
    if (payload_len > 0) memcpy(output + position, payload, payload_len);
    return (int)required;
}

void cab_app_copy_id(char *output, size_t output_size, const uint8_t *id,
                     uint8_t id_len) {
    if (output == NULL || output_size == 0) return;
    size_t copy_len = id_len;
    if (copy_len >= output_size) copy_len = output_size - 1;
    if (copy_len > 0 && id != NULL) memcpy(output, id, copy_len);
    output[copy_len] = '\0';
}

int cab_pack_heartbeat(uint8_t *output, size_t output_size,
                       uint32_t free_heap, uint32_t free_psram,
                       uint16_t min_free_heap, uint8_t layer,
                       uint8_t topology, uint16_t send_failures,
                       uint16_t queue_full, uint16_t recoveries) {
    if (output == NULL || output_size < 18) return -1;
    write_u32_le(output, free_heap);
    write_u32_le(output + 4, free_psram);
    write_u16_le(output + 8, min_free_heap);
    output[10] = layer;
    output[11] = topology;
    write_u16_le(output + 12, send_failures);
    write_u16_le(output + 14, queue_full);
    write_u16_le(output + 16, recoveries);
    return 18;
}

int cab_pack_status(uint8_t *output, size_t output_size, uint32_t uptime,
                    uint8_t lock_mask, uint8_t layer, uint8_t flags,
                    uint16_t fingerprint_count, uint16_t permission_count,
                    uint32_t permission_version, uint16_t send_failures,
                    uint16_t queue_full, int8_t rssi, uint8_t assoc_expire,
                    uint16_t fingerprint_poll_max_ms) {
    if (output == NULL || output_size < 24) return -1;
    output[0] = 1;
    output[1] = lock_mask;
    output[2] = layer;
    output[3] = flags;
    write_u32_le(output + 4, uptime);
    write_u16_le(output + 8, fingerprint_count);
    write_u16_le(output + 10, permission_count);
    write_u32_le(output + 12, permission_version);
    write_u16_le(output + 16, send_failures);
    write_u16_le(output + 18, queue_full);
    output[20] = (uint8_t)rssi;
    output[21] = assoc_expire;
    write_u16_le(output + 22, fingerprint_poll_max_ms);
    return 24;
}

int cab_pack_ack(uint8_t *output, size_t output_size, uint16_t reference_id,
                 uint16_t result_code, const char *tag) {
    const size_t tag_len = tag == NULL ? 0 : strnlen(tag, 63);
    if (output == NULL || output_size < 5 + tag_len) return -1;
    write_u16_le(output, reference_id);
    write_u16_le(output + 2, result_code);
    output[4] = (uint8_t)tag_len;
    if (tag_len > 0) memcpy(output + 5, tag, tag_len);
    return (int)(5 + tag_len);
}
