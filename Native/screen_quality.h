#pragma once
#include <algorithm>
#include <cmath>

namespace fireuser {
struct ScreenQuality {
    int width, height, fps;
    unsigned bitrate;
    bool operator==(const ScreenQuality&) const = default;
};
inline ScreenQuality control_quality(ScreenQuality quality,bool active) {
#if defined(__linux__)
    // Software encoding in Linux guests must not double its workload when a
    // grant arrives. Keep congestion adaptation and bound the high-bandwidth tier.
    (void)active;
    quality.fps=std::min(quality.fps,30);
#else
    if(active&&quality.height>=720)quality.fps=60;
#endif
    return quality;
}
// Upload estimates are bits/sec: 100 decimal KB/sec = 800,000 bits/sec.
inline ScreenQuality screen_quality(double available, bool congested = false) {
    if (congested) return {854, 480, 15, 240000};
    if (!std::isfinite(available) || available < 0) return {1280, 720, 30, 1200000};
    if (available < 800000) return {854, 480, 15, static_cast<unsigned>(std::clamp(available * .8, 120000.0, 640000.0))};
    if (available < 1600000) return {1280, 720, 30, 1200000};
    if (available < 3200000) return {1920, 1080, 30, 2800000};
    return {1920, 1080, 60, 6400000};
}
}
