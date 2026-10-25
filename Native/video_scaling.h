#pragma once
#include <algorithm>
#include <cstdint>
#include <span>
#include <stdexcept>
#include <vector>

namespace fireuser {
// Fixed-point bilinear scaling on Y/U/V planes avoids four-channel floating
// point interpolation and conversion back from RGBA before VP8 encoding.
inline void scale_plane(const uint8_t* source, int width, int height, uint8_t* target, int out_width, int out_height) {
    struct Column { int left, right, weight; };
    std::vector<Column> columns(out_width);
    for (int x = 0; x < out_width; ++x) {
        const auto position = static_cast<int64_t>(x) * width * 256 / out_width;
        const int left = static_cast<int>(position / 256);
        columns[x] = {left, std::min(left + 1, width - 1), static_cast<int>(position % 256)};
    }
    for (int y = 0; y < out_height; ++y) {
        const auto position = static_cast<int64_t>(y) * height * 256 / out_height;
        const int top = static_cast<int>(position / 256), weight = static_cast<int>(position % 256);
        const auto first = source + top * width, second = source + std::min(top + 1, height - 1) * width;
        for (int x = 0; x < out_width; ++x) {
            const auto column = columns[x];
            const int a = first[column.left] * (256 - column.weight) + first[column.right] * column.weight;
            const int b = second[column.left] * (256 - column.weight) + second[column.right] * column.weight;
            target[y * out_width + x] = static_cast<uint8_t>((a * (256 - weight) + b * weight + 32768) >> 16);
        }
    }
}
inline std::vector<uint8_t> scale_i420(std::span<const uint8_t> source, int width, int height, int out_width, int out_height) {
    if (width <= 0 || height <= 0 || out_width <= 0 || out_height <= 0 || width > 4096 || height > 4096 || out_width > 4096 || out_height > 4096 ||
        ((width | height | out_width | out_height) & 1) != 0 || source.size() != static_cast<size_t>(width) * height * 3 / 2)
        throw std::invalid_argument("Invalid I420 frame dimensions");
    if (width == out_width && height == out_height) return {source.begin(), source.end()};
    const int pixels = width * height, out_pixels = out_width * out_height;
    std::vector<uint8_t> result(out_pixels * 3 / 2);
    scale_plane(source.data(), width, height, result.data(), out_width, out_height);
    scale_plane(source.data() + pixels, width / 2, height / 2, result.data() + out_pixels, out_width / 2, out_height / 2);
    scale_plane(source.data() + pixels * 5 / 4, width / 2, height / 2, result.data() + out_pixels * 5 / 4, out_width / 2, out_height / 2);
    return result;
}
}
