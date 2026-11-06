#!/usr/bin/env python3
"""Build embedded resources, never called by the C# runtime.
Python 3.11+. Default: bundled small seed snapshot. --download: explicitly fetch public
RIPE Atlas and GeoNames snapshots for a fuller build. No probing, scanning or tests.
"""
from __future__ import annotations
import argparse, csv, gzip, hashlib, io, ipaddress, json, math, os, struct, sys, tempfile, time, urllib.request, urllib.parse, zipfile
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path


def atomic(path: Path, body: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temp = tempfile.mkstemp(prefix="." + path.name, dir=path.parent)
    try:
        with os.fdopen(handle, "wb") as stream:
            stream.write(body); stream.flush(); os.fsync(stream.fileno())
        os.replace(temp, path)
    finally:
        if os.path.exists(temp): os.unlink(temp)


def download(url: str, destination: Path, maximum: int = 64 * 1024 * 1024) -> bytes:
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme != 'https' or parsed.hostname not in {'atlas.ripe.net', 'download.geonames.org', 'www.speedtest.net'}:
        raise ValueError('Unexpected snapshot source: ' + url)
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={'User-Agent': 'QuicNet-OfflineCatalogBuilder/1.2', 'Accept': 'application/json'})
            with urllib.request.urlopen(request, timeout=45) as response:
                if urllib.parse.urlparse(response.geturl()).hostname != parsed.hostname:
                    raise ValueError('Unexpected cross-host redirect')
                body = response.read(maximum + 1)
            if len(body) > maximum: raise ValueError('Snapshot exceeds size limit')
            atomic(destination, body); return body
        except (OSError, TimeoutError):
            if attempt == 2: raise
            time.sleep(1 + attempt)
    raise RuntimeError('Download failed')


def full_snapshot(raw: Path) -> None:
    raw.mkdir(parents=True, exist_ok=True)
    records = []; seen = set(); url = 'https://atlas.ripe.net/api/v2/anchors/?page_size=100'
    for page in range(1, 501):
        if not url: break
        if url in seen: raise ValueError('Repeated RIPE pagination cursor')
        seen.add(url)
        data = json.loads(download(url, raw / f'anchors-{page}.json'))
        records.extend(data['results']); url = data.get('next')
    else: raise ValueError('Excessive RIPE pagination')
    atomic(raw / 'anchors-all.json', json.dumps(records, ensure_ascii=False).encode())
    for name in ['ES.zip', 'cities500.zip', 'admin1CodesASCII.txt', 'admin2Codes.txt', 'readme.txt']:
        download('https://download.geonames.org/export/dump/' + name, raw / name)
    atomic(raw / 'snapshot.json', json.dumps({'retrievedUtc': datetime.now(timezone.utc).isoformat(),
        'sources': ['https://atlas.ripe.net/api/v2/anchors/', 'https://download.geonames.org/export/dump/']}).encode())


def atlas_landmarks(rows: list[dict]) -> list[dict]:
    result = []; used = set()
    for row in sorted(rows, key=lambda r: int(r['id'])):
        if row.get('is_disabled') is not False or row.get('date_decommissioned'): continue
        coords = (row.get('geometry') or {}).get('coordinates', [])
        if len(coords) != 2: continue
        lon, lat = map(float, coords)
        if not all(map(math.isfinite, [lon, lat])) or abs(lon) > 180 or abs(lat) > 90: continue
        for family in [4, 6]:
            value = row.get(f'ip_v{family}')
            if not value: continue
            address = ipaddress.ip_address(value)
            if address.version != family or not address.is_global or str(address) in used: continue
            used.add(str(address))
            result.append({'id': f"ripe-{row['id']}-v{family}", 'address': str(address),
                'position': {'latitude': lat, 'longitude': lon},
                'source': 'RIPE Atlas public anchor operator declaration',
                'siteId': f"geo-site:{lat:.2f}:{lon:.2f}", 'asn': row.get(f'as_v{family}'),
                'name': row.get('city') or row.get('hostname'), 'countryCode': 'GB' if row.get('country') == 'UK' else row.get('country'),
                'regionName': row.get('quicnet_regionName'),
                'kind': 'anchor', 'locationVerified': False, 'coordinateEvidence': 'operator-declared',
                'anycast': False, 'uncertaintyKm': 50, 'tcpProbeAllowed': True, 'tcpPort': 443})
    if not result: raise ValueError('No usable anchors; existing resources left untouched')
    return result


def geonames_rows(archive: Path):
    with zipfile.ZipFile(archive) as z:
        name = archive.stem + '.txt'
        info = z.getinfo(name)
        if info.file_size > 512 * 1024 * 1024: raise ValueError('GeoNames input exceeds limit')
        with z.open(info) as binary, io.TextIOWrapper(binary, encoding='utf-8') as text:
            for row in csv.reader(text, delimiter='\t'):
                if len(row) >= 19: yield row


def geographic_places(raw: Path, scope: str) -> list[dict]:
    admin1 = {}
    if (raw / 'admin1CodesASCII.txt').exists():
        for row in csv.reader((raw / 'admin1CodesASCII.txt').open(encoding='utf-8'), delimiter='\t'):
            if len(row) >= 2: admin1[row[0]] = row[1]
    es = list(geonames_rows(raw / 'ES.zip'))
    municipalities = {(r[8], r[10], r[11], r[12]): r[1] for r in es if r[6:8] == ['A', 'ADM3']}
    selected = [r for r in es if r[6:8] == ['A', 'ADM3'] or scope == 'populated' and r[6] == 'P']
    if (raw / 'cities500.zip').exists():
        selected.extend(r for r in geonames_rows(raw / 'cities500.zip') if r[8] != 'ES' and r[6] == 'P')
    result = {}
    for r in selected:
        lat, lon = float(r[4]), float(r[5])
        if not math.isfinite(lat) or not math.isfinite(lon) or abs(lat) > 90 or abs(lon) > 180: continue
        result[int(r[0])] = {'id': int(r[0]), 'name': r[1],
            'municipality': r[1] if r[7] == 'ADM3' else municipalities.get((r[8], r[10], r[11], r[12]), ''),
            'region': admin1.get(r[8] + '.' + r[10], r[10]), 'countryCode': r[8],
            'position': {'latitude': lat, 'longitude': lon}, 'population': int(r[14] or 0),
            'featureCode': r[7], 'aliases': '|'.join(r[3].split(','))[:10000]}
    if not result: raise ValueError('No geographic places found')
    return list(result.values())


def locality_binary(places: list[dict]) -> bytes:
    if not 1 <= len(places) <= 500000: raise ValueError('Invalid locality count')
    groups = defaultdict(list)
    def text(value):
        data = str(value or '').encode('utf-8')
        if len(data) > 65535: raise ValueError('Locality text exceeds binary field capacity')
        return struct.pack('<H', len(data)) + data
    for p in sorted(places, key=lambda p: p['id']):
        lat, lon = p['position']['latitude'], p['position']['longitude']
        key = (max(-90, min(89, math.floor(lat))), max(-180, min(179, math.floor(lon))))
        row = struct.pack('<iffq', int(p['id']), lat, lon, int(p.get('population', 0)))
        row += b''.join(text(p.get(field, '')) for field in ['name', 'municipality', 'region', 'countryCode', 'featureCode', 'aliases'])
        groups[key].append(row)
    offset = 12 + 20 * len(groups); directory = bytearray(); pages = bytearray()
    for (lat, lon), rows in sorted(groups.items()):
        compressed = gzip.compress(b''.join(rows), compresslevel=9, mtime=0)
        directory.extend(struct.pack('<hhqii', lat, lon, offset, len(compressed), len(rows)))
        pages.extend(compressed); offset += len(compressed)
    return b'QPL2' + struct.pack('<ii', len(places), len(groups)) + directory + pages


def main() -> None:
    project = Path(__file__).resolve().parents[1]
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--raw', type=Path, default=project.parent / '.artifacts/geolocation-catalogs/raw')
    p.add_argument('--output', type=Path, default=project / 'Data')
    p.add_argument('--download', action='store_true', help='Explicit build-time network access; never runtime')
    p.add_argument('--scope', choices=['municipalities', 'populated'], default='populated')
    args = p.parse_args()
    if args.download: full_snapshot(args.raw)
    full = (args.raw / 'anchors-all.json').is_file() and (args.raw / 'ES.zip').is_file()
    if full:
        anchors = atlas_landmarks(json.loads((args.raw / 'anchors-all.json').read_text(encoding='utf-8')))
        if (args.raw / 'additional-public-targets.json').exists():
            anchors.extend(json.loads((args.raw / 'additional-public-targets.json').read_bytes()))
        supplement = args.raw / 'spanish-measurement-servers.json'
        seen = {a['address'] for a in anchors}
        if supplement.exists():
            for row in json.loads(supplement.read_text(encoding='utf-8')):
                lat, lon = float(row['lat']), float(row['lon'])
                for address in row['resolvedAddresses']:
                    ip = ipaddress.ip_address(address)
                    if not ip.is_global or str(ip) in seen: continue
                    seen.add(str(ip))
                    anchors.append({'id': f"public-speedtest-{row['id']}-v{ip.version}", 'address': str(ip),
                        'position': {'latitude': lat, 'longitude': lon}, 'siteId': f'geo-site:{lat:.2f}:{lon:.2f}',
                        'name': row['name'] + ' · ' + row['sponsor'], 'countryCode': 'ES', 'regionName': row['regionName'],
                        'source': 'https://www.speedtest.net/api/js/servers · public measurement server ' + row['id'] + ' · ' + row['host'],
                        'kind': 'anchor', 'locationVerified': False, 'coordinateEvidence': 'operator-declared',
                        'anycast': False, 'uncertaintyKm': 50, 'tcpProbeAllowed': True, 'tcpPort': int(row['host'].rsplit(':', 1)[1])})
        places = geographic_places(args.raw, args.scope)
        provenance = 'local raw RIPE Atlas and GeoNames snapshot; no live probe verification'
    else:
        seed = json.loads((project / 'Data' / 'seed-snapshot.json').read_text(encoding='utf-8'))
        anchors, places = seed['landmarks'], seed['places']
        provenance = seed['provenance']
    resources = {'anchors.json.gz': gzip.compress(json.dumps(anchors, ensure_ascii=False, separators=(',', ':')).encode(), compresslevel=9, mtime=0),
        'localities.bin': locality_binary(places)}
    for name in ['latency-calibration.json', 'spanish-measurement-servers.json', 'additional-public-targets.json']:
        if full and (args.raw / name).exists(): resources[name] = (args.raw / name).read_bytes()
    coverage = {'scope': 'raw-full' if full else 'small-seed', 'addresses': len(anchors),
        'referenceSites': len({a.get('siteId') or a['id'] for a in anchors}),
        'spanishAddresses': sum(a.get('countryCode') == 'ES' for a in anchors),
        'spanishReferenceSites': len({a.get('siteId') or a['id'] for a in anchors if a.get('countryCode') == 'ES'}),
        'geographicPlaces': len(places), 'countriesWithLandmarks': sorted({a.get('countryCode') for a in anchors if a.get('countryCode')}),
        'municipalityCoverageGuaranteed': False, 'trainedProfilesIncluded': False}
    if full and (args.raw / 'communities.json').exists():
        regional = []
        for r in json.loads((args.raw / 'communities.json').read_bytes()):
            targets = [a for a in anchors if a.get('countryCode') == 'ES' and a.get('regionName') == r['name']]
            regional.append({'id': r['id'], 'name': r['name'], 'addresses': len(targets),
                'ipv4': sum(ipaddress.ip_address(a['address']).version == 4 for a in targets),
                'ipv6': sum(ipaddress.ip_address(a['address']).version == 6 for a in targets),
                'sites': len({a.get('siteId') or a['id'] for a in targets}), 'physicallyVerifiedSites': 0})
        resources['spanish-coverage.json'] = json.dumps({'regions': regional,
            'policy': 'Catalogue coverage is not reachability or physically certified coverage.'}, ensure_ascii=False, indent=2).encode()
        coverage['coveredSpanishRegions'] = sum(r['sites'] > 0 for r in regional)
        coverage['missingSpanishRegions'] = [r['name'] for r in regional if not r['sites']]
    resources['coverage.json'] = json.dumps(coverage, ensure_ascii=False, indent=2).encode()
    locality = resources['localities.bin']
    resources['localities-manifest.json'] = json.dumps({'format': 'QPL2', 'records': len(places),
        'spanishRecords': sum(p['countryCode'] == 'ES' for p in places),
        'spanishMunicipalities': len({p['municipality'] for p in places if p['countryCode'] == 'ES' and p.get('municipality')}),
        'pages': struct.unpack_from('<i', locality, 8)[0], 'bytes': len(locality), 'sha256': hashlib.sha256(locality).hexdigest(),
        'license': 'CC BY 4.0', 'attribution': 'GeoNames, https://www.geonames.org/'}, ensure_ascii=False, indent=2).encode()
    # Run the same build-time migration for new/updated IP tables before recording metadata.
    legacy = [n for n in ['city.bin', 'asn.bin', 'routers.bin'] if (args.output / n).exists()]
    previous_manifest = args.output / 'manifest.json'
    from compact_ip_catalog import prepare_catalogs, inspect_catalog
    if legacy and previous_manifest.exists():
        prepare_catalogs(args.output)
    previous_tables = json.loads(previous_manifest.read_bytes()).get('tables', {}) if previous_manifest.exists() else {}
    tables = {}
    for name in legacy:
        tables[name[:-4]] = inspect_catalog(args.output / name, name == 'asn.bin')
        previous = previous_tables.get(name[:-4], {})
        if previous.get('sha256') == tables[name[:-4]]['sha256']:
            tables[name[:-4]] = {**previous, **tables[name[:-4]]}
    manifest = {'schemaVersion': 2, 'generatedUtc': datetime.now(timezone.utc).isoformat(), 'provenance': provenance,
        'coverage': coverage, 'tables': tables, 'resourceSha256': {n: hashlib.sha256(b).hexdigest() for n, b in resources.items()},
        'presentLegacyResources': legacy, 'missingLegacyResources': [n for n in ['city.bin', 'asn.bin', 'routers.bin'] if n not in legacy],
        'sourceSha256': {f.name: hashlib.sha256(f.read_bytes()).hexdigest() for f in args.raw.iterdir() if f.is_file() and f.suffix in ['.json', '.zip', '.txt']} if full else {},
        'locationPolicy': 'operator declarations remain conditional, not primary physical constraints'}
    resources['manifest.json'] = json.dumps(manifest, ensure_ascii=False, indent=2).encode()
    notice = ('RIPE Atlas public anchor records: https://atlas.ripe.net/api/v2/anchors/\n'
        'RIPE terms and measurement conditions: https://www.ripe.net/about-us/legal/\n'
        'Coordinates are operator declarations; availability has not been probed.\n' +
        ('Full RIPE Atlas snapshot and selected public Speedtest measurement endpoints; DNS resolved only at build time.\n' if full else 'The bundled seed is a small selected snapshot, NOT a complete Spanish municipality network.\n') +
        'When importing full GeoNames: https://www.geonames.org / CC BY 4.0\n'
        'https://creativecommons.org/licenses/by/4.0/ — data provided as-is.\n'
        'GeoNames points are not official municipal boundary polygons.\n'
        'Seed place coordinates describe the anchor city labels at infrastructure coordinates, not municipal centroids.\n')
    resources['NOTICE.txt'] = notice.encode()
    if tables.get('city', {}).get('source') == 'IPTrust geolocation':
        resources['NOTICE.txt'] += ('\nIP geolocation: IPTrust, local user-supplied ' + tables['city'].get('sourceFile', 'MMDB') + ', build ' +
            tables['city']['databaseDate'] + '. Converted to the compact paged GLP2 catalogue without network queries.\n'
            'Country-only records omit nominal capital labels; source identity and SHA-256 are in manifest.json.\n'
            'The input file did not include a licence statement; no additional redistribution rights are inferred.\n').encode()
    if 'asn' in tables:
        resources['NOTICE.txt'] += ('\nASN numbers only: ' + tables['asn'].get('source', 'ASN catalogue') + ', ' +
            tables['asn'].get('databaseDate', 'date not supplied') + ', https://db-ip.com/ (CC BY 4.0).\n').encode()
    for name, body in resources.items(): atomic(args.output / name, body)
    print(json.dumps(coverage, ensure_ascii=False, indent=2))

if __name__ == '__main__':
    try: main()
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        print('Catalog build failed: ' + str(error), file=sys.stderr); raise SystemExit(2)
