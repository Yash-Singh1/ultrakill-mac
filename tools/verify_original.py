#!/usr/bin/env python3
"""Compare the original game and saves against the pre-experiment SHA-256 manifest."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ORIGINAL = ROOT.parent / "ULTRAKILL"
baseline = json.loads((ROOT / "reports" / "original-files.json").read_text())
expected = {entry["path"] for entry in baseline}
actual = {str(path.relative_to(ORIGINAL)) for path in ORIGINAL.rglob("*") if path.is_file()}
errors = []
if actual != expected:
    errors.append({"added": sorted(actual - expected), "missing": sorted(expected - actual)})
for entry in baseline:
    path = ORIGINAL / entry["path"]
    if not path.is_file():
        continue
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    details = path.stat()
    if digest.hexdigest() != entry["sha256"] or details.st_mtime_ns != entry["mtime_ns"]:
        errors.append(entry["path"])
report = {"original_files": len(baseline), "unchanged": not errors, "differences": errors}
(ROOT / "reports" / "original-verification.json").write_text(json.dumps(report, indent=2))
print(json.dumps(report, indent=2))
raise SystemExit(bool(errors))
