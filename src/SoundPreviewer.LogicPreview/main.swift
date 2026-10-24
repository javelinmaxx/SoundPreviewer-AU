// A process-isolated macOS AU preview host. It renders only the graph that the
// Python planner has accepted; it never substitutes a missing instrument/FX.
import Foundation
import AVFoundation
import AudioToolbox
import CryptoKit

struct PreviewError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
    init(_ message: String) { self.message = message }
}

struct Component: Codable {
    let type: String
    let subtype: String
    let manufacturer: String

    func description() throws -> AudioComponentDescription {
        func code(_ text: String) throws -> UInt32 {
            let bytes = Array(text.utf8)
            guard bytes.count == 4, bytes.allSatisfy({ $0 >= 32 && $0 < 127 }) else {
                throw PreviewError("AU identifiers must be four printable ASCII bytes, including spaces.")
            }
            return bytes.reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
        }
        return try AudioComponentDescription(componentType: code(type), componentSubType: code(subtype),
            componentManufacturer: code(manufacturer), componentFlags: 0, componentFlagsMask: 0)
    }
}

struct Plugin: Codable {
    let component: Component
    let stateFile: String
    let stateSHA256: String
}

struct Request: Codable {
    let schemaVersion: Int
    let instrument: Plugin
    let effects: [Plugin]
    let notes: [Int]
    let velocity: Int
    let sampleRate: Int
    let holdSeconds: Double
    let tailSeconds: Double
    let bpm: Double

    func validate() throws {
        guard schemaVersion == 1, instrument.component.type == "aumu", effects.count <= 8,
              effects.allSatisfy({ $0.component.type == "aufx" }),
              (1...16).contains(notes.count), notes.allSatisfy({ (0...127).contains($0) }),
              (1...127).contains(velocity), [44100, 48000].contains(sampleRate),
              holdSeconds.isFinite, (0.05...5).contains(holdSeconds),
              tailSeconds.isFinite, (0...5).contains(tailSeconds),
              bpm.isFinite, (20...300).contains(bpm),
              (holdSeconds + tailSeconds) * Double(notes.count) <= 60 else {
            throw PreviewError("Unsupported or out-of-range preview request.")
        }
    }
}

struct NoteResult: Codable {
    let pitch: Int
    let startFrame: Int
    let frames: Int
    let rms: Double
}

struct Result: Encodable {
    let schemaVersion = 1
    let success: Bool
    let sampleRate: Int
    let channels = 2
    let frames: Int
    let peak: Double
    let rms: Double
    let notes: [NoteResult]
}

func check(_ status: OSStatus, _ action: String) throws {
    guard status == noErr else { throw PreviewError("\(action) failed (OSStatus \(status)).") }
}

func sha256(_ data: Data) -> String {
    SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
}

func instantiate(_ component: Component) throws -> AVAudioUnit {
    var description = try component.description()
    guard AudioComponentFindNext(nil, &description) != nil else {
        throw PreviewError("Missing registered AU: \(component.type)/\(component.subtype)/\(component.manufacturer)")
    }
    // Completion may arrive on the main run loop. Don't block it on a semaphore.
    // A lock also covers components that complete on another thread.
    let lock = NSLock()
    var completed = false
    var instance: AVAudioUnit?
    var failure: Error?
    AVAudioUnit.instantiate(with: description, options: []) { unit, error in
        lock.lock()
        instance = unit
        failure = error
        completed = true
        lock.unlock()
    }
    let deadline = Date().addingTimeInterval(30)
    while true {
        lock.lock()
        let done = completed
        lock.unlock()
        if done { break }
        guard Date() < deadline else { throw PreviewError("Timed out instantiating an Audio Unit.") }
        RunLoop.current.run(until: Date().addingTimeInterval(0.01))
    }
    if let failure = failure { throw failure }
    guard let unit = instance else { throw PreviewError("AU instantiation returned no instance.") }
    let actual = unit.audioComponentDescription
    guard actual.componentType == description.componentType,
          actual.componentSubType == description.componentSubType,
          actual.componentManufacturer == description.componentManufacturer else {
        throw PreviewError("The instantiated AU does not match the requested component.")
    }
    return unit
}

func restore(_ plugin: Plugin, directory: URL) throws -> AVAudioUnit {
    guard !plugin.stateFile.isEmpty, !plugin.stateFile.contains("/"), !plugin.stateFile.contains("\\"),
          plugin.stateFile != ".", plugin.stateFile != ".." else {
        throw PreviewError("Preset state must be a direct staging-directory member.")
    }
    let url = directory.appendingPathComponent(plugin.stateFile)
    let properties = try url.resourceValues(forKeys: [.isSymbolicLinkKey, .isRegularFileKey, .fileSizeKey])
    guard properties.isSymbolicLink != true, properties.isRegularFile == true,
          (properties.fileSize ?? Int.max) <= 8 * 1024 * 1024 else {
        throw PreviewError("Preset state is linked, non-regular, or exceeds 8 MiB.")
    }
    let data = try Data(contentsOf: url)
    guard sha256(data) == plugin.stateSHA256,
          let state = try PropertyListSerialization.propertyList(from: data, options: [], format: nil) as? [String: Any] else {
        throw PreviewError("Preset state checksum/format does not match the request.")
    }
    let description = try plugin.component.description()
    for (key, expected) in [("type", description.componentType), ("subtype", description.componentSubType),
                            ("manufacturer", description.componentManufacturer)] {
        guard (state[key] as? NSNumber)?.uint32Value == expected else {
            throw PreviewError("Preset \(key) does not match its requested component.")
        }
    }
    let unit = try instantiate(plugin.component)
    // Use the AUv2 bridge's checked status, rather than a fullState setter that
    // cannot report state-restoration failure to the caller.
    var dictionary = state as CFDictionary
    try withUnsafePointer(to: &dictionary) { pointer in
        try check(AudioUnitSetProperty(unit.audioUnit, kAudioUnitProperty_ClassInfo,
            kAudioUnitScope_Global, 0, pointer, UInt32(MemoryLayout<CFDictionary>.size)), "Restore AU preset")
    }
    return unit
}

extension Data {
    mutating func littleEndian<T: FixedWidthInteger>(_ value: T) {
        var number = value.littleEndian
        Swift.withUnsafeBytes(of: &number) { append(contentsOf: $0) }
    }
}

func render(_ request: Request, directory: URL) throws -> Result {
    try request.validate()
    let output = directory.appendingPathComponent("preview.wav")
    guard !FileManager.default.fileExists(atPath: output.path) else { throw PreviewError("Refusing to overwrite preview.wav.") }
    let engine = AVAudioEngine()
    let instrument = try restore(request.instrument, directory: directory)
    let effects = try request.effects.map { try restore($0, directory: directory) }
    let units = [instrument] + effects
    guard let format = AVAudioFormat(standardFormatWithSampleRate: Double(request.sampleRate), channels: 2) else {
        throw PreviewError("Cannot create stereo rendering format.")
    }
    for (index, unit) in units.enumerated() {
        let inputs = unit.auAudioUnit.inputBusses.count
        let outputs = unit.auAudioUnit.outputBusses.count
        // AUv2 music devices, including Apple DLS, may expose one nominal input
        // through the AUAudioUnit bridge. Leave it unconnected: this graph has
        // MIDI input only, with no audio input or side-chain route to discard.
        guard outputs == 1, (index == 0 ? inputs <= 1 : inputs == 1) else {
            throw PreviewError("Unsupported AU buses at chain index \(index): \(inputs) input(s), \(outputs) output(s).")
        }
        engine.attach(unit)
        unit.auAudioUnit.isRenderingOffline = true
        // AU callbacks run synchronously during manual rendering. The engine's
        // sample position supplies a stable 4/4 musical context to tempo FX.
        unit.auAudioUnit.musicalContextBlock = { [weak engine] tempo, numerator, denominator, beat, offset, downbeat in
            let position = Double(engine?.manualRenderingSampleTime ?? 0)
            let currentBeat = position / Double(request.sampleRate) * request.bpm / 60
            tempo?.pointee = request.bpm
            numerator?.pointee = 4
            denominator?.pointee = 4
            beat?.pointee = currentBeat
            offset?.pointee = Int((ceil(currentBeat) - currentBeat) * 60 / request.bpm * Double(request.sampleRate))
            downbeat?.pointee = floor(currentBeat / 4) * 4
            return true
        }
    }
    for index in 0..<units.count - 1 { engine.connect(units[index], to: units[index + 1], format: format) }
    engine.connect(units.last!, to: engine.mainMixerNode, format: format)
    try engine.enableManualRenderingMode(.offline, format: format, maximumFrameCount: 4096)
    try engine.start()
    defer { engine.stop() }
    guard let buffer = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: 4096) else {
        throw PreviewError("Cannot allocate render buffer.")
    }
    let hold = Int((request.holdSeconds * Double(request.sampleRate)).rounded())
    let segment = hold + Int((request.tailSeconds * Double(request.sampleRate)).rounded())
    let total = segment * request.notes.count
    var header = Data("RIFF".utf8)
    header.littleEndian(UInt32(36 + total * 4))
    header.append(contentsOf: "WAVEfmt ".utf8)
    header.littleEndian(UInt32(16)); header.littleEndian(UInt16(1)); header.littleEndian(UInt16(2))
    header.littleEndian(UInt32(request.sampleRate)); header.littleEndian(UInt32(request.sampleRate * 4))
    header.littleEndian(UInt16(4)); header.littleEndian(UInt16(16))
    header.append(contentsOf: "data".utf8); header.littleEndian(UInt32(total * 4))
    guard FileManager.default.createFile(atPath: output.path, contents: header) else { throw PreviewError("Cannot create staged WAV.") }
    let file = try FileHandle(forWritingTo: output)
    defer { try? file.close() }
    try file.seek(toOffset: UInt64(header.count))
    var frame = 0, retries = 0
    var peak = 0.0
    var energies = Array(repeating: 0.0, count: request.notes.count)
    let deadline = Date().addingTimeInterval(120)
    while frame < total {
        guard Date() < deadline else { throw PreviewError("Offline render timed out.") }
        let noteIndex = frame / segment
        let local = frame % segment
        if local == 0 {
            try check(MusicDeviceMIDIEvent(instrument.audioUnit, 0x90, UInt32(request.notes[noteIndex]), UInt32(request.velocity), 0), "Note on")
        }
        let boundary = local < hold ? (noteIndex * segment + hold) : ((noteIndex + 1) * segment)
        let count = min(4096, boundary - frame)
        // Split buffers at MIDI boundaries. Events reach the instrument before
        // its next render call, without relying on real-time wall-clock sleeps.
        while true {
            let status = try engine.renderOffline(AVAudioFrameCount(count), to: buffer)
            if status == .success { retries = 0; break }
            retries += 1
            guard status == .cannotDoInCurrentContext, retries < 256, Date() < deadline else {
                throw PreviewError("AU cannot complete offline rendering (status \(status)).")
            }
            RunLoop.current.run(until: Date().addingTimeInterval(0.001))
        }
        guard Int(buffer.frameLength) == count, let samples = buffer.floatChannelData else {
            throw PreviewError("Offline engine returned incomplete PCM frames.")
        }
        var encoded = Data(capacity: count * 4)
        for index in 0..<count {
            for channel in 0..<2 {
                let value = Double(samples[channel][index])
                guard value.isFinite, abs(value) <= 1 else { throw PreviewError("AU output is nonfinite or clips; no preview will be published.") }
                peak = max(peak, abs(value))
                energies[noteIndex] += value * value
                encoded.littleEndian(Int16((value * 32767).rounded()))
            }
        }
        try file.write(contentsOf: encoded)
        if local + count == hold {
            try check(MusicDeviceMIDIEvent(instrument.audioUnit, 0x80, UInt32(request.notes[noteIndex]), 0, 0), "Note off")
        }
        frame += count
    }
    try check(MusicDeviceMIDIEvent(instrument.audioUnit, 0xB0, 123, 0, 0), "All notes off")
    let rms = sqrt(energies.reduce(0, +) / Double(total * 2))
    guard rms > 0.0000001 else { throw PreviewError("The AU chain returned silence; preset/sample availability must be checked.") }
    return Result(success: true, sampleRate: request.sampleRate, frames: total, peak: peak, rms: rms,
        notes: request.notes.enumerated().map { NoteResult(pitch: $0.element, startFrame: $0.offset * segment,
            frames: segment, rms: sqrt(energies[$0.offset] / Double(segment * 2))) })
}

func writeState(_ unit: AVAudioUnit, component: Component, name: String, directory: URL) throws -> Plugin {
    guard var state = unit.auAudioUnit.fullState else { throw PreviewError("Self-test AU cannot export its state.") }
    let description = try component.description()
    state["type"] = description.componentType
    state["subtype"] = description.componentSubType
    state["manufacturer"] = description.componentManufacturer
    let data = try PropertyListSerialization.data(fromPropertyList: state, format: .binary, options: 0)
    try data.write(to: directory.appendingPathComponent(name), options: .withoutOverwriting)
    return Plugin(component: component, stateFile: name, stateSHA256: sha256(data))
}

func selfTest() throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("soundpreviewer-au-test-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let synthComponent = Component(type: "aumu", subtype: "dls ", manufacturer: "appl")
    let filterComponent = Component(type: "aufx", subtype: "lpas", manufacturer: "appl")
    var results: [Result] = []
    for wet in [false, true] {
        let directory = root.appendingPathComponent(wet ? "wet" : "dry")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let synth = try instantiate(synthComponent)
        let instrument = try writeState(synth, component: synthComponent, name: "instrument.aupreset", directory: directory)
        var effects: [Plugin] = []
        if wet {
            let filter = try instantiate(filterComponent)
            try check(AudioUnitSetParameter(filter.audioUnit, kLowPassParam_CutoffFrequency, kAudioUnitScope_Global, 0, 300, 0), "Set self-test cutoff")
            let effect = try writeState(filter, component: filterComponent, name: "effect.aupreset", directory: directory)
            let restored = try restore(effect, directory: directory)
            var value: AudioUnitParameterValue = 0
            try check(AudioUnitGetParameter(restored.audioUnit, kLowPassParam_CutoffFrequency, kAudioUnitScope_Global, 0, &value), "Read restored cutoff")
            guard abs(value - 300) < 0.1 else { throw PreviewError("Self-test effect state was not restored.") }
            effects = [effect]
        }
        let request = Request(schemaVersion: 1, instrument: instrument, effects: effects, notes: [60, 84, 36],
            velocity: 89, sampleRate: 44100, holdSeconds: 0.3, tailSeconds: 0.3, bpm: 120)
        let result = try render(request, directory: directory)
        let audio = try AVAudioFile(forReading: directory.appendingPathComponent("preview.wav"))
        guard audio.length == AVAudioFramePosition(result.frames), audio.fileFormat.channelCount == 2 else {
            throw PreviewError("Self-test WAV frame/channel verification failed.")
        }
        results.append(result)
    }
    guard results[1].notes[1].rms < results[0].notes[1].rms * 0.75 else {
        throw PreviewError("The restored low-pass effect did not attenuate the high-note preview.")
    }
    print("PASS: fresh AU preset restoration, sample-bounded MIDI, non-silent stereo WAV, and low-pass effect response.")
}

do {
    let args = Array(CommandLine.arguments.dropFirst())
    if args == ["--self-test"] {
        try selfTest()
    } else {
        guard args.count == 2, args[0] == "--request" else { throw PreviewError("Usage: LogicPreview --request manifest.json | --self-test") }
        let url = URL(fileURLWithPath: args[1]).standardizedFileURL
        let data = try Data(contentsOf: url)
        guard data.count <= 1024 * 1024 else { throw PreviewError("Request exceeds 1 MiB.") }
        let request = try JSONDecoder().decode(Request.self, from: data)
        let result = try render(request, directory: url.deletingLastPathComponent())
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(result).write(to: url.deletingLastPathComponent().appendingPathComponent("result.json"), options: .withoutOverwriting)
    }
} catch {
    FileHandle.standardError.write(Data("Logic preview failed: \(error.localizedDescription)\n".utf8))
    exit(1)
}
