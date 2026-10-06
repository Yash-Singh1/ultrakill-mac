#!/usr/bin/env python3
"""Extract Unity's NSISBI module without installing it or executing its installer.

Format reference: kmod-midori/unity-nsisbi-ext, MIT, retained in third_party.
"""
import argparse
import lzma
from pathlib import Path
import re
import struct


def decompress(data):
    prop = data[0]
    lc, remainder = prop % 9, prop // 9
    lp, pb = remainder % 5, remainder // 5
    decoder = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[{
        "id": lzma.FILTER_LZMA1, "dict_size": struct.unpack_from("<I", data, 1)[0],
        "lc": lc, "lp": lp, "pb": pb,
    }])
    return decoder.decompress(data[5:])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("installer", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--filter", default=".*")
    parser.add_argument("--list", action="store_true")
    args = parser.parse_args()
    data = args.installer.read_bytes()
    start = data.index(b"\xef\xbe\xad\xdeNullsoftInst") - 4
    flags, _, _, _, _, header_size, _ = struct.unpack_from("<7I", data, start)
    assert flags & 0x30, "Expected NSISBI"
    compressed_size = struct.unpack_from("<I", data, start + 36)[0] & 0x7fffffff
    header = decompress(data[start + 40:start + 40 + compressed_size])
    assert len(header) == header_size
    blocks = [struct.unpack_from("<II", header, 4 + i * 8) for i in range(8)]
    entries_start, entries_count = blocks[2]
    strings_start = blocks[3][0]
    payload_start = start + 40 + compressed_size

    def string(index):
        pos = strings_start + 2 * index
        chars = []
        while True:
            char = struct.unpack_from("<H", header, pos)[0]
            pos += 2
            if char in (2, 3):
                pos += 2
            elif char == 0:
                return "".join(chars).replace("\\", "/").lstrip("/")
            else:
                chars.append(chr(char))

    directory = ""
    pattern = re.compile(args.filter)
    for i in range(entries_count):
        command, *params = struct.unpack_from("<9I", header, entries_start + i * 36)
        if command == 11:
            directory = string(params[0])
        elif command == 20:
            relative = Path(directory) / string(params[1])
            if not pattern.search(str(relative)):
                continue
            assert not relative.is_absolute() and ".." not in relative.parts
            position = payload_start + params[2]
            length = struct.unpack_from("<I", data, position)[0]
            compressed = bool(length & 0x80000000)
            length &= 0x7fffffff
            print(relative, length, "compressed" if compressed else "raw", flush=True)
            if args.list:
                continue
            payload = data[position + 4:position + 4 + length]
            output = args.output / relative
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(decompress(payload) if compressed and length else payload)


if __name__ == "__main__":
    main()
