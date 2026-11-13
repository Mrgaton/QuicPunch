#!/usr/bin/env python3
"""Summarize frozen direct-library runs. No network; reference coordinates are evaluation only."""
import argparse,json,math
from pathlib import Path


def distance(a,b):
    la,lo,lb,ln=map(math.radians,[a['latitude'],a['longitude'],b['latitude'],b['longitude']])
    return 12742*math.asin(min(1,math.sqrt(math.sin((la-lb)/2)**2+math.cos(la)*math.cos(lb)*math.sin((lo-ln)/2)**2)))


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('directory',type=Path);p.add_argument('--reference-lat',type=float);p.add_argument('--reference-lon',type=float);args=p.parse_args()
    rows=[];reference=None if args.reference_lat is None or args.reference_lon is None else {'latitude':args.reference_lat,'longitude':args.reference_lon}
    for slug in ['quick','balanced','detailed']:
        result=json.loads((args.directory/(slug+'.archive.json')).read_bytes())['result'];final=result['final']
        execution=json.loads((args.directory/(slug+'.execution.json')).read_bytes());region=final.get('regionalRefinement',{}).get('calibratedRegion')
        observations=result['session']['observations'];good=[m for m in final['enhanced']['measurements'] if m.get('minimumMs') is not None]
        row={'preset':slug,'seconds':execution['elapsedSeconds'],'phase':final['phase'],'launches':final['sentPackets'],
            'completedEchoAttempts':sum(len(o['samples']) for o in observations),
            'otherOrUnrecordedAttempts':final['sentPackets']-sum(len(o['samples']) for o in observations),
            'targets':len(observations),'respondingSites':final['regionalRefinement']['respondingSites'],
            'primaryStatus':final['region']['status'],'cbgStatus':region['status'] if region else None,
            'cbgCenter':region.get('representative') if region else None,'cbgExtentKm':region.get('coveringRadiusKm') if region else None,
            'cbgAreaKm2':region['areaKm2'] if region else None,'fastest':min(good,key=lambda m:m['minimumMs']),
            'cbgConstraints':sum(c['evidence'].startswith('cbg-') for c in region['constraints']) if region else 0,
            'countries':[{k:h[k] for k in ['countryCode','relativeScore','basis']} for h in final['enhanced']['hierarchy']['hypotheses'] if h['level']=='country']}
        if reference and region and region.get('representative'):
            row['centerDistanceToEvaluationReferenceKm']=distance(reference,region['representative'])
            row['evaluationReferenceSatisfiesAllCbgBounds']=all(distance(reference,c['center'])<=c['radiusKm'] for c in region['constraints'])
        rows.append(row)
        print(slug,json.dumps({k:row[k] for k in ['seconds','launches','completedEchoAttempts','targets','cbgCenter','cbgExtentKm']},ensure_ascii=False))
    (args.directory/'summary.json').write_text(json.dumps({'referenceUsedOnlyForEvaluation':reference,'results':rows},ensure_ascii=False,indent=2),encoding='utf8')


if __name__=='__main__':main()
