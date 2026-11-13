#!/usr/bin/env python3
"""Import a local IPTrust MMDB into the paged, compressed offline city catalogue.

Requires Python 3.11+ and maxminddb (pip install maxminddb). No network access.
The original MMDB is read only. The ASN catalogue is independent.
"""
import argparse
import gzip
import hashlib
import ipaddress
import json
import math
import os
from pathlib import Path
import struct
import tempfile
import sys
from datetime import datetime, timezone

try:
    import maxminddb
except ImportError:
    raise SystemExit('Import requires: python -m pip install -r QuicNet.Geolocation/tools/requirements-catalogs.txt')

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'QuicNet.Geolocation/tools'))
from compact_ip_catalog import prepare_catalogs, compress_catalog, catalog_lock, publish_catalog
PAGE_SIZE = 4096


def location(record):
    data = record.get('location') or {}
    country = data.get('country_iso2', '')
    resolution = data.get('resolution')
    # Country-only records can contain a capital's city/coordinates. Do not
    # promote that capital to a measured or published city for the address.
    city = (data.get('city') or '') if resolution == 'city' else ''
    region = (data.get('state') or '') if resolution in ('city', 'state', 'region') else ''
    lat = data.get('latitude') if resolution == 'city' else data.get('area_latitude')
    lon = data.get('longitude') if resolution == 'city' else data.get('area_longitude')
    if (len(country) != 2 or not country.isascii() or not country.isalpha() or
            not isinstance(lat, (int, float)) or not isinstance(lon, (int, float)) or
            not math.isfinite(lat) or not math.isfinite(lon) or abs(lat) > 90 or abs(lon) > 180):
        return None
    return lat, lon, country.upper(), region, city


def key(version, number):
    return bytes([version]) + number.to_bytes(16, 'big')


def text(value):
    encoded = value.encode('utf-8')
    if len(encoded) > 65535:
        raise ValueError('Catalogue label exceeds UInt16 length')
    return struct.pack('<H', len(encoded)) + encoded


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mmdb', type=Path)
    parser.add_argument('--output', type=Path, default=ROOT / 'QuicNet.Geolocation/Data')
    parser.add_argument('--fixtures', type=Path, default=ROOT / 'QuicNet.Geolocation/tests/catalog-boundaries.json')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    manifest_path = args.output / 'manifest.json'
    manifest = json.loads(manifest_path.read_bytes())
    fixtures = json.loads(args.fixtures.read_bytes()) if args.fixtures.exists() else []
    index, page, total, raw, skipped = [], [], 0, 0, 0
    first_key = last_key = None

    with maxminddb.open_database(str(args.mmdb)) as reader, tempfile.TemporaryFile(dir=args.output) as pages:
        metadata = reader.metadata()
        if metadata.database_type != 'iptrust-geolocation':
            raise ValueError('Expected iptrust-geolocation, got ' + metadata.database_type)
        date = datetime.fromtimestamp(metadata.build_epoch, timezone.utc).strftime('%Y-%m-%d')

        def flush():
            nonlocal page
            if not page:
                return
            body = gzip.compress(b''.join(page), compresslevel=6, mtime=0)
            index.append((first_key, last_key, pages.tell(), len(body), len(page)))
            pages.write(body)
            page = []

        def append(version, start, end, value):
            nonlocal first_key, last_key, total
            start_key, end_key = key(version, start), key(version, end)
            if last_key is not None and start_key <= last_key:
                raise ValueError('MMDB ranges are overlapping or out of order')
            if not page:
                first_key = start_key
            last_key = end_key
            lat, lon, country, region, city = value
            page.append(start_key + end_key + struct.pack('<ff', lat, lon) + text(country) + text(region) + text(city))
            total += 1
            if len(page) >= PAGE_SIZE:
                flush()

        pending = None
        for network, record in reader:
            raw += 1
            value = location(record)
            if value is None:
                skipped += 1
                continue
            current = (network.version, int(network.network_address), int(network.broadcast_address), value)
            if pending and pending[0] == current[0] and pending[2] + 1 == current[1] and pending[3] == value:
                pending = (pending[0], pending[1], current[2], value)
            else:
                if pending:
                    append(*pending)
                pending = current
            if raw % 1_000_000 == 0:
                print(f'Processed {raw:,} MMDB ranges...', flush=True)
        if pending:
            append(*pending)
        flush()
        if not total:
            raise ValueError('MMDB contains no usable locations')
        handle, temporary = tempfile.mkstemp(dir=args.output, prefix='city-', suffix='.tmp')
        try:
            with os.fdopen(handle, 'wb') as output:
                output.write(b'GLP1' + struct.pack('<i', len(index)))
                base = 8 + len(index) * 50
                for start, end, offset, length, count in index:
                    output.write(start + end + struct.pack('<qii', base + offset, length, count))
                pages.seek(0)
                while chunk := pages.read(1024 * 1024):
                    output.write(chunk)
            compact_stats = compress_catalog(Path(temporary))
        except BaseException:
            if os.path.exists(temporary):
                os.unlink(temporary)
            raise

        # Existing boundary addresses are an independent sample of both families;
        # replace their expected city values directly from the input MMDB. Keep
        # the independent ASN fixtures, since this MMDB has no ASN information.
        expected = []
        for item in fixtures:
            if 'asn' in item:
                expected.append(item)
                continue
            value = location(reader.get(item['address']) or {})
            if value:
                lat, lon, country, region, city = value
                expected.append(dict(address=item['address'], country=country, region=region, city=city, latitude=lat, longitude=lon))
        # Include network boundaries selected from the new source, and its
        # country-only entry whose nominal city must not be displayed.
        for address in ['8.8.8.8', '81.2.69.160', '2001:4860:4860::8888', '2001:218::']:
            _, prefix = reader.get_with_prefix_len(address)
            network = ipaddress.ip_network(f'{address}/{prefix}', strict=False)
            for boundary in (network.network_address, network.broadcast_address):
                value = location(reader.get(str(boundary)) or {})
                if value:
                    lat, lon, country, region, city = value
                    expected.append(dict(address=str(boundary), country=country, region=region, city=city, latitude=lat, longitude=lon))
        if expected:
            args.fixtures.parent.mkdir(parents=True, exist_ok=True)
            unique = {(item['address'], 'asn' in item): item for item in expected}
            args.fixtures.write_text(json.dumps(list(unique.values()), ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

    metadata = dict(**compact_stats,
        source='IPTrust geolocation', databaseDate=date,
        sourceFile=args.mmdb.name, sourceSha256=sha256(args.mmdb),
        inputRanges=raw, skippedRanges=skipped,
        description='Local IPTrust MMDB import; country-only entries omit city/state and use area coordinates.')
    try:
        with catalog_lock(args.output):
            publish_catalog(args.output, 'city', temporary, metadata)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    notice_path = args.output / 'NOTICE.txt'
    notice = notice_path.read_text(encoding='utf-8') if notice_path.exists() else ''
    start = notice.find('IP geolocation: IPTrust,')
    if start >= 0:
        end = notice.find('\nASN numbers only:', start)
        notice = notice[:start] + (notice[end:] if end >= 0 else '')
    notice += (f'\nIP geolocation: IPTrust, local user-supplied {args.mmdb.name}, build {date}.\n'
        'Converted to the compact paged GLP2 catalogue without network queries. Country-only records\n'
        'omit nominal capital labels; source identity and SHA-256 are in manifest.json.\n'
        'The input file did not include a licence statement; no additional redistribution rights are inferred.\n')
    notice_path.write_text(notice, encoding='utf-8')
    # All newly imported/updated catalogues use the same verified build-time workflow.
    # The GLP1 intermediate is replaced only after the complete GLP2 round-trip succeeds.
    prepare_catalogs(args.output)


if __name__ == '__main__':
    main()
