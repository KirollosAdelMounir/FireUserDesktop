#include "screen_quality.h"
#include <iostream>
int main() {
    for (double bandwidth : {-1., 400000., 1000000., 2000000., 8000000.}) {
        for (bool congested : {false,true}) {
            const auto base=fireuser::screen_quality(bandwidth,congested);
            const auto idle=fireuser::control_quality(base,false);
            const auto active=fireuser::control_quality(base,true);
            if(active.width!=base.width||active.height!=base.height||active.bitrate!=base.bitrate)return 1;
#ifdef __linux__
            if(active.fps!=idle.fps||active.fps>30||active.fps>base.fps)return 2;
#else
            if(idle!=base||active.fps!=(base.height>=720?60:base.fps))return 3;
#endif
            if(congested&&(active.height!=480||active.fps!=15))return 4;
        }
    }
    std::cout<<"Screen grant preserves Linux frame budget and congestion adaptation; other platforms retain interactive quality\n";
}
