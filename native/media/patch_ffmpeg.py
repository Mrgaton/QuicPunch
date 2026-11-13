"""Small, reproducible patch to select an audited Android decoder component.

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
