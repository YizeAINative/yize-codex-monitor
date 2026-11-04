import Foundation
import CSQLite

struct TaskRow: Codable, Equatable {
    var id = "", title = "", project = "", state = "待确认", turn = ""
    var started: Date?, finished: Date?
    var platform = "codex", cwd = "", file = "", visible = false, estimated = false
    var activity: Date?, notified = ""
    var key: String { platform + ":" + id }
    var round: String { key + ":" + (turn.isEmpty ? String((started ?? finished ?? .distantPast).timeIntervalSince1970) : turn) }
    var terminal: Bool { ["完成", "已中断", "失败"].contains(state) }
    var name: String { title.isEmpty ? "新对话 · " + String(id.prefix(8)) : title }
    var link: URL? {
        if platform == "claude" { guard id.hasPrefix("local_"), UUID(uuidString: String(id.dropFirst(6))) != nil else { return nil }; return URL(string: "claude://claude.ai/epitaxy/" + id) }
        guard UUID(uuidString: id) != nil else { return nil }
        return URL(string: platform == "workbuddy" ? "workbuddy://chat/" + id : "codex://threads/" + id)
    }
}
func object(_ data: Data) -> [String: Any]? { (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] }
func eventDate(_ value: Any?) -> Date? {
    guard let text = value as? String else { return nil }
    let f = ISO8601DateFormatter(); f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    return f.date(from: text) ?? ISO8601DateFormatter().date(from: text)
}
func elapsed(_ row: TaskRow, now: Date = Date()) -> String {
    guard let start = row.started else { return "--:--" }
    let end = row.finished ?? (row.state == "待确认" ? row.activity ?? start : now)
    let seconds = max(0, Int(end.timeIntervalSince(start)))
    return (row.estimated ? "≈" : "") + (seconds >= 3600 ? String(format: "%02d:%02d:%02d", seconds / 3600, seconds / 60 % 60, seconds % 60) : String(format: "%02d:%02d", seconds / 60, seconds % 60))
}
struct Settings: Codable {
    var enabled = ["codex": true, "workbuddy": true, "claude": true]
    var sound = false, notifications = true, pinned = true
    var collapsed = Set<String>()
}
struct Document: Codable {
    var version = 1
    var sessions = [String: TaskRow]()
    var history = [TaskRow]()
    var dismissed = Set<String>()
}
final class Store {
    let root: URL
    var error = ""
    init(_ root: URL) { self.root = root }
    func load<T: Decodable>(_ name: String, as: T.Type) -> T? {
        for suffix in ["", ".bak"] {
            if let data = try? Data(contentsOf: root.appendingPathComponent(name + suffix)), let result = try? JSONDecoder().decode(T.self, from: data) { return result }
        }; return nil
    }
    @discardableResult func save<T: Encodable>(_ value: T, _ name: String) -> Bool {
        do {
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            let url = root.appendingPathComponent(name), backup = root.appendingPathComponent(name + ".bak")
            let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
            let data = try encoder.encode(value)
            if let old = try? Data(contentsOf: url), old == data { return true }
            if let old = try? Data(contentsOf: url), (try? JSONSerialization.jsonObject(with: old)) != nil { try old.write(to: backup, options: .atomic) }
            try data.write(to: url, options: .atomic); error = ""; return true
        } catch { self.error = "本地状态保存失败：" + error.localizedDescription; return false }
    }
}
// Offsets and partial UTF-8 are retained between polls. Oversized lines are skipped in bounded memory.
final class Reader {
    var offset: UInt64 = 0, pending = Data(), skipping = false
    let maxLine = 2 * 1024 * 1024
    func reset(_ offset: UInt64 = 0) { self.offset = offset; pending.removeAll(); skipping = false }
    func read(_ url: URL, budget: Int = 262144, consume: (Data) -> Void) throws {
        let f = try FileHandle(forReadingFrom: url); defer { try? f.close() }; try f.seek(toOffset: offset)
        var left = budget
        while left > 0 {
            guard let bytes = try f.read(upToCount: min(left, 65536)), !bytes.isEmpty else { break }
            offset += UInt64(bytes.count); left -= bytes.count
            for byte in bytes {
                if byte == 10 { if !skipping { consume(pending) }; pending.removeAll(keepingCapacity: true); skipping = false }
                else if !skipping { if pending.count < maxLine { pending.append(byte) } else { pending.removeAll(keepingCapacity: true); skipping = true } }
            }
        }
    }
}
func codexEvent(_ o: [String: Any], _ s: inout TaskRow) -> Bool {
    guard o["type"] as? String == "event_msg", let p = o["payload"] as? [String: Any], let kind = p["type"] as? String, ["task_started", "task_complete", "turn_aborted", "task_failed"].contains(kind) else { return false }
    let turn = p["turn_id"] as? String ?? "", at = eventDate(o["timestamp"])
    if kind == "task_started" {
        if !turn.isEmpty && turn == s.turn && s.terminal { return false }
        s.turn = turn; s.state = "运行中"; s.started = eventDate(p["started_at"]) ?? at; s.finished = nil; s.visible = true; s.estimated = false
    } else {
        if !turn.isEmpty && !s.turn.isEmpty && turn != s.turn { return false }
        if !turn.isEmpty { s.turn = turn }
        s.state = kind == "task_complete" ? "完成" : kind == "turn_aborted" ? "已中断" : "失败"
        s.finished = at; if s.started == nil { s.started = eventDate(p["started_at"]) }
    }
    s.activity = at; return true
}
func claudeEvent(_ o: [String: Any], cli: String, _ s: inout TaskRow) -> Bool {
    if o["isSidechain"] as? Bool == true { return false }
    if let id = o["sessionId"] as? String, id != cli { return false }
    guard let at = eventDate(o["timestamp"]), at >= (s.activity ?? .distantPast) else { return false }
    let type = o["type"] as? String ?? "", message = o["message"] as? [String: Any] ?? [:]
    let blocks = message["content"] as? [[String: Any]] ?? []
    if type == "user" {
        let interrupt = ["[Request interrupted by user]", "[Request interrupted by user for tool use]"]
        if interrupt.contains(message["content"] as? String ?? "") || blocks.contains(where: { interrupt.contains($0["text"] as? String ?? "") }) { s.state = "已中断"; s.finished = at; s.activity = at; return true }
        if o["isMeta"] as? Bool == true { return false }
        if blocks.contains(where: { $0["type"] as? String == "tool_result" }) { if !s.terminal && s.started != nil { s.state = "运行中" }; s.activity = at; return true }
        guard !message.isEmpty else { return false }
        let turn = o["uuid"] as? String ?? "cc-\(at.timeIntervalSince1970)"
        if s.turn == turn { return false }
        s.turn = turn; s.started = at; s.finished = nil; s.state = "运行中"; s.visible = true; s.estimated = false
    } else if type == "assistant" {
        if o["isApiErrorMessage"] as? Bool == true || message["stop_reason"] as? String == "max_tokens" { s.state = "失败"; s.finished = at }
        else if message["stop_reason"] as? String == "end_turn" { if ["已中断", "失败"].contains(s.state) { return false }; s.state = "完成"; s.finished = at }
        else if s.started != nil { s.state = blocks.contains(where: { $0["type"] as? String == "tool_use" && ["AskUserQuestion", "ExitPlanMode"].contains($0["name"] as? String ?? "") }) ? "等待输入" : "运行中"; s.finished = nil }
    } else if type == "system" && o["subtype"] as? String == "turn_duration" { if s.state == "完成" { s.finished = at } }
    else if type == "progress" { if s.started != nil && s.finished == nil { s.state = "运行中" } }
    else { return false }
    s.activity = at; return true
}
struct Runtime {
    var writers = Set<String>(), codex = false, workbuddy = false, claude = false
    static func output(_ executable: String, _ args: [String]) -> String {
        let p = Process(), pipe = Pipe(); p.executableURL = URL(fileURLWithPath: executable); p.arguments = args; p.standardOutput = pipe; p.standardError = FileHandle.nullDevice
        do { try p.run(); let bytes = pipe.fileHandleForReading.readDataToEndOfFile(); p.waitUntilExit(); return String(decoding: bytes, as: UTF8.self) } catch { return "" }
    }
    static func inspect() -> Runtime {
        var result = Runtime()
        let listing = output("/bin/ps", ["-axo", "pid=,ppid=,args="])
        var parents = [Int: Int](), desktop = Set<Int>(), workers = [Int]()
        for line in listing.split(separator: "\n") {
            let parts = line.split(maxSplits: 2, whereSeparator: { $0 == " " || $0 == "\t" }); guard parts.count == 3, let pid = Int(parts[0]), let parent = Int(parts[1]) else { continue }
            parents[pid] = parent; let args = String(parts[2])
            if args.contains("/Codex.app/Contents/MacOS/Codex") || args.contains("/codex app-server") || args.hasPrefix("codex ") || args.contains("/bin/codex ") { result.codex = true }
            if args.contains("/WorkBuddy.app/") || args.hasPrefix("WorkBuddy ") { result.workbuddy = true }
            if args.contains("/Claude.app/Contents/MacOS/Claude") { desktop.insert(pid) }
            if args.contains("--input-format") && args.contains("stream-json") { workers.append(pid) }
        }
        for worker in workers { var pid = worker, seen = Set<Int>(); while let parent = parents[pid], seen.insert(pid).inserted { if desktop.contains(parent) { result.claude = true; break }; pid = parent } }
        // lsof reports descriptors per process; retain writable descriptors only.
        let handles = output("/usr/sbin/lsof", ["-n", "-P", "-Ffan", "-u", NSUserName()])
        var writable = false
        for line in handles.split(separator: "\n") {
            if line.first == "f" { writable = false }
            if line == "aw" || line == "au" { writable = true }
            if line.first == "n", writable, line.hasSuffix(".jsonl") { result.writers.insert(String(line.dropFirst())) }
        }
        return result
    }
    func hasWriter(_ path: String) -> Bool { let canonical = URL(fileURLWithPath: path).resolvingSymlinksInPath().path; return writers.contains(path) || writers.contains(canonical) || writers.contains { URL(fileURLWithPath: $0).resolvingSymlinksInPath().path == canonical } }
    func active(_ platform: String) -> Bool { platform == "codex" ? codex : platform == "workbuddy" ? workbuddy : claude }
}
func hydrateCodex(_ url: URL, size: UInt64, row: inout TaskRow) throws -> Int {
    let f = try FileHandle(forReadingFrom: url); defer { try? f.close() }
    var position = size, carry = Data(), events = [[String: Any]](), read = 0
    outer: while position > 0 {
        let begin = position > 65536 ? position - 65536 : 0
        try f.seek(toOffset: begin)
        var data = try f.read(upToCount: Int(position - begin)) ?? Data(); read += data.count; data.append(carry)
        let chunks = data.split(separator: 10, omittingEmptySubsequences: false)
        for i in chunks.indices.reversed() where i > 0 || begin == 0 {
            if position == size && i == chunks.count - 1 && data.last != 10 { continue }
            guard chunks[i].prefix(600).range(of: Data("\"event_msg\"".utf8)) != nil, let o = object(Data(chunks[i])), o["type"] as? String == "event_msg", let p = o["payload"] as? [String: Any], let kind = p["type"] as? String, ["task_started", "task_complete", "turn_aborted", "task_failed"].contains(kind) else { continue }
            events.append(o)
            if kind == "task_started" { break outer }
            if events.count >= 256 { break outer }
        }
        carry = chunks.first.map { Data($0.prefix(2 * 1024 * 1024)) } ?? Data(); position = begin
    }
    for event in events.reversed() { _ = codexEvent(event, &row) }
    return read
}
final class Track {
    var reader = Reader(), row: TaskRow, cli = "", seed: Date?, hydrated = false
    var identity: UInt64 = 0
    init(_ row: TaskRow) { self.row = row }
}
final class Monitor {
    let store: Store, home: URL, wb: URL, desktop: URL, transcripts: URL
    var document: Document, settings: Settings
    var tracks = [String: Track](), health = [String: String](), initialized = Set<String>()
    var notices = [TaskRow](), runtime = Runtime(), now = Date(), bytesRead = 0
    var runtimeAt = Date.distantPast
    var assignments = [String: String](), localProjects = [String: String]()
    var lastDiscovery = Date.distantPast, paths = [URL](), titleStamp = Date.distantPast, titles = [String: String](), projectNames = [String: String]()
    init(home: URL, data: URL, wb: URL? = nil, desktop: URL? = nil, transcripts: URL? = nil) {
        self.home = home; store = Store(data); document = store.load("current-tasks.json", as: Document.self) ?? store.load("state.json", as: Document.self) ?? Document(); if let history = store.load("completion-history.json", as: [TaskRow].self) { document.history = history }; settings = store.load("settings.json", as: Settings.self) ?? Settings()
        let env = ProcessInfo.processInfo.environment, user = FileManager.default.homeDirectoryForCurrentUser
        self.wb = wb ?? URL(fileURLWithPath: env["WORKBUDDY_HOME"] ?? (FileManager.default.fileExists(atPath: user.appendingPathComponent(".workbuddy-ai/workbuddy.db").path) ? user.appendingPathComponent(".workbuddy-ai").path : user.appendingPathComponent(".workbuddy").path))
        self.desktop = desktop ?? URL(fileURLWithPath: env["CLAUDE_DESKTOP_HOME"] ?? user.appendingPathComponent("Library/Application Support/Claude").path)
        self.transcripts = transcripts ?? URL(fileURLWithPath: env["CLAUDE_CONFIG_DIR"] ?? user.appendingPathComponent(".claude").path)
        for (key, var row) in document.sessions { if ["运行中", "等待输入"].contains(row.state) { row.state = "待确认"; document.sessions[key] = row } }
    }
    var current: [TaskRow] { document.sessions.values.filter { $0.visible && settings.enabled[$0.platform] == true && !document.dismissed.contains($0.round) }.sorted { ($0.platform, $0.cwd, $0.name, $0.id) < ($1.platform, $1.cwd, $1.name, $1.id) } }
    var recent: [TaskRow] { var seen = Set<String>(); return document.history.sorted { ($0.finished ?? .distantPast) > ($1.finished ?? .distantPast) }.filter { seen.insert($0.key).inserted } }
    @discardableResult func saveDocument() -> Bool {
        guard store.save(document.history, "completion-history.json") else { return false }
        var current = document; current.history = []
        return store.save(current, "current-tasks.json")
    }
    func persist() { if saveDocument() { store.save(settings, "settings.json") } }
    func record(_ row: TaskRow) { guard row.state == "完成", row.finished != nil, !document.history.contains(where: { $0.round == row.round }) else { return }; document.history.append(row); document.history.sort { ($0.finished ?? .distantPast) > ($1.finished ?? .distantPast) }; document.history = Array(document.history.prefix(200)) }
    func dismiss(_ row: TaskRow) {
        guard let actual = document.sessions[row.key], actual.round == row.round, actual.state != "运行中", actual.state != "等待输入" else { return }
        let before = document; record(actual); document.dismissed.insert(actual.round); document.sessions[row.key]?.visible = false; if !saveDocument() { document = before }
    }
    func clear() { for row in current where row.terminal { dismiss(row) } }
    func configure(_ platform: String, enabled: Bool) {
        settings.enabled[platform] = enabled; initialized.remove(platform)
        tracks = tracks.filter { $0.value.row.platform != platform }; persist(); lastDiscovery = .distantPast
    }
    @discardableResult func reconcile(_ row: TaskRow, initial: Bool, discovered: Bool) -> TaskRow {
        var row = row; let old = document.sessions[row.key]
        if !row.turn.isEmpty { document.dismissed = document.dismissed.filter { !$0.hasPrefix(row.key + ":") || $0 == row.round } }
        if document.dismissed.contains(row.round) { row.visible = false }
        else if old?.visible == true || ["运行中", "等待输入"].contains(row.state) { row.visible = true }
        else if !initial && discovered && row.terminal && now.timeIntervalSince(row.finished ?? .distantPast) < 30 { row.visible = true }
        else if discovered { row.visible = false }
        if !runtime.active(row.platform) && ["运行中", "等待输入"].contains(row.state) { row.state = "待确认" }
        if row.visible || document.dismissed.contains(row.round) { document.sessions[row.key] = row }
        if row.visible { record(row) }
        if row.state == "完成" {
            let key = row.round
            let notify = !initial && row.visible && old?.notified != key && (old?.round == row.round || (!discovered || now.timeIntervalSince(row.finished ?? .distantPast) < 30))
            row.notified = key
            if row.visible { document.sessions[row.key] = row }
            if notify { notices.append(row) }
        }
        return row
    }
    func poll(runtime: Runtime? = nil, date: Date = Date()) {
        now = date; notices = []; if let runtime { self.runtime = runtime } else if now.timeIntervalSince(runtimeAt) >= 5 { self.runtime = Runtime.inspect(); runtimeAt = now }
        if settings.enabled["codex"] == true { pollCodex() }
        if settings.enabled["workbuddy"] == true { pollWorkBuddy() }
        if settings.enabled["claude"] == true { pollClaude() }
        for (key, var row) in document.sessions where settings.enabled[row.platform] == true && ["运行中", "等待输入"].contains(row.state) {
            if !self.runtime.active(row.platform) || !FileManager.default.fileExists(atPath: row.file) { row.state = "待确认"; document.sessions[key] = row }
        }
        persist()
    }
    func files(_ root: URL, ext: String) -> [URL] { guard let e = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.contentModificationDateKey], options: [.skipsHiddenFiles]) else { return [] }; return e.compactMap { $0 as? URL }.filter { $0.pathExtension == ext } }
    func readTrack(_ t: Track) throws {
        let url = URL(fileURLWithPath: t.row.file), attr = try FileManager.default.attributesOfItem(atPath: url.path)
        let size = (attr[.size] as? NSNumber)?.uint64Value ?? 0, inode = (attr[.systemFileNumber] as? NSNumber)?.uint64Value ?? 0
        let fresh = !t.hydrated || size < t.reader.offset || (t.identity != 0 && inode != t.identity)
        t.identity = inode
        if fresh {
            t.reader.reset(size > 262144 ? size - 262144 : 0)
            if t.reader.offset > 0 { let f = try FileHandle(forReadingFrom: url); try f.seek(toOffset: t.reader.offset - 1); t.reader.skipping = try f.read(upToCount: 1)?.first != 10; try f.close() }
            // Persisted lifecycle survives tail hydration if its start has left the bounded window.
            if !t.hydrated, let old = document.sessions[t.row.key] { t.row = old; t.row.file = url.path }
            else if t.hydrated { t.row.state = "待确认"; t.row.turn = ""; t.row.started = nil; t.row.finished = nil; t.row.activity = nil }
            if t.row.platform == "codex" {
                bytesRead += try hydrateCodex(url, size: size, row: &t.row)
                t.reader.reset(size)
                // Preserve an unfinished final line for the next append.
                if size > 0 { let f = try FileHandle(forReadingFrom: url); let start = size > 2 * 1024 * 1024 ? size - 2 * 1024 * 1024 : 0; try f.seek(toOffset: start); let tail = try f.readToEnd() ?? Data(); try f.close(); if tail.last != 10 { if let newline = tail.lastIndex(of: 10) { t.reader.pending = Data(tail.suffix(from: tail.index(after: newline))) } else if start == 0 { t.reader.pending = tail } else { t.reader.skipping = true } } }
            }
            if let seed = t.seed, (t.row.started ?? .distantPast) < seed { t.row.started = seed; t.row.turn = "desktop-\(seed.timeIntervalSince1970)"; t.row.activity = seed; t.row.finished = nil; t.row.state = runtime.claude ? "运行中" : "待确认"; t.row.estimated = true }
        }
        let before = t.reader.offset
        try t.reader.read(url, budget: t.row.platform == "claude" ? 262144 : 16 * 1024 * 1024) { data in
            guard let o = object(data) else { return }
            if t.row.platform == "claude" { _ = claudeEvent(o, cli: t.cli, &t.row) } else { _ = codexEvent(o, &t.row) }
        }
        bytesRead += Int(t.reader.offset - before)
        if t.row.platform == "codex", t.reader.offset > before, t.row.started != nil, t.row.finished == nil, !fresh { t.row.state = "运行中" }
        let modified = attr[.modificationDate] as? Date ?? .distantPast
        if t.row.platform == "codex" { t.row.activity = max(t.row.activity ?? .distantPast, modified) }
        if ["运行中", "等待输入"].contains(t.row.state), now.timeIntervalSince(t.row.activity ?? .distantPast) > 180, !runtime.hasWriter(t.row.file) { t.row.state = "待确认" }
        else if t.row.state == "待确认", t.row.started != nil, t.row.finished == nil, runtime.hasWriter(t.row.file) { t.row.state = "运行中" }
        t.hydrated = true
    }
    func pollCodex() {
        let initial = !initialized.contains("codex"), dir = home.appendingPathComponent("sessions")
        guard FileManager.default.fileExists(atPath: dir.path) else { unavailable("codex", "等待 Codex 本地会话"); return }
        if now.timeIntervalSince(lastDiscovery) >= 10 || initial { paths = files(dir, ext: "jsonl"); lastDiscovery = now }
        let indexURL = home.appendingPathComponent("session_index.jsonl")
        let stamp = (try? indexURL.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
        if stamp != titleStamp {
            titles = [:]; titleStamp = stamp
            if let data = try? Data(contentsOf: indexURL) { for line in data.split(separator: 10) { if let o = object(Data(line)), let id = o["id"] as? String { titles[id] = o["thread_name"] as? String } } }
        }
        if let data = try? Data(contentsOf: home.appendingPathComponent(".codex-global-state.json")), let o = object(data) {
            projectNames = o["electron-workspace-root-labels"] as? [String: String] ?? [:]
            assignments = o["thread-project-assignments"] as? [String: String] ?? [:]; localProjects = [:]
            for (id, value) in o["local-projects"] as? [String: [String: Any]] ?? [:] { if let name = value["name"] as? String { localProjects[id] = name; for path in value["rootPaths"] as? [String] ?? [] { projectNames[path] = name } } }
        }
        var failed = false
        for url in paths {
            do {
                var discovered = false
                if tracks[url.path] == nil {
                    let stamp = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
                    if now.timeIntervalSince(stamp) > 172800 && !runtime.hasWriter(url.path) && !document.sessions.values.contains(where: { $0.file == url.path }) { continue }
                    let f = try FileHandle(forReadingFrom: url); let data = try f.read(upToCount: 262144); try f.close()
                    guard let line = data?.split(separator: 10).first, let o = object(Data(line)), o["type"] as? String == "session_meta", let p = o["payload"] as? [String: Any], let id = p["id"] as? String, UUID(uuidString: id) != nil else { continue }
                    if (p["parent_thread_id"] as? String)?.isEmpty == false || (p["source"] as? [String: Any])?["subagent"] != nil { continue }
                    if let path = p["agent_path"] as? String, !path.isEmpty && path != "/root" { continue }
                    var row = document.sessions["codex:" + id] ?? TaskRow(); row.id = id; row.cwd = p["cwd"] as? String ?? ""; row.file = url.path
                    tracks[url.path] = Track(row); discovered = true
                }
                let t = tracks[url.path]!; try readTrack(t); t.row.title = titles[t.row.id] ?? t.row.title; t.row.project = localProjects[assignments[t.row.id] ?? ""] ?? projectNames[t.row.cwd] ?? URL(fileURLWithPath: t.row.cwd).lastPathComponent
                t.row = reconcile(t.row, initial: initial, discovered: discovered)
            } catch { failed = true; if let t = tracks[url.path] { if var row = document.sessions[t.row.key], ["运行中", "等待输入"].contains(row.state) { row.state = "待确认"; document.sessions[row.key] = row } } }
        }
        health["codex"] = failed ? "Codex 部分日志不可读，正在重试" : "Codex 已连接"; initialized.insert("codex")
    }
    func unavailable(_ platform: String, _ message: String) { health[platform] = message; for (key, var row) in document.sessions where row.platform == platform && ["运行中", "等待输入"].contains(row.state) { row.state = "待确认"; document.sessions[key] = row } }
    func pollWorkBuddy() {
        let initial = !initialized.contains("workbuddy"), db = wb.appendingPathComponent("workbuddy.db")
        guard FileManager.default.fileExists(atPath: db.path) else { unavailable("workbuddy", "等待 WorkBuddy 本地数据库"); return }
        do {
            let columns = Set(try SQLite.query(db.path, "PRAGMA table_info(sessions)").compactMap { $0["name"] })
            guard Set(["id", "cwd", "status", "updated_at"]).isSubset(of: columns) else { throw NSError(domain: "WorkBuddy 数据结构不匹配", code: 1) }
            let title = columns.contains("title") ? "title" : "NULL", custom = columns.contains("custom_title") ? "custom_title" : "NULL"
            let rows = try SQLite.query(db.path, "SELECT id,cwd,status,COALESCE(NULLIF(\(custom),''),\(title),'') AS title,updated_at,\(columns.contains("created_at") ? "created_at" : "updated_at") AS created,\(columns.contains("last_activity_at") ? "COALESCE(last_activity_at,0)" : "0") AS activity FROM sessions\(columns.contains("deleted_at") ? " WHERE deleted_at IS NULL" : "")")
            var present = Set<String>()
            for data in rows {
                guard let id = data["id"], UUID(uuidString: id) != nil else { continue }
                let key = "workbuddy:" + id; present.insert(key); let old = document.sessions[key]; var row = old ?? TaskRow(); row.id = id; row.platform = "workbuddy"; row.cwd = data["cwd"] ?? ""; row.project = URL(fileURLWithPath: row.cwd).lastPathComponent; row.title = data["title"] ?? ""; row.file = db.path
                let raw = data["status"]?.lowercased() ?? "", at = Date(timeIntervalSince1970: (Double(data["updated_at"] ?? "") ?? 0) / 1000)
                let states = ["working":"运行中", "planning":"运行中", "running":"运行中", "in_progress":"运行中", "inprogress":"运行中", "pending":"等待输入", "awaiting_input":"等待输入", "waiting":"等待输入", "completed":"完成", "terminated":"已中断", "interrupted":"已中断", "cancelled":"已中断", "canceled":"已中断", "error":"失败", "failed":"失败", "errored":"失败", "archived":"已归档"]
                let state = states[raw] ?? "待确认", busy = old.map { ["运行中", "等待输入"].contains($0.state) && $0.finished == nil } ?? false
                if state == "运行中" && (!busy || !row.visible || row.turn.isEmpty) { row.turn = "wb-" + (data["updated_at"] ?? ""); row.started = at; row.finished = nil; row.estimated = initial; row.visible = true }
                if state == "等待输入" && !busy && !row.visible && (Double(data["activity"] ?? "") ?? 0) > (Double(data["created"] ?? "") ?? 0) && now.timeIntervalSince(at) < 172800 { row.turn = "wb-" + (data["updated_at"] ?? ""); row.started = at; row.estimated = true; row.visible = true }
                row.state = state; row.activity = at
                if row.terminal && (old?.state != state || row.finished == nil) { row.finished = at }
                if state == "已归档" { row.visible = false; document.sessions.removeValue(forKey: key); continue }
                reconcile(row, initial: initial, discovered: old == nil)
            }
            for (key, row) in document.sessions where row.platform == "workbuddy" && !present.contains(key) { document.sessions[key]?.visible = false }
            initialized.insert("workbuddy"); health["workbuddy"] = "WorkBuddy 已连接"
        } catch { unavailable("workbuddy", "WorkBuddy 读取失败：" + error.localizedDescription) }
    }
    func pollClaude() {
        let initial = !initialized.contains("claude"), index = desktop.lastPathComponent == "claude-code-sessions" ? desktop : desktop.appendingPathComponent("claude-code-sessions")
        guard FileManager.default.fileExists(atPath: index.path) else { unavailable("claude", "等待 Claude 桌面端 Code 索引"); return }
        var records = [String: (TaskRow, Date?)](), written = [String: Date]()
        for url in files(index, ext: "json") {
            guard let data = try? Data(contentsOf: url), let o = object(data), let id = o["sessionId"] as? String, id.hasPrefix("local_"), UUID(uuidString: String(id.dropFirst(6))) != nil, let cli = o["cliSessionId"] as? String, UUID(uuidString: cli) != nil, o["isArchived"] as? Bool != true, (o["spawnedFrom"] as? [String: Any])?["sideSession"] as? Bool != true else { continue }
            let stamp = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
            if let previous = written[cli], previous >= stamp { continue }; written[cli] = stamp
            var row = document.sessions["claude:" + id] ?? TaskRow(); row.id = id; row.platform = "claude"; row.title = o["title"] as? String ?? ""; row.cwd = o["cwd"] as? String ?? ""; row.project = URL(fileURLWithPath: row.cwd).lastPathComponent
            let ms = (o["latestUserFrameAt"] as? NSNumber)?.doubleValue ?? 0; records[cli] = (row, ms > 0 ? Date(timeIntervalSince1970: ms / 1000) : nil)
        }
        var failures = false
        for url in files(transcripts.appendingPathComponent("projects"), ext: "jsonl") where !url.path.contains("/subagents/") {
            let cli = url.deletingPathExtension().lastPathComponent
            guard let (metadata, seed) = records[cli] else { continue }
            let discovered = tracks[url.path] == nil
            let t = tracks[url.path] ?? Track(metadata); t.cli = cli; t.seed = seed; t.row.file = url.path; t.row.title = metadata.title; t.row.cwd = metadata.cwd; t.row.project = metadata.project; tracks[url.path] = t
            do { try readTrack(t)
                if let seed, (t.row.started ?? .distantPast) > seed.addingTimeInterval(5) { continue }
                if let seed, (t.row.started ?? .distantPast) < seed { t.row.started = seed; t.row.turn = "desktop-\(seed.timeIntervalSince1970)"; t.row.activity = seed; t.row.finished = nil; t.row.state = runtime.claude ? "运行中" : "待确认"; t.row.estimated = true }
                t.row = reconcile(t.row, initial: initial, discovered: discovered)
            } catch { failures = true }
        }
        let desktopIDs = Set(records.values.map { $0.0.id })
        for (key, row) in document.sessions where row.platform == "claude" && !desktopIDs.contains(row.id) { document.sessions[key]?.visible = false }
        initialized.insert("claude"); health["claude"] = failures ? "Claude 部分日志不可读，正在重试" : records.isEmpty ? "等待 Claude 桌面端 Code 会话" : "Claude 桌面端 Code 已连接"
    }
}
enum SQLite {
    static func query(_ path: String, _ sql: String) throws -> [[String: String]] {
        var db: OpaquePointer?, statement: OpaquePointer?
        let uri = URL(fileURLWithPath: path).absoluteString + "?mode=ro" + (FileManager.default.fileExists(atPath: path + "-wal") ? "" : "&immutable=1")
        guard sqlite3_open_v2(uri, &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, nil) == SQLITE_OK else { if db != nil { sqlite3_close(db) }; throw NSError(domain: "SQLite 只读打开失败", code: 1) }
        defer { sqlite3_finalize(statement); sqlite3_close(db) }; sqlite3_busy_timeout(db, 150)
        guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK else { throw NSError(domain: "SQLite 查询失败", code: 2, userInfo: [NSLocalizedDescriptionKey: String(cString: sqlite3_errmsg(db))]) }
        var result = [[String: String]](), code = sqlite3_step(statement)
        while code == SQLITE_ROW {
            var row = [String: String]()
            for i in 0..<sqlite3_column_count(statement) { let name = String(cString: sqlite3_column_name(statement, i)); row[name] = sqlite3_column_text(statement, i).map { String(cString: $0) } ?? "" }
            result.append(row); code = sqlite3_step(statement)
        }
        guard code == SQLITE_DONE else { throw NSError(domain: "SQLite 读取中断", code: 3) }; return result
    }
}
