#include "keyboard_codes.h"
#include <cstdlib>
static void require(bool value){if(!value)std::abort();}
int main(){
    require(fireuser::evdev_key(41)==1);
    require(fireuser::evdev_key(74)==102);
    require(fireuser::evdev_key(77)==107);
    require(fireuser::evdev_key(88)==96);
    require(fireuser::evdev_key(104)==183);
    require(fireuser::evdev_key(115)==194);
    require(!fireuser::keyboard_usage(102));
    require(!fireuser::keyboard_usage(116));
    require(!fireuser::keyboard_usage(223));
    for(int code=4;code<=231;++code)if(fireuser::keyboard_usage(code))require(fireuser::evdev_key(code)>=0);
    require(fireuser::platform_key(4)>=0); // macOS A is keycode zero.
    require(fireuser::platform_key(41)>=0);
    require(fireuser::platform_key(74)>=0);
    require(fireuser::platform_key(88)>=0);
    require(fireuser::platform_key(231)>=0);
    require(fireuser::platform_key(102)<0);
}
