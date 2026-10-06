#!/usr/bin/env python3
"""Stage the verified recovered cage programs. Never modify the running app."""
import hashlib
import json
import pickle

from convert_metal import ROOT, UnityPy, upgrade_recovered_cage, parse_blob, decompress_blob


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def stage():
    manifest_path = ROOT / 'runtime-state/pending-update.json'
    manifest = json.loads(manifest_path.read_text())
    bundle_entry = next(entry for entry in manifest['files'] if entry['target'].endswith('/shaders.bundle'))
    target = ROOT / bundle_entry['source']
    if digest(target) != bundle_entry['sha256']:
        raise ValueError('The pending shader bundle changed')
    base = pickle.loads((ROOT / 'reports/cage-sampler-fixed.pkl').read_bytes())
    patched = upgrade_recovered_cage(dict(base))
    old_entries = parse_blob(decompress_blob(base, 0))
    new_entries = parse_blob(decompress_blob(patched, 0))
    changed_entries = [i for i, (old, new) in enumerate(zip(old_entries, new_entries)) if old['raw'] != new['raw']]
    assert changed_entries == [5083, 9483]
    env = UnityPy.load(str(target))
    before = {(o.assets_file.name, o.path_id): hashlib.sha256(o.get_raw_data()).hexdigest() for o in env.objects}
    obj = next(o for o in env.objects if o.path_id == 6390567462476448578 and o.type.name == 'Shader')
    obj.save_typetree(patched)
    candidate = ROOT / 'runtime-state/cage-source-shaders.bundle'
    candidate.write_bytes(env.file.save())
    check = UnityPy.load(str(candidate))
    after = {(o.assets_file.name, o.path_id): hashlib.sha256(o.get_raw_data()).hexdigest() for o in check.objects}
    assert set(before) == set(after)
    changed_objects = [key for key in before if before[key] != after[key]]
    assert changed_objects in ([], [(obj.assets_file.name, obj.path_id)])
    # Inspect the serialized bundle again, including its actual embedded programs.
    shader = next(o for o in check.objects if o.path_id == obj.path_id and o.type.name == 'Shader').read_typetree()
    saved_entries = parse_blob(decompress_blob(shader, 0))
    assert all(saved_entries[i]['raw'] == new_entries[i]['raw'] for i in changed_entries)
    manifest['name'] = 'recovered-cage-shaders-and-groundcheck'
    bundle_entry['source'] = str(candidate.relative_to(ROOT))
    bundle_entry['sha256'] = digest(candidate)
    temp = manifest_path.with_suffix('.pending')
    temp.write_text(json.dumps(manifest, indent=2) + '\n')
    temp.replace(manifest_path)
    # Future rebuilds use the same verified source. This cache is not loaded by
    # the game and does not change its current application or save files.
    cache = ROOT / 'runtime/metal-shaders/e3fce907dc70613ebfec.pkl'
    cache.write_bytes(pickle.dumps(patched))
    (ROOT / 'reports/cage-source-bundle-audit.json').write_text(json.dumps({
        'changed_code_entries': changed_entries, 'changed_objects': changed_objects,
        'other_objects_unchanged': True, 'bundle_sha256': bundle_entry['sha256'],
        'app_modified': False, 'windows_opened': 0}, indent=2) + '\n')
    print(json.dumps({'staged': str(candidate), 'changed_code_entries': changed_entries, 'app_modified': False}))


if __name__ == '__main__':
    stage()
