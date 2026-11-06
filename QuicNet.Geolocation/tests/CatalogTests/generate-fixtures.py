"""Generate small cross-language GLP2 fixtures into obj/, never runtime resources."""
import io
import json
from pathlib import Path
import struct
import sys
import brotli

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tools'))
import compact_ip_catalog as c

out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent / 'obj/fixtures'
out.mkdir(parents=True, exist_ok=True)
rows = [(4, i*3, i*3+1, (struct.pack('<ff', -0.0 if i == 0 else 41.25, 2.5), 'ES', '', f'Peñíscola {i}')) for i in range(300)]
rows += [(6, 1 << 64, (1 << 64)+17, (struct.pack('<ff', 0, -0.0), 'JP', '', '')),
         (6, (1 << 128)-1, (1 << 128)-1, rows[0][3])]
c.pack_rows(iter(rows), out/'city.bin')
c.pack_rows(iter([(4, 0, 0xffffffff, 0xffffffff), (6, 0, (1 << 128)-1, 0)]), out/'asn.bin', True)
expected = []
for family, first, last, (coordinates, country, region, city) in rows:
    lat, lon = struct.unpack('<ii', coordinates)
    expected.append(dict(family=family, first=str(first), last=str(last), latitudeBits=lat, longitudeBits=lon,
                         country=country, region=region, city=city))
(out/'expected.json').write_text(json.dumps(expected, ensure_ascii=False), encoding='utf-8')

# One-page ASN fixtures exercise the runtime's validation, independently of its encoder.
def malformed(name, raw, first=0, last=1, family=4):
    packed = brotli.compress(raw)
    width = 4 if family == 4 else 16
    body = b'GLP2' + struct.pack('<BII', 1, 0, 1) + bytes([family])
    body += first.to_bytes(width, 'big') + last.to_bytes(width, 'big')
    body += struct.pack('<IIH', 13+1+2*width+10, len(packed), 1) + packed
    (out/(name+'.bad')).write_bytes(body)

valid = c.varint(1) + c.varint(15169) + c.varint(0) + c.varint(1) + b'\0'
malformed('trailing', valid+b'\0')
malformed('palette', valid[:-1]+b'\x01')
malformed('overflow', c.varint(1)+c.varint(1)+b'\x80'*19)
malformed('range', valid, last=0)
malformed('empty-palette', b'\0')
malformed('ipv6-overflow', c.varint(1)+c.varint(1)+c.varint(1)+c.varint(0)+b'\0',
          first=(1 << 128)-1, last=(1 << 128)-1, family=6)
print('Generated GLP2 fixtures:', out)
