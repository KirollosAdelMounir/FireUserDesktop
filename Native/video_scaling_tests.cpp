#include "video_scaling.h"
#include <iostream>

int main() {
    const std::vector<uint8_t> tiny{0, 100, 200, 255, 90, 240};
    if (fireuser::scale_i420(tiny, 2, 2, 2, 2) != tiny) return 1;
    const auto expanded = fireuser::scale_i420(tiny, 2, 2, 4, 4);
    const std::vector<uint8_t> expected{0,50,100,100,100,139,178,178,200,228,255,255,200,228,255,255,90,90,90,90,240,240,240,240};
    if (expanded != expected) return 2;
    std::vector<uint8_t> gradient(8 * 4 * 3 / 2);
    for (int i = 0; i < 32; ++i) gradient[i] = static_cast<uint8_t>(i);
    for (int i = 0; i < 8; ++i) { gradient[32+i] = static_cast<uint8_t>(100+i); gradient[40+i] = static_cast<uint8_t>(150+i); }
    if (fireuser::scale_i420(gradient,8,4,4,2) != std::vector<uint8_t>{0,2,4,6,16,18,20,22,100,102,150,152}) return 3;
    std::vector<uint8_t> full(1920 * 1080 * 3 / 2, 200);
    std::fill(full.begin() + 1920 * 1080, full.begin() + 1920 * 1080 * 5 / 4, 70);
    std::fill(full.begin() + 1920 * 1080 * 5 / 4, full.end(), 190);
    for (const auto size : {std::pair{854,480}, std::pair{1280,720}, std::pair{1920,1080}}) {
        const auto scaled = fireuser::scale_i420(full,1920,1080,size.first,size.second);
        const int plane = size.first * size.second;
        if (scaled.size() != static_cast<size_t>(plane) * 3 / 2 ||
            !std::all_of(scaled.begin(),scaled.begin()+plane,[](auto v){return v==200;}) ||
            !std::all_of(scaled.begin()+plane,scaled.begin()+plane*5/4,[](auto v){return v==70;}) ||
            !std::all_of(scaled.begin()+plane*5/4,scaled.end(),[](auto v){return v==190;})) return 4;
    }
    for (const auto size : {std::pair{0,2}, std::pair{3,2}, std::pair{4098,2}}) {
        try { fireuser::scale_i420(tiny,2,2,size.first,size.second); return 5; } catch (const std::invalid_argument&) { }
    }
    try { fireuser::scale_i420(std::span<const uint8_t>(tiny.data(),5),2,2,2,2); return 6; } catch (const std::invalid_argument&) { }
    std::cout << "I420 identity, bilinear pixels, plane boundaries, quality tiers and invalid frames passed\n";
}
