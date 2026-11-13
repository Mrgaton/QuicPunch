"""Exercise the compiled C ABI with real synthetic compressed frames, no capture."""
import ctypes as c
import pathlib
import json
import base64
import time
import os
import sys

root=pathlib.Path(__file__).resolve().parents[2]
directory=root/'QuicNet.Shared/runtimes/win-x64/native'
search=os.add_dll_directory(str(directory))
lib=c.CDLL(str(directory/'nettt_media.dll'))
lib.qpv_version.restype=c.c_char_p
lib.qpv_error.restype=c.c_char_p
lib.qpv_open.restype=c.c_void_p
lib.qpv_open.argtypes=[c.c_int,c.c_int,c.c_int,c.c_int,c.c_void_p,c.c_int,c.c_void_p,c.c_char_p]
lib.qpv_close.argtypes=[c.c_void_p]
lib.qpv_decode.argtypes=[c.c_void_p,c.c_void_p,c.c_int,c.c_int64,c.c_int,c.c_int]
class Stats(c.Structure):
    _fields_=[('width',c.c_int),('height',c.c_int),('hardware',c.c_int),('reserved',c.c_int),*[(name,c.c_int64) for name in ['decoded','presented','discarded','hardware_frames','decode_us']]]
lib.qpv_stats.argtypes=[c.c_void_p,c.POINTER(Stats)]
surface=None;parent=None
if '--render' in sys.argv:
    user=c.WinDLL('user32',use_last_error=True)
    user.CreateWindowExW.restype=c.c_void_p
    user.CreateWindowExW.argtypes=[c.c_uint32,c.c_wchar_p,c.c_wchar_p,c.c_uint32,c.c_int,c.c_int,c.c_int,c.c_int,c.c_void_p,c.c_void_p,c.c_void_p,c.c_void_p]
    user.DestroyWindow.argtypes=[c.c_void_p]
    parent=user.CreateWindowExW(0,'STATIC','NetTT synthetic renderer test',0x90000000,-20000,-20000,640,360,None,None,None,None)
    assert parent,c.get_last_error()
    lib.qpv_surface_create.restype=c.c_void_p;lib.qpv_surface_create.argtypes=[c.c_void_p]
    lib.qpv_surface_layout.argtypes=[c.c_void_p,*[c.c_int]*5]
    lib.qpv_surface_destroy.argtypes=[c.c_void_p]
    surface=lib.qpv_surface_create(parent);assert surface
    lib.qpv_surface_layout(surface,0,0,640,360,1)
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
                if surface and index in (0,4):lib.qpv_surface_layout(surface,0,0,640 if index==0 else 800,360 if index==0 else 450,1)
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
print('PASS: all software decoders produced real frames; hardware reported only when frames use D3D11.')

if surface:lib.qpv_surface_destroy(surface);user.DestroyWindow(parent)
