#include "fireuser_livekit.h"
#include "screen_quality.h"
#include <condition_variable>
#include <livekit/livekit.h>
#include <atomic>
#include <array>
#include <cstring>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <unordered_set>

namespace {
using namespace livekit;
std::mutex runtime_mutex;
int active_sessions = 0;
struct Publication {
    std::shared_ptr<Track> track;
    std::shared_ptr<AudioSource> audio;
    std::shared_ptr<VideoSource> video;
};
struct Reader {
    std::atomic_bool stopped{false};
    std::atomic_bool finished{false};
    std::shared_ptr<AudioStream> audio;
    std::shared_ptr<VideoStream> video;
    std::thread worker;
};

class Session final : public RoomDelegate {
public:
    Room room;
    std::atomic_bool stopping{false};
    fu_video_callback video;
    fu_audio_callback audio;
    fu_event_callback event;
    fu_data_callback data;
    std::mutex publication_mutex;
    std::array<Publication, 3> publications;
    fireuser::ScreenQuality screen_quality = fireuser::screen_quality(-1);
    fireuser::ScreenQuality published_quality = screen_quality;
    std::chrono::steady_clock::time_point last_screen_frame{};
    std::thread quality_worker;
    std::mutex quality_wait_mutex;
    std::condition_variable quality_wait;
    void startQualityMonitor() {
        quality_worker = std::thread([this] {
            auto healthy_since = std::chrono::steady_clock::now();
            while (!stopping) {
                std::unique_lock wait_lock(quality_wait_mutex);
                if (quality_wait.wait_for(wait_lock, std::chrono::seconds(2), [this] { return stopping.load(); })) break;
                wait_lock.unlock();
                { std::lock_guard lock(publication_mutex); if (!publications[2].video) continue; }
                try {
                    auto pending = room.getStats();
                    while (!stopping && pending.wait_for(std::chrono::milliseconds(100)) != std::future_status::ready) {}
                    if (stopping) break;
                    auto stats = pending.get();
                    double available = -1; bool congested = false;
                    for (const auto& stat : stats.publisher_stats) {
                        if (auto pair = std::get_if<RtcCandidatePairStats>(&stat.stats); pair && pair->candidate_pair.nominated) {
                            if (pair->candidate_pair.available_outgoing_bitrate > 0) available = pair->candidate_pair.available_outgoing_bitrate;
                            congested |= pair->candidate_pair.current_round_trip_time > .8;
                        }
                        if (auto remote = std::get_if<RtcRemoteInboundRtpStats>(&stat.stats)) congested |= remote->remote_inbound.fraction_lost > .08;
                    }
                    auto next = fireuser::screen_quality(available, congested);
                    std::lock_guard lock(publication_mutex);
                    const auto now = std::chrono::steady_clock::now();
                    if (next.height < screen_quality.height || next.fps < screen_quality.fps) { screen_quality = next; healthy_since = now; }
                    else if (now - healthy_since >= std::chrono::seconds(6)) { screen_quality = next; healthy_since = now; }
                } catch (...) { /* No telemetry: retain the conservative initial tier. */ }
            }
        });
    }
    std::mutex readers_mutex;
    std::unordered_map<std::string, std::shared_ptr<Reader>> readers;

    Session(fu_video_callback v, fu_audio_callback a, fu_event_callback e, fu_data_callback d) : video(v), audio(a), event(e), data(d) { room.setDelegate(this); }
    ~Session() override {
        stopping = true;
        quality_wait.notify_all();
        if (quality_worker.joinable()) quality_worker.join();
        // Close queues before disconnect. Joining inside RoomDelegate would deadlock.
        {
            std::lock_guard lock(readers_mutex);
            for (auto& [_, reader] : readers) {
                reader->stopped = true;
                if (reader->audio) reader->audio->close();
                if (reader->video) reader->video->close();
            }
        }
        try { room.disconnect(); } catch (...) { }
        room.setDelegate(nullptr);
        for (auto& [_, reader] : readers) if (reader->worker.joinable()) reader->worker.join();
    }
    void signal(int kind, const std::string& identity = "") noexcept {
        if (!stopping && event) event(kind, identity.c_str());
    }
    void onParticipantDisconnected(Room&, const ParticipantDisconnectedEvent& e) override {
        if (e.participant) { { std::lock_guard lock(speakers_mutex); active_speakers.erase(e.participant->identity()); } signal(1, e.participant->identity()); }
    }
    void onParticipantConnected(Room&, const ParticipantConnectedEvent& e) override { if (e.participant) { signal(4, e.participant->identity()); signal(6, e.participant->identity() + "\n" + e.participant->name()); } }
    void onUserPacketReceived(Room&, const UserDataPacketEvent& e) override {
        if (!stopping && data && e.participant && e.topic == "fireuser.native.v1" && !e.data.empty() && e.data.size() <= 4096)
            data(e.participant->identity().c_str(), e.data.data(), static_cast<int>(e.data.size()));
    }
    void onDisconnected(Room&, const DisconnectedEvent&) override { signal(2); }
    std::mutex speakers_mutex;
    std::unordered_set<std::string> active_speakers;
    void onActiveSpeakersChanged(Room&, const ActiveSpeakersChangedEvent& e) override {
        std::unordered_set<std::string> next;
        for (const auto* participant : e.speakers) {
            if (participant && next.size() < 64) next.insert(participant->identity());
        }
        std::vector<std::pair<std::string, bool>> changes;
        {
            std::lock_guard lock(speakers_mutex);
            for (const auto& identity : active_speakers) if (!next.contains(identity)) changes.emplace_back(identity, false);
            for (const auto& identity : next) if (!active_speakers.contains(identity)) changes.emplace_back(identity, true);
            active_speakers = std::move(next);
        }
        for (const auto& [identity, speaking] : changes) signal(7, identity + "\nspeaking\n" + (speaking ? "1" : "0"));
    }
    void onConnectionQualityChanged(Room&, const ConnectionQualityChangedEvent& e) override {
        if (e.participant) signal(7, e.participant->identity() + "\nquality\n" + std::to_string(static_cast<int>(e.quality)));
    }
    void onTrackMuted(Room&, const TrackMutedEvent& e) override {
        if (e.participant && e.publication && (e.publication->source() == TrackSource::SOURCE_MICROPHONE || e.publication->source() == TrackSource::SOURCE_CAMERA)) signal(7, e.participant->identity() + "\n" + (e.publication->source() == TrackSource::SOURCE_MICROPHONE ? "microphone" : "camera") + "\n0");
    }
    void onTrackUnmuted(Room&, const TrackUnmutedEvent& e) override {
        if (e.participant && e.publication && (e.publication->source() == TrackSource::SOURCE_MICROPHONE || e.publication->source() == TrackSource::SOURCE_CAMERA)) signal(7, e.participant->identity() + "\n" + (e.publication->source() == TrackSource::SOURCE_MICROPHONE ? "microphone" : "camera") + "\n1");
    }
    void onTrackSubscribed(Room&, const TrackSubscribedEvent& e) override {
        if (stopping || !e.track || !e.participant) return;
        const auto source = e.publication ? std::optional<TrackSource>(e.publication->source()) : e.track->source();
        const auto key = e.participant->identity() + (source == TrackSource::SOURCE_SCREENSHARE ? " · screen · " : " · camera · ") + e.track->sid();
        if (source == TrackSource::SOURCE_MICROPHONE || source == TrackSource::SOURCE_CAMERA)
            signal(7, e.participant->identity() + "\n" + (source == TrackSource::SOURCE_MICROPHONE ? "microphone" : "camera") + "\n1");
        const auto sid = e.track->sid();
        auto reader = std::make_shared<Reader>();
        std::lock_guard lock(readers_mutex);
        for (auto it = readers.begin(); it != readers.end();) {
            if (it->second->finished) { if (it->second->worker.joinable()) it->second->worker.join(); it = readers.erase(it); }
            else ++it;
        }
        if (stopping || readers.size() >= 32 || readers.contains(sid)) return;
        readers.emplace(sid, reader);
        reader->worker = std::thread([this, reader, track = e.track, key] {
            try {
                if (track->kind() == TrackKind::KIND_VIDEO) {
                    VideoStream::Options options; options.capacity = 1; options.format = VideoBufferType::RGBA;
                    auto stream = VideoStream::fromTrack(track, options);
                    { std::lock_guard guard(readers_mutex); reader->video = stream; if (reader->stopped || stopping) stream->close(); }
                    VideoFrameEvent frame;
                    while (stream->read(frame) && !stopping && !reader->stopped) {
                        const auto& value = frame.frame;
                        if (value.width() <= 4096 && value.height() <= 4096 && video)
                            video(key.c_str(), value.data(), value.width(), value.height(), static_cast<int>(value.dataSize()));
                    }
                } else if (track->kind() == TrackKind::KIND_AUDIO) {
                    AudioStream::Options options; options.capacity = 10;
                    auto stream = AudioStream::fromTrack(track, options);
                    { std::lock_guard guard(readers_mutex); reader->audio = stream; if (reader->stopped || stopping) stream->close(); }
                    AudioFrameEvent frame;
                    while (stream->read(frame) && !stopping && !reader->stopped) {
                        const auto& value = frame.frame;
                        if (audio) audio(key.c_str(), reinterpret_cast<const uint8_t*>(value.data().data()),
                            static_cast<int>(value.data().size() * sizeof(int16_t)), value.sampleRate(), value.numChannels());
                    }
                }
            } catch (...) { signal(3, key); }
            reader->finished = true;
        });
    }
    void onTrackUnsubscribed(Room&, const TrackUnsubscribedEvent& e) override {
        if (!e.track) return;
        const auto source = e.publication ? std::optional<TrackSource>(e.publication->source()) : e.track->source();
        if (e.participant && e.track->kind() == TrackKind::KIND_VIDEO) signal(5, e.participant->identity() + (source == TrackSource::SOURCE_SCREENSHARE ? " · screen · " : " · camera · ") + e.track->sid());
        if (e.participant && (source == TrackSource::SOURCE_MICROPHONE || source == TrackSource::SOURCE_CAMERA))
            signal(7, e.participant->identity() + "\n" + (source == TrackSource::SOURCE_MICROPHONE ? "microphone" : "camera") + "\n0");
        std::lock_guard lock(readers_mutex);
        auto it = readers.find(e.track->sid());
        if (it != readers.end()) {
            it->second->stopped = true;
            if (it->second->audio) it->second->audio->close();
            if (it->second->video) it->second->video->close();
        }
    }
};
}

int fu_abi_version() { return 3; }
void* fu_create(fu_video_callback video, fu_audio_callback audio, fu_event_callback event, fu_data_callback data) {
    std::lock_guard lock(runtime_mutex);
    try {
        if (active_sessions == 0) livekit::initialize(livekit::LogLevel::Error);
        auto session = new Session(video, audio, event, data);
        ++active_sessions; return session;
    } catch (...) { if (active_sessions == 0) { try { livekit::shutdown(); } catch (...) { } } return nullptr; }
}
int fu_connect(void* handle, const char* url, const char* token) {
    if (!handle || !url || !token) return -1;
    try { RoomOptions options; options.auto_subscribe = true; options.dynacast = true;
        // Use the established dual-PC transport for deployment compatibility.
        options.single_peer_connection = false;
        options.join_retries = 1; options.connect_timeout = std::chrono::seconds(10);
        auto& session = *static_cast<Session*>(handle);
        if (!session.room.connect(url, token, options)) return -1;
        session.startQualityMonitor();
        // A late joiner needs the existing participants, including audio-only peers.
        for (const auto& weak : session.room.remoteParticipants()) if (auto peer = weak.lock()) {
            session.signal(4, peer->identity()); session.signal(6, peer->identity() + "\n" + peer->name());
        }
        return 0;
    } catch (...) { return -1; }
}
int fu_publish(void* handle, int kind, int width, int height) {
    if (!handle || kind < 0 || kind > 2 || width < 1 || height < 1 || width > 4096 || height > 4096) return -1;
    try {
        auto& session = *static_cast<Session*>(handle);
        std::lock_guard lock(session.publication_mutex);
        auto participant = session.room.localParticipant().lock();
        if (!participant || session.publications[kind].track) return -1;
        Publication value;
        if (kind == 0) {
            value.audio = std::make_shared<AudioSource>(48000, 1, 0);
            value.track = LocalAudioTrack::createLocalAudioTrack("microphone", value.audio);
        } else {
            const auto quality = session.screen_quality;
            const bool desktop = kind == 2 && width == 1920 && height == 1080;
            value.video = std::make_shared<VideoSource>(desktop ? quality.width : width, desktop ? quality.height : height);
            value.track = LocalVideoTrack::createLocalVideoTrack(kind == 1 ? "camera" : "screen", value.video);
        }
        TrackPublishOptions options;
        options.source = kind == 0 ? TrackSource::SOURCE_MICROPHONE : kind == 1 ? TrackSource::SOURCE_CAMERA : TrackSource::SOURCE_SCREENSHARE;
        options.simulcast = kind == 1;
        if (kind == 0) {
            options.audio_encoding = AudioEncodingOptions{32000};
            options.dtx = true;
        } else {
            // Match Angular's 1080p camera preset and full-quality screen ceiling.
            options.video_codec = VideoCodec::VP8;
            options.video_encoding = VideoEncodingOptions{kind == 2 ? session.screen_quality.bitrate : 3000000u, kind == 2 ? static_cast<double>(session.screen_quality.fps) : 30.0};
            options.degradation_preference = DegradationPreference::MaintainResolution;
        }
        participant->publishTrack(value.track, options);
        session.publications[kind] = std::move(value);
        if (kind == 2) session.published_quality = session.screen_quality;
        return 0;
    } catch (...) { return -1; }
}
int fu_push(void* handle, int kind, const uint8_t* data, int length) {
    if (!handle || !data || kind < 0 || kind > 2) return -1;
    try {
        auto& session = *static_cast<Session*>(handle);
        std::unique_lock lock(session.publication_mutex);
        auto& value = session.publications[kind];
        if (value.audio && length == 960) {
            std::vector<int16_t> samples(480); std::memcpy(samples.data(), data, 960);
            value.audio->captureFrame(AudioFrame(std::move(samples), 48000, 1, 480));
        } else if (kind == 2 && value.video && length == 1920 * 1080 * 4) {
            const auto next = session.screen_quality;
            // Republish only on a stable tier change; ordinary congestion remains WebRTC's job.
            if (next.height != session.published_quality.height || next.fps != session.published_quality.fps) {
                auto participant = session.room.localParticipant().lock();
                if (!participant) return -1;
                participant->unpublishTrack(value.track->sid());
                value.video = std::make_shared<VideoSource>(next.width, next.height);
                value.track = LocalVideoTrack::createLocalVideoTrack("screen", value.video);
                TrackPublishOptions options; options.source = TrackSource::SOURCE_SCREENSHARE; options.simulcast = false;
                options.video_codec = VideoCodec::VP8; options.video_encoding = VideoEncodingOptions{next.bitrate, static_cast<double>(next.fps)};
                options.degradation_preference = DegradationPreference::MaintainResolution;
                participant->publishTrack(value.track, options); session.published_quality = next;
            }
            const auto due = session.last_screen_frame + std::chrono::microseconds(1000000 / next.fps);
            // Pace the producer instead of dropping the final update from a static portal source.
            // Release the publication lock while waiting so speech is never blocked by screen FPS.
            lock.unlock(); std::this_thread::sleep_until(due); lock.lock();
            if (!value.video) return 0;
            session.last_screen_frame = std::chrono::steady_clock::now();
            if (next.height == 1080) {
                value.video->captureFrame(VideoFrame(1920, 1080, VideoBufferType::RGBA, std::vector<uint8_t>(data, data + length)));
                return 0;
            }
            std::vector<uint8_t> pixels(next.width * next.height * 4);
            // Bilinear downsampling retains text and edges across the lower-bandwidth tiers.
            for (int y = 0; y < next.height; ++y) {
                const double sy = static_cast<double>(y) * 1080 / next.height;
                const int y0 = static_cast<int>(sy), y1 = std::min(y0 + 1, 1079); const double fy = sy - y0;
                for (int x = 0; x < next.width; ++x) {
                    const double sx = static_cast<double>(x) * 1920 / next.width;
                    const int x0 = static_cast<int>(sx), x1 = std::min(x0 + 1, 1919); const double fx = sx - x0;
                    for (int c = 0; c < 4; ++c) pixels[(y * next.width + x) * 4 + c] = static_cast<uint8_t>(
                        (data[(y0 * 1920 + x0) * 4 + c] * (1 - fx) + data[(y0 * 1920 + x1) * 4 + c] * fx) * (1 - fy) +
                        (data[(y1 * 1920 + x0) * 4 + c] * (1 - fx) + data[(y1 * 1920 + x1) * 4 + c] * fx) * fy);
                }
            }
            value.video->captureFrame(VideoFrame(next.width, next.height, VideoBufferType::RGBA, std::move(pixels)));
        } else if (value.video && length == value.video->width() * value.video->height() * 4) {
            value.video->captureFrame(VideoFrame(value.video->width(), value.video->height(), VideoBufferType::RGBA,
                std::vector<uint8_t>(data, data + length)));
        } else return -1;
        return 0;
    } catch (...) { return -1; }
}
int fu_unpublish(void* handle, int kind) {
    if (!handle || kind < 0 || kind > 2) return -1;
    try {
        auto& session = *static_cast<Session*>(handle);
        std::lock_guard lock(session.publication_mutex);
        auto& value = session.publications[kind];
        if (value.track) {
            if (auto participant = session.room.localParticipant().lock()) participant->unpublishTrack(value.track->sid());
            value = {};
        }
        return 0;
    } catch (...) { return -1; }
}
void fu_destroy(void* handle) {
    if (!handle) return;
    try { delete static_cast<Session*>(handle); } catch (...) { }
    std::lock_guard lock(runtime_mutex);
    if (--active_sessions == 0) { try { livekit::shutdown(); } catch (...) { } }
}
int fu_send_data(void* handle, const char* destination, const uint8_t* data, int length) {
    if (!handle || !data || length < 1 || length > 4096) return -1;
    try {
        auto participant = static_cast<Session*>(handle)->room.localParticipant().lock();
        if (!participant) return -1;
        std::vector<std::string> targets;
        if (destination && *destination) targets.emplace_back(destination);
        participant->publishData(std::vector<uint8_t>(data, data + length), true, targets, "fireuser.native.v1");
        return 0;
    } catch (...) { return -1; }
}
