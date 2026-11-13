#!/usr/bin/env python3
"""Build CBG bestlines from a downloaded RIPE anchor mesh. Never probes a target.

The lower hull solves the two-variable LP in Gueye et al., IMC 2004:
maximize sum(distance)*slope + n*intercept, below every observed RTT.
Declarations, direction changes and route changes keep this an empirical hypothesis.
"""
import argparse, hashlib, json, math
from datetime import datetime, timezone
from pathlib import Path


def distance(a, b):
    lon1, lat1 = map(math.radians, a); lon2, lat2 = map(math.radians, b)
    return 12742 * math.asin(min(1, math.sqrt(math.sin((lat1-lat2)/2)**2 + math.cos(lat1)*math.cos(lat2)*math.sin((lon1-lon2)/2)**2)))


def bestline(points):
    baseline = 2 / 299.792458
    hull = []
    for point in sorted(set(points)):
        while len(hull) >= 2:
            a, b = hull[-2:]
            if (b[0]-a[0])*(point[1]-b[1])-(b[1]-a[1])*(point[0]-b[0]) > 0: break
            hull.pop()
        hull.append(point)
    maximum = min(y/x for x,y in points if x > 0)
    if maximum < baseline: return None
    slopes = [baseline, maximum] + [(b[1]-a[1])/(b[0]-a[0]) for a,b in zip(hull,hull[1:]) if b[0]>a[0]]
    candidates = [(m, max(0, min(y-m*x for x,y in points))) for m in slopes if baseline <= m <= maximum]
    return max(candidates, key=lambda v: sum(x for x,y in points)*v[0]+len(points)*v[1])


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--raw',type=Path,default=Path('.artifacts/geolocation-catalogs/raw'));args=p.parse_args();raw=args.raw
    anchors={x['probe']:x for x in json.loads((raw/'anchors-all.json').read_bytes()) if x.get('is_disabled') is False and not x.get('date_decommissioned')}
    targets={x['id']:x for x in anchors.values()}; index=json.loads((raw/'mesh-index-all.json').read_bytes()); result=[]
    for record in index:
        if record['type']!='ping' or not record['is_mesh'] or not record['is_active']: continue
        mid=int(record['measurement'].rstrip('/').split('/')[-1]); file=raw/f'mesh-latest-{mid}.json'
        if not file.exists():continue
        target=targets.get(int(record['target'].rstrip('/').split('/')[-1]))
        if not target:continue
        readings=json.loads(file.read_bytes());points=[];rejected=0; timestamps=[]; family=None
        for r in readings:
            source=anchors.get(r.get('prb_id'));rtt=r.get('min',-1)
            if source is None or source['id']==target['id'] or not isinstance(rtt,(int,float)) or rtt<=0 or not r.get('dst_addr'):continue
            af=r.get('af'); expected=target.get(f'ip_v{af}')
            if r['dst_addr'] != expected:continue
            km=distance(source['geometry']['coordinates'],target['geometry']['coordinates'])
            if km<20:continue
            # Instrument tolerance is explicit; rows inconsistent even with vacuum propagation are audited.
            if rtt+1 < km*2/299.792458:rejected+=1;continue
            points.append((km,rtt+1));family=af;timestamps.append(r['timestamp'])
        if len(points)<8:continue
        line=bestline(points)
        if line is None:continue
        m,b=line
        result.append({'landmarkId':f"ripe-{target['id']}-v{family}",'address':target[f'ip_v{family}'], 'slopeMsPerKm':m,'interceptMs':b,
            'sites':len(points),'measurementId':mid,'capturedUtc':datetime.fromtimestamp(min(timestamps),timezone.utc).isoformat(),
            'rejectedPhysicalRows':rejected,'rawSha256':hashlib.sha256(file.read_bytes()).hexdigest()})
    output={'schemaVersion':1,'method':'CBG lower-envelope LP; vacuum baseline; +1ms mesh timing tolerance; operator coordinates; directional transfer is conditional',
        'source':'https://atlas.ripe.net/api/v2/anchor-measurements/','paper':'https://conferences.sigcomm.org/imc/2004/papers/p288-gueye.pdf',
        'generatedUtc':datetime.now(timezone.utc).isoformat(),'lines':result}
    (raw/'latency-calibration.json').write_text(json.dumps(output,ensure_ascii=False,indent=2),encoding='utf8')
    print('CBG bestlines:',len(result),'Spanish:',sum(x['landmarkId'].split('-')[1] in {str(t['id']) for t in targets.values() if t['country']=='ES'} for x in result))


if __name__=='__main__':main()
