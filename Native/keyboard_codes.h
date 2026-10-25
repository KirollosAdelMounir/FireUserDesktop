#pragma once
#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#elif !defined(__APPLE__)
#include <X11/keysym.h>
#endif
namespace fireuser {
// Prevent XTest input from feeding back through our own active keyboard grab.
bool keyboard_capture_active();
inline bool keyboard_usage(int code){return (code>=4&&code<=101)||(code>=103&&code<=115)||(code>=224&&code<=231);}
inline int evdev_key(int code){
    static constexpr int letters[]={30,48,46,32,18,33,34,35,23,36,37,38,50,49,24,25,16,19,31,20,22,47,17,45,21,44};
    static constexpr int keypad[]={79,80,81,75,76,77,71,72,73};
    if(code>=4&&code<=29)return letters[code-4];
    if(code>=30&&code<=39)return code-28;
    if(code>=58&&code<=67)return code+1;
    if(code>=89&&code<=97)return keypad[code-89];
    if(code>=104&&code<=115)return code+79;
    switch(code){case 40:return 28;case 41:return 1;case 42:return 14;case 43:return 15;case 44:return 57;case 45:return 12;case 46:return 13;case 47:return 26;case 48:return 27;case 49:case 50:return 43;case 51:return 39;case 52:return 40;case 53:return 41;case 54:return 51;case 55:return 52;case 56:return 53;case 57:return 58;case 68:return 87;case 69:return 88;case 70:return 99;case 71:return 70;case 72:return 119;case 73:return 110;case 74:return 102;case 75:return 104;case 76:return 111;case 77:return 107;case 78:return 109;case 79:return 106;case 80:return 105;case 81:return 108;case 82:return 103;case 83:return 69;case 84:return 98;case 85:return 55;case 86:return 74;case 87:return 78;case 88:return 96;case 98:return 82;case 99:return 83;case 100:return 86;case 101:return 127;case 103:return 117;case 224:return 29;case 225:return 42;case 226:return 56;case 227:return 125;case 228:return 97;case 229:return 54;case 230:return 100;case 231:return 126;default:return -1;}
}
inline int platform_key(int code){
#ifdef _WIN32
    if(code>=4&&code<=29)return 'A'+code-4;
    if(code>=30&&code<=38)return '1'+code-30;
    if(code>=58&&code<=69)return VK_F1+code-58;
    if(code>=104&&code<=115)return VK_F13+code-104;
    if(code>=89&&code<=97)return VK_NUMPAD1+code-89;
    switch(code){case 39:return '0';case 40:case 88:return VK_RETURN;case 41:return VK_ESCAPE;case 42:return VK_BACK;case 43:return VK_TAB;case 44:return VK_SPACE;case 45:return VK_OEM_MINUS;case 46:case 103:return VK_OEM_PLUS;case 47:return VK_OEM_4;case 48:return VK_OEM_6;case 49:case 50:return VK_OEM_5;case 51:return VK_OEM_1;case 52:return VK_OEM_7;case 53:return VK_OEM_3;case 54:return VK_OEM_COMMA;case 55:return VK_OEM_PERIOD;case 56:return VK_OEM_2;case 57:return VK_CAPITAL;case 70:return VK_SNAPSHOT;case 71:return VK_SCROLL;case 72:return VK_PAUSE;case 73:return VK_INSERT;case 74:return VK_HOME;case 75:return VK_PRIOR;case 76:return VK_DELETE;case 77:return VK_END;case 78:return VK_NEXT;case 79:return VK_RIGHT;case 80:return VK_LEFT;case 81:return VK_DOWN;case 82:return VK_UP;case 83:return VK_NUMLOCK;case 84:return VK_DIVIDE;case 85:return VK_MULTIPLY;case 86:return VK_SUBTRACT;case 87:return VK_ADD;case 98:return VK_NUMPAD0;case 99:return VK_DECIMAL;case 100:return VK_OEM_102;case 101:return VK_APPS;case 224:return VK_LCONTROL;case 225:return VK_LSHIFT;case 226:return VK_LMENU;case 227:return VK_LWIN;case 228:return VK_RCONTROL;case 229:return VK_RSHIFT;case 230:return VK_RMENU;case 231:return VK_RWIN;default:return -1;}
#elif defined(__APPLE__)
    static constexpr int letters[]={0,11,8,2,14,3,5,4,34,38,40,37,46,45,31,35,12,15,1,17,32,9,13,7,16,6};
    static constexpr int digits[]={18,19,20,21,23,22,26,28,25,29};
    static constexpr int functions[]={122,120,99,118,96,97,98,100,101,109,103,111};
    static constexpr int keypad[]={83,84,85,86,87,88,89,91,92};
    static constexpr int extra_functions[]={105,107,113,106,64,79,80,90};
    if(code>=4&&code<=29)return letters[code-4];
    if(code>=30&&code<=39)return digits[code-30];
    if(code>=58&&code<=69)return functions[code-58];
    if(code>=89&&code<=97)return keypad[code-89];
    if(code>=104&&code<=111)return extra_functions[code-104];
    switch(code){case 40:return 36;case 41:return 53;case 42:return 51;case 43:return 48;case 44:return 49;case 45:return 27;case 46:return 24;case 47:return 33;case 48:return 30;case 49:case 50:return 42;case 51:return 41;case 52:return 39;case 53:return 50;case 54:return 43;case 55:return 47;case 56:return 44;case 57:return 57;case 73:return 114;case 74:return 115;case 75:return 116;case 76:return 117;case 77:return 119;case 78:return 121;case 79:return 124;case 80:return 123;case 81:return 125;case 82:return 126;case 83:return 71;case 84:return 75;case 85:return 67;case 86:return 78;case 87:return 69;case 88:return 76;case 98:return 82;case 99:return 65;case 100:return 10;case 101:return 110;case 103:return 81;case 224:return 59;case 225:return 56;case 226:return 58;case 227:return 55;case 228:return 62;case 229:return 60;case 230:return 61;case 231:return 54;default:return -1;}
#else
    if(code>=4&&code<=29)return XK_a+code-4;
    if(code>=30&&code<=38)return XK_1+code-30;
    if(code>=58&&code<=69)return XK_F1+code-58;
    if(code>=104&&code<=115)return XK_F13+code-104;
    if(code>=89&&code<=97)return XK_KP_1+code-89;
    switch(code){case 39:return XK_0;case 40:return XK_Return;case 41:return XK_Escape;case 42:return XK_BackSpace;case 43:return XK_Tab;case 44:return XK_space;case 45:return XK_minus;case 46:return XK_equal;case 47:return XK_bracketleft;case 48:return XK_bracketright;case 49:case 50:return XK_backslash;case 51:return XK_semicolon;case 52:return XK_apostrophe;case 53:return XK_grave;case 54:return XK_comma;case 55:return XK_period;case 56:return XK_slash;case 57:return XK_Caps_Lock;case 70:return XK_Print;case 71:return XK_Scroll_Lock;case 72:return XK_Pause;case 73:return XK_Insert;case 74:return XK_Home;case 75:return XK_Prior;case 76:return XK_Delete;case 77:return XK_End;case 78:return XK_Next;case 79:return XK_Right;case 80:return XK_Left;case 81:return XK_Down;case 82:return XK_Up;case 83:return XK_Num_Lock;case 84:return XK_KP_Divide;case 85:return XK_KP_Multiply;case 86:return XK_KP_Subtract;case 87:return XK_KP_Add;case 88:return XK_KP_Enter;case 98:return XK_KP_0;case 99:return XK_KP_Decimal;case 100:return XK_less;case 101:return XK_Menu;case 103:return XK_KP_Equal;case 224:return XK_Control_L;case 225:return XK_Shift_L;case 226:return XK_Alt_L;case 227:return XK_Super_L;case 228:return XK_Control_R;case 229:return XK_Shift_R;case 230:return XK_Alt_R;case 231:return XK_Super_R;default:return -1;}
#endif
}
}
