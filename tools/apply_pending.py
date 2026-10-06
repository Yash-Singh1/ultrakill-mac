"""Install a prepared port update only while its game process is stopped."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "runtime-state/pending-update.json"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def apply_pending(app):
    if not MANIFEST.exists():
        return False
    data = json.loads(MANIFEST.read_text())
    if app.resolve() != Path(data["app"]).resolve():
        return False
    executable = str(app / "Contents/MacOS/UnityPlayer")
    processes = subprocess.check_output(["ps", "-axo", "command="], text=True)
    if any(line.strip().startswith(executable) for line in processes.splitlines()):
        raise SystemExit("Quit ULTRAKILL first. The prepared port update will install on the next launcher run.")
    if data.get("kind") == "full-app":
        from install_bundle import apply
        apply(data)
        return True
    changes = []
    for entry in data["files"]:
        source = (ROOT / entry["source"]).resolve()
        target = (app / entry["target"]).resolve()
        if not source.is_relative_to(ROOT) or not target.is_relative_to(app.resolve()):
            raise ValueError("Pending update paths must remain inside mac/")
        if sha(source) != entry["sha256"]:
            raise ValueError(f"Prepared update changed: {source}")
        previous = entry.get("previous_sha256")
        if previous is None:
            if target.exists():
                raise ValueError(f"New update target already exists: {target}")
        elif not target.exists() or sha(target) != previous:
            raise ValueError(f"App changed since this update was prepared: {target}")
        changes.append((source, target, entry))
    backup = ROOT / "backups" / data["name"]
    backup.mkdir(parents=True, exist_ok=True)
    installed = []
    try:
        for source, target, entry in changes:
            saved = backup / entry["target"]
            saved.parent.mkdir(parents=True, exist_ok=True)
            if target.exists():
                shutil.copy2(target, saved)
            else:
                saved = None
            target.parent.mkdir(parents=True, exist_ok=True)
            temporary = target.with_name(target.name + ".pending")
            shutil.copy2(source, temporary)
            temporary.replace(target)
            installed.append((saved, target))
        subprocess.run(["codesign", "--force", "--deep", "--sign", "-", str(app)], check=True)
        subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
    except BaseException:
        for saved, target in installed:
            if saved is None:
                target.unlink(missing_ok=True)
            else:
                shutil.copy2(saved, target)
        subprocess.run(["codesign", "--force", "--deep", "--sign", "-", str(app)], check=True)
        raise
    MANIFEST.replace(backup / "applied-update.json")
    print(f"Installed {data['name']}. Previous files are in {backup}.", flush=True)
    return True
