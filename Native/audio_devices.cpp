#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_ENCODING
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#include "miniaudio.h"
#include "fireuser_livekit.h"
#include <algorithm>
#include <cstring>
#include <memory>

namespace {
struct AudioDevice {
    ma_context context{};
    ma_device device{};
    ma_pcm_rb buffer{};
    bool context_ready = false, device_ready = false, buffer_ready = false;
    int channels = 1;
    fu_pcm_callback callback = nullptr;
    ~AudioDevice() {
        if (device_ready) ma_device_uninit(&device);
        if (buffer_ready) ma_pcm_rb_uninit(&buffer);
        if (context_ready) ma_context_uninit(&context);
    }
};
void audio_callback(ma_device* device, void* output, const void* input, ma_uint32 frames) {
    auto& value = *static_cast<AudioDevice*>(device->pUserData);
    if (input && value.callback) value.callback(static_cast<const uint8_t*>(input), static_cast<int>(frames * value.channels * 2));
    if (!output) return;
    auto* destination = static_cast<uint8_t*>(output);
    std::memset(output, 0, frames * value.channels * 2);
    while (frames > 0) {
        ma_uint32 count = frames; void* data = nullptr;
        ma_pcm_rb_acquire_read(&value.buffer, &count, &data);
        if (count == 0) break;
        std::memcpy(destination, data, count * value.channels * 2);
        ma_pcm_rb_commit_read(&value.buffer, count);
        destination += count * value.channels * 2; frames -= count;
    }
}
}
int fu_audio_device_name(int capture, int index, char* name, int capacity) {
    if (index < 0 || !name || capacity < 2) return -1;
    ma_context context{};
    if (ma_context_init(nullptr, 0, nullptr, &context) != MA_SUCCESS) return -1;
    ma_device_info *playback = nullptr, *recording = nullptr; ma_uint32 plays = 0, records = 0;
    const auto result = ma_context_get_devices(&context, &playback, &plays, &recording, &records);
    int length = 0;
    if (result == MA_SUCCESS && static_cast<ma_uint32>(index) < (capture ? records : plays)) {
        const auto* value = (capture ? recording : playback)[index].name;
        length = static_cast<int>(std::strlen(value));
        if (length >= capacity) length = -1;
        else std::memcpy(name, value, length + 1);
    }
    ma_context_uninit(&context); return result == MA_SUCCESS ? length : -1;
}
void* fu_audio_open(const char* name, int capture, int rate, int channels, fu_pcm_callback callback) {
    if (rate < 8000 || rate > 96000 || channels < 1 || channels > 2 || (capture && !callback)) return nullptr;
    try {
        auto value = std::make_unique<AudioDevice>(); value->channels = channels; value->callback = callback;
        if (ma_context_init(nullptr, 0, nullptr, &value->context) != MA_SUCCESS) return nullptr;
        value->context_ready = true;
        ma_device_config config = ma_device_config_init(capture ? ma_device_type_capture : ma_device_type_playback);
        ma_device_id selected{};
        if (name && *name) {
            ma_device_info *playback = nullptr, *recording = nullptr; ma_uint32 plays = 0, records = 0;
            if (ma_context_get_devices(&value->context, &playback, &plays, &recording, &records) != MA_SUCCESS) return nullptr;
            bool found = false;
            for (ma_uint32 i = 0; i < (capture ? records : plays); ++i) {
                auto& item = (capture ? recording : playback)[i];
                if (std::strcmp(item.name, name) == 0) { selected = item.id; found = true; break; }
            }
            if (!found) return nullptr;
            if (capture) config.capture.pDeviceID = &selected; else config.playback.pDeviceID = &selected;
        }
        config.sampleRate = rate; config.periodSizeInMilliseconds = 10;
        config.capture.format = ma_format_s16; config.capture.channels = channels;
        config.playback.format = ma_format_s16; config.playback.channels = channels;
        config.dataCallback = audio_callback; config.pUserData = value.get();
        if (!capture) {
            if (ma_pcm_rb_init(ma_format_s16, channels, rate / 4, nullptr, nullptr, &value->buffer) != MA_SUCCESS) return nullptr;
            value->buffer_ready = true;
        }
        if (ma_device_init(&value->context, &config, &value->device) != MA_SUCCESS) return nullptr;
        value->device_ready = true;
        if (ma_device_start(&value->device) != MA_SUCCESS) return nullptr;
        return value.release();
    } catch (...) { return nullptr; }
}
int fu_audio_write(void* handle, const uint8_t* pcm, int length) {
    if (!handle || !pcm || length <= 0) return -1;
    auto& value = *static_cast<AudioDevice*>(handle);
    if (!value.buffer_ready || length % (2 * value.channels) != 0) return -1;
    ma_uint32 remaining = length / (2 * value.channels);
    while (remaining > 0) {
        ma_uint32 count = remaining; void* data = nullptr;
        ma_pcm_rb_acquire_write(&value.buffer, &count, &data);
        if (count == 0) break; // Bounded 250 ms queue; discard excess rather than accumulating latency.
        std::memcpy(data, pcm, count * value.channels * 2); ma_pcm_rb_commit_write(&value.buffer, count);
        pcm += count * value.channels * 2; remaining -= count;
    }
    return 0;
}
void fu_audio_close(void* handle) { delete static_cast<AudioDevice*>(handle); }

namespace {
struct Tone {
    ma_decoder decoder{};
    ma_device device{};
    bool decoded = false, opened = false, loop = false;
    ~Tone() { if (opened) ma_device_uninit(&device); if (decoded) ma_decoder_uninit(&decoder); }
};
void tone_callback(ma_device* device, void* output, const void*, ma_uint32 frames) {
    auto& tone = *static_cast<Tone*>(device->pUserData);
    auto* pcm = static_cast<int16_t*>(output);
    std::memset(output, 0, frames * 2 * sizeof(int16_t));
    ma_uint64 read = 0;
    ma_decoder_read_pcm_frames(&tone.decoder, pcm, frames, &read);
    if (read < frames && tone.loop && ma_decoder_seek_to_pcm_frame(&tone.decoder, 0) == MA_SUCCESS)
        ma_decoder_read_pcm_frames(&tone.decoder, pcm + read * 2, frames - read, nullptr);
}
}
void* fu_tone_open(const char* path, int loop) {
    if (!path) return nullptr;
    try {
    auto tone = std::make_unique<Tone>(); tone->loop = loop != 0;
    auto decoder_config = ma_decoder_config_init(ma_format_s16, 2, 48000);
    if (ma_decoder_init_file(path, &decoder_config, &tone->decoder) != MA_SUCCESS) return nullptr;
    tone->decoded = true;
    auto config = ma_device_config_init(ma_device_type_playback);
    config.playback.format = ma_format_s16; config.playback.channels = 2; config.sampleRate = 48000;
    config.dataCallback = tone_callback; config.pUserData = tone.get();
    if (ma_device_init(nullptr, &config, &tone->device) != MA_SUCCESS) return nullptr;
    tone->opened = true;
    if (ma_device_start(&tone->device) != MA_SUCCESS) return nullptr;
    return tone.release();
    } catch (...) { return nullptr; }
}
void fu_tone_close(void* handle) { delete static_cast<Tone*>(handle); }
