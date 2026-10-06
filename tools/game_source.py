"""Resolve a Windows game installation or a DepotDownloader output directory."""
from pathlib import Path
import hashlib
import json
import re

ROOT=Path(__file__).resolve().parents[1]

def resolve_source(path=None):
    if path is None:
        selection=ROOT/'runtime-state/source-selection.json'
        path=json.loads(selection.read_text())['source'] if selection.exists() else ROOT.parent/'ULTRAKILL'
    source=Path(path).expanduser().resolve()
    if source.name=='ULTRAKILL_Data':source=source.parent
    data=source/'ULTRAKILL_Data'
    for required in ['globalgamemanagers','Managed/Assembly-CSharp.dll','Managed/UnityEngine.CoreModule.dll','StreamingAssets/aa/catalog.json']:
        if not (data/required).is_file():raise ValueError(f'Missing game input: {data/required}')
    header=(data/'globalgamemanagers').open('rb').read(256)
    match=re.search(rb'20\d\d\.\d+\.\d+[abfp]\d+',header)
    if not match:raise ValueError('Cannot identify source Unity version')
    if match.group().decode()!='2022.3.29f1':raise ValueError('This port requires Unity 2022.3.29f1; source uses '+match.group().decode())
    return source

def describe_source(source):
    data=source/'ULTRAKILL_Data'
    return {'source':str(source),'layout':'steam-depot' if (source/'.DepotDownloader').is_dir() else 'game-installation','unity_version':'2022.3.29f1','assembly_sha256':hashlib.sha256((data/'Managed/Assembly-CSharp.dll').read_bytes()).hexdigest(),'catalog_sha256':hashlib.sha256((data/'StreamingAssets/aa/catalog.json').read_bytes()).hexdigest()}
