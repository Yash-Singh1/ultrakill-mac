#!/usr/bin/env python3
"""Build the recovered cage HLSL to DXBC and Unity-compatible Metal. No GUI."""
from pathlib import Path
import hashlib
import json
import re
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'third_party/casualties-port/tools/rewrap'))
from dxbc2msl import parse_container
from inject_custom_metal import postprocess_fs, postprocess_vs
from inject_custom_metal2 import relayout_cb_structs, widen_fragment_outputs, STRUCT_RE, MEMBER_RE, BUF_RE


def prune_unused_members(msl):
    # HLSLcc removes unused fields of $Globals, but leaves our named Globals
    # intact. Keep only fields actually read by this stage before restoring
    # their authored offsets, so the vertex stage needs only 64 global bytes.
    for binding in list(BUF_RE.finditer(msl)):
        struct_name, instance = binding.group(1), binding.group(2)
        definition = next(match for match in STRUCT_RE.finditer(msl) if match.group(2) == struct_name)
        outside = msl[:definition.start()] + msl[definition.end():]
        body = MEMBER_RE.sub(lambda member: member.group(0) if re.search(
            r'\b' + re.escape(instance) + r'\s*\.\s*' + re.escape(member.group(2)) + r'\b', outside) else '', definition.group(3))
        msl = msl[:definition.start(3)] + body + msl[definition.end(3):]
    return msl


def reflection_offsets(dxbc):
    """Read authored byte offsets from RDEF, rather than repacking constants."""
    data = parse_container(dxbc)[b'RDEF']
    u32 = lambda offset: struct.unpack_from('<I', data, offset)[0]
    string = lambda offset: data[offset:data.index(b'\0', offset)].decode()
    cb_count, cb_offset, resource_count, resource_offset = struct.unpack_from('<4I', data)
    slots = {}
    for i in range(resource_count):
        offset = resource_offset + i * 32
        if u32(offset + 4) == 0:
            slots[string(u32(offset))] = u32(offset + 20)
    result = {}
    # Shader model 4 variable records are 24 bytes, shader model 5 uses 40.
    target = u32(16)
    stride = 40 if ((target >> 8) & 0xff) >= 5 else 24
    for i in range(cb_count):
        offset = cb_offset + i * 24
        name = string(u32(offset))
        if name not in slots:
            continue
        members = {}
        count, start, size = struct.unpack_from('<3I', data, offset + 4)
        for j in range(count):
            variable = start + j * stride
            members[string(u32(variable))] = u32(variable + 4)
        result[slots[name]] = (members, size)
    return result


def build():
    source = ROOT / 'shader-source/Cage.hlsl'
    compiler = ROOT / 'third_party/vkd3d-2.0/vkd3d-compiler'
    translator = ROOT / 'third_party/casualties-port/tools/hlslcc/hlslcc-cli'
    report = {'source_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
              'windows_opened': 0, 'stages': {}}
    for stage, profile, entry in [('vertex', 'vs_4_0', 'vert'), ('fragment', 'ps_4_0', 'frag')]:
        prefix = ROOT / ('reports/cage-reconstructed-' + stage)
        dxbc = prefix.with_suffix('.dxbc')
        subprocess.run([str(compiler), '-x', 'hlsl', '-b', 'dxbc-tpf', '-p', profile,
                        '-e', entry, '-o', str(dxbc), str(source)], check=True)
        subprocess.run([str(compiler), '-x', 'dxbc-tpf', '-b', 'd3d-asm',
                        '-o', str(prefix.with_suffix('.asm')), str(dxbc)], check=True)
        translated = subprocess.run([str(translator), str(dxbc), 'b0000'],
                                    check=True, capture_output=True, text=True)
        prefix.with_name(prefix.name + '-raw.metal').write_text(translated.stdout)
        offsets = reflection_offsets(dxbc.read_bytes())
        metal = relayout_cb_structs(prune_unused_members(translated.stdout), offsets, 'Recovered cage ' + stage)
        # Preserve the original Unity-facing argument names as well as slots.
        globals_name = 'FGlobals' if stage == 'fragment' else 'VGlobals'
        metal = re.sub(r'\bGlobals_Type\b', globals_name + '_Type', metal)
        metal = re.sub(r'\bGlobals\b', globals_name, metal)
        if stage == 'fragment':
            metal = widen_fragment_outputs(postprocess_fs(metal))
        else:
            metal = postprocess_vs(metal)
        prefix.with_suffix('.metal').write_text(metal)
        report['stages'][stage] = {'profile': profile, 'entry': entry,
                                  'dxbc_sha256': hashlib.sha256(dxbc.read_bytes()).hexdigest(),
                                  'metal_sha256': hashlib.sha256(metal.encode()).hexdigest(),
                                  'buffer_offsets': offsets}
    (ROOT / 'reports/cage-source-build.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'compiled': list(report['stages']), 'windows_opened': 0}))


if __name__ == '__main__':
    build()
