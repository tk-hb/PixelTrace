#pragma once
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define PT_API __attribute__((visibility("default")))
// The returned engine owns all llama/mtmd objects. Release with pt_llama_free.
// Calls on an engine must be serialized, including destruction.
// backend: 0 = CPU, 1 = Vulkan GPU. No automatic backend selection.
PT_API void * pt_llama_create_v2(const char * model, const char * projector, int32_t image_tokens, int32_t backend);
PT_API void pt_llama_free(void * engine);
// kind: 0 = UTF-8 query with task prefix, 1 = encoded image (PNG).
// Input/output buffers are borrowed only for the duration of this synchronous call.
PT_API int32_t pt_llama_encode(void * engine, int32_t kind, const uint8_t * input, size_t length, float * output, int32_t dimensions);
// Borrowed UTF-8 strings; never free. Error is thread-local, valid until next call.
PT_API const char * pt_llama_error(void);
PT_API const char * pt_llama_revision(void);

#ifdef __cplusplus
}
#endif
