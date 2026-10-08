"""Conversion boundary tests. Full build results are recorded separately."""
import ctypes, hashlib, importlib.util, json, pathlib, subprocess, sys, tempfile, unittest, zlib
HERE=pathlib.Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('converter_worker',HERE/'worker.py');w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w)
class Boundaries(unittest.TestCase):
    def supported_source(self, root):
        source=root/'game';data=source/'ULTRAKILL_Data';payload=root/'pack';payload.mkdir()
        for name in ['Managed/Assembly-CSharp.dll','Managed/UnityEngine.CoreModule.dll','sharedassets0.assets','Plugins/x86_64/steam_api64.dll']:
            file=data/name;file.parent.mkdir(parents=True,exist_ok=True);file.write_bytes(name.encode())
        files={str(p.relative_to(data)):w.digest(p) for p in data.rglob('*') if p.is_file()}
        profile=dict(name='test',assembly_sha256=files['Managed/Assembly-CSharp.dll'],files=files)
        (payload/'manifest.json').write_text(json.dumps({'profiles':[profile]}))
        return source,data,payload,profile
    def test_discarded_windows_plugin_files_do_not_block_conversion(self):
        with tempfile.TemporaryDirectory() as t:
            source,data,payload,profile=self.supported_source(pathlib.Path(t))
            plugin=data/'Plugins/x86_64/steam_api64.dll';plugin.write_bytes(b'changed unused Windows binary')
            for name in ['steam_api64.rne','steam_emu.ini']:(plugin.parent/name).write_text('unused extra file')
            self.assertEqual(w.validate(source,payload),profile)
            import shutil
            shutil.rmtree(data/'Plugins')
            self.assertEqual(w.validate(source,payload),profile)
    def test_retained_assets_and_code_still_require_exact_matches(self):
        for mutation in ['changed_asset','changed_dll','missing_asset','asset_is_directory']:
            with self.subTest(mutation=mutation),tempfile.TemporaryDirectory() as t:
                source,data,payload,_=self.supported_source(pathlib.Path(t))
                if mutation=='changed_asset':(data/'sharedassets0.assets').write_bytes(b'changed')
                elif mutation=='changed_dll':(data/'Managed/UnityEngine.CoreModule.dll').write_bytes(b'changed')
                elif mutation=='missing_asset':(data/'sharedassets0.assets').unlink()
                else:
                    file=data/'sharedassets0.assets';file.unlink();file.mkdir()
                with self.assertRaisesRegex(ValueError,'supported build'):w.validate(source,payload)
    def test_extra_files_are_ignored_everywhere_and_not_copied(self):
        with tempfile.TemporaryDirectory() as t:
            root=pathlib.Path(t);source,data,payload,profile=self.supported_source(root)
            extras=['.DS_Store','Managed/.DS_Store','Managed/unknown.dll','sharedassets999.assets',
                    'StreamingAssets/Mods/mod.bundle','PluginsExtra/extra.dll','backup/Managed/Assembly-CSharp.dll']
            for name in extras:
                file=data/name;file.parent.mkdir(parents=True,exist_ok=True);file.write_bytes(b'ignore me')
            (source/'.DS_Store').write_bytes(b'outside data')
            self.assertEqual(w.validate(source,payload),profile)
            output=root/'output/Data';w.copy_game_data(data,output,profile)
            expected={name for name in profile['files'] if not w.discarded_input_file(name)}
            self.assertEqual({str(p.relative_to(output)) for p in output.rglob('*') if p.is_file()},expected)
            for name in extras:
                self.assertFalse((output/name).exists())
                self.assertEqual((data/name).read_bytes(),b'ignore me')
    def test_windows_plugins_are_not_copied_or_changed(self):
        with tempfile.TemporaryDirectory() as t:
            root=pathlib.Path(t);_,data,_,profile=self.supported_source(root)
            plugin=data/'Plugins/x86_64/steam_api64.dll';before=plugin.read_bytes()
            for name in ['steam_api64.rne','steam_emu.ini']:(plugin.parent/name).write_text('unused')
            output=root/'output/Data';w.copy_game_data(data,output,profile)
            self.assertFalse((output/'Plugins').exists())
            self.assertEqual(plugin.read_bytes(),before)
            for name in ['Managed/Assembly-CSharp.dll','Managed/UnityEngine.CoreModule.dll','sharedassets0.assets']:
                self.assertEqual((output/name).read_bytes(),(data/name).read_bytes())
    def test_links_in_extra_files_are_ignored_without_following_them(self):
        for target in ['missing','external-file','external-directory']:
            with self.subTest(target=target),tempfile.TemporaryDirectory() as t:
                root=pathlib.Path(t);source,data,payload,profile=self.supported_source(root)
                outside=root/target
                if target=='external-file':outside.write_text('outside')
                elif target=='external-directory':outside.mkdir()
                (data/'Plugins/x86_64/link').symlink_to(outside,target_is_directory=target=='external-directory')
                (data/'extra-link').symlink_to(outside,target_is_directory=target=='external-directory')
                self.assertEqual(w.validate(source,payload),profile)
                output=root/'output';w.copy_game_data(data,output,profile)
                self.assertFalse((output/'Plugins').exists())
                self.assertFalse((output/'extra-link').is_symlink())
    def test_links_in_required_files_and_directories_are_rejected(self):
        for name in ['sharedassets0.assets','Managed/Assembly-CSharp.dll','Managed/UnityEngine.CoreModule.dll','Managed','ULTRAKILL_Data']:
            with self.subTest(name=name),tempfile.TemporaryDirectory() as t:
                root=pathlib.Path(t);source,data,payload,_=self.supported_source(root)
                file=source/name if name=='ULTRAKILL_Data' else data/name
                target=root/'required-original';file.rename(target)
                file.symlink_to(target,target_is_directory=target.is_dir())
                with self.assertRaisesRegex(ValueError,'symlinks'):w.validate(source,payload)
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
