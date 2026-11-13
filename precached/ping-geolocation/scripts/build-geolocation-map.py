"""Build offline Spanish communities from Natural Earth provinces. Build-only dependency: shapely.
Download the source explicitly with --download; raw inputs and Python caches stay disposable.
"""
import argparse, gzip, hashlib, json, sys, urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / '.artifacts' / 'map-source'
if (CACHE / 'python').exists(): sys.path.insert(0, str(CACHE / 'python'))
from shapely.geometry import shape, mapping
from shapely.ops import unary_union

URL = 'https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_10m_admin_1_states_provinces.geojson'
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--download', action='store_true')
p.add_argument('--source', type=Path, default=CACHE / 'admin1.geojson')
args = p.parse_args()
if args.download:
    args.source.parent.mkdir(parents=True, exist_ok=True)
    urllib.request.urlretrieve(URL, args.source)
features = json.loads(args.source.read_text(encoding='utf-8'))['features']
names = {'AN':'Andalucía','AR':'Aragón','AS':'Asturias','IB':'Illes Balears','CN':'Canarias','CB':'Cantabria',
         'CM':'Castilla-La Mancha','CL':'Castilla y León','CT':'Cataluña','VC':'Comunitat Valenciana',
         'EX':'Extremadura','GA':'Galicia','RI':'La Rioja','MD':'Comunidad de Madrid','MC':'Región de Murcia',
         'NA':'Navarra','PV':'País Vasco','CE':'Ceuta','ML':'Melilla'}
groups = {}
for f in features:
    a = f['properties']
    if a.get('adm0_a3') != 'ESP': continue
    code = a['iso_3166_2'][3:] if a['name'] in ('Ceuta','Melilla') else a['region_cod'][3:]
    code = {'MU':'MC','PM':'IB','LO':'RI'}.get(code, code)
    groups.setdefault(code, []).append(shape(f['geometry']))
communities = []
for code, polygons in sorted(groups.items()):
    geometry = unary_union(polygons).simplify(.002, preserve_topology=True)
    point = geometry.representative_point()
    communities.append({'id':code,'name':names[code],'geometry':mapping(geometry),
                        'bounds':list(geometry.bounds),'label':[point.x,point.y]})
assert len(communities) == 19
asset = ROOT / 'QuicNet.Geolocation' / 'Map' / 'map.json.gz'
data = json.loads(gzip.decompress(asset.read_bytes()))
data['communities'] = communities
data['communitySource'] = {'name':'Natural Earth 1:10m admin-1 (provinces dissolved into communities)',
                          'url':URL, 'sha256':hashlib.sha256(args.source.read_bytes()).hexdigest(),
                          'license':'public domain', 'purpose':'map context only; never geolocation evidence'}
asset.write_bytes(gzip.compress(json.dumps(data,ensure_ascii=False,separators=(',',':')).encode(),mtime=0))
print(f'{len(communities)} communities packaged, {asset.stat().st_size} bytes total; no runtime download.')
