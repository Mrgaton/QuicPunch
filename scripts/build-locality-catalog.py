"""Build the offline GeoNames gazetteer; raw downloads are disposable and reproducible.
No per-IP requests, API accounts, coordinates or user data are sent to GeoNames.
"""
import argparse
import datetime as dt
import gzip
import hashlib
import io
import json
import math
from pathlib import Path
import struct
import urllib.request
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--cache', type=Path, default=Path('.artifacts/locality-sources'))
parser.add_argument('--output', type=Path, default=Path('QuicNet.Geolocation/Data'))
parser.add_argument('--snapshot', default=dt.date.today().isoformat())
args = parser.parse_args()
args.cache.mkdir(parents=True, exist_ok=True)
args.output.mkdir(parents=True, exist_ok=True)
sources = []

def download(name):
    url = 'https://download.geonames.org/export/dump/' + name
    target = args.cache / name
    if not target.exists():
        request = urllib.request.Request(url, headers={'User-Agent': 'QuicNet-offline-catalog/1.0'})
        with urllib.request.urlopen(request, timeout=120) as response:
            data = response.read(80_000_001)
        if len(data) > 80_000_000:
            raise ValueError('Source exceeds size limit: ' + name)
        target.write_bytes(data)
    data = target.read_bytes()
    sources.append({'url': url, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
    return data

def lines(data, member=None):
    if member:
        data = zipfile.ZipFile(io.BytesIO(data)).read(member)
    return (line.split('\t') for line in data.decode('utf-8').splitlines() if line and not line.startswith('#'))

regions = {row[0]: row[1] for row in lines(download('admin1CodesASCII.txt'))}
spanish = list(lines(download('ES.zip'), 'ES.txt'))
# Spanish admin3 is the municipality; other countries are NOT assigned this hierarchy by analogy.
municipalities = {'.'.join((r[8], r[10], r[11], r[12])): r[1] for r in spanish if r[7] == 'ADM3'}
settlements = {'PPL', 'PPLA', 'PPLA2', 'PPLA3', 'PPLA4', 'PPLC', 'PPLG', 'PPLL', 'PPLX'}
places = {}
represented_municipalities = set()
for row in list(lines(download('cities500.zip'), 'cities500.txt')) + spanish:
    if len(row) < 19 or row[6] != 'P' or row[7] not in settlements:
        continue
    municipality = municipalities.get('.'.join((row[8], row[10], row[11], row[12])), '') if row[8] == 'ES' else ''
    if municipality:
        represented_municipalities.add('.'.join((row[8], row[10], row[11], row[12])))
    # Aliases support matching the IP catalogue's labels without replacing them with a nearby village.
    aliases = list(dict.fromkeys([row[1], row[2], *row[3].split(',')]))[:48]
    places[int(row[0])] = (row[1], municipality, regions.get(row[8] + '.' + row[10], ''), row[8],
                           float(row[4]), float(row[5]), int(row[14] or 0), row[7], '|'.join(aliases))

groups = {}
for identifier, place in places.items():
    key = (min(89, math.floor(place[4])), min(179, math.floor(place[5])))
    groups.setdefault(key, []).append((identifier, place))
def text(output, value):
    encoded = value.encode('utf-8')
    if len(encoded) > 65535:
        raise ValueError('String too long')
    output.write(struct.pack('<H', len(encoded)))
    output.write(encoded)
pages = []
for key, entries in sorted(groups.items()):
    output = io.BytesIO()
    for identifier, place in sorted(entries):
        name, municipality, region, country, lat, lon, population, kind, aliases = place
        output.write(struct.pack('<iffq', identifier, lat, lon, population))
        for value in (name, municipality, region, country, kind, aliases):
            text(output, value)
    pages.append((key, len(entries), gzip.compress(output.getvalue(), compresslevel=9, mtime=0)))
output = io.BytesIO()
output.write(b'QPL2')
output.write(struct.pack('<ii', len(places), len(pages)))
offset = 12 + 20 * len(pages)
for (lat, lon), count, compressed in pages:
    output.write(struct.pack('<hhqii', lat, lon, offset, len(compressed), count))
    offset += len(compressed)
for key, count, compressed in pages:
    output.write(compressed)
packed = output.getvalue()
(args.output / 'localities.bin').write_bytes(packed)
manifest = {'format': 'QPL2', 'snapshot': args.snapshot, 'license': 'CC BY 4.0',
            'attribution': 'GeoNames, https://www.geonames.org/', 'records': len(places),
            'spanishRecords': sum(p[3] == 'ES' for p in places.values()),
            'spanishMunicipalities': len(represented_municipalities),
            'bytes': len(packed), 'pages': len(pages), 'sha256': hashlib.sha256(packed).hexdigest(), 'sources': sources,
            'coverage': 'Global cities500 and populated places in ES, including sub-500 settlements. Municipality names are explicit ES ADM3 relationships, never inferred from distance. Points are not administrative boundaries.'}
(args.output / 'localities-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({k: v for k, v in manifest.items() if k not in ('sources', 'coverage')}, ensure_ascii=False))
