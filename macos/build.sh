#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
APP="dist/Codex Monitor.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp macos/Assets/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
mkdir -p work/swift-cache
swiftc -target "$(uname -m)-apple-macos13.0" -I macos/CSQLite -module-cache-path work/swift-cache macos/Model.swift macos/main.swift -O -o "$APP/Contents/MacOS/CodexMonitor" -framework Cocoa -framework ServiceManagement
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>CFBundleExecutable</key><string>CodexMonitor</string><key>CFBundleIdentifier</key><string>local.yize.codexmonitor.mac</string><key>CFBundleIconFile</key><string>AppIcon.icns</string><key>CFBundleVersion</key><string>1601</string><key>CFBundleName</key><string>Codex Monitor</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleShortVersionString</key><string>1.6.0</string><key>LSMinimumSystemVersion</key><string>13.0</string><key>LSUIElement</key><true/></dict></plist>
PLIST
codesign --force --sign - "$APP"
echo "Built: $APP"
