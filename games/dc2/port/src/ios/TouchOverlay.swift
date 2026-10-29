// The on-screen gamepad of iOS, TheMoonPeople/Chronicle's Android overlay (ChronicleTouchOverlay.java) in UIKit: a
// view over the game's that draws the controls and takes every touch, so no draw call enters the game's frame. Its
// state reaches pad 0 through ct.ios.SetTouchOverlay. The player's layout (TouchLayout.swift) moves, resizes and
// hides its parts, in the editor that APP SETTINGS opens over the game, or three fingers held on no control.

import ChronicleBridge
import UIKit

// Sixteen buttons, then the left and the right stick.
private let controls = 18
private let buttons = 16
private let leftStick = 16

private let labels = ["↑", "↓", "←", "→", "×", "○", "□", "△", "L1", "R1", "L2", "R2", "SELECT", "START", "L3", "R3"]

private let labelColours: [UInt32] = [
    0xedf2ff, 0xedf2ff, 0xedf2ff, 0xedf2ff, 0x73b8ff, 0xff878b, 0xefa1e9, 0x8be7a5,
    0xedf2ff, 0xedf2ff, 0xedf2ff, 0xedf2ff, 0xedf2ff, 0xedf2ff, 0xedf2ff, 0xedf2ff,
]

private let buttonBits: [UInt16] = (0..<buttons).map { ct.ios.ButtonBit(Int32($0)) }

private func colour(_ argb: UInt32) -> UIColor {
    UIColor(red: CGFloat((argb >> 16) & 0xff) / 255, green: CGFloat((argb >> 8) & 0xff) / 255,
            blue: CGFloat(argb & 0xff) / 255, alpha: CGFloat((argb >> 24) & 0xff) / 255)
}

private func clamp(_ value: CGFloat, _ low: CGFloat, _ high: CGFloat) -> CGFloat { max(low, min(high, value)) }

private func same(_ a: ct.ios.TouchState, _ b: ct.ios.TouchState) -> Bool {
    a.buttons == b.buttons && a.left_x == b.left_x && a.left_y == b.left_y && a.right_x == b.right_x &&
        a.right_y == b.right_y
}

final class TouchOverlayView: UIView, UIGestureRecognizerDelegate {
    // Android's layout, before the player's.
    private var baseX = [CGFloat](repeating: 0, count: controls)
    private var baseY = [CGFloat](repeating: 0, count: controls)
    private var baseRadius = [CGFloat](repeating: 0, count: controls)
    private var area = CGRect.zero
    private var cx = [CGFloat](repeating: 0, count: controls)
    private var cy = [CGFloat](repeating: 0, count: controls)
    private var radius = [CGFloat](repeating: 0, count: controls)
    private var concealed = [Bool](repeating: false, count: controls)
    // Each touch keeps the control it went down on, as on Android: a finger that slides off a button lets go of
    // it, and one that went down on nothing presses nothing.
    private var captures: [ObjectIdentifier: (touch: UITouch, control: Int)] = [:]
    private var last = ct.ios.TouchState()

    // Whether its state goes to pad 0: the launcher's editor shows one that plays nothing.
    private let feedsGame: Bool
    // The layout shown, the store's active one but while a part is being dragged or resized.
    private(set) var layout = TouchLayout(name: TouchLayoutStore.defaultName)
    private(set) var editing = false
    private(set) var selected: TouchElement?
    private var drag: (touch: UITouch, element: TouchElement, start: CGPoint, placement: TouchPlacement)?
    private var pinchStart: (element: TouchElement, scale: Double)?
    private var editor: TouchLayoutEditor?
    private let pinch = UIPinchGestureRecognizer()
    private let summon = UILongPressGestureRecognizer()
    private let selection = UISelectionFeedbackGenerator()
    // Called when the player leaves the editor.
    var onFinishEditing: (() -> Void)?

    init(feedsGame: Bool) {
        self.feedsGame = feedsGame
        super.init(frame: .zero)
        layout = TouchLayoutStore.shared.active
        isOpaque = false
        backgroundColor = .clear
        isMultipleTouchEnabled = true
        contentMode = .redraw
        autoresizingMask = [.flexibleWidth, .flexibleHeight]

        // Neither gesture may hold back or take the game's touches: they only watch them.
        pinch.addTarget(self, action: #selector(pinched(_:)))
        pinch.isEnabled = false
        summon.addTarget(self, action: #selector(summoned(_:)))
        summon.numberOfTouchesRequired = 3
        summon.minimumPressDuration = 1.2
        summon.isEnabled = feedsGame
        for recognizer in [pinch, summon] as [UIGestureRecognizer] {
            recognizer.cancelsTouchesInView = recognizer === pinch
            recognizer.delaysTouchesBegan = false
            recognizer.delaysTouchesEnded = false
            recognizer.delegate = self
            addGestureRecognizer(recognizer)
        }
        NotificationCenter.default.addObserver(self, selector: #selector(layoutChanged),
                                               name: .touchLayoutChanged, object: nil)
    }

    required init?(coder: NSCoder) { nil }

    // Android's layout, in the safe area so the island and the home indicator cover nothing.
    override func layoutSubviews() {
        super.layoutSubviews()
        let area = bounds.inset(by: safeAreaInsets)
        // The editor's own views lay out here too: only a new size moves the controls.
        guard area != self.area else { return }
        self.area = area
        let x0 = area.origin.x, y0 = area.origin.y, w = area.size.width, h = area.size.height
        guard w > 0, h > 0 else { return }
        let s = min(w, h)
        let scale = min(w / 1280, h / 720)
        let r = 46 * scale, small = 44 * scale, step = 104 * scale
        let side = clamp(0.08 + 0.08 * s / w, 0.10, 0.16)
        let left = x0 + side * w, right = x0 + (1 - side) * w, row = y0 + h * 0.52
        for i in 0..<controls {
            baseRadius[i] = i < 12 ? r : i < buttons ? small : 80 * scale
        }
        baseX[0] = left; baseX[1] = left
        baseY[0] = row - step; baseY[1] = row + step
        baseX[2] = left - step; baseX[3] = left + step
        baseY[2] = row; baseY[3] = row
        baseX[4] = right; baseX[7] = right
        baseY[4] = row + step; baseY[7] = row - step
        baseX[5] = right + step; baseX[6] = right - step
        baseY[5] = row; baseY[6] = row
        baseX[8] = left; baseX[10] = left
        baseX[9] = right; baseX[11] = right
        baseY[8] = y0 + h * 0.22; baseY[9] = baseY[8]
        baseY[10] = y0 + h * 0.08; baseY[11] = baseY[10]
        baseX[12] = x0 + w * 0.43; baseX[13] = x0 + w * 0.57
        baseY[12] = y0 + h * 0.92; baseY[13] = baseY[12]
        baseX[16] = x0 + (side + 0.025) * w; baseX[17] = x0 + (1 - side - 0.025) * w
        baseY[16] = y0 + h * 0.86; baseY[17] = baseY[16]
        // Android has no L3 or R3; here they sit beside their sticks, on the side facing the centre.
        baseX[14] = baseX[16] + baseRadius[16] + small * 1.4
        baseX[15] = baseX[17] - baseRadius[17] - small * 1.4
        baseY[14] = baseY[16]; baseY[15] = baseY[16]
        captures.removeAll()
        drag = nil
        place()
        publish()
    }

    // The centre of a part in Android's layout: the player's offset and size are taken from there.
    private func baseCentre(_ element: TouchElement) -> CGPoint {
        let members = element.controls
        let n = CGFloat(members.count)
        return CGPoint(x: members.reduce(0) { $0 + baseX[$1] } / n, y: members.reduce(0) { $0 + baseY[$1] } / n)
    }

    // Android's layout moved, resized and hidden as the player's layout says.
    private func place() {
        for element in TouchElement.allCases {
            let placement = layout[element]
            let centre = baseCentre(element)
            let scale = CGFloat(placement.scale)
            let dx = CGFloat(placement.x) * area.width, dy = CGFloat(placement.y) * area.height
            for i in element.controls {
                cx[i] = centre.x + (baseX[i] - centre.x) * scale + dx
                cy[i] = centre.y + (baseY[i] - centre.y) * scale + dy
                radius[i] = baseRadius[i] * scale
                concealed[i] = placement.hidden
            }
        }
        setNeedsDisplay()
    }

    @objc private func layoutChanged() {
        layout = TouchLayoutStore.shared.active
        drag = nil
        place()
        editor?.refresh()
    }

    private func distance(_ control: Int, _ p: CGPoint) -> CGFloat { hypot(p.x - cx[control], p.y - cy[control]) }

    // The control under a point, the one whose centre is nearest for its size where they overlap. Hidden ones are
    // there only in the editor, so the player can show them again.
    private func hit(_ p: CGPoint) -> Int {
        var best = -1, nearest = CGFloat.infinity
        for i in 0..<controls where editing || !concealed[i] {
            let d = distance(i, p) / radius[i]
            if d <= 1 && d < nearest {
                best = i
                nearest = d
            }
        }
        return best
    }

    private func publish() {
        var next = ct.ios.TouchState()
        if !isHidden && !editing {
            for (touch, control) in captures.values where control >= 0 {
                let p = touch.location(in: self)
                if control < buttons {
                    if distance(control, p) <= radius[control] {
                        next.buttons |= buttonBits[control]
                    }
                    continue
                }
                var x = clamp((p.x - cx[control]) / radius[control], -1, 1)
                var y = clamp((p.y - cy[control]) / radius[control], -1, 1)
                let magnitude = hypot(x, y)
                if magnitude > 1 {
                    x /= magnitude
                    y /= magnitude
                }
                if control == leftStick {
                    next.left_x = Float(x)
                    next.left_y = Float(y)
                } else {
                    next.right_x = Float(x)
                    next.right_y = Float(y)
                }
            }
        }
        if !same(next, last) {
            last = next
            setNeedsDisplay()
        }
        if feedsGame {
            ct.ios.SetTouchOverlay(next)
        }
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        if editing {
            editBegan(touches, event)
            return
        }
        for touch in touches {
            captures[ObjectIdentifier(touch)] = (touch, hit(touch.location(in: self)))
        }
        publish()
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        if editing {
            editMoved(touches)
            return
        }
        publish()
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        if editing {
            if let drag, touches.contains(drag.touch) {
                self.drag = nil
                commit()
            }
            return
        }
        for touch in touches {
            captures[ObjectIdentifier(touch)] = nil
        }
        publish()
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        touchesEnded(touches, with: event)
    }

    func setControlsVisible(_ visible: Bool) {
        if isHidden == !visible {
            return
        }
        isHidden = !visible
        if !visible {
            captures.removeAll()
            // A controller connected while editing: the editor goes with the overlay.
            if editing {
                finishEditing()
            }
        }
        publish()
    }

    // MARK: The editor

    func beginEditing() {
        guard !editing else { return }
        editing = true
        captures.removeAll()
        drag = nil
        selected = nil
        layout = TouchLayoutStore.shared.active
        publish()
        pinch.isEnabled = true
        summon.isEnabled = false
        let editor = TouchLayoutEditor(overlay: self)
        self.editor = editor
        editor.install(in: self)
        place()
        UINotificationFeedbackGenerator().notificationOccurred(.success)
    }

    func finishEditing() {
        guard editing else { return }
        commit()
        editing = false
        drag = nil
        pinchStart = nil
        selected = nil
        editor?.removeFromSuperview()
        editor = nil
        pinch.isEnabled = false
        summon.isEnabled = feedsGame
        place()
        publish()
        onFinishEditing?()
    }

    // The layout shown becomes the store's, which writes it.
    func commit() {
        if layout != TouchLayoutStore.shared.active {
            TouchLayoutStore.shared.active = layout
        }
    }

    // Changes the selected part's placement on screen; commit() keeps it.
    func changeSelected(_ change: (inout TouchPlacement) -> Void) {
        guard let selected else { return }
        change(&layout[selected])
        place()
    }

    func changeOpacity(_ opacity: Double) {
        layout.opacity = min(max(opacity, TouchLayout.opacities.lowerBound), TouchLayout.opacities.upperBound)
        setNeedsDisplay()
    }

    private func select(_ element: TouchElement?) {
        if element != selected {
            selected = element
            if element != nil {
                selection.selectionChanged()
            }
            editor?.refresh()
            setNeedsDisplay()
        }
    }

    private func editBegan(_ touches: Set<UITouch>, _ event: UIEvent?) {
        for touch in touches where drag == nil {
            let p = touch.location(in: self)
            let control = hit(p)
            guard control >= 0, let element = TouchElement.of(control: control) else {
                // A tap on nothing lets go of the selection.
                if (event?.allTouches?.count ?? 1) == 1 {
                    select(nil)
                }
                continue
            }
            select(element)
            drag = (touch, element, p, layout[element])
        }
    }

    private func editMoved(_ touches: Set<UITouch>) {
        guard let drag, touches.contains(drag.touch), area.width > 0, area.height > 0 else { return }
        let p = drag.touch.location(in: self)
        // The part's centre stays on the screen, the safe area's edges aside.
        let centre = baseCentre(drag.element)
        let limits = bounds.insetBy(dx: 16, dy: 16)
        let x = clamp(centre.x + CGFloat(drag.placement.x) * area.width + p.x - drag.start.x, limits.minX, limits.maxX)
        let y = clamp(centre.y + CGFloat(drag.placement.y) * area.height + p.y - drag.start.y, limits.minY,
                      limits.maxY)
        layout[drag.element].x = Double((x - centre.x) / area.width)
        layout[drag.element].y = Double((y - centre.y) / area.height)
        place()
    }

    @objc private func pinched(_ recognizer: UIPinchGestureRecognizer) {
        switch recognizer.state {
        case .began:
            let control = hit(recognizer.location(in: self))
            if control >= 0, let element = TouchElement.of(control: control) {
                select(element)
            }
            drag = nil
            if let selected {
                pinchStart = (selected, layout[selected].scale)
            } else {
                pinchStart = nil
            }
        case .changed:
            guard let pinchStart else { return }
            let scale = min(max(pinchStart.scale * Double(recognizer.scale), TouchPlacement.scales.lowerBound),
                            TouchPlacement.scales.upperBound)
            layout[pinchStart.element].scale = scale
            place()
            editor?.refresh()
        default:
            if pinchStart != nil {
                pinchStart = nil
                commit()
            }
        }
    }

    @objc private func summoned(_ recognizer: UILongPressGestureRecognizer) {
        guard recognizer.state == .began, !isHidden, !editing else { return }
        // Three fingers on no control: holding the sticks and a button opens nothing.
        let free = (0..<recognizer.numberOfTouches).allSatisfy {
            hit(recognizer.location(ofTouch: $0, in: self)) < 0
        }
        if free {
            beginEditing()
        }
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldReceive touch: UITouch) -> Bool {
        // The editor's own buttons and sliders keep their touches.
        !(touch.view is UIControl) && touch.view === self
    }

    // MARK: Drawing

    private func disc(_ context: CGContext, _ x: CGFloat, _ y: CGFloat, _ r: CGFloat, fill: UInt32, edge: UInt32) {
        let circle = CGRect(x: x - r, y: y - r, width: 2 * r, height: 2 * r)
        context.setFillColor(colour(fill).cgColor)
        context.fillEllipse(in: circle)
        context.setStrokeColor(colour(edge).cgColor)
        context.setLineWidth(max(1, r * 0.027))
        context.strokeEllipse(in: circle)
    }

    // The control's alpha: the layout's, and faint for a hidden one the editor still shows.
    private func shade(_ control: Int) -> CGFloat? {
        if concealed[control] {
            return editing ? 0.3 : nil
        }
        return CGFloat(layout.opacity)
    }

    override func draw(_ rect: CGRect) {
        guard let context = UIGraphicsGetCurrentContext() else { return }
        if editing && feedsGame {
            // The game shows through, darker, so the editor reads as a mode of its own.
            context.setFillColor(UIColor(white: 0, alpha: 0.35).cgColor)
            context.fill(bounds)
        }
        for i in buttons..<controls {
            guard let alpha = shade(i) else { continue }
            context.setAlpha(alpha)
            disc(context, cx[i], cy[i], radius[i], fill: 0x2920_2a3a, edge: 0x779f_abc0)
            let dx = CGFloat(i == leftStick ? last.left_x : last.right_x)
            let dy = CGFloat(i == leftStick ? last.left_y : last.right_y)
            disc(context, cx[i] + dx * radius[i] * 0.45, cy[i] + dy * radius[i] * 0.45, radius[i] * 0.37,
                 fill: 0x552d_374d, edge: 0xaac9_d7f0)
        }
        for i in 0..<buttons {
            guard let alpha = shade(i) else { continue }
            context.setAlpha(alpha)
            let pressed = last.buttons & buttonBits[i] != 0
            disc(context, cx[i], cy[i], radius[i], fill: pressed ? 0x9928_3450 : 0x3c20_2a3a,
                 edge: pressed ? 0xe6d5_e3ff : 0x99c9_d7f0)
            let size = radius[i] * (i >= 12 ? 0.40 : i >= 8 ? 0.50 : 0.70)
            let attributes: [NSAttributedString.Key: Any] = [
                .font: UIFont.systemFont(ofSize: size, weight: .medium),
                .foregroundColor: colour(0xff00_0000 | labelColours[i]),
            ]
            let label = labels[i] as NSString
            let text = label.size(withAttributes: attributes)
            label.draw(at: CGPoint(x: cx[i] - text.width / 2, y: cy[i] - text.height / 2), withAttributes: attributes)
        }
        context.setAlpha(1)
        if editing, let selected {
            context.setStrokeColor(UIColor(red: 0.93, green: 0.74, blue: 0.36, alpha: 1).cgColor)
            context.setLineWidth(3)
            for i in selected.controls {
                let r = radius[i] + 4
                context.strokeEllipse(in: CGRect(x: cx[i] - r, y: cy[i] - r, width: 2 * r, height: 2 * r))
            }
        }
    }
}

@MainActor private final class TouchOverlayDriver {
    private var view: TouchOverlayView?

    // The game's window comes and goes (the launcher has one of its own), and SDL adds its Metal view after the
    // window, so the overlay is placed on top of whichever window is key, every tick.
    func tick() {
        let window = UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .flatMap(\.windows)
            .last { $0.isKeyWindow }
            ?? UIApplication.shared.windows.last { $0.isKeyWindow }
        guard let host = window?.rootViewController?.view else { return }
        let overlay = view ?? TouchOverlayView(feedsGame: true)
        view = overlay
        if overlay.superview !== host {
            overlay.removeFromSuperview()
            overlay.frame = host.bounds
            host.addSubview(overlay)
        } else if host.subviews.last !== overlay {
            host.bringSubviewToFront(overlay)
        }
        overlay.setControlsVisible(ct.ios.TouchOverlayVisible() || overlay.editing)
    }

    func edit() {
        tick()
        view?.setControlsVisible(true)
        view?.beginEditing()
    }
}

@MainActor private let driver = TouchOverlayDriver()

// APP SETTINGS › Controls › Touch controls (options/rows.cpp).
@_cdecl("IosTouchEditorOpen")
public func iosTouchEditorOpen() {
    MainActor.assumeIsolated { driver.edit() }
}

@_cdecl("IosTouchOverlayStart")
public func iosTouchOverlayStart() {
    // The game pumps the main run loop from its own loop, which fires this between frames.
    let timer = Timer(timeInterval: 0.1, repeats: true) { _ in MainActor.assumeIsolated { driver.tick() } }
    RunLoop.main.add(timer, forMode: .common)
}
