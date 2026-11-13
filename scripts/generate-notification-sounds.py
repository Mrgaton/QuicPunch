"""Regenerate Android's stable notification resources (no third-party dependencies).

Keep the waveform in sync with NotificationAudio; NotificationTests compares bytes.
"""
import math
from pathlib import Path
import struct
import wave

root = Path(__file__).resolve().parent.parent / "QuicPunch.AndroidProbe/Resources/raw"
root.mkdir(parents=True, exist_ok=True)
rate = 22050
for category in ("messages", "files", "screen", "system", "calls"):
    two = category in ("messages", "files", "screen")
    count = rate * 13 // 10 if category == "calls" else rate * 3 // 5 if two else rate // 3
    samples = []
    for i in range(count):
        t = i / rate
        if category == "calls":
            pulse = t if t < .46 else t - .68 if .68 <= t < 1.14 else -1
            envelope = 0 if pulse < 0 else min(1, pulse / .015) * min(1, (.46 - pulse) / .04)
            value = (.6 * math.sin(2 * math.pi * 440 * t) + .4 * math.sin(2 * math.pi * 660 * t)) * envelope * (.8 + .2 * math.cos(2 * math.pi * 12 * t)) * 18000
        else:
            second = two and t >= .28
            local = t - .28 if second else t
            pitch = (1.5 if category == "files" else .75 if category == "screen" else 1.25) if second else 1
            envelope = 0 if two and not second and t > .22 else min(1, local * 100) * math.exp(-local * 12)
            value = math.sin(2 * math.pi * 660 * pitch * local) * envelope * 12000
        samples.append(struct.pack('<h', int(value)))
    with wave.open(str(root / ("notice_" + category + ".wav")), 'wb') as output:
        output.setparams((1, 2, rate, 0, 'NONE', 'not compressed'))
        output.writeframes(b''.join(samples))
