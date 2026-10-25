#include "fireuser_livekit.h"
#include "keyboard_codes.h"
#include <cmath>
#include <cstdlib>
#include <mutex>
#include <set>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#elif defined(__APPLE__)
#include <ApplicationServices/ApplicationServices.h>
#else
#include <X11/Xlib.h>
#include <X11/keysym.h>
#include <X11/extensions/XTest.h>
#endif

namespace {
std::recursive_mutex input_mutex;
std::set<int> held_keys, held_buttons;
double horizontal=0,vertical=0;
#ifdef _WIN32
int key_code(int code) {return fireuser::platform_key(code);}
bool key(int code, bool down) {
    const int mapped = key_code(code); if (mapped<0) return false;
    INPUT input{}; input.type = INPUT_KEYBOARD; input.ki.wVk = static_cast<WORD>(mapped);
    input.ki.dwFlags = down ? 0 : KEYEVENTF_KEYUP;
    if (code==70 || (code >= 73 && code <= 84) || code==88 || code==101 || (code >= 79 && code <= 82) || code == 227 || code == 228 || code == 230 || code == 231) input.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
    return SendInput(1, &input, sizeof(input)) == 1;
}
bool button(int code, bool down) {
    INPUT input{}; input.type = INPUT_MOUSE;
    if (code == 1) input.mi.dwFlags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
    else if (code == 2) input.mi.dwFlags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
    else if (code == 3) input.mi.dwFlags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
    else if(code==4||code==5){input.mi.dwFlags=down?MOUSEEVENTF_XDOWN:MOUSEEVENTF_XUP;input.mi.mouseData=code==4?XBUTTON1:XBUTTON2;}
    else return false;
    return SendInput(1, &input, sizeof(input)) == 1;
}
#elif defined(__APPLE__)
int key_code(int code) {return fireuser::platform_key(code);}
bool key(int code, bool down) {
    int mapped = key_code(code); if(mapped < 0) return false;
    auto event = CGEventCreateKeyboardEvent(nullptr, static_cast<CGKeyCode>(mapped), down);
    if (!event) return false;
    auto active = [code,down](int modifier) { return modifier == code ? down : held_keys.contains(modifier); };
    CGEventFlags flags = 0;
    if(active(225)||active(229))flags |= kCGEventFlagMaskShift;
    if(active(224)||active(228))flags |= kCGEventFlagMaskControl;
    if(active(226)||active(230))flags |= kCGEventFlagMaskAlternate;
    if(active(227)||active(231))flags |= kCGEventFlagMaskCommand;
    CGEventSetIntegerValueField(event,kCGEventSourceUserData,0x4655494e);CGEventSetFlags(event,flags);CGEventPost(kCGHIDEventTap, event); CFRelease(event); return true;
}
bool button(int code, bool down) {
    auto current = CGEventCreate(nullptr); if(!current) return false;
    auto point = CGEventGetLocation(current); CFRelease(current);
    auto b = code == 1 ? kCGMouseButtonLeft : code == 2 ? kCGMouseButtonRight : code==3?kCGMouseButtonCenter:static_cast<CGMouseButton>(code-1);
    auto type = code == 1 ? (down ? kCGEventLeftMouseDown : kCGEventLeftMouseUp) : code == 2 ? (down ? kCGEventRightMouseDown : kCGEventRightMouseUp) : (down ? kCGEventOtherMouseDown : kCGEventOtherMouseUp);
    auto event = CGEventCreateMouseEvent(nullptr, type, point, b); if(!event) return false;
    CGEventPost(kCGHIDEventTap, event); CFRelease(event); return true;
}
#else
Display* display() { static Display* value = [] { XInitThreads(); return XOpenDisplay(nullptr); }(); return value; }
KeySym key_code(int code) {auto value=fireuser::platform_key(code);return value<0?0:static_cast<KeySym>(value);}
bool key(int code, bool down) { auto d=display(); auto symbol=key_code(code); if(!d || !symbol) return false; auto k=XKeysymToKeycode(d,symbol); if(!k)return false; XTestFakeKeyEvent(d,k,down,CurrentTime); XFlush(d); return true; }
bool button(int code, bool down) { auto d=display(); if(!d || code<1 || code>5)return false; XTestFakeButtonEvent(d, code == 2 ? 3 : code == 3 ? 2 : code==4?8:code==5?9:1, down,CurrentTime); XFlush(d); return true; }
#endif
}
int fu_desktop_bounds(int* width, int* height) {
    std::lock_guard lock(input_mutex);
    if(!width || !height)return -1;
#ifdef _WIN32
    *width=GetSystemMetrics(SM_CXSCREEN); *height=GetSystemMetrics(SM_CYSCREEN);
#elif defined(__APPLE__)
    auto bounds=CGDisplayBounds(CGMainDisplayID()); *width=static_cast<int>(bounds.size.width); *height=static_cast<int>(bounds.size.height);
#else
    if(!display())return -1; *width=DisplayWidth(display(),DefaultScreen(display())); *height=DisplayHeight(display(),DefaultScreen(display()));
#endif
    return *width>0 && *height>0 ? 0 : -1;
}
int fu_input_supported() {
    std::lock_guard lock(input_mutex);
#ifdef _WIN32
    return 1;
#elif defined(__APPLE__)
    return AXIsProcessTrusted() ? 1 : 0;
#else
    if(std::getenv("WAYLAND_DISPLAY") || !display())return 0;
    int event, error, major, minor; return XTestQueryExtension(display(),&event,&error,&major,&minor) ? 1 : 0;
#endif
}
int fu_apply_input(int kind, double x, double y, int code, int down) {
    std::lock_guard lock(input_mutex);
#if !defined(_WIN32) && !defined(__APPLE__)
    if(fireuser::keyboard_capture_active())return -1;
#endif
    if(!std::isfinite(x)||!std::isfinite(y)||down<0||down>1)return -1;
    if(kind==6){
        if(std::abs(x)>10||std::abs(y)>10||code!=0||down!=0||!fu_input_supported())return -1;
#ifdef _WIN32
        INPUT wheel[2]{};UINT count=0;
        if(y!=0){wheel[count].type=INPUT_MOUSE;wheel[count].mi.dwFlags=MOUSEEVENTF_WHEEL;wheel[count++].mi.mouseData=static_cast<DWORD>(static_cast<LONG>(y*120));}
        if(x!=0){wheel[count].type=INPUT_MOUSE;wheel[count].mi.dwFlags=MOUSEEVENTF_HWHEEL;wheel[count++].mi.mouseData=static_cast<DWORD>(static_cast<LONG>(-x*120));}
        return count==0||SendInput(count,wheel,sizeof(INPUT))==count?0:-1;
#elif defined(__APPLE__)
        auto event=CGEventCreateScrollWheelEvent(nullptr,kCGScrollEventUnitPixel,2,static_cast<int>(y*40),static_cast<int>(x*40));
        if(!event)return -1;
        CGEventFlags flags=0;
        if(held_keys.contains(225)||held_keys.contains(229))flags|=kCGEventFlagMaskShift;
        if(held_keys.contains(224)||held_keys.contains(228))flags|=kCGEventFlagMaskControl;
        if(held_keys.contains(226)||held_keys.contains(230))flags|=kCGEventFlagMaskAlternate;
        if(held_keys.contains(227)||held_keys.contains(231))flags|=kCGEventFlagMaskCommand;
        CGEventSetIntegerValueField(event,kCGEventSourceUserData,0x4655494e);CGEventSetFlags(event,flags);CGEventPost(kCGHIDEventTap,event);CFRelease(event);return 0;
#else
        // Preserve touchpad fractions between packets instead of dropping sub-notch deltas.
        horizontal+=x;vertical+=y;
        while(std::abs(vertical)>=1){const int b=vertical>0?4:5;XTestFakeButtonEvent(display(),b,True,CurrentTime);XTestFakeButtonEvent(display(),b,False,CurrentTime);vertical+=vertical>0?-1:1;}
        while(std::abs(horizontal)>=1){const int b=horizontal>0?6:7;XTestFakeButtonEvent(display(),b,True,CurrentTime);XTestFakeButtonEvent(display(),b,False,CurrentTime);horizontal+=horizontal>0?-1:1;}
        XFlush(display());return 0;
#endif
    }
    if(x<0||x>1||y<0||y>1||!fu_input_supported())return -1;
    if(kind == 2) {
        if(!fireuser::keyboard_usage(code))return -1;
        if(fireuser::platform_key(code)<0)return 0;
        if(!down && !held_keys.contains(code)) return 0;
        if(!key(code,down!=0))return -1; if(down) held_keys.insert(code); else held_keys.erase(code); return 0;
    }
    if(kind == 1) {
        if(code<1 || code>5)return -1;
        if(!down && !held_buttons.contains(code))return 0;
        if(!button(code,down!=0))return -1; if(down)held_buttons.insert(code);else held_buttons.erase(code);return 0;
    }
    int width,height; if(fu_desktop_bounds(&width,&height)!=0)return -1;
    if(kind == 0) {
#ifdef _WIN32
        INPUT input{}; input.type=INPUT_MOUSE; input.mi.dx=static_cast<LONG>(x*65535); input.mi.dy=static_cast<LONG>(y*65535);
        input.mi.dwFlags=MOUSEEVENTF_MOVE|MOUSEEVENTF_ABSOLUTE; return SendInput(1,&input,sizeof(input))==1 ? 0 : -1;
#elif defined(__APPLE__)
        auto type=held_buttons.contains(1) ? kCGEventLeftMouseDragged : held_buttons.contains(2) ? kCGEventRightMouseDragged : kCGEventMouseMoved;
        auto event=CGEventCreateMouseEvent(nullptr,type,CGPointMake(x*(width-1),y*(height-1)),held_buttons.contains(2) ? kCGMouseButtonRight : kCGMouseButtonLeft);
        if(!event)return -1; CGEventPost(kCGHIDEventTap,event);CFRelease(event);return 0;
#else
        XTestFakeMotionEvent(display(),DefaultScreen(display()),static_cast<int>(x*(width-1)),static_cast<int>(y*(height-1)),CurrentTime); XFlush(display());return 0;
#endif
    }
    return -1;
}
void fu_release_input() {
    std::lock_guard lock(input_mutex);
    for(int code:held_keys) key(code,false); for(int code:held_buttons) button(code,false);
    held_keys.clear();held_buttons.clear();horizontal=vertical=0;
}
