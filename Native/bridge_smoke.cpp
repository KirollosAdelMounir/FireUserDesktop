#include "fireuser_livekit.h"
#include <iostream>

int main() {
    if (fu_abi_version() != 3) return 1;
    if (fu_connect(nullptr, "", "") != -1 || fu_push(nullptr, 0, nullptr, 0) != -1) return 2;
    auto session = fu_create(nullptr, nullptr, nullptr, nullptr);
    if (!session) return 3;
    // Unconnected publication is rejected; destruction must not leave native readers alive.
    if (fu_publish(session, 0, 960, 540) != -1) return 4;
    fu_destroy(session);
    std::cout << "C++ bridge ABI and disconnected lifecycle passed\n";
}
