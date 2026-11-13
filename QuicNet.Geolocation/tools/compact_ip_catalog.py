"""Lossless GLP1 -> GLP2 IP catalogue packing (Python + brotli, build time only).

GLP2: packed 4/16-byte IP page bounds; delta/length varints within each page;
one/two-byte page-local value IDs; shared country, UTF-8 and float32 location
dictionaries; independently Brotli-compressed pages. Runtime uses .NET Brotli.
"""
import argparse
import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import struct
import tempfile
from contextlib import contextmanager

# Only conversion needs this optional build dependency; --check works with stdlib.
try:
    import brotli
except ImportError:
    brotli = None

PAGE_SIZE = 4096


def varint(value):
    if value < 0 or value >= 1 << 128:
        raise ValueError('Integer outside UInt128')
    result = bytearray()
    while value >= 128:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return bytes(result)


def read_varint(stream):
    value = 0
    for shift in range(0, 133, 7):
        part = stream.read(1)
        if not part:
            raise ValueError('Truncated varint')
        value |= (part[0] & 127) << shift
        if not part[0] & 128:
            if value >= 1 << 128:
                raise ValueError('Varint exceeds UInt128')
            return value
    raise ValueError('Unterminated varint')


def exact(stream, length):
    data = stream.read(length)
    if len(data) != length:
        raise ValueError('Truncated catalogue')
    return data


def legacy_rows(path, asn=False):
    with Path(path).open('rb') as source:
        if exact(source, 4) != b'GLP1':
            raise ValueError('Expected GLP1 input')
        count, = struct.unpack('<I', exact(source, 4))
        entries = [struct.unpack('<17s17sqii', exact(source, 50)) for _ in range(count)]
        for first, last, offset, length, count in entries:
            source.seek(offset)
            page = io.BytesIO(gzip.decompress(exact(source, length)))
            for _ in range(count):
                start, end = exact(page, 17), exact(page, 17)
                if start[0] != end[0] or start[0] not in (4, 6):
                    raise ValueError('Invalid address family')
                if asn:
                    value, = struct.unpack('<I', exact(page, 4))
                else:
                    coordinates = exact(page, 8)  # Preserve float32 bits, including signed zero.
                    labels = [exact(page, struct.unpack('<H', exact(page, 2))[0]).decode('utf-8') for _ in range(3)]
                    value = (coordinates, *labels)
                yield start[0], int.from_bytes(start[1:], 'big'), int.from_bytes(end[1:], 'big'), value
            if page.read(1):
                raise ValueError('Trailing GLP1 page data')


def compact_rows(path):
    """Independent decoder also used to verify every encoded range before replacement."""
    if brotli is None:
        raise RuntimeError('Full verification requires: python -m pip install -r QuicNet.Geolocation/tools/requirements-catalogs.txt')
    with Path(path).open('rb') as source:
        if exact(source, 4) != b'GLP2':
            raise ValueError('Expected GLP2 input')
        kind, dictionary_size, page_count = struct.unpack('<BII', exact(source, 9))
        index = []
        for _ in range(page_count):
            version = exact(source, 1)[0]
            width = 4 if version == 4 else 16
            first = int.from_bytes(exact(source, width), 'big')
            last = int.from_bytes(exact(source, width), 'big')
            index.append((version, first, last, *struct.unpack('<IIH', exact(source, 10))))
        locations = []
        if dictionary_size:
            dictionary = io.BytesIO(brotli.decompress(exact(source, dictionary_size)))
            countries = [exact(dictionary, read_varint(dictionary)).decode('utf-8') for _ in range(read_varint(dictionary))]
            strings = [exact(dictionary, read_varint(dictionary)).decode('utf-8') for _ in range(read_varint(dictionary))]
            for _ in range(read_varint(dictionary)):
                coordinates = exact(dictionary, 8)
                country = countries[exact(dictionary, 1)[0]]
                region, city = strings[read_varint(dictionary)], strings[read_varint(dictionary)]
                locations.append((coordinates, country, region, city))
            if dictionary.read(1):
                raise ValueError('Trailing dictionary data')
        for version, first, last, offset, length, count in index:
            source.seek(offset)
            page = io.BytesIO(brotli.decompress(exact(source, length)))
            palette = [read_varint(page) for _ in range(read_varint(page))]
            width = 1 if len(palette) <= 256 else 2
            cursor = first
            for _ in range(count):
                start = cursor + read_varint(page)
                end = start + read_varint(page)
                value = palette[int.from_bytes(exact(page, width), 'little')]
                yield version, start, end, value if kind else locations[value]
                cursor = end + 1
            if cursor != last + 1 or page.read(1):
                raise ValueError('Invalid GLP2 page bounds')


def row_digest(rows):
    digest = hashlib.sha256()
    count = 0
    for version, first, last, value in rows:
        digest.update(bytes([version]) + first.to_bytes(16, 'big') + last.to_bytes(16, 'big'))
        if isinstance(value, int):
            digest.update(struct.pack('<I', value))
        else:
            digest.update(value[0])
            for label in value[1:]:
                encoded = label.encode('utf-8')
                digest.update(struct.pack('<I', len(encoded)) + encoded)
        count += 1
    return count, digest.hexdigest()


def pack_rows(rows, destination, asn=False):
    """Write GLP2 using bounded range pages; full dictionaries contain unique values only."""
    if brotli is None:
        raise RuntimeError('Conversion requires: python -m pip install -r QuicNet.Geolocation/tools/requirements-catalogs.txt')
    strings, countries, locations = {}, {}, {}
    index, page = [], []
    byte_pages = 0
    total = one_byte_pages = 0
    previous = None
    with tempfile.TemporaryFile(dir=Path(destination).parent) as pages:
        def flush():
            nonlocal page, one_byte_pages, byte_pages
            if not page:
                return
            palette = dict.fromkeys(item[3] for item in page)
            palette = {value: i for i, value in enumerate(palette)}
            width = 1 if len(palette) <= 256 else 2
            one_byte_pages += width == 1
            body = bytearray(varint(len(palette)))
            for value in palette:
                body.extend(varint(value))
            cursor = page[0][1]
            for version, start, end, value in page:
                body.extend(varint(start - cursor))
                body.extend(varint(end - start))
                body.extend(palette[value].to_bytes(width, 'little'))
                cursor = end + 1
            compressed = brotli.compress(bytes(body), quality=9)
            index.append((page[0][0], page[0][1], page[-1][2], pages.tell(), len(compressed), len(page)))
            byte_pages += len(compressed)
            pages.write(compressed)
            page = []

        for version, start, end, value in rows:
            if version not in (4, 6) or start > end or end >= 1 << (32 if version == 4 else 128):
                raise ValueError('Invalid range')
            if previous and (version < previous[0] or version == previous[0] and start <= previous[1]):
                raise ValueError('Overlapping or unordered ranges')
            if page and (page[0][0] != version or len(page) >= PAGE_SIZE):
                flush()
            previous = version, end
            if not asn:
                location_id = locations.get(value)
                if location_id is None:
                    location_id = len(locations)
                    locations[value] = location_id
                    countries.setdefault(value[1], len(countries))
                    for label in value[2:]:
                        strings.setdefault(label, len(strings))
                value = location_id
            elif value < 0 or value > 0xffffffff:
                raise ValueError('ASN exceeds UInt32')
            page.append((version, start, end, value))
            total += 1
        flush()
        if not total:
            raise ValueError('Empty catalogue')
        if len(countries) > 256:
            raise ValueError('Country dictionary exceeds one-byte IDs')
        if len(strings) > 2_000_000 or len(locations) > 2_000_000 or len(index) > 100_000:
            raise ValueError('Catalogue exceeds runtime dictionary/directory limits')
        dictionary = bytearray()
        if not asn:
            dictionary.extend(varint(len(countries)))
            for label in countries:
                encoded = label.encode('utf-8')
                dictionary.extend(varint(len(encoded)) + encoded)
            dictionary.extend(varint(len(strings)))
            for label in strings:
                encoded = label.encode('utf-8')
                dictionary.extend(varint(len(encoded)) + encoded)
            dictionary.extend(varint(len(locations)))
            for coordinates, country, region, city in locations:
                dictionary.extend(coordinates)
                dictionary.append(countries[country])
                dictionary.extend(varint(strings[region]) + varint(strings[city]))
        compressed_dictionary = brotli.compress(bytes(dictionary), quality=11) if dictionary else b''
        if len(dictionary) > 64 * 1024 * 1024 or len(compressed_dictionary) > 64 * 1024 * 1024:
            raise ValueError('Catalogue dictionary exceeds runtime memory limit')
        base = 13 + sum(11 + (8 if p[0] == 4 else 32) for p in index) + len(compressed_dictionary)
        with Path(destination).open('wb') as output:
            output.write(b'GLP2' + struct.pack('<BII', int(asn), len(compressed_dictionary), len(index)))
            for version, first, last, offset, size, count in index:
                width = 4 if version == 4 else 16
                output.write(bytes([version]) + first.to_bytes(width, 'big') + last.to_bytes(width, 'big'))
                output.write(struct.pack('<IIH', base + offset, size, count))
            output.write(compressed_dictionary)
            pages.seek(0)
            while chunk := pages.read(1024 * 1024):
                output.write(chunk)
    return dict(format='GLP2', compression='Brotli', records=total, pages=len(index),
        oneByteValuePages=one_byte_pages, uniqueLocations=len(locations), uniqueLabels=len(strings),
        countryCodes=len(countries), dictionaryBytes=len(compressed_dictionary), rangePageBytes=byte_pages)


def compress_catalog(path, asn=False, before_replace=None):
    path = Path(path)
    with path.open('rb') as source:
        if source.read(4) == b'GLP2':
            return None
    before = path.stat().st_size
    handle, temp = tempfile.mkstemp(dir=path.parent, suffix='.tmp', prefix=path.stem + '-compact-')
    os.close(handle)
    try:
        stats = pack_rows(legacy_rows(path, asn), temp, asn)
        original_digest = row_digest(legacy_rows(path, asn))
        compact_digest = row_digest(compact_rows(temp))
        if original_digest != compact_digest:
            raise ValueError('Lossless verification failed; original catalogue left untouched')
        with open(temp, 'rb') as source:
            stats.update(bytes=Path(temp).stat().st_size, sha256=hashlib.file_digest(source, 'sha256').hexdigest(),
                decodedSha256=original_digest[1], previousBytes=before)
        if before_replace:
            before_replace(stats)
        os.replace(temp, path)
        return stats
    finally:
        if os.path.exists(temp):
            os.unlink(temp)


def atomic_json(path, value):
    handle, temp = tempfile.mkstemp(dir=path.parent, prefix=path.name + '-', suffix='.tmp')
    try:
        with os.fdopen(handle, 'w', encoding='utf-8', newline='\n') as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2)
            stream.write('\n')
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temp, path)
    finally:
        if os.path.exists(temp):
            os.unlink(temp)


def file_hash(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def inspect_catalog(path, asn=False):
    """Cheap structural check and streaming file hash; does not need Brotli or load the file."""
    size = path.stat().st_size
    with path.open('rb') as stream:
        if exact(stream, 4) != b'GLP2':
            raise ValueError(f'{path.name}: expected GLP2; run scripts/prepare-geolocation.py')
        kind, dictionary_size, count = struct.unpack('<BII', exact(stream, 9))
        if kind != int(asn) or not 1 <= count <= 100000 or dictionary_size > 64 * 1024 * 1024 or bool(dictionary_size) == asn:
            raise ValueError('Invalid GLP2 header')
        records, previous, first_offset, next_offset = 0, None, None, None
        for _ in range(count):
            family = exact(stream, 1)[0]
            if family not in (4, 6):
                raise ValueError('Invalid IP family')
            width = 4 if family == 4 else 16
            first, last = (int.from_bytes(exact(stream, width), 'big') for _ in range(2))
            offset, length, rows = struct.unpack('<IIH', exact(stream, 10))
            if first > last or not 1 <= rows <= PAGE_SIZE or not 0 < length <= 1024 * 1024 or offset + length > size:
                raise ValueError('Invalid GLP2 page bounds')
            if previous and (family < previous[0] or family == previous[0] and first <= previous[1]):
                raise ValueError('Unordered GLP2 pages')
            if next_offset is not None and offset != next_offset:
                raise ValueError('Non-contiguous GLP2 pages')
            first_offset = offset if first_offset is None else first_offset
            next_offset, previous = offset + length, (family, last)
            records += rows
        if first_offset != stream.tell() + dictionary_size or next_offset != size:
            raise ValueError('Invalid GLP2 file length')
    return dict(format='GLP2', compression='Brotli', records=records, pages=count,
                dictionaryBytes=dictionary_size, bytes=size, sha256=file_hash(path))


def recover_update(data):
    """Finish metadata after an interruption between the two atomic file replacements."""
    pending = data / '.catalog-update.pending'
    if not pending.exists():
        return
    state = json.loads(pending.read_bytes())
    name = state['table']
    if name not in ('city', 'asn'):
        raise ValueError('Invalid pending catalogue update')
    path = data / (name + '.bin')
    digest = file_hash(path) if path.exists() else None
    if digest == state['newSha256']:
        atomic_json(data / 'manifest.json', state['manifest'])
    elif digest != state['oldSha256']:
        raise ValueError('Interrupted catalogue update has unexpected file hash; originals preserved')
    pending.unlink()


@contextmanager
def catalog_lock(data):
    """OS lock is released even after a killed build; the inert lock file is never embedded."""
    path = Path(data) / '.catalog-prepare.lock'
    with path.open('a+b') as stream:
        if not path.stat().st_size:
            stream.write(b'\0'); stream.flush()
        stream.seek(0)
        if os.name == 'nt':
            import msvcrt
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == 'nt':
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def publish_catalog(data, name, prepared, metadata):
    """Publish an already verified import; caller holds catalog_lock."""
    data = Path(data)
    recover_update(data)
    manifest = json.loads((data / 'manifest.json').read_bytes())
    manifest.setdefault('tables', {})[name] = metadata
    path = data / (name + '.bin')
    atomic_json(data / '.catalog-update.pending', dict(table=name,
        oldSha256=file_hash(path) if path.exists() else None,
        newSha256=metadata['sha256'], manifest=manifest))
    os.replace(prepared, path)
    atomic_json(data / 'manifest.json', manifest)
    (data / '.catalog-update.pending').unlink()


def prepare_catalogs(data, check=False, verify=False):
    with catalog_lock(data):
        return _prepare_catalogs(Path(data), check, verify)


def _prepare_catalogs(data, check=False, verify=False):
    data = Path(data)
    recover_update(data)
    manifest_path = data / 'manifest.json'
    manifest = json.loads(manifest_path.read_bytes())
    results = {}
    for name in ('city', 'asn'):
        path = data / (name + '.bin')
        if not path.exists():
            if name in manifest.get('tables', {}):
                raise FileNotFoundError(f'Missing catalogue declared in manifest: {path}')
            continue
        asn = name == 'asn'
        with path.open('rb') as stream:
            magic = stream.read(4)
        previous = manifest.get('tables', {}).get(name, {})
        if magic == b'GLP1' and not check:
            print('Packing and verifying ' + name + '...', flush=True)
            old_hash = file_hash(path)
            def journal(stats):
                manifest.setdefault('tables', {})[name] = {**previous, **stats}
                atomic_json(data / '.catalog-update.pending', dict(table=name,
                    oldSha256=old_hash, newSha256=stats['sha256'], manifest=manifest))
            stats = compress_catalog(path, asn, before_replace=journal)
            atomic_json(manifest_path, manifest)
            (data / '.catalog-update.pending').unlink()
        else:
            stats = inspect_catalog(path, asn)
            for key in ('format', 'records', 'bytes', 'sha256'):
                if previous.get(key) != stats[key]:
                    raise ValueError(f'{name}: manifest {key} mismatch (update/import through the preparation workflow)')
            if verify:
                count, digest = row_digest(compact_rows(path))
                if count != stats['records'] or digest != previous.get('decodedSha256'):
                    raise ValueError(f'{name}: decoded data verification failed')
                stats['decodedSha256'] = digest
        results[name] = stats
        print(json.dumps(dict(table=name, **stats)), flush=True)
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data', type=Path, default=Path(__file__).resolve().parents[1] / 'Data')
    parser.add_argument('--check', action='store_true', help='Check format, directory, sizes, counts and hashes without changing files; Brotli not required')
    parser.add_argument('--verify', action='store_true', help='Also decode and hash every row of existing GLP2 tables')
    args = parser.parse_args()
    prepare_catalogs(args.data, args.check, args.verify)


if __name__ == '__main__':
    main()
