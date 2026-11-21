// DXGI acquisition (including the hardware cursor) uses FFmpeg's ddagrab filter.
// The same D3D11 device composes directly into a pooled NV12 encoder texture.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstring>
#include <memory>
#include <string>
#include <vector>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavfilter/avfilter.h>
#include <libavfilter/buffersink.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libavutil/time.h>
}

using Microsoft::WRL::ComPtr;

#define API extern "C" __declspec(dllexport)

namespace {
thread_local std::string error;

int fail(const char* operation, int status = AVERROR_EXTERNAL) {
    char message[AV_ERROR_MAX_STRING_SIZE];
    av_strerror(status, message, sizeof(message));
    error = std::string(operation) + ": " + message;
    return status;
}

struct DisplayRegion { int index, x, y, width, height; };
struct FrameTimings { int64_t capture_us, compose_us, encode_us, captured; };

struct Source {
    AVFilterGraph* graph = avfilter_graph_alloc();
    AVFilterContext* sink = nullptr;
    AVFrame* frame = av_frame_alloc();
    RECT destination{};
    ~Source() { av_frame_free(&frame); avfilter_graph_free(&graph); }
};

struct Encoder {
    AVBufferRef* device = nullptr;
    AVBufferRef* surfaces = nullptr;
    AVCodecContext* codec = nullptr;
    AVFrame* frame = av_frame_alloc();
    AVPacket* packet = av_packet_alloc();
    ComPtr<ID3D11VideoDevice> video;
    ComPtr<ID3D11VideoContext> context;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;
    ComPtr<ID3D11VideoProcessor> processor;
    std::vector<std::unique_ptr<Source>> sources;
    FrameTimings timings{};
    int64_t origin = 0, last_pts = -1, next_key = 0, key_interval = 0;
    int64_t next_capture = 0;
    int previous_rate = 0;
    HANDLE timer = nullptr;
    int width = 0, height = 0;

    ~Encoder() {
        av_packet_free(&packet);
        av_frame_free(&frame);
        avcodec_free_context(&codec);
        av_buffer_unref(&surfaces);
        av_buffer_unref(&device);
        if (timer) CloseHandle(timer);
    }

    AVD3D11VADeviceContext* d3d() const {
        return static_cast<AVD3D11VADeviceContext*>(reinterpret_cast<AVHWDeviceContext*>(device->data)->hwctx);
    }

    int open_source(const DisplayRegion& region, const RECT& desktop, int fps, IDXGIAdapter* adapter) {
        ComPtr<IDXGIOutput> output;
        DXGI_OUTPUT_DESC desc{};
        if (FAILED(adapter->EnumOutputs(region.index, output.GetAddressOf())) || FAILED(output->GetDesc(&desc)) ||
            !desc.AttachedToDesktop || desc.Rotation != DXGI_MODE_ROTATION_IDENTITY ||
            desc.DesktopCoordinates.left != region.x || desc.DesktopCoordinates.top != region.y ||
            desc.DesktopCoordinates.right != region.x + region.width || desc.DesktopCoordinates.bottom != region.y + region.height)
            return fail("DXGI output changed");

        auto source = std::make_unique<Source>();
        if (!source->graph || !source->frame) return fail("allocate capture graph", AVERROR(ENOMEM));
        source->graph->nb_threads = 1;
        const AVFilter* filter = avfilter_get_by_name("ddagrab");
        if (!filter) return fail("ddagrab filter unavailable", AVERROR_FILTER_NOT_FOUND);
        AVFilterContext* capture = avfilter_graph_alloc_filter(source->graph, filter, "desktop");
        if (!capture) return fail("allocate ddagrab", AVERROR(ENOMEM));
        capture->hw_device_ctx = av_buffer_ref(device);
        if (!capture->hw_device_ctx) return fail("reference D3D11 device", AVERROR(ENOMEM));
        std::string args = "output_idx=" + std::to_string(region.index) + ":framerate=" + std::to_string(fps) +
            ":draw_mouse=1:output_fmt=bgra:dup_frames=1:application_paced=1";
        int status = avfilter_init_str(capture, args.c_str());
        if (status >= 0) status = avfilter_graph_create_filter(&source->sink, avfilter_get_by_name("buffersink"), "textures", nullptr, nullptr, source->graph);
        if (status >= 0) status = avfilter_link(capture, 0, source->sink, 0);
        if (status >= 0) status = avfilter_graph_config(source->graph, nullptr);
        if (status < 0) return fail("open DXGI capture filter", status);
        if (av_buffersink_get_w(source->sink) != region.width || av_buffersink_get_h(source->sink) != region.height)
            return fail("DXGI texture bounds changed");

        auto scale_x = [&](int x) { return MulDiv(x - desktop.left, width, desktop.right - desktop.left); };
        auto scale_y = [&](int y) { return MulDiv(y - desktop.top, height, desktop.bottom - desktop.top); };
        source->destination = {scale_x(region.x), scale_y(region.y), scale_x(region.x + region.width), scale_y(region.y + region.height)};
        if (source->destination.left >= source->destination.right || source->destination.top >= source->destination.bottom)
            return fail("output is too small for the monitor layout");
        sources.push_back(std::move(source));
        return 0;
    }

    int initialize(const DisplayRegion* displays, int count, int codec_id, int fps, int bitrate, int gop_ms,
        int buffer_ms, const char* preset, bool vbr, int quality) {
        auto status = av_hwdevice_ctx_create(&device, AV_HWDEVICE_TYPE_D3D11VA, nullptr, nullptr, 0);
        if (status < 0) return fail("create capture D3D11 device", status);
        constexpr DWORD high_resolution_timer = 0x00000002;
        timer = CreateWaitableTimerExW(nullptr, nullptr, high_resolution_timer, TIMER_MODIFY_STATE | SYNCHRONIZE);
        ComPtr<IDXGIDevice> dxgi;
        ComPtr<IDXGIAdapter> adapter;
        auto d3d_device = d3d()->device;
        if (FAILED(d3d_device->QueryInterface(IID_PPV_ARGS(dxgi.GetAddressOf()))) || FAILED(dxgi->GetAdapter(adapter.GetAddressOf())) ||
            FAILED(d3d_device->QueryInterface(IID_PPV_ARGS(video.GetAddressOf()))) ||
            FAILED(d3d()->device_context->QueryInterface(IID_PPV_ARGS(context.GetAddressOf()))))
            return fail("query D3D11 video processor");

        RECT desktop{0, 0, width, height};
        int input_width = width, input_height = height;
        if (count) {
            const auto& first = displays[0];
            desktop = {first.x, first.y, first.x + first.width, first.y + first.height};
            for (int i = 0; i < count; ++i) {
                const auto& display = displays[i];
                desktop.left = std::min<long>(desktop.left, display.x);
                desktop.top = std::min<long>(desktop.top, display.y);
                desktop.right = std::max<long>(desktop.right, display.x + display.width);
                desktop.bottom = std::max<long>(desktop.bottom, display.y + display.height);
                input_width = std::max(input_width, display.width);
                input_height = std::max(input_height, display.height);
            }
            auto previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            for (int i = 0; i < count && status >= 0; ++i) status = open_source(displays[i], desktop, fps, adapter.Get());
            if (previous) SetThreadDpiAwarenessContext(previous);
            if (status < 0) return status;
        }

        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
        content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputWidth = input_width;
        content.InputHeight = input_height;
        content.OutputWidth = width;
        content.OutputHeight = height;
        content.InputFrameRate = content.OutputFrameRate = {static_cast<UINT>(fps), 1};
        content.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;
        D3D11_VIDEO_PROCESSOR_CAPS capabilities{};
        UINT rgb = 0, nv12 = 0;
        if (FAILED(video->CreateVideoProcessorEnumerator(&content, enumerator.GetAddressOf())) ||
            FAILED(enumerator->GetVideoProcessorCaps(&capabilities)) || capabilities.MaxInputStreams < static_cast<UINT>(count) ||
            FAILED(enumerator->CheckVideoProcessorFormat(DXGI_FORMAT_B8G8R8A8_UNORM, &rgb)) || !(rgb & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) ||
            FAILED(enumerator->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &nv12)) || !(nv12 & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) ||
            FAILED(video->CreateVideoProcessor(enumerator.Get(), 0, processor.GetAddressOf())))
            return fail("GPU monitor composition unsupported");

        RECT full{0, 0, width, height};
        context->VideoProcessorSetOutputTargetRect(processor.Get(), TRUE, &full);
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE input_color{}, output_color{};
        input_color.RGB_Range = 0;
        output_color.YCbCr_Matrix = 1;
        output_color.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
        context->VideoProcessorSetOutputColorSpace(processor.Get(), &output_color);
        for (UINT i = 0; i < sources.size(); ++i) {
            RECT source{0, 0, displays[i].width, displays[i].height};
            context->VideoProcessorSetStreamSourceRect(processor.Get(), i, TRUE, &source);
            context->VideoProcessorSetStreamDestRect(processor.Get(), i, TRUE, &sources[i]->destination);
            context->VideoProcessorSetStreamAutoProcessingMode(processor.Get(), i, FALSE);
            context->VideoProcessorSetStreamColorSpace(processor.Get(), i, &input_color);
        }

        surfaces = av_hwframe_ctx_alloc(device);
        if (!surfaces) return fail("allocate NV12 textures", AVERROR(ENOMEM));
        auto frames = reinterpret_cast<AVHWFramesContext*>(surfaces->data);
        frames->format = AV_PIX_FMT_D3D11;
        frames->sw_format = AV_PIX_FMT_NV12;
        frames->width = width;
        frames->height = height;
        static_cast<AVD3D11VAFramesContext*>(frames->hwctx)->BindFlags = D3D11_BIND_RENDER_TARGET;
        if ((status = av_hwframe_ctx_init(surfaces)) < 0) return fail("initialize NV12 textures", status);

        const AVCodec* encoder = avcodec_find_encoder_by_name(codec_id == 1 ? "h264_nvenc" : "hevc_nvenc");
        if (!encoder) return fail("NVENC unavailable", AVERROR_ENCODER_NOT_FOUND);
        codec = avcodec_alloc_context3(encoder);
        if (!frame || !packet || !codec) return fail("allocate encoder", AVERROR(ENOMEM));
        codec->hw_frames_ctx = av_buffer_ref(surfaces);
        if (!codec->hw_frames_ctx) return fail("reference NV12 textures", AVERROR(ENOMEM));
        codec->width = width;
        codec->height = height;
        codec->pix_fmt = AV_PIX_FMT_D3D11;
        codec->time_base = {1, 1000000};
        codec->framerate = {fps, 1};
        codec->color_range = AVCOL_RANGE_MPEG;
        codec->colorspace = AVCOL_SPC_BT709;
        codec->color_primaries = AVCOL_PRI_BT709;
        codec->color_trc = AVCOL_TRC_BT709;
        codec->bit_rate = codec->rc_max_rate = bitrate;
        codec->rc_buffer_size = std::max<int64_t>(bitrate / fps, int64_t(bitrate) * buffer_ms / 1000);
        codec->gop_size = std::max(1, fps * gop_ms / 1000);
        codec->max_b_frames = 0;
        codec->flags |= AV_CODEC_FLAG_LOW_DELAY;
        key_interval = gop_ms * 1000LL;
        AVDictionary* options = nullptr;
        av_dict_set(&options, "preset", preset, 0);
        av_dict_set(&options, "profile", codec_id == 1 ? "high" : "main", 0);
        av_dict_set(&options, "tune", "ull", 0);
        av_dict_set(&options, "rc", vbr ? "vbr" : "cbr", 0);
        av_dict_set(&options, "rc-lookahead", "0", 0);
        av_dict_set(&options, "zerolatency", "1", 0);
        av_dict_set(&options, "forced-idr", "1", 0);
        av_dict_set(&options, "delay", "0", 0);
        if (vbr) av_dict_set_int(&options, "cq", quality, 0);
        status = avcodec_open2(codec, encoder, &options);
        av_dict_free(&options);
        return status < 0 ? fail("open D3D11 NVENC encoder", status) : 0;
    }

    int compose() {
        int status = av_hwframe_get_buffer(surfaces, frame, 0);
        if (status < 0) return fail("acquire NV12 texture", status);
        std::vector<ComPtr<ID3D11VideoProcessorInputView>> views(sources.size());
        std::vector<D3D11_VIDEO_PROCESSOR_STREAM> streams(sources.size());
        for (size_t i = 0; i < sources.size(); ++i) {
            const AVFrame* input = sources[i]->frame;
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC desc{};
            desc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
            desc.Texture2D.ArraySlice = static_cast<UINT>(reinterpret_cast<uintptr_t>(input->data[1]));
            if (FAILED(video->CreateVideoProcessorInputView(reinterpret_cast<ID3D11Texture2D*>(input->data[0]), enumerator.Get(), &desc, views[i].GetAddressOf())))
                return fail("create captured texture view");
            streams[i].Enable = TRUE;
            streams[i].pInputSurface = views[i].Get();
        }
        ComPtr<ID3D11VideoProcessorOutputView> target;
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC desc{};
        desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        if (FAILED(video->CreateVideoProcessorOutputView(reinterpret_cast<ID3D11Texture2D*>(frame->data[0]), enumerator.Get(), &desc, target.GetAddressOf())))
            return fail("create NV12 output view");
        // Empty input is used by the hardware smoke test. It exercises composition
        // and NVENC on a synthetic solid surface without capturing the desktop.
        D3D11_VIDEO_COLOR background{};
        background.YCbCr = {0.0625f, 0.5f, 0.5f, 1.0f};
        if (sources.empty()) background.YCbCr.Y = 0.25f + 0.125f * ((last_pts / 16667) & 3);
        context->VideoProcessorSetOutputBackgroundColor(processor.Get(), TRUE, &background);
        if (FAILED(context->VideoProcessorBlt(processor.Get(), target.Get(), 0, static_cast<UINT>(streams.size()), streams.data())))
            return fail("compose GPU textures");
        return 0;
    }

    int receive(uint8_t* buffer, int capacity, int64_t* pts, int* key) {
        int status = avcodec_receive_packet(codec, packet);
        if (status == AVERROR(EAGAIN)) return 0;
        if (status < 0) return fail("read NVENC packet", status);
        int size = packet->size;
        if (size > capacity) size = fail("NVENC packet exceeds media limit", AVERROR(ENOSPC));
        else {
            std::memcpy(buffer, packet->data, size);
            *pts = packet->pts;
            *key = !!(packet->flags & AV_PKT_FLAG_KEY);
        }
        av_packet_unref(packet);
        return size;
    }

    int read(uint8_t* buffer, int capacity, int64_t* pts, int* key, bool force_key, int target_bitrate, int target_fps, int buffer_ms) {
        timings = {};
        int rate = std::clamp(target_fps, 5, codec->framerate.num);
        int64_t interval = 1000000 / rate;
        auto now = av_gettime_relative();
        if (rate != previous_rate || next_capture < now - interval) next_capture = now;
        previous_rate = rate;
        if (auto remaining = next_capture - now; remaining > 0) {
            LARGE_INTEGER due;
            due.QuadPart = -remaining * 10;
            if (timer && SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE)) WaitForSingleObject(timer, INFINITE);
            else av_usleep(static_cast<unsigned>(remaining));
        }
        next_capture += interval;
        auto start = av_gettime_relative();
        for (auto& source : sources) {
            av_frame_unref(source->frame);
            int status = av_buffersink_get_frame(source->sink, source->frame);
            if (status == AVERROR(EAGAIN)) return 0;
            if (status < 0) return fail("read desktop texture", status);
        }
        timings.capture_us = av_gettime_relative() - start;
        timings.captured = 1;
        start = av_gettime_relative();
        if (target_bitrate >= 500000 && target_bitrate <= 100000000 && target_bitrate != codec->bit_rate) {
            codec->bit_rate = codec->rc_max_rate = target_bitrate;
            codec->rc_buffer_size = std::max<int64_t>(target_bitrate / rate, int64_t(target_bitrate) * buffer_ms / 1000);
        }
        int status = compose();
        timings.compose_us = av_gettime_relative() - start;
        if (status < 0) return status;

        start = av_gettime_relative();
        if (!origin) origin = start;
        last_pts = std::max(last_pts + 1, start - origin);
        frame->pts = last_pts;
        frame->pict_type = force_key || last_pts >= next_key ? AV_PICTURE_TYPE_I : AV_PICTURE_TYPE_NONE;
        if (frame->pict_type == AV_PICTURE_TYPE_I) next_key = last_pts + key_interval;
        status = avcodec_send_frame(codec, frame);
        av_frame_unref(frame);
        if (status < 0) return fail("submit NVENC texture", status);
        status = receive(buffer, capacity, pts, key);
        timings.encode_us = av_gettime_relative() - start;
        return status;
    }
};
} // namespace

API const char* qpv_screen_error() { return error.c_str(); }

API void* qpv_screen_open(const DisplayRegion* displays, int count, int codec_id, int width, int height,
    int fps, int bitrate, int gop_ms, int buffer_ms, const char* preset, int vbr, int quality) {
    if (count < 0 || count > 16 || (count && !displays) || (codec_id != 1 && codec_id != 5) || !preset ||
        width < 128 || width > 7680 || height < 128 || height > 4320 || ((width | height) & 1) ||
        fps < 5 || fps > 144 || bitrate < 500000 || bitrate > 100000000 || gop_ms < 250 || gop_ms > 5000 || buffer_ms < 16 || buffer_ms > 250) {
        fail("screen encoder settings", AVERROR(EINVAL));
        return nullptr;
    }
    try {
        auto encoder = std::make_unique<Encoder>();
        encoder->width = width;
        encoder->height = height;
        if (encoder->initialize(displays, count, codec_id, fps, bitrate, gop_ms, buffer_ms, preset, vbr != 0, quality) < 0) return nullptr;
        return encoder.release();
    } catch (const std::bad_alloc&) { fail("allocate screen encoder", AVERROR(ENOMEM)); return nullptr; }
}

API int qpv_screen_read(void* handle, uint8_t* buffer, int capacity, int64_t* pts, int* key, int force_key,
    int target_bitrate, int target_fps, int buffer_ms, FrameTimings* timings) {
    if (!handle || !buffer || capacity <= 0 || !pts || !key || !timings) return fail("read screen encoder", AVERROR(EINVAL));
    auto encoder = static_cast<Encoder*>(handle);
    try {
        int status = encoder->read(buffer, capacity, pts, key, force_key != 0, target_bitrate, target_fps, buffer_ms);
        *timings = encoder->timings;
        return status;
    } catch (const std::bad_alloc&) { return fail("allocate screen frame", AVERROR(ENOMEM)); }
}

API void qpv_screen_close(void* handle) { delete static_cast<Encoder*>(handle); }
