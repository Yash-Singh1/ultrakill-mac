#!/usr/bin/env python3
"""Package the original Windows executable's icon as a macOS app icon."""
from pathlib import Path
from io import BytesIO
import argparse
import hashlib
import json
import plistlib
import struct
import subprocess

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT.parent / "ULTRAKILL/ULTRAKILL.exe"
OUTPUT = ROOT / "runtime/app-icon"


def executable_icons(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Expected a Windows PE executable")
    count, optional_size = (struct.unpack_from("<H", data, pe + offset)[0]
                            for offset in (6, 20))
    optional = pe + 24
    directories = optional + (112 if struct.unpack_from("<H", data, optional)[0] == 0x20B else 96)
    resource_rva = struct.unpack_from("<I", data, directories + 16)[0]
    sections = []
    for index in range(count):
        virtual_size, address, raw_size, raw_offset = struct.unpack_from(
            "<IIII", data, optional + optional_size + 40 * index + 8)
        sections.append((address, max(virtual_size, raw_size), raw_offset))

    def file_offset(address):
        return next(raw + address - start for start, size, raw in sections
                    if start <= address < start + size)

    base = file_offset(resource_rva)
    resources = {}

    def walk(relative, path):
        offset = base + relative
        entries = sum(struct.unpack_from("<HH", data, offset + 12))
        for index in range(entries):
            name, value = struct.unpack_from("<II", data, offset + 16 + index * 8)
            if not path and name not in (3, 14):
                continue
            if value & 0x80000000:
                walk(value & 0x7FFFFFFF, path + (name,))
            else:
                address, size = struct.unpack_from("<II", data, base + value)
                start = file_offset(address)
                resources[path + (name,)] = data[start:start + size]

    walk(0, ())
    groups = [(key, value) for key, value in resources.items() if key[0] == 14]
    if not groups:
        raise ValueError("No icon group in the original executable")
    key, group = groups[0]
    reserved, kind, count = struct.unpack_from("<HHH", group)
    directory = bytearray(struct.pack("<HHH", reserved, kind, count))
    payloads = []
    offset = 6 + 16 * count
    for index in range(count):
        entry = group[6 + 14 * index:6 + 14 * (index + 1)]
        resource_id = struct.unpack_from("<H", entry, 12)[0]
        payload = resources[(3, resource_id, key[2])]
        directory.extend(entry[:8] + struct.pack("<II", len(payload), offset))
        payloads.append(payload)
        offset += len(payload)
    return bytes(directory) + b"".join(payloads)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", type=Path, help="Install into an existing app under mac/")
    parser.add_argument("--source", type=Path, default=SOURCE, help="Source Windows executable from an installation or depot")
    args = parser.parse_args()
    OUTPUT.mkdir(parents=True, exist_ok=True)
    source = args.source.read_bytes()
    ico = executable_icons(source)
    (OUTPUT / "ULTRAKILL.ico").write_bytes(ico)
    image_file = Image.open(BytesIO(ico))
    sizes = image_file.ico.sizes()
    image = image_file.ico.getimage(max(sizes, key=lambda size: size[0] * size[1])).convert("RGBA")
    image.save(OUTPUT / "original-icon.png")
    iconset = OUTPUT / "ULTRAKILL.iconset"
    iconset.mkdir(exist_ok=True)
    for size in (16, 32, 128, 256, 512):
        for scale in (1, 2):
            pixels = size * scale
            available = image_file.ico.getimage((pixels, pixels)).convert("RGBA") if (pixels, pixels) in sizes else image.resize((pixels, pixels), Image.Resampling.LANCZOS)
            suffix = "@2x" if scale == 2 else ""
            available.save(iconset / f"icon_{size}x{size}{suffix}.png")
    icon = OUTPUT / "ULTRAKILL.icns"
    subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(icon)], check=True)
    report = dict(source=str(args.source.resolve()), source_sha256=hashlib.sha256(source).hexdigest(),
                  original_sizes=sorted(sizes), mac_icon=str(icon))
    if args.app:
        app = args.app.resolve()
        if not app.is_relative_to(ROOT) or app.suffix != ".app":
            raise ValueError("Only install into an app under mac/")
        contents = app / "Contents"
        (contents / "Resources/ULTRAKILL.icns").write_bytes(icon.read_bytes())
        info_path = contents / "Info.plist"
        info = plistlib.loads(info_path.read_bytes())
        info["CFBundleIconFile"] = "ULTRAKILL.icns"
        info_path.write_bytes(plistlib.dumps(info))
        report["installed_app"] = str(app)
    (ROOT / "reports/app-icon.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
