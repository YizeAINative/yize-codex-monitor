import Cocoa
import ServiceManagement
import Darwin

let args = CommandLine.arguments
func argument(_ key: String, _ fallback: String) -> String { guard let i = args.firstIndex(of: key), i + 1 < args.count else { return fallback }; return args[i + 1] }
let dataPath = argument("--data-dir", NSHomeDirectory() + "/Library/Application Support/CodexFloatMac")
let codexPath = argument("--home", ProcessInfo.processInfo.environment["CODEX_HOME"] ?? NSHomeDirectory() + "/.codex")
final class TaskStack: NSStackView { override var isFlipped: Bool { true } }
final class App: NSObject, NSApplicationDelegate, NSWindowDelegate {
    let monitor = Monitor(home: URL(fileURLWithPath: codexPath), data: URL(fileURLWithPath: dataPath))
    let queue = DispatchQueue(label: "local.codex.monitor", qos: .utility)
    var window: NSWindow!, stack: NSStackView!, tabs: NSSegmentedControl!, footer: NSTextField!, scroll: NSScrollView!, status: NSStatusItem!
    var toastTarget: URL?
    var timer: Timer?, clockTimer: Timer?, toastTimer: Timer?, toast: NSPanel?
    var recent = false, polling = false, rows = [TaskRow](), current = [TaskRow](), history = [TaskRow](), settings = Settings()
    var health = "正在连接…", signature = "", labels = [(NSTextField, TaskRow)](), menu: NSMenu!, lockFD: Int32 = -1
    func applicationDidFinishLaunching(_ notification: Notification) {
        try? FileManager.default.createDirectory(atPath: dataPath, withIntermediateDirectories: true)
        lockFD = Darwin.open(dataPath + "/instance.lock", O_CREAT | O_RDWR, S_IRUSR | S_IWUSR)
        if lockFD >= 0 && flock(lockFD, LOCK_EX | LOCK_NB) != 0 { DistributedNotificationCenter.default().postNotificationName(Notification.Name("local.yize.codexmonitor.show"), object: dataPath); NSApp.terminate(nil); return }
        DistributedNotificationCenter.default().addObserver(self, selector: #selector(show), name: Notification.Name("local.yize.codexmonitor.show"), object: dataPath)
        settings = monitor.settings
        // Preserve the user's earlier explicit sound preference, including the default silence.
        if monitor.store.load("settings.json", as: Settings.self) == nil { settings.sound = UserDefaults.standard.bool(forKey: "completionSoundEnabled"); monitor.settings = settings }
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 500, height: 590), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "一泽的 AI Agent 任务监控"; window.delegate = self; window.isReleasedWhenClosed = false; window.minSize = NSSize(width: 400, height: 280)
        window.appearance = NSAppearance(named: .aqua); window.backgroundColor = NSColor(calibratedRed: 0.966, green: 0.975, blue: 0.953, alpha: 1)
        window.level = settings.pinned ? .floating : .normal; window.setFrameAutosaveName("MonitorWindowV2"); if !window.setFrameUsingName("MonitorWindowV2") { window.center() }
        let root = window.contentView!
        tabs = NSSegmentedControl(labels: ["当前任务", "最近完成"], trackingMode: .selectOne, target: self, action: #selector(changeTab)); tabs.selectedSegment = 0; tabs.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(tabs)
        scroll = NSScrollView(); scroll.translatesAutoresizingMaskIntoConstraints = false; scroll.hasVerticalScroller = true; scroll.drawsBackground = false
        stack = TaskStack(); stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 5; stack.edgeInsets = NSEdgeInsets(top: 10, left: 12, bottom: 12, right: 12); stack.translatesAutoresizingMaskIntoConstraints = false; scroll.documentView = stack; root.addSubview(scroll)
        footer = label("正在检查本地任务…", size: 10, color: .secondaryLabelColor); footer.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(footer)
        let clear = NSButton(title: "清除完成", target: self, action: #selector(clearCompleted)); clear.bezelStyle = .inline; clear.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(clear)
        NSLayoutConstraint.activate([tabs.topAnchor.constraint(equalTo: root.topAnchor, constant: 12), tabs.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 12), tabs.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -12), tabs.heightAnchor.constraint(equalToConstant: 30), scroll.topAnchor.constraint(equalTo: tabs.bottomAnchor, constant: 10), scroll.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 8), scroll.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -8), scroll.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -38), stack.widthAnchor.constraint(equalTo: scroll.contentView.widthAnchor), footer.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 16), footer.trailingAnchor.constraint(lessThanOrEqualTo: clear.leadingAnchor, constant: -8), footer.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -12), clear.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -14), clear.centerYAnchor.constraint(equalTo: footer.centerYAnchor)])
        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength); status.button?.title = "◉ AI"; buildMenu()
        current = monitor.current; history = monitor.recent; render(); poll()
        timer = Timer.scheduledTimer(withTimeInterval: 0.8, repeats: true) { [weak self] _ in self?.poll() }
        clockTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in guard let self else { return }; for (field, row) in self.labels { field.stringValue = elapsed(row) } }
        show()
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool { sender.orderOut(nil); return false }
    @objc func show() { window?.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true) }
    @objc func quit() { timer?.invalidate(); queue.sync { monitor.persist() }; NSApp.terminate(nil) }
    func buildMenu() {
        menu = NSMenu()
        func add(_ title: String, _ action: Selector, _ check: Bool? = nil, tag: Int = 0) { let i = menu.addItem(withTitle: title, action: action, keyEquivalent: ""); i.target = self; i.tag = tag; if let check { i.state = check ? .on : .off } }
        add("显示监控", #selector(show)); menu.addItem(.separator())
        for (i, p) in ["Codex", "WorkBuddy", "Claude 桌面端 Code"].enumerated() { add(p, #selector(togglePlatform(_:)), settings.enabled[["codex", "workbuddy", "claude"][i]] == true, tag: i) }
        menu.addItem(.separator()); add("始终置顶", #selector(togglePin), settings.pinned); add("完成提示音", #selector(toggleSound), settings.sound); add("完成时弹出提醒", #selector(toggleNotifications), settings.notifications)
        add("测试完成提醒", #selector(testNotification)); add("重置窗口位置", #selector(resetPosition)); add("登录时自动启动", #selector(toggleStartup), SMAppService.mainApp.status == .enabled)
        menu.addItem(.separator()); add("诊断与使用说明", #selector(about)); add("退出", #selector(quit)); status.menu = menu
    }
    func saveSettings() { let value = settings; queue.async { self.monitor.settings = value; self.monitor.persist() }; signature = ""; buildMenu(); render() }
    @objc func togglePin() { settings.pinned.toggle(); window.level = settings.pinned ? .floating : .normal; saveSettings() }
    @objc func toggleSound() { settings.sound.toggle(); saveSettings() }
    @objc func toggleNotifications() { settings.notifications.toggle(); saveSettings() }
    @objc func togglePlatform(_ sender: NSMenuItem) { let p = ["codex", "workbuddy", "claude"][sender.tag]; let enabled = settings.enabled[p] != true; settings.enabled[p] = enabled; queue.async { self.monitor.configure(p, enabled: enabled) }; saveSettings(); poll() }
    @objc func toggleStartup() { do { if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() } else { try SMAppService.mainApp.register() }; buildMenu() } catch { let alert = NSAlert(); alert.messageText = "自动启动设置未完成"; alert.informativeText = error.localizedDescription + "\n可在系统设置 → 通用 → 登录项中检查。"; alert.runModal() } }
    @objc func resetPosition() { window.setContentSize(NSSize(width: 500, height: 590)); window.center() }
    @objc func about() { let alert = NSAlert(); alert.messageText = "AI Agent 任务监控 · Mac"; alert.informativeText = "监控 Codex / WorkBuddy / Claude 桌面端 Code。只读本地数据，不调用模型。\n绿色表示本轮结束，不代表整个业务目标完成。\n当前列表与最近 200 条完成记录独立保存。右键完成项可单独加入完成，项目标题可折叠。\n\n" + health + "\n\n状态目录：" + dataPath; alert.runModal() }
    @objc func testNotification() { var row = TaskRow(); row.title = "提醒已开启"; row.project = "任务监控"; notify([row]) }
    func poll() { guard !polling else { return }; polling = true
        queue.async {
            self.monitor.poll(); let current = self.monitor.current, history = self.monitor.recent, notices = self.monitor.notices, health = self.monitor.health.sorted { $0.key < $1.key }.map { $0.value }.joined(separator: " · ") + (self.monitor.store.error.isEmpty ? "" : " · " + self.monitor.store.error)
            DispatchQueue.main.async { self.current = current; self.history = history; self.health = health; self.polling = false; self.render(); if !notices.isEmpty { self.notify(notices.filter { row in current.contains { $0.round == row.round && $0.state == "完成" } }) } }
        }
    }
    @objc func changeTab() { recent = tabs.selectedSegment == 1; signature = ""; render(); scroll.contentView.scroll(to: .zero); scroll.reflectScrolledClipView(scroll.contentView) }
    @objc func clearCompleted() { queue.async { self.monitor.clear(); DispatchQueue.main.async { self.poll() } } }
    @objc func archive(_ sender: NSMenuItem) { guard let row = sender.representedObject as? TaskRow else { return }; queue.async { self.monitor.dismiss(row); DispatchQueue.main.async { self.poll() } } }
    @objc func openThread(_ sender: NSButton) { guard rows.indices.contains(sender.tag), let url = rows[sender.tag].link else { return }; NSWorkspace.shared.open(url) }
    @objc func collapse(_ sender: NSButton) { let key = sender.identifier!.rawValue; if settings.collapsed.contains(key) { settings.collapsed.remove(key) } else { settings.collapsed.insert(key) }; saveSettings() }
    func label(_ text: String, size: CGFloat, color: NSColor = .labelColor, bold: Bool = false) -> NSTextField { let v = NSTextField(labelWithString: text); v.font = .systemFont(ofSize: size, weight: bold ? .semibold : .regular); v.textColor = color; v.lineBreakMode = .byTruncatingTail; v.setContentCompressionResistancePriority(.init(1), for: .horizontal); return v }
    func render() {
        tabs.setLabel("当前任务  \(current.count)", forSegment: 0); tabs.setLabel("最近完成  \(history.count)", forSegment: 1)
        footer.stringValue = "\(current.filter { $0.state == "运行中" }.count) 个运行中 · \(current.filter { $0.state == "完成" }.count) 个完成"; footer.toolTip = health
        let shown = recent ? history : current
        let newSignature = shown.map { "\($0.key)|\($0.title)|\($0.cwd)|\($0.project)|\($0.state)|\($0.turn)|\($0.started?.timeIntervalSince1970 ?? 0)|\($0.finished?.timeIntervalSince1970 ?? 0)|\($0.estimated)|\($0.state == "待确认" ? $0.activity?.timeIntervalSince1970 ?? 0 : 0)" }.joined(separator: "\n") + String(describing: settings.collapsed.sorted()) + "\(recent)"
        guard newSignature != signature else { return }; signature = newSignature
        let origin = scroll.contentView.bounds.origin; rows = shown; labels = []
        for view in stack.arrangedSubviews { stack.removeArrangedSubview(view); view.removeFromSuperview() }
        let platforms = ["codex", "workbuddy", "claude"], names = ["Codex", "WorkBuddy", "Claude 桌面端 Code"]
        for (pi, platform) in platforms.enumerated() {
            let indexes = rows.indices.filter { rows[$0].platform == platform }; if indexes.isEmpty { continue }
            let tint = pi == 0 ? NSColor(calibratedRed: 0.92, green: 0.95, blue: 0.89, alpha: 1) : pi == 1 ? NSColor(calibratedRed: 0.90, green: 0.95, blue: 0.98, alpha: 1) : NSColor(calibratedRed: 0.99, green: 0.94, blue: 0.88, alpha: 1)
            let name = label(names[pi], size: 12, color: .secondaryLabelColor, bold: true); stack.addArrangedSubview(name)
            var groups = [String](); for i in indexes { if !groups.contains(rows[i].cwd) { groups.append(rows[i].cwd) } }
            for cwd in groups {
                let group = indexes.filter { rows[$0].cwd == cwd }, key = (recent ? "recent:" : "current:") + platform + ":" + cwd, folded = settings.collapsed.contains(key)
                let header = NSButton(title: (folded ? "▸ " : "▾ ") + "▱  " + (rows[group[0]].project.isEmpty ? "未分类对话" : rows[group[0]].project), target: self, action: #selector(collapse(_:))); header.identifier = NSUserInterfaceItemIdentifier(key); header.isBordered = false; header.cell?.lineBreakMode = .byTruncatingTail; header.setContentCompressionResistancePriority(.init(1), for: .horizontal); header.alignment = .left; header.font = .systemFont(ofSize: 13, weight: .medium); header.toolTip = cwd; stack.addArrangedSubview(header); header.heightAnchor.constraint(equalToConstant: 30).isActive = true; header.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -24).isActive = true
                if folded { continue }
                for i in group {
                    let row = rows[i], card = NSView(); card.translatesAutoresizingMaskIntoConstraints = false; card.wantsLayer = true; card.layer?.cornerRadius = 7; card.layer?.backgroundColor = tint.withAlphaComponent(row.state == "完成" ? 1 : 0.35).cgColor
                    let title = NSButton(title: row.name, target: self, action: #selector(openThread(_:))); title.tag = i; title.isBordered = false; title.alignment = .left; title.font = .systemFont(ofSize: 13); title.contentTintColor = .labelColor; title.cell?.lineBreakMode = .byTruncatingTail; title.setContentCompressionResistancePriority(.init(1), for: .horizontal); title.toolTip = row.name
                    if !recent { let context = NSMenu(); let item = context.addItem(withTitle: row.state == "完成" ? "加入完成" : "从当前列表移除", action: #selector(archive(_:)), keyEquivalent: ""); item.target = self; item.tag = i; item.representedObject = row; item.isEnabled = !["运行中", "等待输入"].contains(row.state); title.menu = context }
                    let time = label(elapsed(row), size: 10, color: .secondaryLabelColor); time.font = .monospacedDigitSystemFont(ofSize: 10, weight: .regular); labels.append((time, row))
                    let state = label(row.state == "完成" ? "✓ 完成" : row.state, size: 10, color: row.state == "完成" ? .systemGreen : .secondaryLabelColor)
                    title.translatesAutoresizingMaskIntoConstraints = false; card.addSubview(title)
                    if recent, let finished = row.finished { let f = DateFormatter(); f.dateFormat = "MM-dd HH:mm"; title.toolTip = row.name + "\n完成于 " + f.string(from: finished); let stamp = label(f.string(from: finished), size: 10, color: .secondaryLabelColor); stamp.translatesAutoresizingMaskIntoConstraints = false; card.addSubview(stamp); NSLayoutConstraint.activate([stamp.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 12), stamp.topAnchor.constraint(equalTo: title.bottomAnchor, constant: 2)]) }
                    if row.state == "运行中" { state.stringValue = ""; let spinner = NSProgressIndicator(); spinner.style = .spinning; spinner.controlSize = .small; spinner.translatesAutoresizingMaskIntoConstraints = false; card.addSubview(spinner); spinner.startAnimation(nil); NSLayoutConstraint.activate([spinner.centerYAnchor.constraint(equalTo: card.centerYAnchor), spinner.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -15), spinner.widthAnchor.constraint(equalToConstant: 16), spinner.heightAnchor.constraint(equalToConstant: 16)]) }
                    for v in [title, time, state] { v.translatesAutoresizingMaskIntoConstraints = false; card.addSubview(v) }; stack.addArrangedSubview(card)
                    NSLayoutConstraint.activate([card.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -24), card.heightAnchor.constraint(equalToConstant: recent ? 52 : 38), title.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 12), title.trailingAnchor.constraint(equalTo: time.leadingAnchor, constant: -8), title.centerYAnchor.constraint(equalTo: card.centerYAnchor, constant: recent ? -7 : 0), time.trailingAnchor.constraint(equalTo: state.leadingAnchor, constant: -8), time.widthAnchor.constraint(equalToConstant: 66), time.centerYAnchor.constraint(equalTo: card.centerYAnchor), state.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -12), state.widthAnchor.constraint(equalToConstant: 52), state.centerYAnchor.constraint(equalTo: card.centerYAnchor)])
                }
            }
        }
        if rows.isEmpty { stack.addArrangedSubview(label(recent ? "暂无完成记录" : "等待新的任务", size: 14)); stack.addArrangedSubview(label(recent ? "本轮完成后会自动保存到这里" : "已选平台开始执行后，会自动加入列表", size: 11, color: .secondaryLabelColor)) }
        stack.layoutSubtreeIfNeeded(); scroll.contentView.scroll(to: NSPoint(x: 0, y: max(0, min(origin.y, stack.bounds.height - scroll.contentView.bounds.height)))); scroll.reflectScrolledClipView(scroll.contentView)
    }
    @objc func openToast() { if let url = toastTarget { NSWorkspace.shared.open(url) } else { show() }; toast?.close() }
    func notify(_ tasks: [TaskRow]) { guard !tasks.isEmpty else { return }; if settings.sound { NSSound.beep() }; guard settings.notifications else { return }
        toastTimer?.invalidate(); toast?.close()
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 340, height: 100), styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false); panel.title = "本轮任务已完成"; panel.level = .floating; panel.isReleasedWhenClosed = false
        let title = label(tasks.count == 1 ? tasks[0].name : "\(tasks.count) 个任务已完成", size: 13, bold: true); title.frame = NSRect(x: 15, y: 48, width: 310, height: 24); panel.contentView?.addSubview(title)
        toastTarget = tasks.count == 1 ? tasks[0].link : nil
        let button = NSButton(title: "查看任务", target: self, action: #selector(openToast)); button.frame = NSRect(x: 230, y: 10, width: 96, height: 28); panel.contentView?.addSubview(button)
        if let screen = NSScreen.main?.visibleFrame { panel.setFrameOrigin(NSPoint(x: screen.maxX - 360, y: screen.maxY - 150)) }; panel.orderFrontRegardless(); toast = panel; toastTimer = Timer.scheduledTimer(withTimeInterval: 8, repeats: false) { [weak self] _ in self?.toast?.close(); self?.toast = nil }
    }
}
if args.contains("--dump") {
    let monitor = Monitor(home: URL(fileURLWithPath: codexPath), data: URL(fileURLWithPath: dataPath)); monitor.poll()
    let summary: [String: Any] = ["current": monitor.current.count, "running": monitor.current.filter { $0.state == "运行中" }.count, "history": monitor.recent.count, "bytesRead": monitor.bytesRead, "runtime": ["codex":monitor.runtime.codex,"workbuddy":monitor.runtime.workbuddy,"claude":monitor.runtime.claude], "health": monitor.health]
    if let data = try? JSONSerialization.data(withJSONObject: summary, options: [.prettyPrinted, .sortedKeys]), let text = String(data: data, encoding: .utf8) { print(text) }
} else { let application = NSApplication.shared; let delegate = App(); application.delegate = delegate; application.setActivationPolicy(.accessory); application.run() }
