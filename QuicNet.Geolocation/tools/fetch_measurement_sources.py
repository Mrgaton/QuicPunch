#!/usr/bin/env python3
"""Explicit, bounded build-time downloads. No measurements or downloads at application runtime.

python QuicNet.Geolocation/tools/fetch_measurement_sources.py --download
python QuicNet.Geolocation/tools/build_latency_calibration.py
python QuicNet.Geolocation/tools/build_catalogs.py
Cached raw files are reproducible/disposable; generated resources include source hashes.
"""
import argparse, concurrent.futures, gzip, ipaddress, json, math, socket, urllib.parse
from pathlib import Path
from build_catalogs import atomic, download

CAPITALS = ['Spain','Sevilla','Zaragoza','Oviedo','Palma','Las Palmas','Santander','Toledo','Valladolid',
    'Barcelona','Valencia','Merida','Santiago','Logrono','Madrid','Murcia','Pamplona','Bilbao','Ceuta','Melilla','Canarias']


def ring_contains(ring, x, y):
    inside=False
    for a,b in zip(ring,ring[1:]+ring[:1]):
        if (a[1]>y)!=(b[1]>y) and x < (b[0]-a[0])*(y-a[1])/(b[1]-a[1])+a[0]:inside=not inside
    return inside


def community_for(communities, lon, lat):
    for r in communities:
        g=r['geometry'];polygons=g['coordinates'] if g['type']=='MultiPolygon' else [g['coordinates']]
        if any(ring_contains(p[0],lon,lat) and not any(ring_contains(h,lon,lat) for h in p[1:]) for p in polygons):return r
    # Simplified coastlines can put an operator's city-centre point just outside the polygon.
    candidates=[]
    for r in communities:
        g=r['geometry'];polygons=g['coordinates'] if g['type']=='MultiPolygon' else [g['coordinates']]
        squared=min((lon-p[0])**2+(lat-p[1])**2 for poly in polygons for p in poly[0])
        if squared<.035**2:candidates.append((squared,r))
    return min(candidates,key=lambda x:x[0])[1] if candidates else None


def supplement(raw, communities):
    rows={}
    for f in sorted(raw.glob('speedtest-*.json')):
        for row in json.loads(f.read_bytes()):
            if row.get('cc')!='ES':continue
            r=community_for(communities,float(row['lon']),float(row['lat']))
            if r:rows[row['id']]={**row,'regionId':r['id'],'regionName':r['name']}
    selected=[]
    for region in sorted({r['regionId'] for r in rows.values()}):
        remaining=[r for r in rows.values() if r['regionId']==region]; chosen=[]; providers=set()
        while remaining and len(chosen)<10:
            row=max(remaining,key=lambda r:(r['sponsor'] not in providers,
                min([(float(r['lat'])-float(s['lat']))**2+(float(r['lon'])-float(s['lon']))**2 for s in chosen] or [0]),-int(r['id'])))
            chosen.append(row);providers.add(row['sponsor']);remaining.remove(row)
        selected.extend(chosen)
    def resolve(row):
        host=row['host'].rsplit(':',1)[0]
        try:row['resolvedAddresses']=sorted({a[4][0] for a in socket.getaddrinfo(host,None,socket.AF_UNSPEC,socket.SOCK_STREAM) if ipaddress.ip_address(a[4][0]).is_global})
        except OSError as error:row['resolvedAddresses']=[];row['dnsError']=str(error)
        return row
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:selected=list(pool.map(resolve,selected))
    assigned={}
    for r in selected:
        for ip in r['resolvedAddresses']:assigned.setdefault(ip,set()).add(r['regionId'])
    for r in selected:r['resolvedAddresses']=[ip for ip in r['resolvedAddresses'] if len(assigned[ip])==1]
    atomic(raw/'spanish-measurement-servers.json',json.dumps(selected,ensure_ascii=False,indent=2).encode())


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--download',action='store_true');p.add_argument('--raw',type=Path,default=Path('.artifacts/geolocation-catalogs/raw'));args=p.parse_args()
    if not args.download:p.error('--download is required for network access')
    raw=args.raw;raw.mkdir(parents=True,exist_ok=True)
    def fetch(url,name):
        target=raw/name
        return target.read_bytes() if target.exists() else download(url,target)
    def pages(endpoint,stem):
        records=[];page=1
        while page<=100:
            d=json.loads(fetch(f'https://atlas.ripe.net/api/v2/{endpoint}?page_size=500&page={page}',f'{stem}-{page}.json'));records.extend(d['results'])
            if not d.get('next'):break
            page+=1
        else:raise ValueError('Pagination exceeds bound')
        atomic(raw/f'{stem}-all.json',json.dumps(records,ensure_ascii=False).encode());return records
    anchors=pages('anchors/','anchors')
    for name in ['ES.zip','cities500.zip','admin1CodesASCII.txt','admin2Codes.txt','readme.txt']:
        fetch('https://download.geonames.org/export/dump/'+name,name)
    for term in CAPITALS:
        name='speedtest-'+('es' if term=='Spain' else term.lower().replace(' ','-'))+'.json'
        fetch('https://www.speedtest.net/api/js/servers?engine=js&search='+urllib.parse.quote(term)+'&limit=100',name)
    communities=json.loads(gzip.decompress(Path('QuicNet.Geolocation/Map/map.json.gz').read_bytes()))['communities']
    atomic(raw/'communities.json',json.dumps([{k:r[k] for k in ['id','name','bounds']} for r in communities],ensure_ascii=False).encode())
    # Classifying official operator coordinates is geographic bookkeeping, never a measured verification.
    for a in anchors:
        if a.get('country')=='ES' and (xy:=(a.get('geometry') or {}).get('coordinates')):
            r=community_for(communities,*xy)
            if r:a['quicnet_regionName']=r['name']
    atomic(raw/'anchors-all.json',json.dumps(anchors,ensure_ascii=False).encode())
    if not (raw/'spanish-measurement-servers.json').exists():supplement(raw,communities)
    extra = raw/'additional-public-targets.json'
    if not extra.exists():
        # The operator publishes these measurement endpoints and city names on its own frontend.
        # Coordinates are city-centre references with nominal 50km uncertainty, not certified hardware GPS.
        import urllib.request
        html = urllib.request.urlopen('https://speedtest.rediris.es/',timeout=30).read(2000000)
        atomic(raw/'rediris-speedtest.html',html)
        nodes = [('speedtest.rediris.es','Madrid',40.4168,-3.7038,'Comunidad de Madrid'),
            ('speedtest-rediris.cesga.es','Santiago de Compostela',42.8805,-8.5457,'Galicia'),
            ('speedtest-rediris.scayle.es','León',42.5987,-5.5671,'Castilla y León')]
        targets=[]
        for host,city,lat,lon,region in nodes:
            if host.encode() not in html:raise ValueError('Published RedIRIS endpoint changed: '+host)
            for ip in sorted({r[4][0] for r in socket.getaddrinfo(host,None,socket.AF_INET,socket.SOCK_STREAM)}):
                if not ipaddress.ip_address(ip).is_global:continue
                targets.append({'id':'rediris-speedtest-'+host,'address':ip,'position':{'latitude':lat,'longitude':lon},
                    'source':'https://speedtest.rediris.es/ · operator-published LibreSpeed node '+host,
                    'siteId':f'geo-site:{lat:.2f}:{lon:.2f}','name':city+' · RedIRIS LibreSpeed','countryCode':'ES','regionName':region,
                    'kind':'anchor','locationVerified':False,'coordinateEvidence':'operator-declared','uncertaintyKm':50,'tcpProbeAllowed':True,'tcpPort':443})
        atomic(extra,json.dumps(targets,ensure_ascii=False,indent=2).encode())
    index=[]
    # This endpoint does not support target/type filters: fetch the bounded index, filter locally.
    if (raw/'mesh-index-all.json').exists():index=json.loads((raw/'mesh-index-all.json').read_bytes())
    else:
        for page in range(1,101):
            d=json.loads(fetch(f'https://atlas.ripe.net/api/v2/anchor-measurements/?is_active=true&page_size=500&page={page}',f'mesh-index-{page}.json'));index.extend(d['results'])
            if not d.get('next'):break
        else:raise ValueError('Mesh index exceeds bound')
        atomic(raw/'mesh-index-all.json',json.dumps(index).encode())
    active=[a for a in anchors if a.get('is_disabled') is False and not a.get('date_decommissioned')]
    selected=[a for a in active if a['country']=='ES']
    for cc in ['PT','FR','GB','DE','IT','NL','BE','CH','CZ','PL','FI','EE','NO','GR','US','BR','AU','JP','ZA','IN','SG']:
        candidates=[a for a in active if a['country']==cc]
        if candidates:selected.append(min(candidates,key=lambda a:int(a['id'])))
    ids={a['id'] for a in selected}
    for record in index:
        target=int(record['target'].rstrip('/').split('/')[-1])
        if target in ids and record['type']=='ping' and record['is_mesh'] and record['is_active']:
            mid=int(record['measurement'].rstrip('/').split('/')[-1]);fetch(record['measurement']+'latest/',f'mesh-latest-{mid}.json')
    print('Downloaded sources ready. Build the calibration and catalog; inspect coverage.json for gaps.')


if __name__=='__main__':main()
