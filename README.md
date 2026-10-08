# ULTRAKILL Mac port

Source for an unofficial macOS port and converter. It converts a supported Windows Steam installation, downloaded Steam depot, or game folder into a universal `ULTRAKILL.app` for Apple silicon and Intel on macOS 11 or later.

The port uses Unity 2022.3.29f1's Mac Mono player, translates Windows graphics shaders to Metal 2.2, patches platform-dependent managed code, and compiles the game's Burst routines for both architectures. It does not reconstruct an editable Unity project. Complete shader parity remains unverified. Intel execution was tested through Rosetta, but physical Intel GPU and macOS 11 testing remain outstanding.

The repository contains source only. Game files, saves, app bundles, runtime binaries, extracted shaders, generated patches, caches, and test captures stay local and are ignored by Git. `shader-source/Cage.hlsl` is the handwritten port implementation used by the shader comparison tools.

Chess uses a universal Stockfish executable built from the source included in the Windows game, with its matching NNUE network embedded. The converter includes Stockfish's GPL license and corresponding source archive, including the network and macOS build recipe. It preserves the shipped source in the output and replaces its Windows executable. The native binary keeps an `.exe` suffix because the game's unchanged UCI launcher searches for that extension. Developer payload preparation downloads the matching network from official-stockfish if it is absent; the converter itself works offline.

## Converter

A locally packaged `ULTRAKILL Converter.app` accepts a game directory, `ULTRAKILL_Data`, Steam library, or DepotDownloader version directory. Choose an input and output folder, then click Convert. Double-click the resulting `ULTRAKILL.app` to play.

The verified patch pack supports the original Windows build used during development and Steam depot 1229491, manifest 22957324. It checks all retained game code and assets and rejects unsupported versions or modified files. Windows files under `ULTRAKILL_Data/Plugins` are discarded and replaced by the native payload, so extra files, modifications, or missing files there do not block conversion. Symlinks remain rejected throughout the input data. Future updates need a new verified patch pack.

The optional save import copies Windows saves into a new Mac profile without importing Windows graphics settings. Each output uses `~/Library/Application Support/ULTRAKILL Mac/<profile ID>/`. Moving an app preserves its profile; converting again creates a new one. Conversion never overwrites an existing output app or source files.

The packaged converter includes its native workers and runtime dependencies and works offline. It does not launch the game. Converter logs go to `~/Library/Logs/ULTRAKILL Converter/`; game logs and frame diagnostics go into the output's profile. The prebuilt converter and its generated payload are excluded from this repository.

## Build

Run these commands from the repository root. Development requires macOS, Xcode command line tools and the Metal compiler, .NET 8, Python 3.11 or later, CMake, and Git. Shader recovery also uses glslang, SPIRV-Cross, and vkd3d 2.0.

```sh
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements-dev.txt
python3 tools/bootstrap_sources.py
bash third_party/casualties-port/tools/hlslcc/build.sh
bash third_party/HLSLDecompiler/portable/build.sh
```

`patches/dependencies.json` pins upstream tool revisions. The bootstrap applies the local source patches and refuses to overwrite unrelated changes. The fetched checkouts remain under the ignored `third_party/` directory.

Obtain the matching Unity Mac Mono player templates and Mac .NET libraries separately, and supply a compatible universal Steam API dylib. The tools expect these under `runtime/`. `extract_runtime.py` and `fetch_mac_bcl.py` handle extraction; the latter also requires a local library inventory under `reports/`. A fresh clone does not include these dependencies or the generated verification inputs.

Once the local dependencies are ready, build from an installation or depot:

```sh
.venv/bin/python tools/bundle.py --source /path/to/Windows/ULTRAKILL
.venv/bin/python tools/bundle.py --depot /path/to/downloaded/depot
```

The bundler prepares a separate candidate, converts shaders, patches copies of assemblies, compiles native Burst routines, and combines the ARM64 and Intel runtimes. `--steam-api /path/to/libsteam_api.dylib` supplies a different compatible universal Steam library. `--install` installs the candidate after the game closes and keeps the previous app in `backups/`. Saves and preferences remain in the local `user-data/` profile.

To package the converter, first prepare a verified local app and shader caches. Then run:

```sh
.venv/bin/python converter/prepare_payload.py
.venv/bin/python converter/package.py
```

Preparation currently expects both development inputs at `../ULTRAKILL` and `../depots/depots/1229491/22957324`. Packaging also needs an Intel Python environment at `converter/build-work/venv-x86_64` with matching dependencies. The output is `ULTRAKILL Converter.app`.

## Port changes and checks

The port repairs Metal buffer bindings, texture dimension queries, fixed samplers, and portal clipping outputs. It replaces Windows engine memory-layout accesses, caches blood decal geometry, enables native Burst routines, and guards blood cleanup after buffer disposal. It disables native graphics jobs because captures showed long rendering-job waits. Portal visibility caching is enabled by default for the current Steam depot renderer. The older supported Windows build retains its original managed portal renderer. Identical visibility inputs reuse the last GPU mask; camera or portal changes refresh it. Room images and effects still render every frame. Compute shader effects remain disabled. Steam initialization and read-only stats callbacks passed on both architectures; overlay and cloud synchronization remain untested.

The FPS display toggles with F7. Frame diagnostics record CPU and frame-time summaries, portal camera passes, rendering resolution, and asynchronous CPU/GPU timings in Fraud. Shader checks use windowless Metal fixtures. Controlled blood workloads and isolated Burst routines improved, but those measurements do not establish performance throughout the campaign. Fraud benchmarks reject blank render targets and require a rendered image before reporting timings.

```sh
.venv/bin/python -m unittest discover -s converter -p 'test_*.py'
python3 tools/audit_publish.py
```

A local Fraud fixture records the player and camera pose plus the room hierarchy in a JSON file and a binary sidecar for internal rendering tests. It is not included in the gameplay test bundle. Fixtures, copied saves, benchmark apps, and rendered images stay excluded from Git.

The publication audit checks the index and all reachable commits for excluded files, binary content, and unexpected paths. Local test launches through `tools/run.py` support muted, isolated profiles and offscreen rendering.

## Third-party notices

ULTRAKILL belongs to Arsi "Hakita" Patala and New Blood Interactive. This project is unofficial.

Source patches build on [casualties-unknown-apple-silicon](https://github.com/Andreansx/casualties-unknown-apple-silicon), [Unity HLSLcc](https://github.com/Unity-Technologies/HLSLcc), and [HLSLDecompiler](https://github.com/etnlGD/HLSLDecompiler). Unchanged dependencies include [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) and [unity-nsisbi-ext](https://github.com/kmod-midori/unity-nsisbi-ext). Fetched projects retain their own notices.

A locally packaged converter also uses Unity's Mac player and Mono/.NET libraries, Valve's Steam API, compiled Burst routines, UnityPy, bsdiff4, Python, and PyInstaller with its bootloader exception. Their binaries are not included here. Generated payloads contain converted shader programs and binary modifications, so they also remain outside this source repository.

MIT notices for the patched conversion and decompiler sources:

Copyright (c) 2026 Andreansx.

Copyright (c) 2014-2015 the 3Dmigoto project authors. See the upstream AUTHORS.txt for the complete list.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Only one local game test bundle is retained, `ULTRAKILL Portal Test.app`. It starts normally, keeps enemies and gameplay enabled, and enables the portal visibility cache automatically. Its save profile is separate. The same portal optimization is enabled in the default build and converter output. Before packaging a replacement, `tools/make_portal_test.py` removes obsolete game test and staging bundles and refuses to replace a running bundle.
