import AudioToolbox
import Darwin
import Foundation
import IOKit
import CoreAudio
import ServiceManagement

/// Reads Apple Silicon temperature events without sudo. IOHID's temperature
/// symbols are not a stable public API, so load them dynamically and return
/// explicit unavailable values if macOS stops exporting them.
private enum HIDTemperatureProbe {
    private typealias Create = @convention(c) (CFAllocator?) -> UnsafeMutableRawPointer?
    private typealias SetMatching = @convention(c) (
        UnsafeMutableRawPointer,
        CFDictionary
    ) -> Int32
    private typealias CopyServices = @convention(c) (
        UnsafeMutableRawPointer
    ) -> Unmanaged<CFArray>?
    private typealias CopyProperty = @convention(c) (
        UnsafeMutableRawPointer,
        CFString
    ) -> Unmanaged<AnyObject>?
    private typealias CopyEvent = @convention(c) (
        UnsafeMutableRawPointer,
        Int64,
        Int32,
        Int64
    ) -> UnsafeMutableRawPointer?
    private typealias GetFloatValue = @convention(c) (
        UnsafeMutableRawPointer,
        Int32
    ) -> Double

    private static let handle = dlopen(
        "/System/Library/Frameworks/IOKit.framework/IOKit",
        RTLD_LAZY
    )
    private static let create: Create? = load("IOHIDEventSystemClientCreate")
    private static let setMatching: SetMatching? = load("IOHIDEventSystemClientSetMatching")
    private static let copyServices: CopyServices? = load("IOHIDEventSystemClientCopyServices")
    private static let copyProperty: CopyProperty? = load("IOHIDServiceClientCopyProperty")
    private static let copyEvent: CopyEvent? = load("IOHIDServiceClientCopyEvent")
    private static let getFloatValue: GetFloatValue? = load("IOHIDEventGetFloatValue")
    private static let eventType: Int64 = 15
    private static let eventField = Int32(eventType << 16)

    private static let matching: CFDictionary = {
        var page: Int32 = 0xff00
        var usage: Int32 = 5
        let pageNumber = CFNumberCreate(kCFAllocatorDefault, .sInt32Type, &page)!
        let usageNumber = CFNumberCreate(kCFAllocatorDefault, .sInt32Type, &usage)!
        return [
            "PrimaryUsagePage" as CFString: pageNumber,
            "PrimaryUsage" as CFString: usageNumber,
        ] as CFDictionary
    }()

    private static func load<T>(_ name: String) -> T? {
        guard let handle, let symbol = dlsym(handle, name) else { return nil }
        return unsafeBitCast(symbol, to: T.self)
    }

    private static func release(_ pointer: UnsafeMutableRawPointer) {
        Unmanaged<CFTypeRef>.fromOpaque(pointer).release()
    }

    private static func readSensors() -> (values: [String: Double]?, failure: String?) {
        guard handle != nil else {
            return (nil, "macOS could not load the IOKit framework.")
        }
        guard let create else {
            return (nil, "macOS does not export IOHIDEventSystemClientCreate.")
        }
        guard let setMatching else {
            return (nil, "macOS does not export IOHIDEventSystemClientSetMatching.")
        }
        guard let copyServices else {
            return (nil, "macOS does not export IOHIDEventSystemClientCopyServices.")
        }
        guard let copyProperty else {
            return (nil, "macOS does not export IOHIDServiceClientCopyProperty.")
        }
        guard let copyEvent else {
            return (nil, "macOS does not export IOHIDServiceClientCopyEvent.")
        }
        guard let getFloatValue else {
            return (nil, "macOS does not export IOHIDEventGetFloatValue.")
        }
        guard let system = create(kCFAllocatorDefault) else {
            return (nil, "macOS refused to create an IOHID temperature client.")
        }
        defer { release(system) }

        _ = setMatching(system, matching)
        guard let services = copyServices(system)?.takeRetainedValue() else {
            return (nil, "macOS returned no IOHID temperature services.")
        }
        var sensors: [String: Double] = [:]
        for index in 0..<CFArrayGetCount(services) {
            guard let rawService = CFArrayGetValueAtIndex(services, index) else { continue }
            let service = UnsafeMutableRawPointer(mutating: rawService)
            guard let property = copyProperty(service, "Product" as CFString),
                  let name = property.takeRetainedValue() as? String,
                  let event = copyEvent(service, eventType, 0, 0)
            else { continue }
            let value = getFloatValue(event, eventField)
            release(event)
            guard value.isFinite, value > 0, value < 150 else { continue }
            // Duplicate Product services exist on some Macs; retain the hottest
            // reading without letting duplicates weight the aggregate.
            sensors[name] = max(sensors[name] ?? value, value)
        }
        return (sensors, nil)
    }

    private static func metric(
        value: Double?,
        source: String,
        unavailableReason: String
    ) -> [String: Any] {
        [
            "value": value.map { $0 as Any } ?? NSNull(),
            "source": source,
            "reason": value == nil ? unavailableReason : NSNull(),
        ]
    }

    /// IOHID names a GPU sensor only on some Macs, so read AppleSMC whenever the
    /// HID pass found nothing rather than reporting an unavailable GPU.
    private static func gpuMetric(hidValue: Double?) -> [String: Any] {
        if let hidValue {
            return metric(
                value: hidValue,
                source: "macOS IOHID GPU sensors",
                unavailableReason: ""
            )
        }

        let smc = SMCTemperatureProbe.gpuTemperature()
        return metric(
            value: smc.value,
            source: "macOS AppleSMC GPU sensors",
            unavailableReason: smc.failure ??
                "macOS exposed no readable GPU temperature sensor."
        )
    }

    static func printSnapshot() {
        let probe = readSensors()
        let commonUnavailable = probe.failure ??
            "The macOS IOHID temperature interface is unavailable."
        var object: [String: Any]
        if let sensors = probe.values {
            var cpu: [Double] = []
            var soc: [Double] = []
            var gpu: [Double] = []
            var disk: [Double] = []
            for (name, value) in sensors {
                let key = name.lowercased()
                if key.hasPrefix("pacc mtr temp") ||
                    key.hasPrefix("eacc mtr temp") ||
                    (key.contains("cpu") && key.contains("temp")) {
                    cpu.append(value)
                } else if key.hasPrefix("gpu mtr temp") ||
                            (key.contains("gpu") && key.contains("temp")) {
                    gpu.append(value)
                } else if key.hasPrefix("pmu tdie") ||
                            key.hasPrefix("pmu2 tdie") ||
                            key.hasPrefix("pmu tdev") ||
                            key.hasPrefix("pmu2 tdev") ||
                            key.hasPrefix("soc mtr temp") {
                    soc.append(value)
                }
                if (key.contains("nand") || key.contains("ssd")) && key.contains("temp") {
                    disk.append(value)
                }
            }

            let explicitCpu = cpu.max()
            let cpuValue = explicitCpu ?? soc.max()
            object = [
                "cpu": metric(
                    value: cpuValue,
                    source: explicitCpu == nil
                        ? "macOS IOHID SoC die sensors"
                        : "macOS IOHID CPU sensors",
                    unavailableReason: "macOS exposed no readable CPU or SoC die temperature sensor."
                ),
                "gpu": gpuMetric(hidValue: gpu.max()),
                "disk": metric(
                    value: disk.max(),
                    source: "macOS IOHID NAND sensors",
                    unavailableReason: "macOS exposed no readable internal NAND temperature sensor."
                ),
            ]
        } else {
            object = [
                "cpu": metric(value: nil, source: "macOS IOHID", unavailableReason: commonUnavailable),
                "gpu": gpuMetric(hidValue: nil),
                "disk": metric(value: nil, source: "macOS IOHID", unavailableReason: commonUnavailable),
            ]
        }

        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]),
              let text = String(data: data, encoding: .utf8)
        else {
            print("{\"cpu\":null,\"gpu\":null,\"disk\":null}")
            fflush(stdout)
            return
        }
        print(text)
        fflush(stdout)
    }
}

/// Reads GPU temperature from AppleSMC. macOS has no public GPU temperature
/// API, and IOHID only names GPU sensors on some Macs, so fall back to the SMC
/// keys that ship on Apple Silicon ("Tg..") and Intel ("TG..") hardware. Every
/// call here is read-only and needs no administrator rights.
private enum SMCTemperatureProbe {
    private static let readBytes: UInt8 = 5
    private static let readIndex: UInt8 = 8
    private static let readKeyInfo: UInt8 = 9
    private static let gpuKeyPrefixes = ["TG", "Tg"]

    private struct KeyData {
        typealias Bytes = (
            UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8,
            UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8,
            UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8,
            UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8
        )

        struct Version {
            var major: UInt8 = 0
            var minor: UInt8 = 0
            var build: UInt8 = 0
            var reserved: UInt8 = 0
            var release: UInt16 = 0
        }

        struct LimitData {
            var version: UInt16 = 0
            var length: UInt16 = 0
            var cpuPLimit: UInt32 = 0
            var gpuPLimit: UInt32 = 0
            var memPLimit: UInt32 = 0
        }

        struct KeyInfo {
            var dataSize: UInt32 = 0
            var dataType: UInt32 = 0
            var dataAttributes: UInt8 = 0
        }

        var key: UInt32 = 0
        var version = Version()
        var limitData = LimitData()
        var keyInfo = KeyInfo()
        var padding: UInt16 = 0
        var result: UInt8 = 0
        var status: UInt8 = 0
        var data8: UInt8 = 0
        var data32: UInt32 = 0
        var bytes: Bytes = (
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0
        )
    }

    static func gpuTemperature() -> (value: Double?, failure: String?) {
        let service = IOServiceGetMatchingService(
            kIOMainPortDefault,
            IOServiceMatching("AppleSMC")
        )
        guard service != IO_OBJECT_NULL else {
            return (nil, "macOS exposed no AppleSMC service.")
        }
        defer { IOObjectRelease(service) }

        var connection: io_connect_t = 0
        guard IOServiceOpen(service, mach_task_self_, 0, &connection) == kIOReturnSuccess else {
            return (nil, "macOS refused a read-only AppleSMC connection.")
        }
        defer { IOServiceClose(connection) }

        guard let count = keyCount(connection) else {
            return (nil, "AppleSMC did not report how many sensor keys it has.")
        }

        var hottest: Double?
        for prefix in gpuKeyPrefixes {
            for key in gpuKeys(connection, count: count, prefix: prefix) {
                guard let value = temperature(connection, key: key) else { continue }
                hottest = max(hottest ?? value, value)
            }
        }
        return hottest == nil
            ? (nil, "AppleSMC exposed no readable GPU temperature key.")
            : (hottest, nil)
    }

    private static func keyCount(_ connection: io_connect_t) -> UInt32? {
        guard let value = read(connection, key: "#KEY"), value.bytes.count >= 4 else {
            return nil
        }
        let bytes = value.bytes
        let count = UInt32(bytes[0]) << 24 | UInt32(bytes[1]) << 16 |
            UInt32(bytes[2]) << 8 | UInt32(bytes[3])
        return count == 0 ? nil : count
    }

    /// AppleSMC returns its key table in ascending key order, so binary search
    /// the first key in a prefix instead of walking all ~2800 keys.
    private static func gpuKeys(
        _ connection: io_connect_t,
        count: UInt32,
        prefix: String
    ) -> [String] {
        var low: UInt32 = 0
        var high = count
        while low < high {
            let middle = low + (high - low) / 2
            guard let key = keyName(connection, at: middle) else { return [] }
            if key < prefix {
                low = middle + 1
            } else {
                high = middle
            }
        }

        var matches: [String] = []
        var index = low
        while index < count, let key = keyName(connection, at: index), key.hasPrefix(prefix) {
            matches.append(key)
            index += 1
        }
        return matches
    }

    private static func keyName(_ connection: io_connect_t, at index: UInt32) -> String? {
        var input = KeyData()
        var output = KeyData()
        input.data8 = readIndex
        input.data32 = index
        guard call(connection, input: &input, output: &output), output.result == 0 else {
            return nil
        }
        return fourCharacterCode(output.key)
    }

    private static func temperature(_ connection: io_connect_t, key: String) -> Double? {
        guard let value = read(connection, key: key) else { return nil }
        let celsius: Double
        switch value.type {
        case "flt ":
            guard value.bytes.count >= 4 else { return nil }
            var raw: Float = 0
            withUnsafeMutableBytes(of: &raw) { destination in
                destination.copyBytes(from: value.bytes.prefix(4))
            }
            celsius = Double(raw)
        case "sp78":
            guard value.bytes.count >= 2 else { return nil }
            let raw = Int16(bitPattern: UInt16(value.bytes[0]) << 8 | UInt16(value.bytes[1]))
            celsius = Double(raw) / 256.0
        default:
            return nil
        }
        return celsius.isFinite && celsius > 0 && celsius < 150 ? celsius : nil
    }

    private static func read(
        _ connection: io_connect_t,
        key: String
    ) -> (type: String, bytes: [UInt8])? {
        guard key.utf8.count == 4 else { return nil }
        var input = KeyData()
        var output = KeyData()
        input.key = key.utf8.reduce(0) { ($0 << 8) | UInt32($1) }
        input.data8 = readKeyInfo
        guard call(connection, input: &input, output: &output), output.result == 0,
              output.keyInfo.dataSize > 0, output.keyInfo.dataSize <= 32
        else { return nil }

        let size = output.keyInfo.dataSize
        let type = output.keyInfo.dataType
        input.keyInfo.dataSize = size
        input.data8 = readBytes
        output = KeyData()
        guard call(connection, input: &input, output: &output), output.result == 0 else {
            return nil
        }
        let bytes = withUnsafeBytes(of: output.bytes) { Array($0.prefix(Int(size))) }
        return (fourCharacterCode(type), bytes)
    }

    private static func call(
        _ connection: io_connect_t,
        input: inout KeyData,
        output: inout KeyData
    ) -> Bool {
        var outputSize = MemoryLayout<KeyData>.size
        let result = withUnsafePointer(to: &input) { inputPointer in
            withUnsafeMutablePointer(to: &output) { outputPointer in
                IOConnectCallStructMethod(
                    connection,
                    2,
                    inputPointer,
                    MemoryLayout<KeyData>.size,
                    outputPointer,
                    &outputSize
                )
            }
        }
        return result == kIOReturnSuccess && outputSize == MemoryLayout<KeyData>.size
    }

    private static func fourCharacterCode(_ value: UInt32) -> String {
        let scalars = [24, 16, 8, 0].compactMap {
            UnicodeScalar(UInt8(truncatingIfNeeded: value >> UInt32($0)))
        }
        return String(String.UnicodeScalarView(scalars))
    }
}

/// A private, unmuted system mix. No display, window or screen permission is
/// involved. The audio callback only reduces samples; JSON I/O stays off it.
@available(macOS 14.2, *)
private final class SystemAudioTap {
    private var tap = AudioObjectID(kAudioObjectUnknown)
    private var device = AudioObjectID(kAudioObjectUnknown)
    private var io: AudioDeviceIOProcID?
    private let gate = NSLock()
    private var peak = 0.0
    private var squares = 0.0
    private var count = 0
    private var lastCallback = DispatchTime.now().uptimeNanoseconds

    private func check(_ status: OSStatus, _ operation: String) throws {
        guard status == noErr else {
            // Core Audio's !hog permission error; do not relaunch into a
            // permission prompt loop. Other HAL failures are recoverable.
            throw TapError(operation: operation, status: status)
        }
    }

    func start() throws {
        do {
            let description = CATapDescription(stereoGlobalTapButExcludeProcesses: [])
            description.name = "Pico Vibe system audio"
            description.uuid = UUID()
            description.isPrivate = true
            description.muteBehavior = .unmuted
            try check(AudioHardwareCreateProcessTap(description, &tap), "Create audio tap")
            var address = AudioObjectPropertyAddress(
                mSelector: kAudioTapPropertyFormat,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain)
            var format = AudioStreamBasicDescription()
            var size = UInt32(MemoryLayout.size(ofValue: format))
            try check(AudioObjectGetPropertyData(tap, &address, 0, nil, &size, &format),
                      "Read audio tap format")
            guard format.mFormatID == kAudioFormatLinearPCM,
                  format.mFormatFlags & kAudioFormatFlagIsFloat != 0,
                  format.mFormatFlags & kAudioFormatFlagIsBigEndian == 0,
                  format.mBitsPerChannel == 32 else {
                throw TapError(operation: "Unsupported audio tap PCM format", status: -50)
            }
            let properties: [String: Any] = [
                kAudioAggregateDeviceNameKey: "Pico Vibe audio capture",
                kAudioAggregateDeviceUIDKey: "com.picovibe.tap." + UUID().uuidString,
                kAudioAggregateDeviceIsPrivateKey: true,
                kAudioAggregateDeviceIsStackedKey: false,
                kAudioAggregateDeviceTapAutoStartKey: true,
                kAudioAggregateDeviceTapListKey: [[
                    kAudioSubTapUIDKey: description.uuid.uuidString,
                    kAudioSubTapDriftCompensationKey: true
                ]]
            ]
            try check(AudioHardwareCreateAggregateDevice(properties as CFDictionary, &device),
                      "Create audio capture device")
            try check(AudioDeviceCreateIOProcIDWithBlock(&io, device, nil) {
                [weak self] _, input, _, _, _ in self?.consume(input)
            }, "Create audio callback")
            try check(AudioDeviceStart(device, io), "Start system audio capture")
        } catch {
            stop()
            throw error
        }
    }

    private func consume(_ input: UnsafePointer<AudioBufferList>) {
        var blockPeak = 0.0
        var blockSquares = 0.0
        var blockCount = 0
        for buffer in UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input)) {
            guard let data = buffer.mData else { continue }
            let n = Int(buffer.mDataByteSize) / MemoryLayout<Float>.size
            let samples = data.assumingMemoryBound(to: Float.self)
            for index in 0..<n {
                let raw = Double(samples[index])
                guard raw.isFinite else { continue }
                let value = min(1.0, max(-1.0, raw))
                blockPeak = max(blockPeak, abs(value))
                blockSquares += value * value
                blockCount += 1
            }
        }
        gate.lock()
        peak = max(peak, blockPeak)
        squares += blockSquares
        count += blockCount
        lastCallback = DispatchTime.now().uptimeNanoseconds
        gate.unlock()
    }

    func emit() throws {
        gate.lock()
        let p = peak
        let rms = count == 0 ? 0 : sqrt(squares / Double(count))
        let age = DispatchTime.now().uptimeNanoseconds - lastCallback
        peak = 0
        squares = 0
        count = 0
        gate.unlock()
        // A silent stream still calls back. Do not conceal a dead stream by
        // endlessly publishing fabricated silence.
        guard age < 5_000_000_000 else {
            throw TapError(operation: "System audio tap stopped delivering samples", status: -1)
        }
        print(String(format: "{\"peak\":%.8f,\"rms\":%.8f,\"source\":\"macOS Core Audio Tap\"}", p, rms))
        fflush(stdout)
    }

    func stop() {
        if let callback = io {
            AudioDeviceStop(device, callback)
            AudioDeviceDestroyIOProcID(device, callback)
            io = nil
        }
        if device != kAudioObjectUnknown {
            AudioHardwareDestroyAggregateDevice(device)
            device = AudioObjectID(kAudioObjectUnknown)
        }
        if tap != kAudioObjectUnknown {
            AudioHardwareDestroyProcessTap(tap)
            tap = AudioObjectID(kAudioObjectUnknown)
        }
    }

    deinit { stop() }

    /// Offline regression: no tap, aggregate device or permission is created.
    static func selfTest() throws {
        let capture = SystemAudioTap()
        var samples: [Float] = [0.5, -0.5, 1, -1, .nan, .infinity]
        samples.withUnsafeMutableBytes { bytes in
            var input = AudioBufferList(mNumberBuffers: 1, mBuffers: AudioBuffer(
                mNumberChannels: 2, mDataByteSize: UInt32(bytes.count),
                mData: bytes.baseAddress))
            withUnsafePointer(to: &input) { capture.consume($0) }
        }
        precondition(capture.count == 4)
        precondition(capture.peak == 1)
        precondition(abs(capture.squares - 2.5) < 0.000001)
        try capture.emit()
        precondition(capture.count == 0 && capture.peak == 0 && capture.squares == 0)
        capture.lastCallback = DispatchTime.now().uptimeNanoseconds - 6_000_000_000
        do {
            try capture.emit()
            preconditionFailure("A stalled tap must not publish endless silence")
        } catch is TapError {}
        capture.stop()
        capture.stop()
        print("Core Audio tap sample reduction and stall checks: PASS")
    }
}

private struct TapError: LocalizedError {
    let operation: String
    let status: OSStatus
    var permissionDenied: Bool { status == 0x21686f67 || status == 0x7065726d }
    var errorDescription: String? {
        permissionDenied
            ? "System audio capture permission denied. Allow Pico Vibe in System Settings → Privacy & Security → Screen & System Audio Recording, then reopen the app."
            : "\(operation) failed (Core Audio OSStatus \(status))."
    }
}

private enum BridgeError: LocalizedError, Equatable {
    case unsupportedMode
    case mediaCommandUnavailable
    case noActiveMediaSession
    case mediaSessionQueryTimedOut
    case mediaCommandRejected

    var errorDescription: String? {
        switch self {
        case .unsupportedMode: return "Unsupported PicoVibeMacBridge mode."
        case .mediaCommandUnavailable: return "The macOS system media command is unavailable."
        case .noActiveMediaSession: return "No active macOS Now Playing session is available."
        case .mediaSessionQueryTimedOut: return "The macOS Now Playing session query timed out."
        case .mediaCommandRejected: return "The active macOS media player rejected next track."
        }
    }
}

@main
private struct PicoVibeMacBridge {
    static func main() async {
        let mode = CommandLine.arguments.count >= 2 ? CommandLine.arguments[1] : nil
        do {
            guard let mode else { throw BridgeError.unsupportedMode }
            switch mode {
            case "audio":
                try await runAudio()
            case "audio-self-test":
                try SystemAudioTap.selfTest()
            case "next-track":
                try sendNextTrack()
            case "temperatures":
                HIDTemperatureProbe.printSnapshot()
            case "login-register":
                if #available(macOS 13.0, *) { try SMAppService.mainApp.register() }
            case "login-unregister":
                if #available(macOS 13.0, *) { try await SMAppService.mainApp.unregister() }
            case "login-status":
                if #available(macOS 13.0, *) {
                    print(SMAppService.mainApp.status.rawValue)
                }
            default:
                throw BridgeError.unsupportedMode
            }
        } catch {
            let text = error.localizedDescription
                .replacingOccurrences(of: "\\", with: "\\\\")
                .replacingOccurrences(of: "\"", with: "\\\"")
                .replacingOccurrences(of: "\n", with: " ")
            // HAL failures can clear after sleep or an output-device change.
            // Only explicit permission rejection suppresses audio retries.
            let terminal = mode == "audio" ? ((error as? TapError)?.permissionDenied ?? true)
                : true
            print("{\"peak\":0,\"rms\":0,\"error\":\"\(text)\",\"terminal\":\(terminal)}")
            fflush(stdout)
            exit(exitCode(for: error, mode: mode))
        }
    }

    private static func exitCode(for error: Error, mode: String?) -> Int32 {
        guard mode == "next-track", let bridgeError = error as? BridgeError else {
            return 1
        }
        switch bridgeError {
        case .noActiveMediaSession: return 2
        case .mediaCommandUnavailable: return 3
        case .mediaCommandRejected: return 4
        default: return 1
        }
    }

    private static func sendNextTrack() throws {
        let helperDirectory = URL(fileURLWithPath: CommandLine.arguments[0])
            .deletingLastPathComponent()
        let script = helperDirectory
            .deletingLastPathComponent()
            .appendingPathComponent("Resources/PicoVibeMediaRemote.pl")
        let shim = helperDirectory.appendingPathComponent("PicoVibeMediaRemote.dylib")
        guard FileManager.default.fileExists(atPath: script.path),
              FileManager.default.fileExists(atPath: shim.path)
        else {
            throw BridgeError.mediaCommandUnavailable
        }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/perl")
        process.arguments = [script.path, shim.path]
        try process.run()
        process.waitUntilExit()
        switch process.terminationStatus {
        case 0: return
        case 2: throw BridgeError.noActiveMediaSession
        case 3: throw BridgeError.mediaCommandUnavailable
        case 4: throw BridgeError.mediaCommandRejected
        default: throw BridgeError.mediaSessionQueryTimedOut
        }
    }

    @available(macOS 14.2, *)
    private static func runAudio() async throws {
        let tap = SystemAudioTap()
        try tap.start()
        defer { tap.stop() }
        while !Task.isCancelled {
            try await Task.sleep(nanoseconds: 40_000_000)
            try tap.emit()
        }
    }
}
