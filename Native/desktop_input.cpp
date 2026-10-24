#include "fireuser_livekit.h"
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
#ifdef _WIN32
int key_code(int code) {
    if (code >= 4 && code <= 29) return 'A' + code - 4;
    if (code >= 30 && code <= 38) return '1' + code - 30;
    if (code == 39) return '0';
    if (code >= 58 && code <= 69) return VK_F1 + code - 58;
    switch (code) {
    case 40: return VK_RETURN; case 41: return VK_ESCAPE; case 42: return VK_BACK;
    case 43: return VK_TAB; case 44: return VK_SPACE; case 45: return VK_OEM_MINUS;
    case 46: return VK_OEM_PLUS; case 47: return VK_OEM_4; case 48: return VK_OEM_6;
    case 49: return VK_OEM_5; case 51: return VK_OEM_1; case 52: return VK_OEM_7;
    case 53: return VK_OEM_3; case 54: return VK_OEM_COMMA; case 55: return VK_OEM_PERIOD;
    case 56: return VK_OEM_2; case 76: return VK_DELETE; case 79: return VK_RIGHT;
    case 80: return VK_LEFT; case 81: return VK_DOWN; case 82: return VK_UP;
    case 224: return VK_LCONTROL; case 225: return VK_LSHIFT; case 226: return VK_LMENU;
    case 227: return VK_LWIN; case 228: return VK_RCONTROL; case 229: return VK_RSHIFT;
    case 230: return VK_RMENU; case 231: return VK_RWIN; default: return 0;
    }
}
bool key(int code, bool down) {
    const int mapped = key_code(code); if (!mapped) return false;
    INPUT input{}; input.type = INPUT_KEYBOARD; input.ki.wVk = static_cast<WORD>(mapped);
    input.ki.dwFlags = down ? 0 : KEYEVENTF_KEYUP;
    if (code == 76 || (code >= 79 && code <= 82) || code == 228 || code == 230) input.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
    return SendInput(1, &input, sizeof(input)) == 1;
}
bool button(int code, bool down) {
    INPUT input{}; input.type = INPUT_MOUSE;
    if (code == 1) input.mi.dwFlags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
    else if (code == 2) input.mi.dwFlags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
    else if (code == 3) input.mi.dwFlags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
    else return false;
    return SendInput(1, &input, sizeof(input)) == 1;
}
#elif defined(__APPLE__)
int key_code(int code) {
    static const int letters[] = {0,11,8,2,14,3,5,4,34,38,40,37,46,45,31,35,12,15,1,17,32,9,13,7,16,6};
    static const int digits[] = {18,19,20,21,23,22,26,28,25,29};
    if (code >= 4 && code <= 29) return letters[code - 4];
    if (code >= 30 && code <= 39) return digits[code - 30];
    switch(code) { case 40:return 36; case 41:return 53; case 42:return 51; case 43:return 48;
    case 44:return 49; case 45:return 27; case 46:return 24; case 47:return 33; case 48:return 30;
    case 49:return 42; case 51:return 41; case 52:return 39; case 53:return 50; case 54:return 43;
    case 55:return 47; case 56:return 44; case 76:return 117; case 79:return 124; case 80:return 123;
    case 81:return 125; case 82:return 126; case 224:return 59; case 225:return 56; case 226:return 58;
    case 227:return 55; case 228:return 62; case 229:return 60; case 230:return 61; case 231:return 54;
    default:return -1; }
}
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
    CGEventSetFlags(event,flags);CGEventPost(kCGHIDEventTap, event); CFRelease(event); return true;
}
bool button(int code, bool down) {
    auto current = CGEventCreate(nullptr); if(!current) return false;
    auto point = CGEventGetLocation(current); CFRelease(current);
    auto b = code == 1 ? kCGMouseButtonLeft : code == 2 ? kCGMouseButtonRight : kCGMouseButtonCenter;
    auto type = code == 1 ? (down ? kCGEventLeftMouseDown : kCGEventLeftMouseUp) : code == 2 ? (down ? kCGEventRightMouseDown : kCGEventRightMouseUp) : (down ? kCGEventOtherMouseDown : kCGEventOtherMouseUp);
    auto event = CGEventCreateMouseEvent(nullptr, type, point, b); if(!event) return false;
    CGEventPost(kCGHIDEventTap, event); CFRelease(event); return true;
}
#else
Display* display() { static Display* value = [] { XInitThreads(); return XOpenDisplay(nullptr); }(); return value; }
KeySym key_code(int code) {
    if(code >= 4 && code <= 29) return XK_a + code - 4;
    if(code >= 30 && code <= 38) return XK_1 + code - 30;
    if(code == 39) return XK_0;
    if(code >= 58 && code <= 69) return XK_F1 + code - 58;
    switch(code) { case 40:return XK_Return; case 41:return XK_Escape; case 42:return XK_BackSpace; case 43:return XK_Tab;
    case 44:return XK_space; case 45:return XK_minus; case 46:return XK_equal; case 47:return XK_bracketleft; case 48:return XK_bracketright;
    case 49:return XK_backslash; case 51:return XK_semicolon; case 52:return XK_apostrophe; case 53:return XK_grave; case 54:return XK_comma;
    case 55:return XK_period; case 56:return XK_slash; case 76:return XK_Delete; case 79:return XK_Right; case 80:return XK_Left;
    case 81:return XK_Down; case 82:return XK_Up; case 224:return XK_Control_L; case 225:return XK_Shift_L; case 226:return XK_Alt_L;
    case 227:return XK_Super_L; case 228:return XK_Control_R; case 229:return XK_Shift_R; case 230:return XK_Alt_R; case 231:return XK_Super_R; default:return 0; }
}
bool key(int code, bool down) { auto d=display(); auto symbol=key_code(code); if(!d || !symbol) return false; auto k=XKeysymToKeycode(d,symbol); if(!k)return false; XTestFakeKeyEvent(d,k,down,CurrentTime); XFlush(d); return true; }
bool button(int code, bool down) { auto d=display(); if(!d || code<1 || code>3)return false; XTestFakeButtonEvent(d, code == 2 ? 3 : code == 3 ? 2 : 1, down,CurrentTime); XFlush(d); return true; }
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
    if(!fu_input_supported() || !std::isfinite(x) || !std::isfinite(y) || x<0 || x>1 || y<0 || y>1 || down<0 || down>1)return -1;
    if(kind == 2) {
        if(!down && !held_keys.contains(code)) return 0;
        if(!key(code,down!=0))return -1; if(down) held_keys.insert(code); else held_keys.erase(code); return 0;
    }
    if(kind == 1) {
        if(code<1 || code>3)return -1;
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
    held_keys.clear();held_buttons.clear();
}
