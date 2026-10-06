#!/usr/bin/env python3
"""Stream the official editor payload and retain only its macOS .NET libraries."""
import gzip
from pathlib import Path
import stat
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
URL = "https://download.unity3d.com/download_unity/8d510ca76d2b/MacEditorInstallerArm64/Unity-2022.3.29f1.pkg"
PREFIX = "Unity/Unity.app/Contents/MonoBleedingEdge/lib/mono/unityjit-macos/"


def main():
    wanted = {line.strip().removeprefix("./") for line in
              (ROOT / "reports" / "mac-bcl-files.txt").read_text().splitlines()
              if line.strip().endswith((".dll", ".config"))}
    destination = ROOT / "runtime" / "mac-bcl"
    destination.mkdir(parents=True, exist_ok=True)
    request = urllib.request.Request(URL, headers={"Range": "bytes=3357744-4162485787"})
    started = time.monotonic()
    with urllib.request.urlopen(request, timeout=60) as response:
        assert response.status == 206, response.status
        assert response.headers["Content-Range"].startswith("bytes 3357744-")
        stream = gzip.GzipFile(fileobj=response)
        total = 0
        last = 0
        while wanted:
            magic = stream.read(6)
            if magic != b"070707":
                raise RuntimeError(f"Expected old ASCII CPIO, got {magic!r}")
            header = magic + stream.read(70)
            mode = int(header[18:24], 8)
            namesize = int(header[59:65], 8)
            size = int(header[65:76], 8)
            name = stream.read(namesize).rstrip(b"\0").decode().removeprefix("./")
            if name == "TRAILER!!!":
                break
            if name in wanted:
                relative = Path(name.removeprefix(PREFIX))
                assert not relative.is_absolute() and ".." not in relative.parts
                payload = stream.read(size)
                output = destination / relative
                output.parent.mkdir(parents=True, exist_ok=True)
                if stat.S_ISLNK(mode):
                    output.symlink_to(payload.decode())
                else:
                    output.write_bytes(payload)
                wanted.remove(name)
                print(f"Extracted {relative}, {len(wanted)} remaining", flush=True)
            else:
                remaining = size
                while remaining:
                    chunk = stream.read(min(remaining, 8 * 1024 * 1024))
                    if not chunk:
                        raise EOFError(name)
                    remaining -= len(chunk)
            total += 76 + namesize + size
            now = time.monotonic()
            if now - last > 15:
                print(f"Scanned {total / 1024**3:.2f} GiB in {now-started:.0f}s; {name}", flush=True)
                last = now
        if wanted:
            raise RuntimeError(f"Missing {len(wanted)} libraries")
    print("Mac .NET libraries extracted without installing the editor.")


if __name__ == "__main__":
    main()
