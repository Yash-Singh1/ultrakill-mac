#!/usr/bin/env python3
"""Reject Metal vertex programs with unwritten clip-distance outputs."""
from pathlib import Path
import argparse
import gc
import json
import re
import struct
from convert_metal import UnityPy, parse_blob, decompress_blob, parse_code_entry

ROOT = Path(__file__).resolve().parents[1]


def audit(app):
    rows = json.loads((app / 'Contents/Resources/shader-inventory.json').read_text())
    checked = 0
    failures = []
    for file in sorted({r['file'] for r in rows}):
        relative = Path(file)
        if relative.parts[:2] == ('ULTRAKILL', 'ULTRAKILL_Data'):
            relative = relative.relative_to('ULTRAKILL/ULTRAKILL_Data')
        env = UnityPy.load(str(app / 'Contents/Resources/Data' / relative))
        for obj in env.objects:
            if obj.type.name != 'Shader':
                continue
            data = obj.read_typetree()
            if 14 not in data.get('platforms', []):
                continue
            entries = parse_blob(decompress_blob(data, data['platforms'].index(14)))
            for index, entry in enumerate(entries):
                if entry['type'] != 23:
                    continue
                payload = parse_code_entry(entry['raw'])['payload']
                offset = struct.unpack_from('<I', payload, 12)[0]
                source = payload[offset:].split(b'\0', 1)[0].decode()
                declaration = re.search(r'float\s+mtl_ClipDistance\s*\[\[\s*clip_distance\s*\]\]\s*(?:\[(\d+)\])?\s*;', source)
                if not declaration:
                    continue
                checked += 1
                size = int(declaration[1] or 1)
                writes = set(map(int, re.findall(r'output\.mtl_ClipDistance\[(\d+)\]\s*=', source))) if size > 1 else {0} if re.search(r'output\.mtl_ClipDistance\s*=', source) else set()
                if writes != set(range(size)):
                    failures.append(dict(file=str(relative), shader=data['m_ParsedForm']['m_Name'], entry=index, declared=size, written=sorted(writes)))
        del env
        gc.collect()
    return dict(clip_programs_checked=checked, failures=failures, passed=not failures,
        scope='Output assignment coverage only. This does not establish full shader visual parity.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--app', type=Path, default=ROOT / 'ULTRAKILL.app')
    parser.add_argument('--report', type=Path, default=ROOT / 'reports/clip-distance-audit.json')
    args = parser.parse_args()
    report = audit(args.app.resolve())
    args.report.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))
    raise SystemExit(not report['passed'])
