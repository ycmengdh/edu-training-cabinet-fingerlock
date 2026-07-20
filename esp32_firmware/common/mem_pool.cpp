/**
 * mem_pool.cpp - PSRAM permanent buffers and heap metrics
 */
#include "mem_pool.h"
#include "config_common.h"

#include <Arduino.h>
#include <esp_heap_caps.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>

namespace {

bool s_inited = false;
uint8_t* s_frameTx = nullptr;
uint8_t* s_meshTxScratch = nullptr;
uint32_t s_minFreeInternal = UINT32_MAX;

uint8_t* allocPreferPsram(size_t size) {
    if (size == 0) return nullptr;
    uint8_t* p = (uint8_t*)heap_caps_malloc(size, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (p == nullptr) {
        p = (uint8_t*)malloc(size);
    }
    return p;
}

} // namespace

namespace MemPool {

void init() {
    if (s_inited) return;

    s_frameTx = allocPreferPsram(FRAME_TX_POOL_SIZE);
    if (s_frameTx == nullptr) {
        // Last resort: static BSS (still better than per-send malloc churn).
        static uint8_t s_frameTxFallback[FRAME_TX_POOL_SIZE];
        s_frameTx = s_frameTxFallback;
    }

    s_meshTxScratch = allocPreferPsram(MESH_TX_SCRATCH_SIZE);
    if (s_meshTxScratch == nullptr) {
        static uint8_t s_meshTxFallback[MESH_TX_SCRATCH_SIZE];
        s_meshTxScratch = s_meshTxFallback;
    }

    s_minFreeInternal = ESP.getFreeHeap();
    s_inited = true;
}

uint8_t* allocPsram(size_t size) {
    return allocPreferPsram(size);
}

uint8_t* frameTxBuf() {
    if (!s_inited) init();
    return s_frameTx;
}

size_t frameTxBufSize() {
    return FRAME_TX_POOL_SIZE;
}

uint8_t* meshTxScratch() {
    if (!s_inited) init();
    return s_meshTxScratch;
}

size_t meshTxScratchSize() {
    return MESH_TX_SCRATCH_SIZE;
}

uint32_t freeInternalHeap() {
    return ESP.getFreeHeap();
}

uint32_t minFreeInternalHeap() {
    noteHeapSample();
    return s_minFreeInternal;
}

uint32_t freePsram() {
#if defined(BOARD_HAS_PSRAM)
    return (uint32_t)heap_caps_get_free_size(MALLOC_CAP_SPIRAM);
#else
    return 0;
#endif
}

uint32_t largestFreeBlock() {
    // Prefer internal DRAM largest free block — the scarce resource.
    size_t internal = heap_caps_get_largest_free_block(MALLOC_CAP_8BIT | MALLOC_CAP_INTERNAL);
    if (internal > 0) return (uint32_t)internal;
    return (uint32_t)heap_caps_get_largest_free_block(MALLOC_CAP_8BIT);
}

void noteHeapSample() {
    uint32_t free = ESP.getFreeHeap();
    if (free < s_minFreeInternal) {
        s_minFreeInternal = free;
    }
}

} // namespace MemPool
