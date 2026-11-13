// Synthetic input used only by the Debug Android test activity; never packaged in Release.
#include <jni.h>
#include <android/native_window_jni.h>
#include <EGL/egl.h>
#include <GLES2/gl2.h>
#include <thread>
#include <chrono>
extern "C" __attribute__((visibility("default"))) int qpt_pattern(void* jni,void* surface,int frames) {
    auto window=ANativeWindow_fromSurface((JNIEnv*)jni,(jobject)surface);
    if(!window)return -1;
    EGLDisplay display=eglGetDisplay(EGL_DEFAULT_DISPLAY);eglInitialize(display,nullptr,nullptr);
    const EGLint attributes[]={EGL_SURFACE_TYPE,EGL_WINDOW_BIT,EGL_RENDERABLE_TYPE,EGL_OPENGL_ES2_BIT,EGL_RED_SIZE,8,EGL_GREEN_SIZE,8,EGL_BLUE_SIZE,8,EGL_NONE};
    EGLConfig config;EGLint count;
    if(!eglChooseConfig(display,attributes,&config,1,&count)||!count){ANativeWindow_release(window);return -2;}
    const EGLint contextAttrs[]={EGL_CONTEXT_CLIENT_VERSION,2,EGL_NONE};
    EGLContext context=eglCreateContext(display,config,EGL_NO_CONTEXT,contextAttrs);
    EGLSurface target=eglCreateWindowSurface(display,config,window,nullptr);
    if(!eglMakeCurrent(display,target,target,context)){ANativeWindow_release(window);return -3;}
    glViewport(0,0,640,360);
    int result=0;
    for(int i=0;i<frames;i++){
        glDisable(GL_SCISSOR_TEST);glClearColor((i%30)/30.0f,.2f,.7f,1);glClear(GL_COLOR_BUFFER_BIT);
        glEnable(GL_SCISSOR_TEST);glScissor((i*10)%600,0,40,360);glClearColor(1,1,0,1);glClear(GL_COLOR_BUFFER_BIT);
        if(!eglSwapBuffers(display,target)){result=-4;break;}
        std::this_thread::sleep_for(std::chrono::milliseconds(33));
    }
    eglMakeCurrent(display,EGL_NO_SURFACE,EGL_NO_SURFACE,EGL_NO_CONTEXT);eglDestroySurface(display,target);eglDestroyContext(display,context);ANativeWindow_release(window);return result;
}
