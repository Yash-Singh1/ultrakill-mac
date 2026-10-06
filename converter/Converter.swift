import AppKit
import Foundation

final class Converter: NSObject, NSApplicationDelegate, NSWindowDelegate {
    var window: NSWindow!
    let sourceField=NSTextField(string:""), outputField=NSTextField(string:"")
    let sourceButton=NSButton(title:"Choose game folder…",target:nil,action:nil)
    let outputButton=NSButton(title:"Choose output folder…",target:nil,action:nil)
    let saves=NSButton(checkboxWithTitle:"Copy Windows saves into the new Mac profile",target:nil,action:nil)
    let convertButton=NSButton(title:"Convert",target:nil,action:nil)
    let cancelButton=NSButton(title:"Cancel",target:nil,action:nil)
    let revealButton=NSButton(title:"Show app in Finder",target:nil,action:nil)
    let logButton=NSButton(title:"Show log",target:nil,action:nil)
    let progress=NSProgressIndicator()
    let status=NSTextField(wrappingLabelWithString:"Choose the Windows game folder or a downloaded Steam depot.")
    let detail=NSTextField(wrappingLabelWithString:"")
    let logView=NSTextView()
    var process:Process?, logFile:FileHandle?, logURL:URL?, resultURL:URL?
    var receivedError=false, cancelling=false, terminatePending=false
    let workerQueue=DispatchQueue(label:"converter.worker")

    func label(_ text:String,_ size:CGFloat=13) -> NSTextField { let f=NSTextField(wrappingLabelWithString:text);f.font=NSFont.systemFont(ofSize:size);return f }
    func applicationDidFinishLaunching(_ notification:Notification) {
        let menu=NSMenu();let item=NSMenuItem();menu.addItem(item)
        let appMenu=NSMenu();appMenu.addItem(withTitle:"Quit ULTRAKILL Converter",action:#selector(NSApplication.terminate(_:)),keyEquivalent:"q");item.submenu=appMenu
        let editItem=NSMenuItem();menu.addItem(editItem);let editMenu=NSMenu(title:"Edit");editItem.submenu=editMenu
        for (title,action,key) in [("Copy",#selector(NSText.copy(_:)),"c"),("Paste",#selector(NSText.paste(_:)),"v"),("Select all",#selector(NSText.selectAll(_:)),"a")] { editMenu.addItem(withTitle:title,action:action,keyEquivalent:key) }
        NSApp.mainMenu=menu
        window=NSWindow(contentRect:NSRect(x:0,y:0,width:740,height:700),styleMask:[.titled,.closable,.miniaturizable,.resizable],backing:.buffered,defer:false)
        window.title="ULTRAKILL Converter";window.minSize=NSSize(width:660,height:690);window.delegate=self;window.center()
        let stack=NSStackView();stack.orientation = .vertical;stack.alignment = .leading;stack.spacing=10
        stack.translatesAutoresizingMaskIntoConstraints=false
        window.contentView!.addSubview(stack)
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo:window.contentView!.leadingAnchor,constant:26),stack.trailingAnchor.constraint(equalTo:window.contentView!.trailingAnchor,constant:-26),stack.topAnchor.constraint(equalTo:window.contentView!.topAnchor,constant:24),stack.bottomAnchor.constraint(equalTo:window.contentView!.bottomAnchor,constant:-24)])
        let title=label("Make a Mac app from your Windows copy",23);title.font=NSFont.systemFont(ofSize:23,weight:.semibold);stack.addArrangedSubview(title)
        stack.addArrangedSubview(label("Select a Steam installation, a downloaded depot version, or the folder containing ULTRAKILL_Data. The converter copies the files and builds a universal app for macOS 11 or later."))
        sourceField.placeholderString="Path to Windows game files";outputField.placeholderString="Output folder"
        sourceField.lineBreakMode = .byTruncatingMiddle;outputField.lineBreakMode = .byTruncatingMiddle
        outputField.stringValue=FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Desktop/Converted ULTRAKILL").path
        for (caption,field,button) in [("Game files",sourceField,sourceButton),("Output folder",outputField,outputButton)] {
            let row=NSStackView(views:[field,button]);row.orientation = .horizontal;row.spacing=8
            stack.addArrangedSubview(label(caption));stack.addArrangedSubview(row)
            row.widthAnchor.constraint(equalTo:stack.widthAnchor).isActive=true
            field.setContentHuggingPriority(.defaultLow,for:.horizontal);button.setContentHuggingPriority(.required,for:.horizontal)
        }
        sourceButton.target=self;sourceButton.action=#selector(chooseSource)
        outputButton.target=self;outputButton.action=#selector(chooseOutput)
        stack.addArrangedSubview(saves)
        let note=label("Supports the two verified Unity 2022.3.29f1 builds used for this port. Other builds are rejected. Steam must be running and signed in to use Steam features.")
        note.textColor = .secondaryLabelColor;stack.addArrangedSubview(note)
        progress.style = .bar;progress.minValue=0;progress.maxValue=100;progress.isIndeterminate=false
        stack.addArrangedSubview(progress);progress.widthAnchor.constraint(equalTo:stack.widthAnchor).isActive=true
        status.font=NSFont.systemFont(ofSize:14,weight:.medium);stack.addArrangedSubview(status)
        detail.font=NSFont.systemFont(ofSize:11);detail.textColor = .secondaryLabelColor;detail.maximumNumberOfLines=2;stack.addArrangedSubview(detail)
        let scroll=NSScrollView();scroll.hasVerticalScroller=true;scroll.borderType = .bezelBorder;scroll.documentView=logView
        logView.isEditable=false;logView.isSelectable=true;logView.font=NSFont.monospacedSystemFont(ofSize:11,weight:.regular)
        logView.autoresizingMask = [.width];logView.isHorizontallyResizable=false;logView.textContainer?.widthTracksTextView=true
        stack.addArrangedSubview(scroll);scroll.widthAnchor.constraint(equalTo:stack.widthAnchor).isActive=true;scroll.heightAnchor.constraint(greaterThanOrEqualToConstant:110).isActive=true
        let buttons=NSStackView(views:[logButton,revealButton,cancelButton,convertButton]);buttons.spacing=10;buttons.orientation = .horizontal;stack.addArrangedSubview(buttons)
        for child in stack.arrangedSubviews where child is NSTextField {
            child.widthAnchor.constraint(equalTo:stack.widthAnchor).isActive=true
        }
        for (b,action) in [(convertButton,#selector(start)),(cancelButton,#selector(cancel)),(revealButton,#selector(reveal)),(logButton,#selector(showLog))] {b.target=self;b.action=action}
        convertButton.bezelStyle = .rounded;convertButton.keyEquivalent="\r"
        cancelButton.isEnabled=false;revealButton.isEnabled=false;logButton.isEnabled=false
        window.makeKeyAndOrderFront(nil);NSApp.activate(ignoringOtherApps:true)
        let args=CommandLine.arguments
        func value(_ flag:String)->String? {guard let i=args.firstIndex(of:flag),i+1<args.count else{return nil};return args[i+1]}
        if let s=value("--source"){sourceField.stringValue=s}
        if let o=value("--output-folder"){outputField.stringValue=o}
        if args.contains("--import-saves"){saves.state = .on}
        if args.contains("--autostart"){start()}
        if args.contains("--ui-snapshot") && !args.contains("--autostart") {
            DispatchQueue.main.async {self.writeSnapshot();NSApp.terminate(nil)}
        }
    }
    @objc func chooseSource(){pick(sourceField)}
    @objc func chooseOutput(){pick(outputField)}
    func pick(_ field:NSTextField){let p=NSOpenPanel();p.canChooseDirectories=true;p.canChooseFiles=false;p.allowsMultipleSelection=false;p.canCreateDirectories=true;p.directoryURL=URL(fileURLWithPath:field.stringValue.isEmpty ? NSHomeDirectory():field.stringValue);p.beginSheetModal(for:window){r in if r == .OK,let u=p.url{field.stringValue=u.path}}}
    func setRunning(_ running:Bool){for b in [sourceButton,outputButton,convertButton,saves]{b.isEnabled = !running};sourceField.isEditable = !running;outputField.isEditable = !running;cancelButton.isEnabled=running}
    @objc func start(){
        guard process==nil else{return}
        let source=sourceField.stringValue.trimmingCharacters(in:.whitespacesAndNewlines),folder=outputField.stringValue.trimmingCharacters(in:.whitespacesAndNewlines)
        guard !source.isEmpty && !folder.isEmpty else{status.stringValue="Choose both folders first.";return}
        let res=Bundle.main.resourceURL!
        #if arch(arm64)
        let worker=res.appendingPathComponent("worker-arm64/convert-worker")
        #else
        let worker=res.appendingPathComponent("worker-x86_64/convert-worker")
        #endif
        guard FileManager.default.isExecutableFile(atPath:worker.path) else{status.stringValue="The converter worker is missing. Rebuild the converter app.";return}
        let output=URL(fileURLWithPath:folder,isDirectory:true).appendingPathComponent("ULTRAKILL.app")
        let p=Process();p.executableURL=worker;p.arguments=["--payload",res.appendingPathComponent("payload").path,"--source",source,"--output",output.path]+(saves.state == .on ? ["--import-saves"]:[])
        let pipe=Pipe();p.standardOutput=pipe;p.standardError=pipe
        var env=ProcessInfo.processInfo.environment
        // PyInstaller workers use their own bundled interpreter and libraries.
        for key in ["PYTHONPATH","PYTHONHOME","DYLD_LIBRARY_PATH"] {env.removeValue(forKey:key)}
        env["PYTHONUNBUFFERED"]="1";p.environment=env
        do {
            let logs=FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/ULTRAKILL Converter")
            try FileManager.default.createDirectory(at:logs,withIntermediateDirectories:true)
            let log=logs.appendingPathComponent("conversion-\(UUID().uuidString).log");FileManager.default.createFile(atPath:log.path,contents:nil)
            logFile=try FileHandle(forWritingTo:log);logURL=log;logButton.isEnabled=true
            logView.string="";progress.doubleValue=0;resultURL=nil;revealButton.isEnabled=false;receivedError=false;cancelling=false
            try p.run();process=p;setRunning(true);status.stringValue="Checking game files…"
            workerQueue.async {
                var pending=Data()
                while true {
                    let data=pipe.fileHandleForReading.availableData
                    if data.isEmpty{break}
                    pending.append(data)
                    while let newline=pending.firstIndex(of:10) {
                        let line=Data(pending[..<newline]);pending.removeSubrange(...newline)
                        DispatchQueue.main.async {self.consume(line)}
                    }
                }
                if !pending.isEmpty {let tail=pending;DispatchQueue.main.async{self.consume(tail)}}
                p.waitUntilExit()
                DispatchQueue.main.async {self.finished(p.terminationStatus)}
            }
        } catch {try? logFile?.close();logFile=nil;status.stringValue=error.localizedDescription;setRunning(false)}
    }
    func consume(_ data:Data){
        try? logFile?.write(contentsOf:data+Data([10]))
        let line=String(data:data,encoding:.utf8) ?? ""
        if let obj=(try? JSONSerialization.jsonObject(with:data)) as? [String:Any],let event=obj["event"] as? String {
            if let text=obj["message"] as? String {status.stringValue=text;appendLog(text)}
            if let percent=obj["progress"] as? Double {progress.doubleValue=percent}
            detail.stringValue=obj["detail"] as? String ?? ""
            if event=="error"{receivedError=true}
            if event=="complete",let app=obj["app"] as? String {resultURL=URL(fileURLWithPath:app);revealButton.isEnabled=true;detail.stringValue="Double-click the finished app to play. Saves and settings stay in its separate Mac profile."}
        } else {appendLog(line)}
    }
    func appendLog(_ text:String){logView.textStorage?.append(NSAttributedString(string:text+"\n",attributes:[.font:NSFont.monospacedSystemFont(ofSize:11,weight:.regular)]));logView.scrollToEndOfDocument(nil)}
    func finished(_ code:Int32){
        process=nil;try? logFile?.close();logFile=nil;setRunning(false)
        if code != 0 && !receivedError{status.stringValue=cancelling ? "Conversion cancelled. Partial output removed.":"Conversion failed. Open the log for details."}
        writeSnapshot()
        if terminatePending{NSApp.reply(toApplicationShouldTerminate:true)}
        else if CommandLine.arguments.contains("--quit-on-complete"){NSApp.terminate(nil)}
    }
    func writeSnapshot(){
        let args=CommandLine.arguments
        guard let i=args.firstIndex(of:"--ui-snapshot"),i+1<args.count else{return}
        let path=URL(fileURLWithPath:args[i+1]);let view=window.contentView!
        view.layoutSubtreeIfNeeded()
        if let rep=view.bitmapImageRepForCachingDisplay(in:view.bounds) {
            view.cacheDisplay(in:view.bounds,to:rep)
            if let bytes=rep.representation(using:.png,properties:[:]){try? bytes.write(to:path)}
        }
        let report:[String:Any]=["status":status.stringValue,"progress":progress.doubleValue,"result":resultURL?.path ?? "","convert_enabled":convertButton.isEnabled,"cancel_enabled":cancelButton.isEnabled,"reveal_enabled":revealButton.isEnabled,"source":sourceField.stringValue,"output":outputField.stringValue]
        if let bytes=try? JSONSerialization.data(withJSONObject:report,options:.prettyPrinted){try? bytes.write(to:path.appendingPathExtension("json"))}
    }
    @objc func cancel(){cancelling=true;cancelButton.isEnabled=false;status.stringValue="Cancelling and removing partial output…";process?.terminate()}
    @objc func reveal(){if let url=resultURL{NSWorkspace.shared.activateFileViewerSelecting([url])}}
    @objc func showLog(){if let url=logURL{NSWorkspace.shared.activateFileViewerSelecting([url])}}
    func applicationShouldTerminate(_ sender:NSApplication)->NSApplication.TerminateReply {if process != nil{terminatePending=true;cancel();return .terminateLater};return .terminateNow}
    func applicationShouldTerminateAfterLastWindowClosed(_ sender:NSApplication)->Bool{true}
}
let app=NSApplication.shared
let delegate=Converter();app.delegate=delegate;app.setActivationPolicy(.regular);app.run()
