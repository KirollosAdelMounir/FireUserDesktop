# Native dependencies

The bridge links the official LiveKit C++ Client SDK 1.12.2, licensed under Apache-2.0:
https://github.com/livekit/client-sdk-cpp/tree/v1.12.2

The SDK distributes its native WebRTC/LiveKit runtime and third-party dependencies.
Preserve upstream notices and licenses when creating distributable installers:
https://github.com/livekit/client-sdk-cpp/blob/v1.12.2/LICENSE
https://github.com/livekit/rust-sdks

Native audio uses miniaudio 0.11.23 under MIT-0 (or public domain), pinned and
checksum-verified by the native build script. Preserve its license:
https://github.com/mackron/miniaudio/blob/0.11.23/LICENSE

miniaudio — MIT No Attribution
Copyright 2025 David Reid

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

Windows packages also include app-local Microsoft Visual C++ runtime files
from the installed compiler's redistributable directory, under its runtime
redistribution terms. No machine-wide redistributable installer is invoked.

The optional Windows media setup downloads the Gyan FFmpeg essentials build
and preserves its included license/readme files. FFplay is no longer used.
Review the selected build's GPL obligations and corresponding source before
public distribution; this script prepares a local developer runtime:
https://www.gyan.dev/ffmpeg/builds/
https://ffmpeg.org/legal.html

Linux packages also include curl 8.23.0 built from the upstream release with
OpenSSL and versioned symbols for LiveKit's CURL_OPENSSL_4 dependency. The build
verifies SHA-256 and copies upstream COPYING as CURL-COPYING beside the library.
https://github.com/curl/curl/releases/tag/curl-8_23_0
https://curl.se/docs/copyright.html
OpenSSL, zlib and platform libraries remain system dependencies. MinGW builds
require a compatible SDK and include their compiler runtime DLLs under the
respective MinGW/GCC runtime redistribution terms.

Wayland screen sharing dynamically uses the distribution's GStreamer core/app,
base plugins and PipeWire plugin; these remain system dependencies under their
upstream licenses. The managed portal client uses Tmds.DBus.Protocol (MIT).
macOS source capture links the system ScreenCaptureKit/CoreMedia/CoreVideo
frameworks and does not bundle an alternative implementation of those frameworks.
https://gstreamer.freedesktop.org/documentation/licensing.html
https://github.com/tmds/Tmds.DBus
