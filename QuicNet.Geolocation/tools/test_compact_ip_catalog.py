import gzip
import json
import struct
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import compact_ip_catalog as c


def legacy(path, rows, asn=False):
    index, pages = [], []
    for base in range(0, len(rows), 4096):
        group = rows[base:base + 4096]
        body = bytearray()
        for family, start, end, value in group:
            body += bytes([family]) + start.to_bytes(16, 'big') + bytes([family]) + end.to_bytes(16, 'big')
            if asn:
                body += struct.pack('<I', value)
            else:
                body += value[0]
                for label in value[1:]:
                    encoded = label.encode('utf-8')
                    body += struct.pack('<H', len(encoded)) + encoded
        pages.append(gzip.compress(body, mtime=0))
        first, last = group[0], group[-1]
        index.append((bytes([first[0]]) + first[1].to_bytes(16, 'big'), bytes([last[0]]) + last[2].to_bytes(16, 'big'), len(group)))
    offset = 8 + 50 * len(index)
    out = bytearray(b'GLP1' + struct.pack('<I', len(index)))
    for (first, last, count), page in zip(index, pages):
        out += struct.pack('<17s17sqii', first, last, offset, len(page), count)
        offset += len(page)
    path.write_bytes(out + b''.join(pages))


class CompactCatalogTests(unittest.TestCase):
    def test_lossless_edges_gaps_utf8_and_wide_palette(self):
        with tempfile.TemporaryDirectory() as d:
            original, packed = Path(d)/'old.bin', Path(d)/'new.bin'
            rows = [(4, i*3, i*3+1, (struct.pack('<ff', -0.0 if i == 0 else 41.25, 2.5), 'ES', '', f'Peñíscola {i}')) for i in range(300)]
            rows += [(6, 1 << 64, (1 << 64) + 17, (struct.pack('<ff', 0, -0.0), 'JP', '', '')),
                     (6, (1 << 128)-1, (1 << 128)-1, rows[0][3])]
            legacy(original, rows)
            stats = c.pack_rows(c.legacy_rows(original), packed)
            self.assertEqual(c.row_digest(c.legacy_rows(original)), c.row_digest(c.compact_rows(packed)))
            self.assertEqual(list(c.compact_rows(packed)), rows)
            self.assertEqual(stats['oneByteValuePages'], 1)
            self.assertEqual(c.inspect_catalog(packed)['records'], len(rows))

    def test_asn_uint32_and_one_byte_palette(self):
        with tempfile.TemporaryDirectory() as d:
            original, packed = Path(d)/'old.bin', Path(d)/'new.bin'
            rows = [(4, 0, 0xffffffff, 0xffffffff), (6, 0, (1 << 128)-1, 0)]
            legacy(original, rows, True)
            c.pack_rows(c.legacy_rows(original, True), packed, True)
            self.assertEqual(list(c.compact_rows(packed)), rows)
            self.assertEqual(c.inspect_catalog(packed, True)['records'], 2)

    def test_failure_preserves_original_and_idempotent_workflow(self):
        with tempfile.TemporaryDirectory() as d:
            data = Path(d); path = data/'city.bin'
            legacy(path, [(4, 10, 20, (struct.pack('<ff', 1, 2), 'ES', 'Aragón', 'Aínsa'))])
            (data/'manifest.json').write_text(json.dumps({'tables': {'city': {'source': 'fixture', 'databaseDate': 'test'}}}))
            original = path.read_bytes()
            with patch.object(c, 'compact_rows', return_value=iter([])):
                with self.assertRaises(ValueError): c.prepare_catalogs(data)
            self.assertEqual(path.read_bytes(), original)
            c.prepare_catalogs(data)
            manifest = (data/'manifest.json').read_bytes(); packed = path.read_bytes()
            c.prepare_catalogs(data, check=True, verify=True)
            self.assertEqual(path.read_bytes(), packed)
            self.assertEqual((data/'manifest.json').read_bytes(), manifest)
            path.write_bytes(packed[:-1] + bytes([packed[-1] ^ 1]))
            with self.assertRaises(ValueError): c.prepare_catalogs(data, check=True)

    def test_interrupted_publish_recovers_metadata(self):
        with tempfile.TemporaryDirectory() as d:
            data = Path(d); path = data/'city.bin'
            legacy(path, [(4, 1, 3, (struct.pack('<ff', 1, 2), 'ES', '', 'fixture'))])
            (data/'manifest.json').write_text(json.dumps({'tables': {'city': {}}}))
            real_replace = c.os.replace
            def interrupted(src, dst):
                real_replace(src, dst)
                if Path(dst) == path: raise OSError('simulated power loss after table replacement')
            with patch.object(c.os, 'replace', side_effect=interrupted):
                with self.assertRaises(OSError): c.prepare_catalogs(data)
            self.assertTrue((data/'.catalog-update.pending').exists())
            c.prepare_catalogs(data, check=True, verify=True)
            self.assertFalse((data/'.catalog-update.pending').exists())

    def test_reject_overlap_and_invalid_order(self):
        with tempfile.TemporaryDirectory() as d:
            for rows in [[(4, 10, 20, 1), (4, 20, 30, 2)], [(6, 1, 2, 1), (4, 1, 2, 1)]]:
                with self.assertRaises(ValueError): c.pack_rows(rows, Path(d)/'new.bin', True)

    def test_missing_table_is_not_silently_published(self):
        with tempfile.TemporaryDirectory() as d:
            data = Path(d)
            (data/'manifest.json').write_text(json.dumps({'tables': {'city': {}}}))
            with self.assertRaises(FileNotFoundError): c.prepare_catalogs(data, check=True)


if __name__ == '__main__': unittest.main()
