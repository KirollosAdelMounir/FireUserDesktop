#include "fireuser_livekit.h"
#include "screen_quality.h"
#include <iostream>

int main() {
    if (fireuser::screen_quality(799999).height != 480 || fireuser::screen_quality(800000).height != 720 ||
        fireuser::screen_quality(1600000).height != 1080 || fireuser::screen_quality(3199999).fps != 30 ||
        fireuser::screen_quality(3200000).fps != 60 || fireuser::screen_quality(6400000, true).fps != 15 ||
        fireuser::screen_quality(-1).height != 720) return 5;
    if (fu_abi_version() != 3) return 1;
    if (fu_connect(nullptr, "", "") != -1 || fu_push(nullptr, 0, nullptr, 0) != -1) return 2;
    auto session = fu_create(nullptr, nullptr, nullptr, nullptr);
    if (!session) return 3;
    // Unconnected publication is rejected; destruction must not leave native readers alive.
    if (fu_publish(session, 0, 960, 540) != -1) return 4;
    fu_destroy(session);
    std::cout << "C++ bridge ABI and disconnected lifecycle passed\n";
}
