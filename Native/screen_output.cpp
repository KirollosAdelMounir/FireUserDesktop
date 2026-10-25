#include "fireuser_livekit.h"
#ifdef _WIN32
#include <dxgi.h>
#include <wrl/client.h>
#endif

// FFmpeg's ddagrab defaults to adapter zero. Match its output by physical
// desktop bounds rather than assuming Avalonia's monitor order is DXGI order.
int fu_screen_output_index(int x, int y, int width, int height) {
#ifdef _WIN32
    using Microsoft::WRL::ComPtr;
    ComPtr<IDXGIFactory1> factory;
    ComPtr<IDXGIAdapter1> adapter;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) || FAILED(factory->EnumAdapters1(0, &adapter))) return -1;
    for (UINT index = 0; ; ++index) {
        ComPtr<IDXGIOutput> output;
        if (adapter->EnumOutputs(index, &output) != S_OK) break;
        DXGI_OUTPUT_DESC description{};
        if (FAILED(output->GetDesc(&description)) || !description.AttachedToDesktop) continue;
        const auto bounds = description.DesktopCoordinates;
        if (bounds.left == x && bounds.top == y && bounds.right - bounds.left == width && bounds.bottom - bounds.top == height)
            return static_cast<int>(index);
    }
#else
    (void)x; (void)y; (void)width; (void)height;
#endif
    return -1;
}
