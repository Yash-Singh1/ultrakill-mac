"""Stage a complete, signed app and swap it only when the player is stopped."""
from pathlib import Path
import datetime, json, subprocess
from make_universal import sha, audit
ROOT=Path(__file__).resolve().parents[1]
MANIFEST=ROOT/'runtime-state/pending-update.json'

def running(app):
    executable=str(app/'Contents/MacOS/UnityPlayer')
    lines=subprocess.check_output(['ps','-axo','command='],text=True).splitlines()
    return any(line.strip().startswith(executable) for line in lines)

def file_manifest(app):
    return [{'path':str(p.relative_to(app)),'sha256':sha(p)} for p in sorted(app.rglob('*')) if p.is_file() and not p.is_symlink()]

def stage(candidate, target=ROOT/'ULTRAKILL.app'):
    candidate=candidate.resolve();target=target.resolve()
    if not all(p.is_relative_to(ROOT) and p.suffix=='.app' for p in [candidate,target]) or candidate==target:raise ValueError('Use separate app paths under mac/')
    if MANIFEST.exists():raise ValueError('Another update is pending')
    audit(candidate)
    stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S')
    prepared=ROOT/'runtime-state'/f'full-install-{stamp}'/'ULTRAKILL.app';prepared.parent.mkdir()
    subprocess.run(['/bin/cp','-cR',str(candidate),str(prepared)],check=True)
    data={'kind':'full-app','name':f'depot-install-{stamp}','app':str(target),'candidate':str(prepared),'files':file_manifest(prepared),'source_selection':json.loads((candidate/'Contents/Resources/port-source.json').read_text()),'previous_signature':sha(target/'Contents/_CodeSignature/CodeResources') if target.exists() else None}
    MANIFEST.write_text(json.dumps(data,indent=2)+'\n')
    if running(target):return {'installed':False,'pending':str(MANIFEST)}
    return apply(data)

def apply(data):
    app=Path(data['app']).resolve();candidate=Path(data['candidate']).resolve()
    if not all(p.is_relative_to(ROOT) and p.suffix=='.app' for p in [app,candidate]):raise ValueError('Install paths must stay under mac/')
    if running(app):raise SystemExit('Quit ULTRAKILL first. The prepared app will install on the next launcher run.')
    if file_manifest(candidate)!=data['files']:raise ValueError('Prepared bundle changed since staging')
    if app.exists() and sha(app/'Contents/_CodeSignature/CodeResources')!=data['previous_signature']:raise ValueError('Installed app changed since staging')
    audit(candidate)
    backup=ROOT/'backups'/data['name'];backup.mkdir(parents=True)
    old=backup/'ULTRAKILL.app'
    had_app=app.exists()
    try:
        if had_app:app.rename(old)
        candidate.rename(app)
        audit(app)
    except BaseException:
        if app.exists():app.rename(candidate)
        if had_app and old.exists():old.rename(app)
        raise
    (ROOT/'runtime-state/source-selection.json').write_text(json.dumps(data['source_selection'],indent=2)+'\n')
    # Only the installation link changes; save/preference directories stay in place.
    link=ROOT/'user-data/ULTRAKILL_Data'
    if link.is_symlink():link.unlink()
    if not link.exists():link.symlink_to('../ULTRAKILL.app/Contents/Resources/Data')
    MANIFEST.replace(backup/'applied-update.json')
    print(f'Installed {data["name"]}. Previous app is in {backup}.',flush=True)
    return {'installed':True,'app':str(app),'backup':str(backup)}
