// The panel of the touch gamepad's editor, over the game while TouchOverlayView edits: the player's layouts, the
// selected part's size and visibility, and the opacity. APP SETTINGS › Controls › Touch controls opens it.

import UIKit

private let accent = UIColor(red: 0.93, green: 0.74, blue: 0.36, alpha: 1)

final class TouchLayoutEditor: UIView {
    private weak var overlay: TouchOverlayView?
    private let layouts = UIButton(type: .system)
    private let part = UILabel()
    private let size = UISlider()
    private let visibility = UIButton(type: .system)
    private let opacity = UISlider()
    private let hint = UILabel()
    private var partRow = UIStackView()

    private var store: TouchLayoutStore { .shared }

    init(overlay: TouchOverlayView) {
        self.overlay = overlay
        super.init(frame: .zero)
        backgroundColor = UIColor(white: 0.08, alpha: 0.88)
        layer.cornerRadius = 14
        tintColor = accent

        layouts.showsMenuAsPrimaryAction = true
        layouts.titleLabel?.font = .systemFont(ofSize: 15, weight: .semibold)
        layouts.contentHorizontalAlignment = .leading
        let reset = UIButton(type: .system)
        reset.setTitle("Reset", for: .normal)
        reset.addTarget(self, action: #selector(resetTapped), for: .touchUpInside)
        let done = UIButton(type: .system)
        done.setTitle("Done", for: .normal)
        done.titleLabel?.font = .systemFont(ofSize: 15, weight: .bold)
        done.addTarget(self, action: #selector(doneTapped), for: .touchUpInside)
        let top = UIStackView(arrangedSubviews: [layouts, reset, done])
        top.spacing = 16

        part.font = .systemFont(ofSize: 14, weight: .medium)
        part.textColor = .white
        part.widthAnchor.constraint(equalToConstant: 96).isActive = true
        size.minimumValue = Float(TouchPlacement.scales.lowerBound)
        size.maximumValue = Float(TouchPlacement.scales.upperBound)
        size.addTarget(self, action: #selector(sizeChanged), for: .valueChanged)
        size.addTarget(self, action: #selector(released), for: [.touchUpInside, .touchUpOutside, .touchCancel])
        visibility.addTarget(self, action: #selector(visibilityTapped), for: .touchUpInside)
        visibility.widthAnchor.constraint(equalToConstant: 56).isActive = true
        partRow = UIStackView(arrangedSubviews: [part, size, visibility])
        partRow.spacing = 10

        hint.text = "Drag a control to move it. Pinch to resize."
        hint.font = .systemFont(ofSize: 13)
        hint.textColor = .secondaryLabel

        let fade = UILabel()
        fade.text = "Opacity"
        fade.font = .systemFont(ofSize: 14, weight: .medium)
        fade.textColor = .white
        fade.widthAnchor.constraint(equalToConstant: 96).isActive = true
        opacity.minimumValue = Float(TouchLayout.opacities.lowerBound)
        opacity.maximumValue = Float(TouchLayout.opacities.upperBound)
        opacity.addTarget(self, action: #selector(opacityChanged), for: .valueChanged)
        opacity.addTarget(self, action: #selector(released), for: [.touchUpInside, .touchUpOutside, .touchCancel])
        let fadeRow = UIStackView(arrangedSubviews: [fade, opacity])
        fadeRow.spacing = 10

        let stack = UIStackView(arrangedSubviews: [top, hint, partRow, fadeRow])
        stack.axis = .vertical
        stack.spacing = 8
        stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            stack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
            stack.topAnchor.constraint(equalTo: topAnchor, constant: 10),
            stack.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -10),
        ])
    }

    required init?(coder: NSCoder) { nil }

    // A touch on the panel is no touch on a control beneath it.
    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {}

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {}

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {}

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {}

    // At the top of the screen, between the shoulder buttons.
    func install(in overlay: UIView) {
        translatesAutoresizingMaskIntoConstraints = false
        overlay.addSubview(self)
        let safe = overlay.safeAreaLayoutGuide
        NSLayoutConstraint.activate([
            centerXAnchor.constraint(equalTo: safe.centerXAnchor),
            topAnchor.constraint(equalTo: safe.topAnchor, constant: 8),
            widthAnchor.constraint(equalToConstant: 380),
        ])
        refresh()
    }

    func refresh() {
        guard let overlay else { return }
        layouts.setTitle("\(overlay.layout.name) ▾", for: .normal)
        layouts.menu = menu()
        opacity.value = Float(overlay.layout.opacity)
        if let selected = overlay.selected {
            part.text = selected.title
            size.value = Float(overlay.layout[selected].scale)
            visibility.setTitle(overlay.layout[selected].hidden ? "Show" : "Hide", for: .normal)
            partRow.isHidden = false
            hint.isHidden = true
        } else {
            partRow.isHidden = true
            hint.isHidden = false
        }
    }

    private func menu() -> UIMenu {
        let choices = store.layouts.indices.map { index in
            UIAction(title: store.layouts[index].name, state: index == store.activeIndex ? .on : .off) { [weak self] _ in
                self?.overlay?.commit()
                self?.store.select(index)
            }
        }
        let new = UIAction(title: "Save as New Layout…", image: UIImage(systemName: "plus")) { [weak self] _ in
            self?.askName("New layout", self?.store.suggestedName() ?? "") { self?.store.add($0) ?? false }
        }
        let rename = UIAction(title: "Rename…", image: UIImage(systemName: "pencil")) { [weak self] _ in
            self?.askName("Rename layout", self?.store.active.name ?? "") { self?.store.rename($0) ?? false }
        }
        let delete = UIAction(title: "Delete \(store.active.name)", image: UIImage(systemName: "trash"),
                              attributes: store.layouts.count > 1 ? .destructive : .disabled) { [weak self] _ in
            self?.store.deleteActive()
        }
        return UIMenu(children: [UIMenu(options: .displayInline, children: choices),
                                 UIMenu(options: .displayInline, children: [new, rename, delete])])
    }

    private func askName(_ title: String, _ name: String, _ apply: @escaping (String) -> Bool) {
        overlay?.commit()
        var presenter = window?.rootViewController
        while let next = presenter?.presentedViewController {
            presenter = next
        }
        let alert = UIAlertController(title: title, message: nil, preferredStyle: .alert)
        alert.addTextField { $0.text = name }
        alert.addAction(UIAlertAction(title: "Cancel", style: .cancel))
        alert.addAction(UIAlertAction(title: "Save", style: .default) { [weak alert] _ in
            if !apply(alert?.textFields?.first?.text ?? "") {
                UINotificationFeedbackGenerator().notificationOccurred(.error)
            }
        })
        presenter?.present(alert, animated: true)
    }

    @objc private func sizeChanged() {
        let scale = Double(size.value)
        overlay?.changeSelected { $0.scale = scale }
    }

    @objc private func opacityChanged() { overlay?.changeOpacity(Double(opacity.value)) }

    @objc private func released() { overlay?.commit() }

    @objc private func visibilityTapped() {
        overlay?.changeSelected { $0.hidden.toggle() }
        overlay?.commit()
        refresh()
    }

    @objc private func resetTapped() {
        overlay?.commit()
        store.resetActive()
    }

    @objc private func doneTapped() { overlay?.finishEditing() }
}
