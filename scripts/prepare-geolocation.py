#!/usr/bin/env python3
"""Build-time catalogue workflow: import an optional MMDB, compact GLP1 and verify GLP2.

Normal builds consume the checked-in prepared resources and do not need Python.
Use --check before packaging, --verify for the full decoded-data audit.
"""
import argparse
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'QuicNet.Geolocation/tools'))
from compact_ip_catalog import prepare_catalogs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data', type=Path, default=ROOT / 'QuicNet.Geolocation/Data')
    parser.add_argument('--iptrust', type=Path, help='Import a new IPTrust edition before preparation')
    parser.add_argument('--fixtures', type=Path, help='Optional output path for independent source fixtures')
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--verify', action='store_true')
    args = parser.parse_args()
    if args.iptrust:
        if args.check:
            parser.error('--check cannot import a database')
        command = [sys.executable, str(ROOT / 'scripts/import-iptrust-geolocation.py'), str(args.iptrust), '--output', str(args.data)]
        if args.fixtures:
            command += ['--fixtures', str(args.fixtures)]
        subprocess.run(command, check=True)
    else:
        prepare_catalogs(args.data, args.check, args.verify)
    if args.iptrust and args.verify:
        prepare_catalogs(args.data, check=True, verify=True)


if __name__ == '__main__':
    main()
