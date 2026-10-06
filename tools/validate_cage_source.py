#!/usr/bin/env python3
"""Run the recovered cage shader checks without GUI processes."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def run(args):
    result = subprocess.run([str(arg) for arg in args], capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stderr + result.stdout)


def validate():
    build = json.loads((ROOT / 'reports/cage-source-build.json').read_text())
    assert build['source_sha256'] == hashlib.sha256((ROOT / 'shader-source/Cage.hlsl').read_bytes()).hexdigest()
    for stage in ['vertex', 'fragment']:
        source = ROOT / ('reports/cage-reconstructed-' + stage + '.metal')
        assert build['stages'][stage]['metal_sha256'] == hashlib.sha256(source.read_bytes()).hexdigest()
        run(['xcrun', 'metal', '-c', source, '-o', source.with_suffix('.air')])
    run([ROOT / 'runtime-state/cage_pipeline_check', ROOT / 'reports/cage-reconstructed-vertex.metal',
         ROOT / 'reports/cage-reconstructed-fragment.metal', ROOT / 'reports/cage-pipeline-validation.json'])
    reports = []
    for texture in sorted((ROOT / 'reports').glob('tile_fancypanel*.rgba')):
        target = texture.with_name(texture.stem + '-source-comparison.json')
        run([ROOT / 'runtime-state/cage_source_check', ROOT / 'reports/cage-source-comparison.metal', texture, target])
        data = json.loads(target.read_text())
        reports.append({'texture': texture.name, 'texture_sha256': hashlib.sha256(texture.read_bytes()).hexdigest(),
                        'report': str(target.relative_to(ROOT)), 'passed': data['passed'],
                        'exact_components': sum(f['exact_components'] for stage in ['vertex', 'fragment'] for f in data[stage]),
                        'components': sum(f['components'] for stage in ['vertex', 'fragment'] for f in data[stage])})
    run([ROOT / 'runtime-state/cage_sampler_check', ROOT / 'reports/cage-reconstructed-fragment.metal',
         ROOT / 'reports/tile_fancypanelstransparent3.rgba', ROOT / 'reports/cage-source-sampler.json'])
    samplers = json.loads((ROOT / 'reports/cage-source-sampler.json').read_text())
    stable_holes = all(s['holes'] == samplers[0]['holes'] and s['holes'] > 0 for s in samplers)
    report = {'source_sha256': build['source_sha256'],
              'metal_sha256': {stage: build['stages'][stage]['metal_sha256'] for stage in ['vertex', 'fragment']},
              'fixtures_per_stage_per_texture': 4096, 'windows_opened': 0, 'textures': reports,
              'sampler_stable': stable_holes,
              'scope': 'Original DXBC and rebuilt DXBC both translated to Metal; no Windows GPU execution.',
              'passed': len(reports) == 6 and stable_holes and all(x['passed'] and x['exact_components'] == x['components'] for x in reports)}
    (ROOT / 'reports/cage-source-validation.json').write_text(json.dumps(report, indent=2) + '\n')
    assert report['passed']
    print(json.dumps({'passed': True, 'fixtures_per_stage': len(reports) * 4096,
                      'exact_components': sum(x['exact_components'] for x in reports), 'windows_opened': 0}))


if __name__ == '__main__':
    validate()
