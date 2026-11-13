"""Regenerate synthetic decoder fixtures; never captures user content."""
import base64
import json
import pathlib
import struct
import subprocess
import sys

root=pathlib.Path(__file__).resolve().parent.parent
ffmpeg=pathlib.Path(sys.argv[1])
ffprobe=ffmpeg.with_name('ffprobe.exe' if sys.platform=='win32' else 'ffprobe')
work=root/'.artifacts'/'native-video-probes'
work.mkdir(parents=True,exist_ok=True)
def unhex(value):
    return bytes.fromhex(''.join(line.split(':',1)[1].strip().split('  ',1)[0] for line in value.splitlines() if ':' in line))
fixtures=[]
for codec,id,encoder,tuning in [
    ('avc1.64001E',1,'libx264',['-preset','veryfast','-tune','zerolatency','-bf','0']),
    ('hvc1.1.6.L90.B0',5,'libx265',['-preset','ultrafast','-x265-params','bframes=0:rc-lookahead=0:log-level=error']),
    ('vp09.00.30.08',3,'libvpx-vp9',['-deadline','realtime','-cpu-used','8','-lag-in-frames','0']),
    ('av01.0.04M.08',4,'libsvtav1',['-preset','12','-svtav1-params','tune=0'])]:
    file=work/f'{id}.mkv'
    subprocess.run([str(ffmpeg),'-y','-v','error','-f','lavfi','-i','testsrc2=size=640x360:rate=30','-frames:v','8','-c:v',encoder,'-g','4','-pix_fmt','yuv420p',*tuning,str(file)],check=True)
    result=json.loads(subprocess.check_output([str(ffprobe),'-v','error','-show_streams','-show_packets','-show_data','-of','json',str(file)]))
    stream=result['streams'][0]
    desc=base64.b64encode(unhex(stream['extradata'])).decode() if stream.get('extradata') else None
    packets=[]
    for packet in result['packets']:
        metadata=json.dumps(dict(c=codec,d=desc,t=round(float(packet['pts_time'])*1e6),g=1,w=640,h=360),separators=(',',':')).encode()
        wire=bytes([2 | int('K' in packet['flags'])])+struct.pack('>H',len(metadata))+metadata+unhex(packet['data'])
        packets.append(base64.b64encode(wire).decode())
    fixtures.append(dict(codecId=id,codec=codec,width=640,height=360,description=desc,packets=packets))
(root/'QuicNet.Shared'/'Video'/'Probes.json').write_text(json.dumps(fixtures,separators=(',',':')))
print('Generated four 640x360 synthetic FFmpeg decoder probes (8 frames each).')
