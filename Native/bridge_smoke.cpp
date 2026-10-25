#include "fireuser_livekit.h"
#include "screen_quality.h"
#include <iostream>

int main() {
    if (fireuser::screen_quality(799999).height != 480 || fireuser::screen_quality(800000).height != 720 ||
        fireuser::screen_quality(1600000).height != 1080 || fireuser::screen_quality(3199999).fps != 30 ||
        fireuser::screen_quality(3200000).fps != 60 || fireuser::screen_quality(6400000, true).fps != 15 ||
        fireuser::screen_quality(-1).height != 720) return 5;
    if (fu_abi_version() != 7) return 1;
#ifdef __linux__
    constexpr int interactive_fps=30;
#else
    constexpr int interactive_fps=60;
#endif
    if(fireuser::control_quality(fireuser::screen_quality(-1),true).fps!=interactive_fps||fireuser::control_quality(fireuser::screen_quality(100000),true).fps!=15||fireuser::control_quality(fireuser::screen_quality(-1),false).fps!=30)return 8;
    if (fu_connect(nullptr, "", "") != -1 || fu_push(nullptr, 0, nullptr, 0) != -1) return 2;
    if(fu_send_input(nullptr,"peer",nullptr,0)!=-1||fu_send_transfer(nullptr,"peer",nullptr,0)!=-1)return 7;
    auto session = fu_create(nullptr, nullptr, nullptr, nullptr);
    if (!session) return 3;
    // Unconnected publication is rejected; destruction must not leave native readers alive.
    if (fu_publish(session, 0, 960, 540) != -1) return 4;
    fu_destroy(session);
    std::cout << "C++ bridge ABI and disconnected lifecycle passed\n";
}
