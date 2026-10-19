#pragma once
#include <stdint.h>

#ifdef _WIN32
#define FU_API __declspec(dllexport)
#else
#define FU_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif
// Callbacks run on native worker threads. Buffers are valid only during the call.
// Copy data before returning. No managed exception may cross this ABI.
typedef void (*fu_video_callback)(const char* identity, const uint8_t* rgba, int width, int height, int length);
typedef void (*fu_audio_callback)(const char* identity, const uint8_t* pcm, int length, int rate, int channels);
typedef void (*fu_event_callback)(int kind, const char* identity); // 1 participant left; 2 disconnected; 3 pipeline error
typedef void (*fu_data_callback)(const char* sender, const uint8_t* data, int length);
FU_API int fu_abi_version(void);
FU_API void* fu_create(fu_video_callback video, fu_audio_callback audio, fu_event_callback event, fu_data_callback data);
FU_API int fu_connect(void* session, const char* url, const char* token);
// kind: 0 microphone (48kHz mono PCM16); 1 camera; 2 screen (RGBA).
FU_API int fu_publish(void* session, int kind, int width, int height);
FU_API int fu_push(void* session, int kind, const uint8_t* data, int length);
FU_API int fu_unpublish(void* session, int kind);
// Stops native readers and joins workers before returning. Never call from a callback.
FU_API void fu_destroy(void* session);
FU_API int fu_send_data(void* session, const char* destination, const uint8_t* data, int length);
FU_API int fu_desktop_bounds(int* width, int* height);
FU_API int fu_input_supported(void);
FU_API int fu_apply_input(int kind, double x, double y, int code, int down);
FU_API void fu_release_input(void);
typedef void (*fu_pcm_callback)(const uint8_t* pcm, int length);
FU_API int fu_audio_device_name(int capture, int index, char* name, int capacity);
FU_API void* fu_audio_open(const char* name, int capture, int rate, int channels, fu_pcm_callback callback);
FU_API int fu_audio_write(void* handle, const uint8_t* pcm, int length);
FU_API void fu_audio_close(void* handle);
#ifdef __cplusplus
}
#endif
