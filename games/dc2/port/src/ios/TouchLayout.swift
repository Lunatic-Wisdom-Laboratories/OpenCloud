// The player's layouts of the on-screen gamepad: where each of its parts sits, how big it is and whether it is shown,
// kept by name in opencloud/dc2/touch_layouts.json in Documents, beside the saves. A part is moved as an offset from
// the place the default layout gives it, in fractions of the safe area, so a layout fits any iPhone or iPad.

import Foundation

// The parts the player moves: the D-pad and the face buttons go as a group, the rest one by one.
enum TouchElement: Int, CaseIterable {
    case dpad, face, l1, r1, l2, r2, select, start, l3, r3, leftStick, rightStick

    // The overlay's controls in this part: its buttons in ct.ios.ButtonBit's order, then the two sticks.
    var controls: [Int] {
        switch self {
        case .dpad: return [0, 1, 2, 3]
        case .face: return [4, 5, 6, 7]
        case .l1: return [8]
        case .r1: return [9]
        case .l2: return [10]
        case .r2: return [11]
        case .select: return [12]
        case .start: return [13]
        case .l3: return [14]
        case .r3: return [15]
        case .leftStick: return [16]
        case .rightStick: return [17]
        }
    }

    static func of(control: Int) -> TouchElement? { allCases.first { $0.controls.contains(control) } }

    // Its name in touch_layouts.json.
    var key: String {
        switch self {
        case .dpad: return "dpad"
        case .face: return "face"
        case .l1: return "l1"
        case .r1: return "r1"
        case .l2: return "l2"
        case .r2: return "r2"
        case .select: return "select"
        case .start: return "start"
        case .l3: return "l3"
        case .r3: return "r3"
        case .leftStick: return "left_stick"
        case .rightStick: return "right_stick"
        }
    }

    var title: String {
        switch self {
        case .dpad: return "D-Pad"
        case .face: return "Face Buttons"
        case .l1: return "L1"
        case .r1: return "R1"
        case .l2: return "L2"
        case .r2: return "R2"
        case .select: return "SELECT"
        case .start: return "START"
        case .l3: return "L3"
        case .r3: return "R3"
        case .leftStick: return "Left Stick"
        case .rightStick: return "Right Stick"
        }
    }
}

struct TouchPlacement: Codable, Equatable {
    // The offset from the default place, in fractions of the safe area's width and height.
    var x: Double = 0
    var y: Double = 0
    var scale: Double = 1
    var hidden = false

    static let scales: ClosedRange<Double> = 0.5...2

    init() {}

    // Each field may be missing, so a file from an older build still reads.
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        x = try values.decodeIfPresent(Double.self, forKey: .x) ?? 0
        y = try values.decodeIfPresent(Double.self, forKey: .y) ?? 0
        scale = min(max(try values.decodeIfPresent(Double.self, forKey: .scale) ?? 1, Self.scales.lowerBound),
                    Self.scales.upperBound)
        hidden = try values.decodeIfPresent(Bool.self, forKey: .hidden) ?? false
    }
}

struct TouchLayout: Codable, Equatable {
    var name: String
    var opacity: Double = 1
    var placements: [String: TouchPlacement] = [:]

    static let opacities: ClosedRange<Double> = 0.2...1

    init(name: String) { self.name = name }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        name = try values.decode(String.self, forKey: .name)
        opacity = min(max(try values.decodeIfPresent(Double.self, forKey: .opacity) ?? 1, Self.opacities.lowerBound),
                      Self.opacities.upperBound)
        placements = try values.decodeIfPresent([String: TouchPlacement].self, forKey: .placements) ?? [:]
    }

    subscript(element: TouchElement) -> TouchPlacement {
        get { placements[element.key] ?? TouchPlacement() }
        set { placements[element.key] = newValue == TouchPlacement() ? nil : newValue }
    }
}

private struct TouchLayoutFile: Codable {
    var active: String
    var layouts: [TouchLayout]
}

extension Notification.Name {
    // Posted on the main thread whenever the active layout or the list of layouts changes.
    static let touchLayoutChanged = Notification.Name("ChronicleTouchLayoutChanged")
}

@MainActor final class TouchLayoutStore {
    static let shared = TouchLayoutStore()

    static let defaultName = "Default"

    private(set) var layouts: [TouchLayout] = [TouchLayout(name: defaultName)]
    private(set) var activeIndex = 0

    var active: TouchLayout {
        get { layouts[activeIndex] }
        set {
            layouts[activeIndex] = newValue
            changed()
        }
    }

    private let url: URL = {
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Documents")
        return documents.appendingPathComponent("opencloud/dc2/touch_layouts.json")
    }()

    private init() {
        // A missing or unreadable file leaves the default layout, which the next change writes.
        guard let data = try? Data(contentsOf: url),
              let file = try? JSONDecoder().decode(TouchLayoutFile.self, from: data), !file.layouts.isEmpty
        else { return }
        layouts = file.layouts
        activeIndex = layouts.firstIndex { $0.name == file.active } ?? 0
    }

    private func changed() {
        save()
        NotificationCenter.default.post(name: .touchLayoutChanged, object: self)
    }

    private func save() {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(TouchLayoutFile(active: active.name, layouts: layouts)) else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                 withIntermediateDirectories: true)
        if (try? data.write(to: url, options: .atomic)) == nil {
            NSLog("opencloud: could not write %@", url.path)
        }
    }

    func select(_ index: Int) {
        guard layouts.indices.contains(index), index != activeIndex else { return }
        activeIndex = index
        changed()
    }

    // The name trimmed, or nil when it is empty or another layout has it.
    func available(_ name: String, except index: Int? = nil) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            return nil
        }
        let taken = layouts.indices.contains {
            $0 != index && layouts[$0].name.compare(trimmed, options: .caseInsensitive) == .orderedSame
        }
        return taken ? nil : trimmed
    }

    // A name for a new layout that no other one has.
    func suggestedName() -> String {
        var number = layouts.count + 1
        while available("Layout \(number)") == nil {
            number += 1
        }
        return "Layout \(number)"
    }

    // A new layout, a copy of the active one, which becomes active.
    @discardableResult func add(_ name: String) -> Bool {
        guard let name = available(name) else { return false }
        var layout = active
        layout.name = name
        layouts.append(layout)
        activeIndex = layouts.count - 1
        changed()
        return true
    }

    @discardableResult func rename(_ name: String) -> Bool {
        guard let name = available(name, except: activeIndex) else { return false }
        layouts[activeIndex].name = name
        changed()
        return true
    }

    // The last layout cannot go: there is always one to play with.
    func deleteActive() {
        guard layouts.count > 1 else { return }
        layouts.remove(at: activeIndex)
        activeIndex = min(activeIndex, layouts.count - 1)
        changed()
    }

    // Every part back to the default place and size, and full opacity.
    func resetActive() {
        var layout = TouchLayout(name: active.name)
        layout.opacity = 1
        active = layout
    }
}
