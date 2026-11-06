import gzip, hashlib, json, struct, unittest
from pathlib import Path
from build_catalogs import atlas_landmarks, locality_binary
from build_latency_calibration import bestline


class CatalogBuilderTests(unittest.TestCase):
    def test_cbg_lower_envelope_not_least_squares(self):
        points=[(100,3),(200,4),(300,5),(150,80)]
        slope,intercept=bestline(points)
        self.assertAlmostEqual(slope,.01);self.assertAlmostEqual(intercept,2)
        self.assertTrue(all(slope*x+intercept <= y+1e-9 for x,y in points))
        self.assertIsNone(bestline([(1000,.1),(2000,.2)]))

    def test_disabled_private_and_duplicate_atlas_addresses(self):
        base={'id':1,'geometry':{'coordinates':[2,41]},'is_disabled':False,'ip_v4':'84.88.18.254','country':'ES','city':'Fixture'}
        rows=atlas_landmarks([base,{**base,'id':2,'is_disabled':True},{**base,'id':3,'ip_v4':'192.168.1.1'},
            {**base,'id':4},{**base,'id':5,'date_decommissioned':'2026-01-01'}])
        self.assertEqual(len(rows),1);self.assertFalse(rows[0]['locationVerified'])

    def test_tiled_gazetteer_utf8_and_antimeridian(self):
        places=[{'id':i,'name':n,'position':{'latitude':lat,'longitude':lon}} for i,n,lat,lon in
            [(1,'Pequeño lugar',41,2),(2,'Dateline',-17,179.9),(3,'Other side',-17,-179.9)]]
        binary=locality_binary(places)
        self.assertEqual(binary[:4],b'QPL2');self.assertEqual(struct.unpack_from('<ii',binary,4),(3,3))
        self.assertEqual(binary,locality_binary(list(reversed(places))))

    def test_shipped_hashes_counts_and_coverage(self):
        root=Path(__file__).resolve().parents[1]/'Data';manifest=json.loads((root/'manifest.json').read_bytes())
        for name,digest in manifest['resourceSha256'].items():self.assertEqual(hashlib.sha256((root/name).read_bytes()).hexdigest(),digest,name)
        anchors=json.loads(gzip.decompress((root/'anchors.json.gz').read_bytes()))
        self.assertEqual(len(anchors),manifest['coverage']['addresses'])
        self.assertEqual(len({a['id'] for a in anchors}),len(anchors))
        self.assertEqual(len({a['address'] for a in anchors}),len(anchors))
        regions=json.loads((root/'spanish-coverage.json').read_bytes())['regions']
        self.assertEqual(len(regions),19)
        self.assertEqual({r['name'] for r in regions if not r['sites']},set(manifest['coverage']['missingSpanishRegions']))
        self.assertTrue(all(r['physicallyVerifiedSites']==0 for r in regions))


if __name__=='__main__':unittest.main()
