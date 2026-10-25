#include "fireuser_livekit.h"
#include <X11/Xlib.h>
#include <X11/extensions/XTest.h>
#include <chrono>
#include <thread>
#include <vector>
#include <mutex>
#include <tuple>
#include <cstdlib>
using namespace std::chrono_literals;
namespace {
std::mutex gate;
std::vector<std::tuple<int64_t,int,int>> events;
void input(int64_t epoch,int key,int down){std::lock_guard lock(gate);events.emplace_back(epoch,key,down);}
void require(bool value){if(!value)std::abort();}
bool contains(int64_t epoch,int code,int down){std::lock_guard lock(gate);for(auto e:events)if(e==std::tuple(epoch,code,down))return true;return false;}
template<class Predicate>void until(Predicate predicate){for(int i=0;i<400;++i){if(predicate())return;std::this_thread::sleep_for(5ms);}require(false);}
}
int main(){
    // Only the CTest wrapper's private Xvfb may run input tests.
    require(std::getenv("FIREUSER_PRIVATE_INPUT_TEST")!=nullptr);
    XInitThreads();auto display=XOpenDisplay(nullptr);require(display!=nullptr);
    auto window=XCreateSimpleWindow(display,DefaultRootWindow(display),0,0,200,200,0,0,0);
    XMapWindow(display,window);XSetInputFocus(display,window,RevertToParent,CurrentTime);XSync(display,False);
    auto capture=fu_keyboard_capture_open(window,input);require(capture!=nullptr);
    until([&]{return fu_keyboard_capture_renew(capture,1,7)==1;});
    XTestFakeKeyEvent(display,38,True,CurrentTime);XTestFakeKeyEvent(display,38,False,CurrentTime);XFlush(display);
    until([]{return contains(7,4,1)&&contains(7,4,0);});
    // Stop renewing: the UI cannot leave a keyboard grab behind when stalled.
    until([]{return contains(7,-1,0);});
    require(fu_keyboard_capture_renew(capture,1,7)==-3);
    require(XGrabKeyboard(display,window,False,GrabModeAsync,GrabModeAsync,CurrentTime)==GrabSuccess);
    XUngrabKeyboard(display,CurrentTime);XSync(display,False);
    fu_keyboard_capture_renew(capture,0,0);
    until([&]{return fu_keyboard_capture_renew(capture,1,8)==1;});
    XTestFakeKeyEvent(display,38,True,CurrentTime);XTestFakeKeyEvent(display,38,False,CurrentTime);XFlush(display);
    until([]{return contains(8,4,1)&&contains(8,4,0);});
    // Focus departure releases the grab even before the managed timer observes it.
    XSetInputFocus(display,DefaultRootWindow(display),RevertToParent,CurrentTime);XFlush(display);
    until([]{return contains(8,-1,0);});
    fu_keyboard_capture_close(capture);
    XDestroyWindow(display,window);XCloseDisplay(display);
}
