"""Exercise the compiled C ABI with real synthetic compressed frames, no capture."""
import ctypes as c
import pathlib
import json
import base64
import time
import os
import sys

root=pathlib.Path(__file__).resolve().parents[2]
windows=sys.platform=='win32'
if windows:
    directory=root/'QuicNet.Shared/runtimes/win-x64/native'
    search=os.add_dll_directory(str(directory))
    lib=c.CDLL(str(directory/'nettt_media.dll'))
else:
    lib=c.CDLL(str(root/'QuicNet.Shared/bin/native/linux-x64/libnettt_media.so'))
lib.qpv_version.restype=c.c_char_p
lib.qpv_error.restype=c.c_char_p
lib.qpv_open.restype=c.c_void_p
lib.qpv_open.argtypes=[c.c_int,c.c_int,c.c_int,c.c_int,c.c_void_p,c.c_int,c.c_void_p,c.c_char_p]
lib.qpv_close.argtypes=[c.c_void_p]
lib.qpv_decode.argtypes=[c.c_void_p,c.c_void_p,c.c_int,c.c_int64,c.c_int,c.c_int]
class Stats(c.Structure):
    _fields_=[('width',c.c_int),('height',c.c_int),('hardware',c.c_int),('reserved',c.c_int),*[(name,c.c_int64) for name in ['decoded','presented','discarded','hardware_frames','decode_us']]]
lib.qpv_stats.argtypes=[c.c_void_p,c.POINTER(Stats)]
surface=None
if '--render' in sys.argv:
    lib.qpv_surface_create.restype=c.c_void_p;lib.qpv_surface_create.argtypes=[c.c_void_p]
    lib.qpv_surface_destroy.argtypes=[c.c_void_p]
    lib.qpv_desktop_surface_crop.argtypes=[c.c_void_p,*[c.c_int]*5,*[c.c_double]*4]
    if windows:
        user=c.WinDLL('user32',use_last_error=True)
        user.CreateWindowExW.restype=c.c_void_p
        user.CreateWindowExW.argtypes=[c.c_uint32,c.c_wchar_p,c.c_wchar_p,c.c_uint32,c.c_int,c.c_int,c.c_int,c.c_int,c.c_void_p,c.c_void_p,c.c_void_p,c.c_void_p]
        user.DestroyWindow.argtypes=[c.c_void_p]
        parent=user.CreateWindowExW(0,'STATIC','NetTT synthetic renderer test',0x90000000,-20000,-20000,640,360,None,None,None,None)
        assert parent,c.get_last_error()
        user.SendMessageW.argtypes=[c.c_void_p,c.c_uint32,c.c_size_t,c.c_ssize_t]
        user.SendMessageW.restype=c.c_ssize_t
    else:
        x11=c.CDLL('libX11.so.6')
        x11.XInitThreads()
        x11.XOpenDisplay.restype=c.c_void_p
        x11.XDefaultRootWindow.argtypes=[c.c_void_p]
        x11.XDefaultRootWindow.restype=c.c_ulong
        x11.XCreateSimpleWindow.argtypes=[c.c_void_p,c.c_ulong,c.c_int,c.c_int,c.c_uint,c.c_uint,c.c_uint,c.c_ulong,c.c_ulong]
        x11.XCreateSimpleWindow.restype=c.c_ulong
        x11.XMapWindow.argtypes=[c.c_void_p,c.c_ulong]
        x11.XSync.argtypes=[c.c_void_p,c.c_int]
        x11.XCloseDisplay.argtypes=[c.c_void_p]
        display=x11.XOpenDisplay(None)
        assert display, 'Use an isolated X server for the renderer test.'
        parent=x11.XCreateSimpleWindow(display,x11.XDefaultRootWindow(display),0,0,800,450,0,0,0)
        x11.XMapWindow(display,parent);x11.XSync(display,0)
    def layout(surface, width, height):
        lib.qpv_desktop_surface_crop(surface,0,0,width,height,1,.48,.48,.52,.52)
    surface=lib.qpv_surface_create(parent);assert surface
    layout(surface,640,360)
    if windows:
        assert user.SendMessageW(surface,0x84,0,0)==-1, 'Video window intercepts WebView hit tests.'
print('Native FFmpeg' ,lib.qpv_version().decode())
for fixture in json.loads((root/'QuicNet.Shared/Video/Probes.json').read_text()):
    for hardware in [0,1]:
        extra=base64.b64decode(fixture['description']) if fixture['description'] else b''
        decoder=lib.qpv_open(fixture['codecId'],hardware,fixture['width'],fixture['height'],extra,len(extra),surface,None)
        if not decoder:
            assert hardware, lib.qpv_error()
            print(fixture['codec'],'hardware unavailable:',lib.qpv_error().decode());continue
        error=None
        try:
            for index,encoded in enumerate(fixture['packets']):
                if surface and index in (0,4):layout(surface,640 if index==0 else 800,360 if index==0 else 450)
                packet=base64.b64decode(encoded);length=int.from_bytes(packet[1:3],'big');metadata=json.loads(packet[3:3+length]);data=packet[3+length:]
                result=lib.qpv_decode(decoder,data,len(data),metadata['t'],packet[0]&1,1 if surface else 0)
                if surface:time.sleep(.034)
                if result<0:error=lib.qpv_error().decode();break
            stats=Stats();lib.qpv_stats(decoder,c.byref(stats))
            if error or not stats.decoded:
                assert hardware,(fixture['codec'],error,stats.decoded)
                print(fixture['codec'],'hardware unavailable:',error or 'no frames')
            else:
                assert (stats.width,stats.height)==(640,360)
                if surface:assert stats.presented>0,(fixture['codec'],'no native presentation')
                assert not hardware or stats.hardware_frames==stats.decoded
                print(fixture['codec'],'hardware' if hardware else 'software',stats.decoded,'frames',stats.decode_us,'us last packet')
        finally:lib.qpv_close(decoder)
print('PASS: all software decoders produced real frames.')

if surface:
    lib.qpv_surface_destroy(surface)
    if windows:user.DestroyWindow(parent)
    else:x11.XCloseDisplay(display)
