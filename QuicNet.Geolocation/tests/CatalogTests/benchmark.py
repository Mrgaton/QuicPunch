"""Reproducible GLP1/GLP2 benchmarks, in separate .NET processes. Never shipped.

Without --baseline, regenerate the legacy representation from the verified GLP2
rows into obj/. An original GLP1 directory can be supplied for independent checks.
"""
import argparse
import gzip
import json
from pathlib import Path
import shutil
import statistics
import struct
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(ROOT/'QuicNet.Geolocation/tools'))
import compact_ip_catalog as c


def export_legacy(path, output, asn):
    index, page = [], []
    first = last = None
    with tempfile.TemporaryFile(dir=output.parent) as pages:
        def flush():
            if page:
                body = gzip.compress(b''.join(page), compresslevel=6, mtime=0)
                index.append((first, last, pages.tell(), len(body), len(page)))
                pages.write(body)
                page.clear()
        for family, start, end, value in c.compact_rows(path):
            begin = bytes([family])+start.to_bytes(16, 'big')
            last = bytes([family])+end.to_bytes(16, 'big')
            if not page: first = begin
            row = begin+last
            if asn:
                row += struct.pack('<I', value)
            else:
                row += value[0]
                for label in value[1:]:
                    encoded = label.encode('utf-8'); row += struct.pack('<H', len(encoded))+encoded
            page.append(row)
            if len(page) == 4096: flush()
        flush()
        with output.open('wb') as stream:
            stream.write(b'GLP1'+struct.pack('<I', len(index)))
            base = 8+50*len(index)
            for a,b,offset,size,count in index:
                stream.write(struct.pack('<17s17sqii',a,b,base+offset,size,count))
            pages.seek(0); shutil.copyfileobj(pages, stream)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data', type=Path, default=ROOT/'QuicNet.Geolocation/Data')
    parser.add_argument('--baseline', type=Path)
    parser.add_argument('--repetitions', type=int, default=3)
    parser.add_argument('--output', type=Path, default=HERE/'obj/benchmark-results.json')
    args = parser.parse_args()
    if args.repetitions < 1: parser.error('At least one repetition is required')
    args.data = args.data.resolve()
    baseline = args.baseline.resolve() if args.baseline else HERE/'obj/legacy-baseline'
    if not args.baseline:
        baseline.mkdir(parents=True, exist_ok=True)
        for name in ('city','asn','routers'):
            print('Regenerating legacy layout:', name, flush=True)
            export_legacy(args.data/(name+'.bin'), baseline/(name+'.bin'), name=='asn')
        shutil.copy2(args.data/'manifest.json', baseline/'manifest.json')
    subprocess.run(['dotnet','build',str(HERE),'-c','Release','-v','quiet'], check=True)
    dll = HERE/'bin/Release/net10.0/QuicNet.CatalogTests.dll'
    samples = HERE/'obj/benchmark-addresses.txt'
    subprocess.run(['dotnet',str(dll),'samples',str(baseline),str(samples)], check=True)
    subprocess.run(['dotnet',str(dll),'verify',str(baseline),str(args.data)], check=True)
    results = {'runs': [], 'medians': {}, 'files': {}}
    for _ in range(args.repetitions):
        for mode, data in [('legacy',baseline),('compact',args.data)]:
            output = subprocess.check_output(['dotnet',str(dll),'bench',mode,str(data),str(samples)], text=True)
            run = json.loads(output.strip().splitlines()[-1]); results['runs'].append(run)
            print(json.dumps(run), flush=True)
    for mode in ('legacy','compact'):
        rows = [r for r in results['runs'] if r['mode'] == mode]
        results['medians'][mode] = {k: statistics.median(r[k] for r in rows) for k in rows[0] if isinstance(rows[0][k], (float,int))}
    for name in ('city','asn','routers'):
        results['files'][name] = dict(legacyBytes=(baseline/(name+'.bin')).stat().st_size,
                                     compactBytes=(args.data/(name+'.bin')).stat().st_size)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, indent=2)+'\n', encoding='utf-8')
    print('Medians:', json.dumps(results['medians'], indent=2))
    print('Results:', args.output)


if __name__ == '__main__': main()
