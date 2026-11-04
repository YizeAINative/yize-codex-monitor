#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p work/swift-cache
swiftc -I macos/CSQLite -module-cache-path work/swift-cache macos/Model.swift macos/tests/main.swift -o work/MacCoreTests
work/MacCoreTests
