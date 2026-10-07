"""Conversion boundary tests. Full build results are recorded separately."""
import ctypes, hashlib, importlib.util, json, pathlib, subprocess, sys, tempfile, unittest, zlib
HERE=pathlib.Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('converter_worker',HERE/'worker.py');w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w)
class Boundaries(unittest.TestCase):
    def test_chess_payload_is_verified_before_removing_windows_engine(self):
        with tempfile.TemporaryDirectory() as t:
            root=pathlib.Path(t);directory=root/'data/StreamingAssets/ChessEngine';directory.mkdir(parents=True)
            windows=directory/'stockfish-windows-x86-64.exe';windows.write_bytes(b'windows')
            payload=root/'payload';helper=payload/'helpers';helper.mkdir(parents=True)
            native=helper/'stockfish-macos.exe';native.write_bytes(b'corrupt')
            profile=dict(helpers='helpers',chess_engine=dict(file=native.name,sha256='wrong'))
            with self.assertRaisesRegex(ValueError,'damaged'):w.install_chess_engine(root/'data',profile,payload)
            self.assertEqual(windows.read_bytes(),b'windows')
            profile['chess_engine']['file']='../escape.exe'
            with self.assertRaisesRegex(ValueError,'path'):w.install_chess_engine(root/'data',profile,payload)
    def test_source_layouts(self):
        with tempfile.TemporaryDirectory() as t:
            library=pathlib.Path(t);root=library/'steamapps/common/ULTRAKILL';file=root/'ULTRAKILL_Data/Managed/Assembly-CSharp.dll';file.parent.mkdir(parents=True);file.write_bytes(b'input')
            for p in [root,root/'ULTRAKILL_Data',library,library/'steamapps',library/'steamapps/common']:
                self.assertEqual(w.source_root(p),root.resolve())
    def test_publish_does_not_replace_existing_app(self):
        with tempfile.TemporaryDirectory() as t:
            root=pathlib.Path(t);source=root/'candidate.app';source.mkdir();output=root/'existing.app';output.mkdir();(output/'save').write_bytes(b'keep')
            with self.assertRaises(OSError):w.publish(source,output)
            self.assertEqual((output/'save').read_bytes(),b'keep');self.assertTrue(source.is_dir())
    def test_publish_is_atomic(self):
        with tempfile.TemporaryDirectory() as t:
            root=pathlib.Path(t);source=root/'candidate.app';source.mkdir();(source/'result').write_bytes(b'ready');output=root/'output.app'
            w.publish(source,output);self.assertFalse(source.exists());self.assertEqual((output/'result').read_bytes(),b'ready')
    def test_corrupted_patch_and_wrong_input_are_rejected(self):
        with tempfile.TemporaryDirectory() as t:
            root=pathlib.Path(t);before=b'windows';after=b'mac';patch=root/'patch.zlib';patch.write_bytes(zlib.compress(after));sha=lambda raw:hashlib.sha256(raw).hexdigest()
            spec=dict(before=sha(before),after=sha(after),codec='zlib',patch='patch.zlib',patch_sha256=w.digest(patch))
            self.assertEqual(w.patched_bytes(before,spec,root),after)
            with self.assertRaises(ValueError):w.patched_bytes(b'wrong',spec,root)
            patch.write_bytes(b'bad')
            with self.assertRaises(ValueError):w.patched_bytes(before,spec,root)
    def test_unrecognized_build(self):
        with tempfile.TemporaryDirectory() as t:
            root=pathlib.Path(t);payload=root/'pack';payload.mkdir();(payload/'manifest.json').write_text(json.dumps({'profiles':[]}));source=root/'game';dll=source/'ULTRAKILL_Data/Managed/Assembly-CSharp.dll';dll.parent.mkdir(parents=True);dll.write_bytes(b'unknown')
            with self.assertRaisesRegex(ValueError,'not supported'):w.validate(source,payload)
    def test_input_and_existing_output_are_protected(self):
        with tempfile.TemporaryDirectory() as t:
            root=pathlib.Path(t);dll=root/'game/ULTRAKILL_Data/Managed/Assembly-CSharp.dll';dll.parent.mkdir(parents=True);dll.write_bytes(b'unknown');source=dll.parents[2]
            with self.assertRaisesRegex(ValueError,'outside'):w.convert(source,source/'out.app')
            output=root/'existing.app';output.mkdir();(output/'kept').write_text('keep')
            with self.assertRaisesRegex(ValueError,'already exists'):w.convert(source,output)
            self.assertEqual((output/'kept').read_text(),'keep')
    def test_sigterm_cleans_partial_build(self):
        with tempfile.TemporaryDirectory() as t:
            root=pathlib.Path(t);source=root/'game';managed=source/'ULTRAKILL_Data/Managed';managed.mkdir(parents=True)
            for name in ['Assembly-CSharp.dll','UnityEngine.CoreModule.dll']:(managed/name).write_bytes(name.encode())
            payload=root/'pack';payload.mkdir();files={str(p.relative_to(source/'ULTRAKILL_Data')):w.digest(p) for p in managed.iterdir()}
            (payload/'manifest.json').write_text(json.dumps({'profiles':[dict(name='test',assembly_sha256=w.digest(managed/'Assembly-CSharp.dll'),files=files,shader_files={})]}))
            script=root/'cancel_test.py'
            script.write_text('import sys,time\nsys.path.insert(0,'+repr(str(HERE))+')\nimport worker\ndef blocked_copy(*_):\n worker.emit(17,"copy started")\n time.sleep(30)\nworker.clone=blocked_copy\nraise SystemExit(worker.main())\n')
            output=root/'out/ULTRAKILL.app'
            p=subprocess.Popen([sys.executable,str(script),'--source',str(source),'--payload',str(payload),'--output',str(output)],stdout=subprocess.PIPE,text=True)
            try:
                while True:
                    line=p.stdout.readline()
                    self.assertTrue(line,'worker exited before the copy')
                    if 'copy started' in line:break
                p.terminate();p.communicate(timeout=10)
                self.assertEqual(p.returncode,130)
                self.assertFalse(output.exists());self.assertFalse(list(output.parent.glob('.ultrakill-convert-*')))
                for name,checksum in files.items():self.assertEqual(w.digest(source/'ULTRAKILL_Data'/name),checksum)
            finally:
                if p.poll() is None:p.kill();p.wait()
if __name__=='__main__':unittest.main()
