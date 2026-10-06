#!/usr/bin/env python3
"""Verify repaired texture queries in the installed Mac shader bundle."""
from pathlib import Path
import json
import re
import struct
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'third_party/casualties-port/tools/rewrap'))
import UnityPy
from entrytools import parse_blob, parse_code_entry
from inject_metal import decompress_blob

bad = re.compile(r'(\w+)\.x = (?:uint\(0\)|0\.0);\s*\1\.y = (?:uint\(0\)|0\.0);\s*\1\.z = (?:uint\(0\)|0\.0);\s*\1\.w = \w+\.get_num_mip_levels\(\);')
previous = json.loads((ROOT / 'reports/texture-dimension-audit.json').read_text())
targets = {entry['shader'] for entry in previous} | {'Hidden/ULTRAKILL/ULTRAKILL-Stationary'}
bundle = ROOT / 'ULTRAKILL.app/Contents/Resources/Data/StreamingAssets/aa/StandaloneWindows64/assets_assets_assets/shaders.bundle'
env = UnityPy.load(str(bundle))
found = set()
remaining = []
dimension_queries = 0
for obj in env.objects:
    if obj.type.name != 'Shader':
        continue
    data = obj.read_typetree()
    name = data['m_ParsedForm']['m_Name']
    if name not in targets:
        continue
    found.add(name)
    for index, entry in enumerate(parse_blob(decompress_blob(data, 0))):
        if entry['type'] not in (23, 24):
            continue
        payload = parse_code_entry(entry['raw'])['payload']
        start = struct.unpack_from('<I', payload, 12)[0]
        source = payload[start:].split(b'\0', 1)[0].decode()
        dimension_queries += source.count('.get_width(') + source.count('.get_height(')
        if bad.search(source):
            remaining.append({'shader': name, 'entry': index})
report = {'checked_shader_objects': len(found), 'dimension_getters': dimension_queries,
          'remaining_broken_programs': remaining, 'missing_shaders': sorted(targets - found)}
(ROOT / 'reports/installed-texture-dimensions-audit.json').write_text(json.dumps(report, indent=2))
print(json.dumps(report, indent=2))
raise SystemExit(bool(remaining or targets - found))
