// Native media ABI. Compressed packets enter once; decoded pixels never enter JS/.NET.
// FFmpeg owns decoding. D3D11 and Android Surface own presentation.
#include <cstdint>
#include <cstring>
#include <string>
#include <chrono>
#include <algorithm>
#include <vector>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/hwcontext.h>
#include <libavutil/imgutils.h>
#include <libavutil/opt.h>
#include <libavutil/pixdesc.h>
#include <libswscale/swscale.h>
}
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_3.h>
extern "C" {
#include <libavutil/hwcontext_d3d11va.h>
}
#define API extern "C" __declspec(dllexport)
template<class T> static void release(T*& p) { if(p) p->Release(); p=nullptr; }
#else
#include <jni.h>
#include <android/native_window_jni.h>
#include <EGL/egl.h>
#include <GLES3/gl3.h>
extern "C" {
#include <libavcodec/jni.h>
#include <libavcodec/mediacodec.h>
#include <libavutil/hwcontext_mediacodec.h>
}
#define API extern "C" __attribute__((visibility("default")))
#endif

static thread_local std::string error_text;
static int failure(int error, const char* where) {
    char message[AV_ERROR_MAX_STRING_SIZE]; av_strerror(error,message,sizeof(message));
    error_text=std::string(where)+": "+message; return error;
}
API const char* qpv_error() { return error_text.c_str(); }
API const char* qpv_version() { return av_version_info(); }
static AVCodecID codec_id(int id) { switch(id){case 1:return AV_CODEC_ID_H264;case 3:return AV_CODEC_ID_VP9;case 4:return AV_CODEC_ID_AV1;case 5:return AV_CODEC_ID_HEVC;default:return AV_CODEC_ID_NONE;} }
static const char* codec_name(int id) { switch(id){case 1:return "h264";case 3:return "vp9";case 4:return "av1";case 5:return "hevc";default:return "";} }

struct Video {
    AVCodecContext* codec=nullptr;
    AVBufferRef* device=nullptr;
    AVFrame* frame=av_frame_alloc();
    AVFrame* newest=av_frame_alloc();
    AVFrame* converted=av_frame_alloc();
    AVPacket* packet=av_packet_alloc();
    SwsContext* scaler=nullptr;
    int hardware=0;
    int width=0,height=0;
    int64_t decoded=0,presented=0,discarded=0,hardware_frames=0,last_decode_us=0;
    void* surface=nullptr;
#ifdef _WIN32
    ID3D11Device* d3d=nullptr;
    ID3D11DeviceContext* context=nullptr;
    ID3D11VideoDevice* video_device=nullptr;
    ID3D11VideoContext* video_context=nullptr;
    IDXGISwapChain1* swap=nullptr;
    ID3D11VideoProcessorEnumerator* enumerator=nullptr;
    ID3D11VideoProcessor* processor=nullptr;
    ID3D11Texture2D* upload=nullptr;
    HANDLE latency=nullptr;
    int render_width=0,render_height=0,output_width=0,output_height=0;
    std::vector<uint8_t> upload_bytes;
#else
    ANativeWindow* window=nullptr;
    EGLDisplay display=EGL_NO_DISPLAY;
    EGLContext gl_context=EGL_NO_CONTEXT;
    EGLSurface gl_surface=EGL_NO_SURFACE;
    GLuint program=0,textures[3]={};
    int upload_width=0,upload_height=0;
#endif
    ~Video();
};

static int convert(Video* v, AVFrame* input, AVPixelFormat format) {
    AVFrame* out=v->converted;
    if(out->width!=input->width || out->height!=input->height || out->format!=format) {
        av_frame_unref(out); out->format=format;out->width=input->width;out->height=input->height;
        int ret=av_frame_get_buffer(out,32);if(ret<0)return ret;
    }
    int ret=av_frame_make_writable(out);if(ret<0)return ret;
    v->scaler=sws_getCachedContext(v->scaler,input->width,input->height,(AVPixelFormat)input->format,input->width,input->height,format,SWS_FAST_BILINEAR,nullptr,nullptr,nullptr);
    if(!v->scaler)return AVERROR(ENOMEM);
    return sws_scale(v->scaler,input->data,input->linesize,0,input->height,out->data,out->linesize)<0?AVERROR_EXTERNAL:0;
}

#ifdef _WIN32
static int init_d3d(Video* v) {
    HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_VIDEO_SUPPORT|D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&v->d3d,nullptr,&v->context);
    if(FAILED(hr))return AVERROR_EXTERNAL;
    if(FAILED(v->d3d->QueryInterface(__uuidof(ID3D11VideoDevice),(void**)&v->video_device)) || FAILED(v->context->QueryInterface(__uuidof(ID3D11VideoContext),(void**)&v->video_context)))return AVERROR_EXTERNAL;
    return 0;
}
static int present(Video* v, AVFrame* f) {
    if(!v->surface)return 0;
    HWND window=(HWND)v->surface;
    if(!IsWindowVisible(window))return 0;
    RECT rect;GetClientRect(window,&rect);
    int ow=rect.right,oh=rect.bottom;if(ow<1||oh<1)return 0;
    if(!v->swap){
        IDXGIDevice* dxdev=nullptr;IDXGIAdapter* adapter=nullptr;IDXGIFactory2* factory=nullptr;
        HRESULT hr=v->d3d->QueryInterface(__uuidof(IDXGIDevice),(void**)&dxdev);
        if(SUCCEEDED(hr))hr=dxdev->GetAdapter(&adapter);
        if(SUCCEEDED(hr))hr=adapter->GetParent(__uuidof(IDXGIFactory2),(void**)&factory);
        DXGI_SWAP_CHAIN_DESC1 desc={};desc.Width=ow;desc.Height=oh;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count=1;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.Flags=DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        if(SUCCEEDED(hr))hr=factory->CreateSwapChainForHwnd(v->d3d,window,&desc,nullptr,nullptr,&v->swap);
        release(factory);release(adapter);release(dxdev);if(FAILED(hr))return AVERROR_EXTERNAL;
        IDXGISwapChain2* swap2=nullptr;
        if(SUCCEEDED(v->swap->QueryInterface(__uuidof(IDXGISwapChain2),(void**)&swap2))){swap2->SetMaximumFrameLatency(1);v->latency=swap2->GetFrameLatencyWaitableObject();release(swap2);}
        v->output_width=ow;v->output_height=oh;
    }
    if(ow!=v->output_width||oh!=v->output_height){
        if(FAILED(v->swap->ResizeBuffers(0,ow,oh,DXGI_FORMAT_UNKNOWN,DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT)))return AVERROR_EXTERNAL;
        v->output_width=ow;v->output_height=oh;
        // Output dimensions are part of the video processor's content description.
        release(v->processor);release(v->enumerator);
    }
    // No busy waiting and no queue of presents behind a blocked compositor.
    if(v->latency && WaitForSingleObject(v->latency,0)!=WAIT_OBJECT_0){++v->discarded;return 0;}
    if(!v->processor || v->render_width!=f->width || v->render_height!=f->height){
        release(v->processor);release(v->enumerator);release(v->upload);
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content={};content.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputWidth=f->width;content.InputHeight=f->height;content.OutputWidth=ow;content.OutputHeight=oh;content.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        if(FAILED(v->video_device->CreateVideoProcessorEnumerator(&content,&v->enumerator))||FAILED(v->video_device->CreateVideoProcessor(v->enumerator,0,&v->processor)))return AVERROR_EXTERNAL;
        v->render_width=f->width;v->render_height=f->height;
    }
    ID3D11Texture2D* input;UINT slice=0;
    if(f->format==AV_PIX_FMT_D3D11){input=(ID3D11Texture2D*)f->data[0];slice=(UINT)(uintptr_t)f->data[1];}
    else {
        if(convert(v,f,AV_PIX_FMT_NV12)<0)return AVERROR_EXTERNAL;
        if(!v->upload){D3D11_TEXTURE2D_DESC td={};td.Width=f->width;td.Height=f->height;td.MipLevels=td.ArraySize=1;td.Format=DXGI_FORMAT_NV12;td.SampleDesc.Count=1;td.Usage=D3D11_USAGE_DEFAULT;td.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;
            if(FAILED(v->d3d->CreateTexture2D(&td,nullptr,&v->upload)))return AVERROR_EXTERNAL;}
        // NV12 allocation may align plane heights; use tightly packed reusable input.
        int pitch=f->width;int size=av_image_get_buffer_size(AV_PIX_FMT_NV12,f->width,f->height,1);
        v->upload_bytes.resize(size);uint8_t* packed=v->upload_bytes.data();
        av_image_copy_to_buffer(packed,size,v->converted->data,v->converted->linesize,AV_PIX_FMT_NV12,f->width,f->height,1);
        v->context->UpdateSubresource(v->upload,0,nullptr,packed,pitch,0);input=v->upload;
    }
    ID3D11VideoProcessorInputView* in=nullptr;ID3D11VideoProcessorOutputView* out=nullptr;ID3D11Texture2D* back=nullptr;
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC iv={};iv.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;iv.Texture2D.ArraySlice=slice;
    HRESULT hr=v->video_device->CreateVideoProcessorInputView(input,v->enumerator,&iv,&in);
    if(SUCCEEDED(hr))hr=v->swap->GetBuffer(0,__uuidof(ID3D11Texture2D),(void**)&back);
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ov={};ov.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;
    if(SUCCEEDED(hr))hr=v->video_device->CreateVideoProcessorOutputView(back,v->enumerator,&ov,&out);
    if(SUCCEEDED(hr)){
        RECT source={0,0,f->width,f->height};double scale=std::min((double)ow/f->width,(double)oh/f->height);
        int dw=(int)(f->width*scale),dh=(int)(f->height*scale);RECT destination={(ow-dw)/2,(oh-dh)/2,(ow+dw)/2,(oh+dh)/2};
        v->video_context->VideoProcessorSetStreamSourceRect(v->processor,0,TRUE,&source);
        v->video_context->VideoProcessorSetStreamDestRect(v->processor,0,TRUE,&destination);
        v->video_context->VideoProcessorSetOutputTargetRect(v->processor,TRUE,&rect);
        v->video_context->VideoProcessorSetStreamAutoProcessingMode(v->processor,0,FALSE);
        D3D11_VIDEO_COLOR color={};color.RGBA.A=1;v->video_context->VideoProcessorSetOutputBackgroundColor(v->processor,FALSE,&color);
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE cs={};cs.YCbCr_Matrix=f->colorspace==AVCOL_SPC_BT709 || f->height>=720;cs.Nominal_Range=f->color_range==AVCOL_RANGE_JPEG?1:2;
        v->video_context->VideoProcessorSetStreamColorSpace(v->processor,0,&cs);
        D3D11_VIDEO_PROCESSOR_STREAM stream={};stream.Enable=TRUE;stream.pInputSurface=in;
        hr=v->video_context->VideoProcessorBlt(v->processor,out,0,1,&stream);
    }
    release(out);release(in);release(back);
    if(FAILED(hr))return AVERROR_EXTERNAL;
    hr=v->swap->Present(0,DXGI_PRESENT_DO_NOT_WAIT);
    if(hr==DXGI_ERROR_WAS_STILL_DRAWING){++v->discarded;return 0;}
    if(FAILED(hr))return AVERROR_EXTERNAL;
    ++v->presented;return 0;
}
API void* qpv_surface_create(void* parent){return CreateWindowExW(0,L"STATIC",L"NetTT native video",WS_CHILD|WS_CLIPSIBLINGS,0,0,1,1,(HWND)parent,nullptr,GetModuleHandle(nullptr),nullptr);}
API void qpv_surface_layout(void* surface,int x,int y,int w,int h,int visible){SetWindowPos((HWND)surface,HWND_TOP,x,y,w,h,SWP_NOACTIVATE|(visible?SWP_SHOWWINDOW:SWP_HIDEWINDOW));}
API void qpv_surface_destroy(void* surface){DestroyWindow((HWND)surface);}
#else
API void qpv_android_init(void* env){JavaVM* vm=nullptr;((JNIEnv*)env)->GetJavaVM(&vm);av_jni_set_java_vm(vm,nullptr);}
static GLuint shader(GLenum kind,const char* source){GLuint id=glCreateShader(kind);glShaderSource(id,1,&source,nullptr);glCompileShader(id);GLint success;glGetShaderiv(id,GL_COMPILE_STATUS,&success);if(!success){glDeleteShader(id);return 0;}return id;}
static int init_gl(Video* v){
    v->display=eglGetDisplay(EGL_DEFAULT_DISPLAY);if(v->display==EGL_NO_DISPLAY||!eglInitialize(v->display,nullptr,nullptr))return AVERROR_EXTERNAL;
    EGLint attrs[]={EGL_RENDERABLE_TYPE,EGL_OPENGL_ES3_BIT,EGL_SURFACE_TYPE,EGL_WINDOW_BIT,EGL_RED_SIZE,8,EGL_GREEN_SIZE,8,EGL_BLUE_SIZE,8,EGL_NONE};
    EGLConfig config;EGLint count;if(!eglChooseConfig(v->display,attrs,&config,1,&count)||!count)return AVERROR_EXTERNAL;
    EGLint ctx[]={EGL_CONTEXT_CLIENT_VERSION,3,EGL_NONE};v->gl_context=eglCreateContext(v->display,config,EGL_NO_CONTEXT,ctx);
    v->gl_surface=eglCreateWindowSurface(v->display,config,v->window,nullptr);
    if(v->gl_context==EGL_NO_CONTEXT||v->gl_surface==EGL_NO_SURFACE||!eglMakeCurrent(v->display,v->gl_surface,v->gl_surface,v->gl_context))return AVERROR_EXTERNAL;
    eglSwapInterval(v->display,0);
    const char* vertex="#version 300 es\nprecision mediump float;out vec2 uv;void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);uv=vec2(p.x,1.0-p.y);gl_Position=vec4(p*2.0-1.0,0,1);}";
    const char* fragment="#version 300 es\nprecision mediump float;in vec2 uv;uniform sampler2D ytex;uniform sampler2D utex;uniform sampler2D vtex;uniform mat3 matrix;uniform vec3 offset;out vec4 color;void main(){vec3 yuv=vec3(texture(ytex,uv).r,texture(utex,uv).r,texture(vtex,uv).r)+offset;color=vec4(matrix*yuv,1);}";
    GLuint vs=shader(GL_VERTEX_SHADER,vertex),fs=shader(GL_FRAGMENT_SHADER,fragment);if(!vs||!fs)return AVERROR_EXTERNAL;
    v->program=glCreateProgram();glAttachShader(v->program,vs);glAttachShader(v->program,fs);glLinkProgram(v->program);glDeleteShader(vs);glDeleteShader(fs);
    GLint linked;glGetProgramiv(v->program,GL_LINK_STATUS,&linked);if(!linked)return AVERROR_EXTERNAL;
    glUseProgram(v->program);glGenTextures(3,v->textures);
    for(int i=0;i<3;i++){glActiveTexture(GL_TEXTURE0+i);glBindTexture(GL_TEXTURE_2D,v->textures[i]);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_LINEAR);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_LINEAR);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_S,GL_CLAMP_TO_EDGE);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_T,GL_CLAMP_TO_EDGE);}
    glUniform1i(glGetUniformLocation(v->program,"ytex"),0);glUniform1i(glGetUniformLocation(v->program,"utex"),1);glUniform1i(glGetUniformLocation(v->program,"vtex"),2);return 0;
}
static int present(Video* v,AVFrame* frame){
    if(!v->surface)return 0;
    if(frame->format==AV_PIX_FMT_MEDIACODEC){int ret=av_mediacodec_release_buffer((AVMediaCodecBuffer*)frame->data[3],1);if(ret<0)return ret;++v->presented;return 0;}
    if(!v->window)return AVERROR_EXTERNAL;
    if(v->gl_context==EGL_NO_CONTEXT&&init_gl(v)<0)return AVERROR_EXTERNAL;
    // Decoder worker is dedicated: EGL current context never crosses threads.
    AVFrame* f=frame;if(frame->format!=AV_PIX_FMT_YUV420P){if(convert(v,frame,AV_PIX_FMT_YUV420P)<0)return AVERROR_EXTERNAL;f=v->converted;}
    const bool resized=f->width!=v->upload_width||f->height!=v->upload_height;
    glPixelStorei(GL_UNPACK_ALIGNMENT,1);
    for(int i=0;i<3;i++){
        int w=i?(f->width+1)/2:f->width,h=i?(f->height+1)/2:f->height;
        glActiveTexture(GL_TEXTURE0+i);glBindTexture(GL_TEXTURE_2D,v->textures[i]);glPixelStorei(GL_UNPACK_ROW_LENGTH,f->linesize[i]);
        if(resized)glTexImage2D(GL_TEXTURE_2D,0,GL_R8,w,h,0,GL_RED,GL_UNSIGNED_BYTE,f->data[i]);
        else glTexSubImage2D(GL_TEXTURE_2D,0,0,0,w,h,GL_RED,GL_UNSIGNED_BYTE,f->data[i]);
    }
    glPixelStorei(GL_UNPACK_ROW_LENGTH,0);v->upload_width=f->width;v->upload_height=f->height;
    int w=ANativeWindow_getWidth(v->window),h=ANativeWindow_getHeight(v->window);double scale=std::min((double)w/f->width,(double)h/f->height);
    glViewport(0,0,w,h);glClearColor(0,0,0,1);glClear(GL_COLOR_BUFFER_BIT);int dw=(int)(f->width*scale),dh=(int)(f->height*scale);glViewport((w-dw)/2,(h-dh)/2,dw,dh);
    bool full=frame->color_range==AVCOL_RANGE_JPEG,hd=frame->colorspace==AVCOL_SPC_BT709||frame->height>=720;
    float y=full?1.0f:1.164384f,c=full?1.0f:255.0f/224.0f;
    const float matrix[]={y,y,y,0,(hd?-.187324f:-.344136f)*c,(hd?1.8556f:1.772f)*c,(hd?1.5748f:1.402f)*c,(hd?-.468124f:-.714136f)*c,0};
    glUniformMatrix3fv(glGetUniformLocation(v->program,"matrix"),1,GL_FALSE,matrix);glUniform3f(glGetUniformLocation(v->program,"offset"),full?0:-16.0f/255,-128.0f/255,-128.0f/255);
    glDrawArrays(GL_TRIANGLES,0,3);if(!eglSwapBuffers(v->display,v->gl_surface))return AVERROR_EXTERNAL;++v->presented;return 0;
}
#endif

Video::~Video(){
    av_packet_free(&packet);av_frame_free(&newest);av_frame_free(&frame);av_frame_free(&converted);
    avcodec_free_context(&codec);av_buffer_unref(&device);sws_freeContext(scaler);
#ifdef _WIN32
    release(upload);release(processor);release(enumerator);release(swap);if(latency)CloseHandle(latency);
    release(video_context);release(video_device);release(context);release(d3d);
#else
    if(gl_context!=EGL_NO_CONTEXT){eglMakeCurrent(display,gl_surface,gl_surface,gl_context);glDeleteTextures(3,textures);glDeleteProgram(program);eglMakeCurrent(display,EGL_NO_SURFACE,EGL_NO_SURFACE,EGL_NO_CONTEXT);eglDestroyContext(display,gl_context);}
    if(gl_surface!=EGL_NO_SURFACE)eglDestroySurface(display,gl_surface);
    // EGL_DEFAULT_DISPLAY is shared across participants. Do not eglTerminate it here.
    if(window)ANativeWindow_release(window);
#endif
}
static AVPixelFormat choose_format(AVCodecContext* ctx,const AVPixelFormat* formats){
    Video* v=(Video*)ctx->opaque;
#ifdef _WIN32
    AVPixelFormat desired=AV_PIX_FMT_D3D11;
#else
    AVPixelFormat desired=AV_PIX_FMT_MEDIACODEC;
#endif
    if(v->hardware){for(auto p=formats;*p!=AV_PIX_FMT_NONE;p++)if(*p==desired)return desired;return AV_PIX_FMT_NONE;}
    for(auto p=formats;*p!=AV_PIX_FMT_NONE;p++){const AVPixFmtDescriptor* desc=av_pix_fmt_desc_get(*p);if(desc&&!(desc->flags&AV_PIX_FMT_FLAG_HWACCEL))return *p;}
    return AV_PIX_FMT_NONE;
}
API void* qpv_open(int id,int hardware,int width,int height,const uint8_t* extra,int extra_size,void* surface,const char* component){
    error_text.clear();if(codec_id(id)==AV_CODEC_ID_NONE||width<2||height<2||width>7680||height>4320||extra_size<0||extra_size>65536){failure(AVERROR(EINVAL),"configuration");return nullptr;}
    Video* v=new Video();v->hardware=hardware;v->surface=surface;
    const AVCodec* decoder=nullptr;
#ifdef _WIN32
    decoder=id==4?avcodec_find_decoder_by_name(hardware?"av1":"libdav1d"):avcodec_find_decoder(codec_id(id));
    if((hardware||surface) && init_d3d(v)<0){failure(AVERROR_EXTERNAL,"D3D11 device");delete v;return nullptr;}
#else
    std::string name=std::string(codec_name(id))+"_mediacodec";
    decoder=hardware?avcodec_find_decoder_by_name(name.c_str()):id==4?avcodec_find_decoder_by_name("libdav1d"):avcodec_find_decoder(codec_id(id));
    if(surface){JavaVM* vm=(JavaVM*)av_jni_get_java_vm(nullptr);JNIEnv* env=nullptr;if(vm){vm->AttachCurrentThread(&env,nullptr);v->window=ANativeWindow_fromSurface(env,(jobject)surface);}if(!v->window){failure(AVERROR_EXTERNAL,"Android surface");delete v;return nullptr;}}
    if(hardware && (!surface||!component||!*component)){failure(AVERROR(EINVAL),"hardware component/surface missing");delete v;return nullptr;}
#endif
    if(!decoder){failure(AVERROR_DECODER_NOT_FOUND,"decoder");delete v;return nullptr;}
    v->codec=avcodec_alloc_context3(decoder);if(!v->codec){delete v;return nullptr;}
    auto ctx=v->codec;ctx->width=width;ctx->height=height;ctx->pkt_timebase={1,1000000};ctx->max_pixels=7680LL*4320;ctx->thread_count=2;ctx->thread_type=FF_THREAD_SLICE;ctx->flags|=AV_CODEC_FLAG_LOW_DELAY;ctx->opaque=v;ctx->get_format=choose_format;
    if(extra_size){ctx->extradata=(uint8_t*)av_mallocz(extra_size+AV_INPUT_BUFFER_PADDING_SIZE);if(!ctx->extradata){delete v;return nullptr;}memcpy(ctx->extradata,extra,extra_size);ctx->extradata_size=extra_size;}
    if(hardware){
#ifdef _WIN32
        v->device=av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA);
        if(v->device){auto hw=(AVD3D11VADeviceContext*)((AVHWDeviceContext*)v->device->data)->hwctx;hw->device=v->d3d;v->d3d->AddRef();}
#else
        v->device=av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_MEDIACODEC);
        if(v->device){auto hw=(AVMediaCodecDeviceContext*)((AVHWDeviceContext*)v->device->data)->hwctx;hw->native_window=v->window;}
#endif
        if(!v->device||av_hwdevice_ctx_init(v->device)<0){failure(AVERROR_EXTERNAL,"hardware device");delete v;return nullptr;}ctx->hw_device_ctx=av_buffer_ref(v->device);
    }
    AVDictionary* options=nullptr;
#ifndef _WIN32
    if(hardware){av_dict_set(&options,"ndk_codec","1",0);av_dict_set(&options,"codec_name",component,0);}
#endif
    int ret=avcodec_open2(ctx,decoder,&options);av_dict_free(&options);
    if(ret<0){failure(ret,"open decoder");delete v;return nullptr;}return v;
}
API void qpv_close(void* handle){delete (Video*)handle;}
static int receive(Video* v,bool render){
    int ret,count=0;
    while((ret=avcodec_receive_frame(v->codec,v->frame))>=0){
        if(v->frame->width>7680||v->frame->height>4320){av_frame_unref(v->frame);return failure(AVERROR(EINVAL),"frame bounds");}
        ++v->decoded;++count;v->width=v->frame->width;v->height=v->frame->height;
#ifdef _WIN32
        if(v->frame->format==AV_PIX_FMT_D3D11)++v->hardware_frames;
#else
        if(v->hardware&&v->frame->format==AV_PIX_FMT_MEDIACODEC)++v->hardware_frames;
#endif
        if(v->newest->buf[0])++v->discarded;
        av_frame_unref(v->newest);av_frame_move_ref(v->newest,v->frame);
    }
    if(ret!=AVERROR(EAGAIN)&&ret!=AVERROR_EOF)return failure(ret,"receive frame");
    if(v->newest->buf[0]){if(render){ret=present(v,v->newest);if(ret<0){av_frame_unref(v->newest);return failure(ret,"present frame");}}av_frame_unref(v->newest);}
    return count;
}
API int qpv_decode(void* handle,const uint8_t* data,int size,int64_t pts,int key,int render){
    Video* v=(Video*)handle;if(!v||size<0||size>2097152)return failure(AVERROR(EINVAL),"packet");
    auto start=std::chrono::steady_clock::now();
    int ret=0;
    if(size){av_packet_unref(v->packet);ret=av_new_packet(v->packet,size);if(ret<0)return ret;memcpy(v->packet->data,data,size);v->packet->pts=v->packet->dts=pts;if(key)v->packet->flags|=AV_PKT_FLAG_KEY;
        ret=avcodec_send_packet(v->codec,v->packet);
        if(ret==AVERROR(EAGAIN)){ret=receive(v,render!=0);if(ret<0)return ret;ret=avcodec_send_packet(v->codec,v->packet);}
        av_packet_unref(v->packet);if(ret<0)return failure(ret,"send packet");}
    ret=receive(v,render!=0);v->last_decode_us=std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now()-start).count();return ret;
}
struct VideoStats {int width,height,hardware,reserved;int64_t decoded,presented,discarded,hardware_frames,decode_us;};
API int qpv_stats(void* handle,VideoStats* stats){auto v=(Video*)handle;if(!v||!stats)return -1;*stats={v->width,v->height,v->hardware,0,v->decoded,v->presented,v->discarded,v->hardware_frames,v->last_decode_us};return 0;}

#ifndef _WIN32
struct Encoder{AVCodecContext* codec=nullptr;AVPacket* packet=av_packet_alloc();AVBufferRef* device=nullptr;~Encoder(){av_packet_free(&packet);avcodec_free_context(&codec);av_buffer_unref(&device);}};
API void* qpv_encoder_open(int id,int width,int height,int fps,int bitrate,int gop,void* surface,const char* component,int vbr,int buffer_ms){
    std::string name=std::string(codec_name(id))+"_mediacodec";const AVCodec* codec=avcodec_find_encoder_by_name(name.c_str());
    if(!codec||!surface||width<128||height<128||fps<5||fps>144||bitrate<500000||bitrate>100000000){failure(AVERROR(EINVAL),"encoder configuration");return nullptr;}
    Encoder* e=new Encoder();e->codec=avcodec_alloc_context3(codec);auto c=e->codec;c->width=width;c->height=height;c->time_base={1,1000000};c->framerate={fps,1};c->bit_rate=bitrate;c->gop_size=std::max(1,fps*gop/1000);c->max_b_frames=0;c->rc_buffer_size=(int)((int64_t)bitrate*std::clamp(buffer_ms,16,250)/1000);c->pix_fmt=AV_PIX_FMT_MEDIACODEC;
    e->device=av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_MEDIACODEC);if(!e->device){delete e;return nullptr;}
    auto hw=(AVMediaCodecDeviceContext*)((AVHWDeviceContext*)e->device->data)->hwctx;hw->surface=surface;
    if(av_hwdevice_ctx_init(e->device)<0){failure(AVERROR_EXTERNAL,"encoder surface");delete e;return nullptr;}c->hw_device_ctx=av_buffer_ref(e->device);
    // Java persistent input surfaces must use the Java MediaCodec wrapper.
    // AMediaCodec_setInputSurface expects an NDK PersistentSurface, not a Java Surface native window.
    AVDictionary* options=nullptr;av_dict_set(&options,"ndk_codec","0",0);av_dict_set(&options,"bitrate_mode",vbr?"vbr":"cbr",0);if(component&&*component)av_dict_set(&options,"codec_name",component,0);
    int ret=avcodec_open2(c,codec,&options);av_dict_free(&options);if(ret<0){failure(ret,"open encoder");delete e;return nullptr;}return e;
}
API int qpv_encoder_read(void* handle,uint8_t* output,int capacity,int64_t* pts,int* key){
    auto e=(Encoder*)handle;if(!e)return -1;av_packet_unref(e->packet);int ret=avcodec_receive_packet(e->codec,e->packet);if(ret==AVERROR(EAGAIN))return 0;if(ret<0)return failure(ret,"encode");
    if(e->packet->size>capacity)return failure(AVERROR(ENOSPC),"encoded packet");memcpy(output,e->packet->data,e->packet->size);*pts=e->packet->pts;*key=(e->packet->flags&AV_PKT_FLAG_KEY)!=0;return e->packet->size;
}
API void qpv_encoder_close(void* handle){delete (Encoder*)handle;}
#endif
