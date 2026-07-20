/**
 * mem_pool.h - PSRAM-aware permanent buffers + heap metrics (Phase 0)
 * Stops protocol-path heap churn by providing static/PSRAM TX pools.
 */
#pragma once

#include <stddef.h>
#include <stdint.h>

namespace MemPool {
    // Allocate permanent pools (idempotent). Safe to call from ProtocolFrame::init.
    void init();

    // Permanent large buffer from PSRAM (fallback internal). Caller owns for life of process.
    uint8_t* allocPsram(size_t size);

    // Shared frame encode TX pool (FRAME_TX_POOL_SIZE). Single-threaded loop use only.
    uint8_t* frameTxBuf();
    size_t frameTxBufSize();

    // Scratch for mesh TX assembly (MESH_TX_SCRATCH_SIZE).
    uint8_t* meshTxScratch();
    size_t meshTxScratchSize();

    // Heap metrics (internal DRAM + PSRAM)
    uint32_t freeInternalHeap();
    uint32_t minFreeInternalHeap();
    uint32_t freePsram();
    uint32_t largestFreeBlock();

    // Sample free heap and update min watermark.
    void noteHeapSample();
}
