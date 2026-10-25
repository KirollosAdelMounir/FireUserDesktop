#include "video_scaling.h"
#include <livekit/livekit.h>
#include <iostream>

int main() {
    livekit::initialize(livekit::LogLevel::Error);
    int result = 0;
    try {
        // Synthetic pixels only: no room, network, monitor, camera or input provider.
        livekit::VideoSource source(1920, 1080);
        std::vector<uint8_t> red(1920 * 1080 * 3 / 2, 81);
        std::fill(red.begin()+1920*1080,red.begin()+1920*1080*5/4,90);
        std::fill(red.begin()+1920*1080*5/4,red.end(),240);
        for (const auto size : {std::pair{1280,720},std::pair{854,480},std::pair{1920,1080}}) {
            livekit::VideoFrame frame(size.first,size.second,livekit::VideoBufferType::I420,
                fireuser::scale_i420(red,1920,1080,size.first,size.second));
            source.captureFrame(frame);
            auto preview = frame.convert(livekit::VideoBufferType::RGBA);
            if (preview.width()!=size.first || preview.height()!=size.second || preview.dataSize()!=static_cast<size_t>(size.first)*size.second*4 ||
                preview.data()[0]<245 || preview.data()[1]>10 || preview.data()[2]>10 || preview.data()[3]!=255) result=1;
        }
    } catch (const std::exception& ex) { std::cerr << "Synthetic I420 source failure: " << ex.what() << '\n'; result=2; }
    livekit::shutdown();
    if (!result) std::cout << "One SDK source accepts adaptive I420 frames and converts local preview correctly\n";
    return result;
}
