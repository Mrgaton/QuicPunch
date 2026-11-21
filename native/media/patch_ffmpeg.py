"""Reproducible MediaCodec selection and low-latency DXGI polling patches.

FFmpeg n8.1.3 exposes codec_name for encoders but not decoders. Selecting the
component explicitly prevents a software MediaCodec from being labelled hardware.
"""
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
def replace(file, old, new):
    path = root / file
    text = path.read_text()
    if new in text:
        return
    if text.count(old) != 1:
        raise RuntimeError(f"Unexpected pinned FFmpeg source: {file}")
    path.write_text(text.replace(old, new))

replace('libavcodec/mediacodecdec.c', '    int delay_flush;\n', '    int delay_flush;\n    char *component_name;\n')
replace('libavcodec/mediacodecdec.c', '    s->ctx->delay_flush = s->delay_flush;', '    s->ctx->component_name = s->component_name;\n    s->ctx->delay_flush = s->delay_flush;')
replace('libavcodec/mediacodecdec.c', 'static const AVOption ff_mediacodec_vdec_options[] = {', 'static const AVOption ff_mediacodec_vdec_options[] = {\n    { "codec_name", "Explicit decoder component", OFFSET(component_name), AV_OPT_TYPE_STRING, {0}, 0, 0, VD },')
replace('libavcodec/mediacodecdec_common.h', '    char *codec_name;', '    char *codec_name;\n    const char *component_name; /* borrowed from parent AVCodecContext */')
replace('libavcodec/mediacodecdec_common.c', '    s->codec_name = ff_AMediaCodecList_getCodecNameByType(mime, profile, 0, avctx);', '    s->codec_name = s->component_name ? av_strdup(s->component_name) : ff_AMediaCodecList_getCodecNameByType(mime, profile, 0, avctx);')

replace('libavcodec/mediacodecdec.c', '    char *component_name;\n', '    char *component_name;\n    int low_latency;\n')
replace('libavcodec/mediacodecdec.c', '    s->ctx->delay_flush = s->delay_flush;', '    s->ctx->delay_flush = s->delay_flush;\n    s->ctx->low_latency = s->low_latency;')
replace('libavcodec/mediacodecdec.c', '    { "codec_name", "Explicit decoder component", OFFSET(component_name), AV_OPT_TYPE_STRING, {0}, 0, 0, VD },', '    { "codec_name", "Explicit decoder component", OFFSET(component_name), AV_OPT_TYPE_STRING, {0}, 0, 0, VD },\n    { "low_latency", "Enable low latency decoding", OFFSET(low_latency), AV_OPT_TYPE_BOOL, {.i64 = 0}, 0, 1, VD },')
replace('libavcodec/mediacodecdec_common.h', '    const char *component_name; /* borrowed from parent AVCodecContext */', '    const char *component_name; /* borrowed from parent AVCodecContext */\n    int low_latency;')
replace('libavcodec/mediacodecdec_common.c', '    status = ff_AMediaCodec_configure(s->codec, format, s->surface, NULL, 0);', '    if (s->low_latency && avctx->codec_type == AVMEDIA_TYPE_VIDEO)\n        ff_AMediaFormat_setInt32(format, "low-latency", 1);\n    status = ff_AMediaCodec_configure(s->codec, format, s->surface, NULL, 0);')

# QuicNet paces acquisition once for the whole desktop. Per-monitor waits would
# otherwise add up when several screens are static or refresh at different rates.
replace('libavfilter/vsrc_ddagrab.c', '    int        dup_frames;', '    int        dup_frames;\n    int        application_paced;')
replace('libavfilter/vsrc_ddagrab.c', '                                                   OFFSET(dup_frames), AV_OPT_TYPE_BOOL,       { .i64 = 1    },       0,       1, FLAGS },', '                                                   OFFSET(dup_frames), AV_OPT_TYPE_BOOL,       { .i64 = 1    },       0,       1, FLAGS },\n    { "application_paced", "let the caller pace desktop polling", OFFSET(application_paced), AV_OPT_TYPE_BOOL, { .i64 = 0 }, 0, 1, FLAGS },')
replace('libavfilter/vsrc_ddagrab.c', '        dda->time_timeout,\n        &frame_info,', '        dda->application_paced && !need_frame ? 0 : dda->time_timeout,\n        &frame_info,')
replace('libavfilter/vsrc_ddagrab.c', '    int64_t now, delay;', '    int64_t now = av_gettime_relative(), delay;')
replace('libavfilter/vsrc_ddagrab.c', '    time_frame += TIMER_RES64;\n    for (;;) {', '    time_frame += TIMER_RES64;\n    while (!dda->application_paced) {')
