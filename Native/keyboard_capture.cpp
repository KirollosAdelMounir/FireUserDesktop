#include "fireuser_livekit.h"
#include "keyboard_codes.h"
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <memory>
#include <thread>
#ifdef __APPLE__
#import <AppKit/AppKit.h>
#include <ApplicationServices/ApplicationServices.h>
#include <unistd.h>
#elif !defined(_WIN32)
#include <xcb/xcb.h>
#include <xcb/xproto.h>
#endif
namespace {
std::atomic_bool keyboardCaptured{false};
using namespace std::chrono_literals;
int64_t ticks(){return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();}
struct Capture {
    uintptr_t window;
    fu_keyboard_callback callback;
    std::atomic_bool stopping{false};
    std::atomic<int64_t> expires{0},epoch{0};
    int64_t activeEpoch=0;
    std::atomic<int> status{0};
    std::thread worker;
    bool live()const{return !stopping&&expires.load()>ticks();}
    void release()noexcept{try{callback(activeEpoch,-1,0);}catch(...){}}
    void key(int code,bool down)noexcept{if(live()&&activeEpoch==epoch.load())try{callback(activeEpoch,code,down?1:0);}catch(...){}}
#ifdef __APPLE__
    uint32_t number;
    AXUIElementRef system=nullptr;
    bool foreground(){
        CFTypeRef front=nullptr;
        if(AXUIElementCopyAttributeValue(system,kAXFocusedApplicationAttribute,&front)!=kAXErrorSuccess||!front)return false;
        pid_t pid=0;const auto result=AXUIElementGetPid(static_cast<AXUIElementRef>(const_cast<void*>(front)),&pid);CFRelease(front);
        if(result!=kAXErrorSuccess||pid!=getpid())return false;
        auto windows=CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly|kCGWindowListExcludeDesktopElements,kCGNullWindowID);
        bool matches=false;
        if(windows){
            for(CFIndex i=0;i<CFArrayGetCount(windows);++i){
                auto info=static_cast<CFDictionaryRef>(CFArrayGetValueAtIndex(windows,i));int layer=-1;uint32_t id=0;
                auto value=static_cast<CFNumberRef>(CFDictionaryGetValue(info,kCGWindowLayer));if(value)CFNumberGetValue(value,kCFNumberIntType,&layer);
                if(layer!=0)continue;
                value=static_cast<CFNumberRef>(CFDictionaryGetValue(info,kCGWindowNumber));if(value)CFNumberGetValue(value,kCFNumberSInt32Type,&id);
                matches=id==number;break;
            }
            CFRelease(windows);
        }
        return matches;
    }
    static CGEventRef event(CGEventTapProxy,CGEventType type,CGEventRef event,void* context){
        auto& capture=*static_cast<Capture*>(context);
        if(type==kCGEventTapDisabledByTimeout||type==kCGEventTapDisabledByUserInput){capture.expires=0;capture.status=-3;capture.release();return event;}
        if(!capture.live()||!capture.foreground())return event;
        if(CGEventGetIntegerValueField(event,kCGEventSourceUnixProcessID)==getpid()||CGEventGetIntegerValueField(event,kCGEventSourceUserData)==0x4655494e)return nullptr;
        const auto native=static_cast<int>(CGEventGetIntegerValueField(event,kCGKeyboardEventKeycode));int code=0;
        for(int hid=4;hid<=231;++hid)if(fireuser::keyboard_usage(hid)&&fireuser::platform_key(hid)==native){code=hid;break;}
        if(code){
            const bool down=type==kCGEventFlagsChanged?CGEventSourceKeyState(kCGEventSourceStateHIDSystemState,static_cast<CGKeyCode>(native)):type==kCGEventKeyDown;
            capture.key(code,down);
        }
        return nullptr; // Unknown keys must not leak into the local focused application.
    }
    void run(){
        system=AXUIElementCreateSystemWide();AXUIElementSetMessagingTimeout(system,.02f);
        CFMachPortRef tap=nullptr;CFRunLoopSourceRef source=nullptr;bool active=false,prompted=false;
        while(!stopping){
            if(live()&&!tap){
                if(!AXIsProcessTrusted()){
                    if(!prompted){const void* keys[]={kAXTrustedCheckOptionPrompt};const void* values[]={kCFBooleanTrue};auto options=CFDictionaryCreate(nullptr,keys,values,1,&kCFTypeDictionaryKeyCallBacks,&kCFTypeDictionaryValueCallBacks);AXIsProcessTrustedWithOptions(options);CFRelease(options);prompted=true;}
                    status=-2;std::this_thread::sleep_for(50ms);continue;
                }
                const auto mask=CGEventMaskBit(kCGEventKeyDown)|CGEventMaskBit(kCGEventKeyUp)|CGEventMaskBit(kCGEventFlagsChanged);
                tap=CGEventTapCreate(kCGSessionEventTap,kCGHeadInsertEventTap,kCGEventTapOptionDefault,mask,event,this);
                if(!tap){status=-2;std::this_thread::sleep_for(50ms);continue;}
                CGEventTapEnable(tap,false);
                source=CFMachPortCreateRunLoopSource(nullptr,tap,0);CFRunLoopAddSource(CFRunLoopGetCurrent(),source,kCFRunLoopDefaultMode);
            }
            if(active&&activeEpoch!=epoch.load()){CGEventTapEnable(tap,false);release();active=false;}
            const bool next=live()&&status.load()!=-3;
            if(tap&&next!=active){CGEventTapEnable(tap,next);if(!next){release();if(expires.load()!=0||status.load()==-3)status=-3;else status=0;}else {activeEpoch=epoch.load();status=1;}active=next;}
            if(tap)CFRunLoopRunInMode(kCFRunLoopDefaultMode,.005,true);else std::this_thread::sleep_for(5ms);
        }
        if(tap){CGEventTapEnable(tap,false);CFRunLoopRemoveSource(CFRunLoopGetCurrent(),source,kCFRunLoopDefaultMode);CFRelease(source);CFRelease(tap);}
        release();CFRelease(system);system=nullptr;
    }
#elif !defined(_WIN32)
    void run(){
        int index=0;auto connection=xcb_connect(nullptr,&index);
        if(!connection||xcb_connection_has_error(connection)){status=-1;if(connection)xcb_disconnect(connection);return;}
        auto screens=xcb_setup_roots_iterator(xcb_get_setup(connection));while(index-->0&&screens.rem)xcb_screen_next(&screens);
        if(!screens.rem){status=-1;xcb_disconnect(connection);return;}
        const auto root=screens.data->root;
        auto atom=[connection](const char* name,uint16_t length){auto reply=xcb_intern_atom_reply(connection,xcb_intern_atom(connection,0,length,name),nullptr);auto result=reply?reply->atom:0;if(reply)std::free(reply);return result;};
        const auto active_atom=atom("_NET_ACTIVE_WINDOW",18),may_grab=atom("_XWAYLAND_MAY_GRAB_KEYBOARD",27);
        auto request=[&](bool enabled){xcb_client_message_event_t message{};message.response_type=XCB_CLIENT_MESSAGE;message.format=32;message.window=static_cast<xcb_window_t>(window);message.type=may_grab;message.data.data32[0]=enabled?1:0;xcb_send_event(connection,0,root,XCB_EVENT_MASK_SUBSTRUCTURE_REDIRECT|XCB_EVENT_MASK_SUBSTRUCTURE_NOTIFY,reinterpret_cast<const char*>(&message));xcb_flush(connection);};
        auto foreground=[&]{
            auto property=xcb_get_property_reply(connection,xcb_get_property(connection,0,root,active_atom,XCB_ATOM_WINDOW,0,1),nullptr);
            xcb_window_t active_window=0;
            if(property&&xcb_get_property_value_length(property)==4)active_window=*static_cast<xcb_window_t*>(xcb_get_property_value(property));
            if(property)std::free(property);
            if(active_window)return active_window==window;
            auto focus=xcb_get_input_focus_reply(connection,xcb_get_input_focus(connection),nullptr);
            const bool matches=focus&&focus->focus==window;if(focus)std::free(focus);return matches;
        };
        bool grabbed=false;int64_t checked=0;
        while(!stopping&&!xcb_connection_has_error(connection)){
            const bool wanted=live();
            if(grabbed&&activeEpoch!=epoch.load()){keyboardCaptured=false;xcb_ungrab_keyboard(connection,XCB_CURRENT_TIME);request(false);grabbed=false;release();}
            if(wanted&&!grabbed&&status.load()!=-3){
                if(foreground()){
                    request(true);
                    auto reply=xcb_grab_keyboard_reply(connection,xcb_grab_keyboard(connection,0,static_cast<xcb_window_t>(window),XCB_CURRENT_TIME,XCB_GRAB_MODE_ASYNC,XCB_GRAB_MODE_ASYNC),nullptr);
                    grabbed=reply&&reply->status==XCB_GRAB_STATUS_SUCCESS;if(reply)std::free(reply);
                    if(grabbed){activeEpoch=epoch.load();keyboardCaptured=true;}
                    status=grabbed?(std::getenv("WAYLAND_DISPLAY")?2:1):-1;checked=ticks();
                }
            }
            bool focused=true;if(grabbed&&ticks()-checked>=25){focused=foreground();checked=ticks();}
            if(grabbed&&(!wanted||!focused)){
                keyboardCaptured=false;xcb_ungrab_keyboard(connection,XCB_CURRENT_TIME);request(false);grabbed=false;release();
                if(expires.load()!=0){expires=0;status=-3;}else status=0;
            }
            while(auto event=xcb_poll_for_event(connection)){
                const auto type=event->response_type&0x7f;
                if(grabbed&&live()&&(type==XCB_KEY_PRESS||type==XCB_KEY_RELEASE)&&foreground()){
                    const int evdev=reinterpret_cast<xcb_key_press_event_t*>(event)->detail-8;
                    for(int hid=4;hid<=231;++hid)if(fireuser::keyboard_usage(hid)&&fireuser::evdev_key(hid)==evdev){key(hid,type==XCB_KEY_PRESS);break;}
                }
                std::free(event);
            }
            std::this_thread::sleep_for(2ms);
        }
        if(grabbed){keyboardCaptured=false;xcb_ungrab_keyboard(connection,XCB_CURRENT_TIME);request(false);}release();xcb_disconnect(connection);
        if(!stopping)status=-1;
    }
#else
    void run(){}
#endif
    Capture(uintptr_t handle,fu_keyboard_callback cb):window(handle),callback(cb){
#ifdef __APPLE__
        // Called from Avalonia's UI thread. Worker callbacks never access AppKit windows.
        number=static_cast<uint32_t>(((__bridge NSWindow*)reinterpret_cast<void*>(handle)).windowNumber);
#endif
        worker=std::thread([this]{try{run();}catch(...){status=-1;release();}});
    }
    ~Capture(){stopping=true;expires=0;if(worker.joinable())worker.join();}
};
}
namespace fireuser {bool keyboard_capture_active(){return keyboardCaptured.load();}}
void* fu_keyboard_capture_open(uintptr_t window,fu_keyboard_callback callback){
#ifdef _WIN32
    (void)window;(void)callback;return nullptr; // Windows retains the managed low-level hook.
#else
    if(!window||!callback)return nullptr;
    try{return new Capture(window,callback);}catch(...){return nullptr;}
#endif
}
int fu_keyboard_capture_renew(void* handle,int enabled,int64_t epoch){if(!handle)return -1;auto& capture=*static_cast<Capture*>(handle);if(!enabled){capture.expires=0;capture.epoch=0;capture.status=0;}else if(capture.status.load()!=-3){capture.epoch=epoch;capture.expires=ticks()+750;}return capture.status.load();}
void fu_keyboard_capture_close(void* handle){if(handle)delete static_cast<Capture*>(handle);}
