import Foundation
import AppKit
import Darwin

func fail(_ message: String) -> Never {
    fputs("ULTRAKILL: \(message)\n", stderr)
    if ProcessInfo.processInfo.environment["ULTRAKILL_MAC_TEST_MUTE"] != "1" {
        let alert = NSAlert(); alert.messageText = "ULTRAKILL could not start"; alert.informativeText = message; alert.runModal()
    }
    exit(1)
}
let fm = FileManager.default
let executable = URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
let contents = executable.deletingLastPathComponent().deletingLastPathComponent()
let resources = contents.appendingPathComponent("Resources")
let data = resources.appendingPathComponent("Data")
do {
    let config = try JSONSerialization.jsonObject(with: Data(contentsOf: resources.appendingPathComponent("converter-profile.json"))) as! [String:Any]
    guard let id = config["profile_id"] as? String, UUID(uuidString:id) != nil else { fail("The profile identifier is invalid.") }
    let override = ProcessInfo.processInfo.environment["ULTRAKILL_MAC_DATA_PATH"]
    let profile = override.map { URL(fileURLWithPath:$0).deletingLastPathComponent() } ?? fm.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/ULTRAKILL Mac/\(id)")
    try fm.createDirectory(at:profile,withIntermediateDirectories:true)
    let lock = open(profile.appendingPathComponent(".initialization.lock").path,O_CREAT|O_RDWR,0o600)
    guard lock >= 0 else { fail("Cannot open the profile lock.") }
    flock(lock,LOCK_EX)
    if !fm.fileExists(atPath:profile.appendingPathComponent(".initialized").path) {
        let seed = resources.appendingPathComponent("ProfileSeed")
        for name in try fm.contentsOfDirectory(atPath:seed.path) {
            let dst=profile.appendingPathComponent(name)
            if !fm.fileExists(atPath:dst.path) { try fm.copyItem(at:seed.appendingPathComponent(name),to:dst) }
        }
        try Data().write(to:profile.appendingPathComponent(".initialized"),options:.atomic)
    }
    let link = profile.appendingPathComponent("ULTRAKILL_Data")
    // Each bundle has its own profile. Refresh the link when the app is moved.
    if let old = try? fm.destinationOfSymbolicLink(atPath:link.path) {
        if old != data.path { try fm.removeItem(at:link);try fm.createSymbolicLink(atPath:link.path,withDestinationPath:data.path) }
    } else if !fm.fileExists(atPath:link.path) {
        try fm.createSymbolicLink(atPath:link.path,withDestinationPath:data.path)
    } else { fail("ULTRAKILL_Data in the profile must be a symbolic link.") }
    flock(lock,LOCK_UN);close(lock)
    let logs = profile.appendingPathComponent("Logs"), diagnostics = profile.appendingPathComponent("Diagnostics")
    try fm.createDirectory(at:logs,withIntermediateDirectories:true)
    try fm.createDirectory(at:diagnostics,withIntermediateDirectories:true)
    setenv("ULTRAKILL_MAC_DATA_PATH",link.path,1)
    setenv("ULTRAKILL_MAC_DIAGNOSTICS_PATH",diagnostics.path,1)
    let source = config["source"] as? String ?? ""
    let escaped = source.replacingOccurrences(of:"\\",with:"\\\\").replacingOccurrences(of:"\"",with:"\\\"")
    let sandbox = "(version 1)\n(allow default)\n" + (source.isEmpty ? "" : "(deny file-write* (subpath \"\(escaped)\"))\n")
    let policy = profile.appendingPathComponent("source-protection.sb")
    try sandbox.write(to:policy,atomically:true,encoding:.utf8)
    var arguments = ["/usr/bin/sandbox-exec","-f",policy.path,contents.appendingPathComponent("MacOS/UnityPlayer").path,"-force-metal"]
    let userArgs=Array(CommandLine.arguments.dropFirst())
    if !userArgs.contains("-logFile") { arguments += ["-logFile",logs.appendingPathComponent("Player.log").path] }
    if let bytes=try? Data(contentsOf:profile.appendingPathComponent("Preferences/LocalPrefs.json")), let prefs=(try? JSONSerialization.jsonObject(with:bytes)) as? [String:Any] {
        if !userArgs.contains("-screen-width"),let width=prefs["resolutionWidth"] as? Int,let height=prefs["resolutionHeight"] as? Int,width>0,height>0 { arguments += ["-screen-width",String(width),"-screen-height",String(height)] }
        if !userArgs.contains("-screen-fullscreen"),let fullscreen=prefs["fullscreen"] as? Bool { arguments += ["-screen-fullscreen",fullscreen ? "1":"0"] }
    }
    arguments += userArgs
    chdir(profile.path)
    let log=open(logs.appendingPathComponent("launcher.log").path,O_CREAT|O_WRONLY|O_TRUNC,0o600)
    if log>=0 {dup2(log,STDOUT_FILENO);dup2(log,STDERR_FILENO);close(log)}
    let pointers=arguments.map { strdup($0) } + [nil]
    pointers.withUnsafeBufferPointer { execv(arguments[0],$0.baseAddress!) }
    fail("Could not execute the Unity player: \(String(cString:strerror(errno)))")
} catch { fail(error.localizedDescription) }
