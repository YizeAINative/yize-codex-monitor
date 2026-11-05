import Foundation
import CSQLite
var checks = 0
func check(_ condition: @autoclosure () -> Bool, _ label: String) { checks += 1; if !condition() { fatalError("FAIL: " + label) } }
let fm = FileManager.default, root = FileManager.default.temporaryDirectory.appendingPathComponent("monitor-test-" + UUID().uuidString)
try fm.createDirectory(at: root, withIntermediateDirectories: true)
defer { try? fm.removeItem(at: root) }
let home = root.appendingPathComponent("codex"), state = root.appendingPathComponent("state"), wb = root.appendingPathComponent("wb"), desktop = root.appendingPathComponent("desktop"), claude = root.appendingPathComponent("claude")
for p in [home.appendingPathComponent("sessions"), wb, desktop.appendingPathComponent("claude-code-sessions"), claude.appendingPathComponent("projects/p")] { try fm.createDirectory(at: p, withIntermediateDirectories: true) }
let id = UUID().uuidString, file = home.appendingPathComponent("sessions/one.jsonl"), now = Date()
let iso = ISO8601DateFormatter()
func json(_ value: [String: Any]) throws -> Data { var d = try JSONSerialization.data(withJSONObject: value); d.append(10); return d }
func event(_ kind: String, turn: String, at: Date = Date()) -> [String: Any] { ["type":"event_msg", "timestamp":iso.string(from: at), "payload":["type":kind, "turn_id":turn]] }
func append(_ value: [String: Any], to url: URL = file) throws { let f = try FileHandle(forWritingTo: url); try f.seekToEnd(); try f.write(contentsOf: json(value)); try f.close() }
try json(["type":"session_meta", "payload":["id":id,"cwd":"/a/shared"]]).write(to:file)
try append(event("task_started", turn:"one", at:now.addingTimeInterval(-65)))
var live = Runtime(); live.codex = true; live.workbuddy = true; live.claude = true; live.writers.insert(file.path)
func engine(_ data: URL = state) -> Monitor { let m = Monitor(home:home, data:data, wb:wb, desktop:desktop, transcripts:claude); m.settings.automaticAgents = false; m.settings.enabled = ["codex":true,"workbuddy":true,"claude":true]; return m }
let m = engine(); m.poll(runtime:live)
check(m.current.count == 1 && m.current[0].state == "运行中", "initial active discovered")
check(elapsed(m.current[0]).hasPrefix("01:"), "runtime from start")
check(m.notices.isEmpty, "startup no notification")
let bytes = m.bytesRead, reads = m.incrementalReads; m.poll(runtime:live); check(m.bytesRead == bytes && m.incrementalReads == reads, "unchanged logs are not opened for incremental reads")
try append(event("task_complete", turn:"older")); m.poll(runtime:live); check(m.current[0].state == "运行中", "late older completion ignored")
// Tool output pushes start beyond the initial tail; incrementally retained start must survive.
try append(["type":"response_item", "payload":["text":String(repeating:"x",count:400000)]])
m.poll(runtime:live); check(m.current[0].started != nil && m.current[0].state == "运行中", "large output preserves lifecycle")
let fresh = engine(root.appendingPathComponent("fresh")); fresh.poll(runtime:live); check(fresh.current.first?.state == "运行中", "reverse hydration finds start beyond tail")
try append(event("task_complete", turn:"one")); m.poll(runtime:live)
check(m.notices.count == 1 && m.recent.count == 1, "completion notified and archived")
let finished = m.current[0]; check(elapsed(finished) == elapsed(finished, now:Date().addingTimeInterval(100)), "completion timer frozen")
m.poll(runtime:live); check(m.notices.isEmpty, "notification deduplicated")
m.clear(); check(m.current.isEmpty && m.recent.count == 1, "clear keeps history")
let restart = engine(); restart.poll(runtime:live); check(restart.current.isEmpty && restart.recent.count == 1, "dismissal survives restart")
try append(event("task_started", turn:"two")); restart.poll(runtime:live); check(restart.current.first?.turn == "two", "new round reappears")
check(restart.recent.count == 1, "new round retains previous completion")
try append(event("turn_aborted", turn:"two")); restart.poll(runtime:live); check(restart.current.first?.state == "已中断" && restart.notices.isEmpty, "abort not successful")
restart.configure("codex", enabled:false); check(restart.current.isEmpty, "disabled platform hidden")
try append(event("task_started", turn:"three")); try append(event("task_complete", turn:"three"))
restart.configure("codex", enabled:true); restart.poll(runtime:live); check(restart.notices.isEmpty, "reenable no missed sound")
let historyCount = restart.recent.count; restart.configure("codex", enabled:false); check(restart.recent.count == historyCount, "history independent of enabled")
let cold = engine(root.appendingPathComponent("cold")); cold.poll(runtime:live); check(cold.current.isEmpty && cold.recent.isEmpty, "cold start excludes old completions"); cold.poll(runtime:live); check(cold.current.isEmpty && cold.recent.isEmpty, "second poll still excludes historical completions")
// Incremental UTF8 and partial lines.
let readerFile = root.appendingPathComponent("reader"), r = Reader(); try Data([0xe4,0xbd]).write(to:readerFile); var parsed = [String]()
try r.read(readerFile) { parsed.append(String(data:$0,encoding:.utf8) ?? "bad") }; check(parsed.isEmpty, "partial UTF8 buffered")
let h = try FileHandle(forWritingTo:readerFile); try h.seekToEnd(); try h.write(contentsOf:Data([0xa0,10])); try h.close(); try r.read(readerFile) { parsed.append(String(data:$0,encoding:.utf8) ?? "bad") }; check(parsed == ["你"], "UTF8 joins on newline")
// Child sessions filtered.
let child = home.appendingPathComponent("sessions/child.jsonl"); var childData = try json(["type":"session_meta","payload":["id":UUID().uuidString,"source":["subagent":"test"]]]); childData.append(try json(event("task_started",turn:"child"))); try childData.write(to:child)
let childEngine = engine(root.appendingPathComponent("child-state")); childEngine.poll(runtime:live); check(childEngine.current.isEmpty, "child session excluded")
// SQLite fixture with metadata only.
let dbFile = wb.appendingPathComponent("workbuddy.db"); var db: OpaquePointer?; sqlite3_open(dbFile.path,&db)
let wid = UUID().uuidString, ms = Int(now.timeIntervalSince1970 * 1000)
sqlite3_exec(db,"CREATE TABLE sessions(id TEXT,cwd TEXT,status TEXT,updated_at INTEGER,title TEXT,created_at INTEGER,last_activity_at INTEGER,deleted_at INTEGER); INSERT INTO sessions VALUES('\(wid)','/wb','working',\(ms),'WB',\(ms-1000),\(ms),NULL)",nil,nil,nil); sqlite3_close(db)
let w = engine(root.appendingPathComponent("wb-state")); w.poll(runtime:live); check(w.current.contains { $0.platform == "workbuddy" && $0.estimated && $0.state == "运行中" }, "workbuddy active estimated start")
sqlite3_open(dbFile.path,&db); sqlite3_exec(db,"UPDATE sessions SET status='completed', updated_at=\(ms+5000)",nil,nil,nil); sqlite3_close(db); w.poll(runtime:live); check(w.notices.filter { $0.platform == "workbuddy" }.count == 1, "workbuddy completion")
// Desktop-only Claude identity, end-turn, interruption and CLI filtering.
let cli = UUID().uuidString, did = "local_" + UUID().uuidString, cf = claude.appendingPathComponent("projects/p/\(cli).jsonl")
let index = desktop.appendingPathComponent("claude-code-sessions/\(did).json")
try JSONSerialization.data(withJSONObject:["sessionId":did,"cliSessionId":cli,"title":"Desktop","cwd":"/claude","latestUserFrameAt":ms]).write(to:index)
try json(["type":"user","sessionId":cli,"uuid":"prompt","timestamp":iso.string(from:now),"message":["content":"fixture"]]).write(to:cf)
try fm.setAttributes([.modificationDate:now.addingTimeInterval(30)],ofItemAtPath:cf.path)
let c = engine(root.appendingPathComponent("claude-state")); c.poll(runtime:live); check(c.current.contains { $0.id == did && $0.state == "运行中" }, "claude desktop mapped")
try append(["type":"assistant","sessionId":cli,"timestamp":iso.string(from:now.addingTimeInterval(2)),"message":["stop_reason":"end_turn"]],to:cf); c.poll(runtime:live); check(c.recent.contains { $0.id == did } && c.notices.contains { $0.id == did }, "claude end-turn archives")
var cs = TaskRow(); cs.id = did; cs.platform = "claude"; cs.started = now
_ = claudeEvent(["type":"user","timestamp":iso.string(from:now),"message":["content":"[Request interrupted by user]"]],cli:cli,&cs)
_ = claudeEvent(["type":"system","subtype":"turn_duration","timestamp":iso.string(from:now.addingTimeInterval(1))],cli:cli,&cs)
check(cs.state == "已中断", "claude duration not success")
check(cs.link?.absoluteString == "claude://claude.ai/epitaxy/" + did, "desktop deep link")
// Backup fallback and invalid-source resilience.
let store = Store(root.appendingPathComponent("backup")); store.save(["one":1],"test.json"); store.save(["two":2],"test.json"); try Data("broken".utf8).write(to:store.root.appendingPathComponent("test.json")); check(store.load("test.json",as:[String:Int].self)?["one"] == 1, "backup recovery")
// Duplicate starts cannot resurrect terminal rounds.
var dup = TaskRow(); _ = codexEvent(event("task_started",turn:"d"),&dup); _ = codexEvent(event("task_failed",turn:"d"),&dup); _ = codexEvent(event("task_started",turn:"d"),&dup); check(dup.state == "失败", "duplicate terminal start ignored")
// Writer evidence sustains quiet turns; without evidence stale turns become unknown.
let quiet = engine(root.appendingPathComponent("quiet")); try append(event("task_started",turn:"quiet",at:now.addingTimeInterval(-500)))
try fm.setAttributes([.modificationDate:now.addingTimeInterval(-500)],ofItemAtPath:file.path)
quiet.poll(runtime:live,date:now); check(quiet.current.first(where: { $0.platform == "codex" })?.state == "运行中", "quiet task with writer remains active")
var noWriter = live; noWriter.writers = []; quiet.poll(runtime:noWriter,date:now); check(quiet.current.first(where: { $0.platform == "codex" })?.state == "待确认", "quiet task without writer is unknown")
try append(["type":"response_item","payload":["type":"tool_result"]]); quiet.poll(runtime:live); check(quiet.current.first(where: { $0.platform == "codex" })?.state == "运行中", "new bytes restore running")
let titleIndex = home.appendingPathComponent("session_index.jsonl"); try json(["id":id,"thread_name":"Renamed"]).write(to:titleIndex); quiet.poll(runtime:live); check(quiet.current.first(where: { $0.platform == "codex" })?.title == "Renamed", "title updates")
// Truncation resets prior lifecycle and reads new one.
var replacement = try json(["type":"session_meta","payload":["id":id,"cwd":"/a/shared"]]); replacement.append(try json(event("task_started",turn:"replacement"))); try replacement.write(to:file); quiet.poll(runtime:live); check(quiet.current.first(where: { $0.platform == "codex" })?.turn == "replacement", "truncation resets lifecycle")
// History stays capped, deduplicated and latest per session.
let cap = engine(root.appendingPathComponent("capacity"))
for i in 0..<205 { var row = TaskRow(); row.id = "same"; row.turn = "round-\(i)"; row.state = "完成"; row.finished = now.addingTimeInterval(Double(i)); cap.record(row) }
check(cap.document.history.count == 200, "history capacity 200")
check(cap.recent.count == 1 && cap.recent[0].turn == "round-204", "latest completion per conversation")
cap.record(cap.recent[0]); check(cap.document.history.count == 200, "history duplicate ignored")
check(fm.fileExists(atPath:state.appendingPathComponent("current-tasks.json").path) && fm.fileExists(atPath:state.appendingPathComponent("completion-history.json").path), "independent persisted documents")
let badLink = TaskRow(id:"invalid"); check(badLink.link == nil, "invalid deep link rejected")
check(Settings().sound == false, "default sound disabled")
let legacy = try JSONDecoder().decode(Settings.self, from: Data("{\"enabled\":{\"codex\":false,\"workbuddy\":true},\"sound\":false,\"notifications\":false,\"pinned\":false,\"collapsed\":[]}".utf8))
check(!legacy.setupCompleted && legacy.enabled["workbuddy"] == true && !legacy.notifications, "legacy settings survive upgrade and request setup")
check(legacy.configured(name:"  ",agents:["codex":true]) == nil && legacy.configured(name:"My Monitor",agents:[:]) == nil, "configuration rejects empty name and no agents")
let configured = legacy.configured(name:"  我的工作台  ",agents:["codex":true])!
let decoded = try JSONDecoder().decode(Settings.self,from:JSONEncoder().encode(configured))
check(decoded.setupCompleted && decoded.displayName == "我的工作台" && decoded.enabled["claude"] == false && !decoded.notifications, "name and agents persist without resetting other preferences")
var pa = TaskRow(); pa.cwd = "/one/project"; pa.state = "运行中"
var pb = pa; pb.id = "second"; pb.platform = "claude"
var pc = pa; pc.cwd = "/two/project"
var pd = pa; pd.cwd = "/waiting"; pd.state = "等待输入"
check(runningProjectCount([pa,pb,pc,pd]) == 2, "compact counts unique running paths across agents")
check(runningProjectCount([]) == 0 && runningProjectCount([pd]) == 0, "compact excludes waiting and empty states")
var mini = configured; mini.immersive = true
check(try! JSONDecoder().decode(Settings.self,from:JSONEncoder().encode(mini)).immersive, "compact preference persists")
let automaticMonitor = engine(root.appendingPathComponent("auto-state")); automaticMonitor.settings.automaticAgents = true
let manualChoices = automaticMonitor.settings.enabled
var justCodex = Runtime(); justCodex.codex = true
 automaticMonitor.poll(runtime:justCodex)
check(automaticMonitor.selectedPlatforms == ["codex"] && automaticMonitor.health["workbuddy"] == nil, "auto selects only running supported clients")
var desktopOnly = Runtime(); desktopOnly.claudeDesktopOpen = true
 automaticMonitor.poll(runtime:desktopOnly)
check(automaticMonitor.selectedPlatforms == ["claude"] && !desktopOnly.active("claude"), "open desktop selected without claiming active code task")
check(automaticMonitor.recent.allSatisfy { $0.platform != "claude" }, "automatic initial attach does not import old completions")
automaticMonitor.poll(runtime:Runtime())
check(automaticMonitor.selectedPlatforms.isEmpty && automaticMonitor.current.isEmpty, "closing clients removes automatic selection")
check(automaticMonitor.settings.enabled == manualChoices, "auto detection preserves manual choices")
automaticMonitor.settings.automaticAgents = false; automaticMonitor.poll(runtime:Runtime())
check(automaticMonitor.selectedPlatforms == Set(manualChoices.keys), "manual choices restored")
check(configured.configured(name:"Auto",agents:[:],automatic:true) != nil, "auto accepts no currently running clients")
check(!legacy.automaticAgents && Settings().automaticAgents, "legacy manual preference retained while new users default automatic")
let burstState = root.appendingPathComponent("burst-state"), burst = engine(root.appendingPathComponent("burst-state"))
burst.poll(runtime:live)
try append(event("task_started", turn:"burst-a")); try append(event("task_complete", turn:"burst-a")); try append(event("task_started", turn:"burst-b"))
burst.poll(runtime:live)
check(burst.document.history.contains { $0.turn == "burst-a" } && burst.current.contains { $0.turn == "burst-b" && $0.state == "运行中" }, "completion between checks retained before next round")
let burstRestart = engine(burstState); burstRestart.poll(runtime:live)
check(burstRestart.document.history.contains { $0.turn == "burst-a" }, "intermediate completion persists across restart")
let beforeInterrupted = burst.document.history.count
try append(event("turn_aborted",turn:"burst-b")); try append(event("task_started",turn:"burst-c")); try append(event("task_failed",turn:"burst-c"))
burst.poll(runtime:live)
check(burst.document.history.count == beforeInterrupted, "intermediate interruption and failure never enter success history")
print("PASS: \(checks) checks")
