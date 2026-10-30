// iOS's launcher: UIKit screens shown before the game's window. The first chooses the game (Dark Cloud is not
// playable yet); Dark Cloud 2's extracts the game data from the player's disc image, changes the APP SETTINGS,
// manages the saves and the photo album (LauncherLibrary.swift) and starts the game. The main thread turns the run
// loop here until Play is tapped, as the game does from its own loop later. Every text is the launcher's own, in the
// language chosen on the first screen (launcher/launcher_text.cpp).

import ChronicleBridge
import CxxStdlib
import UIKit
import UniformTypeIdentifiers

let accent = UIColor(red: 0.93, green: 0.74, blue: 0.36, alpha: 1)

@MainActor var deviceName: String { UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone" }

// Set once Play is tapped: the run loop below then returns to the game.
@MainActor private var launcherStarted = false

/// The launcher's text for key, with each "{}" replaced by the next value.
func tr(_ key: String, _ values: CustomStringConvertible...) -> String {
    var text = String(ct.ios.Text(std.string(key)))
    for value in values {
        if let range = text.range(of: "{}") {
            text.replaceSubrange(range, with: value.description)
        }
    }
    return text
}

@MainActor func makeButton(_ title: String, _ symbol: String, primary: Bool) -> UIButton {
    var configuration: UIButton.Configuration = primary ? .filled() : .gray()
    configuration.title = title
    configuration.image = UIImage(systemName: symbol)
    configuration.imagePadding = 8
    configuration.cornerStyle = .large
    configuration.buttonSize = .large
    if primary {
        configuration.baseBackgroundColor = accent
        configuration.baseForegroundColor = .black
    } else {
        configuration.baseForegroundColor = .white
    }
    let button = UIButton(configuration: configuration, primaryAction: nil)
    button.heightAnchor.constraint(greaterThanOrEqualToConstant: 48).isActive = true
    return button
}

@MainActor private func setTitle(_ button: UIButton, _ title: String, _ symbol: String) {
    var configuration = button.configuration
    configuration?.title = title
    configuration?.image = UIImage(systemName: symbol)
    button.configuration = configuration
}

@MainActor func alert(_ title: String, _ message: String) -> UIAlertController {
    let alert = UIAlertController(title: title, message: message, preferredStyle: .alert)
    alert.addAction(UIAlertAction(title: tr("ios.ok"), style: .default))
    return alert
}

@MainActor func label(_ size: CGFloat, _ weight: UIFont.Weight, _ colour: UIColor) -> UILabel {
    let label = UILabel()
    label.font = .systemFont(ofSize: size, weight: weight)
    label.textColor = colour
    label.numberOfLines = 0
    return label
}

// The launcher's dark violet backdrop; viewDidLayoutSubviews sizes it.
@MainActor func addBackdrop(_ view: UIView) {
    view.backgroundColor = UIColor(red: 0.05, green: 0.06, blue: 0.09, alpha: 1)
    let gradient = CAGradientLayer()
    gradient.colors = [UIColor(red: 0.16, green: 0.11, blue: 0.20, alpha: 1).cgColor,
                       UIColor(red: 0.04, green: 0.05, blue: 0.08, alpha: 1).cgColor]
    gradient.startPoint = CGPoint(x: 0, y: 0)
    gradient.endPoint = CGPoint(x: 1, y: 1)
    view.layer.insertSublayer(gradient, at: 0)
}

// The launcher's navigation, and each sheet's: landscape like the game.
final class SheetNavigationController: UINavigationController {
    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { .landscape }
}

@MainActor func presentSheet(_ controller: UIViewController, from presenter: UIViewController) {
    let navigation = SheetNavigationController(rootViewController: controller)
    navigation.modalPresentationStyle = .formSheet
    navigation.overrideUserInterfaceStyle = .dark
    presenter.present(navigation, animated: true)
}

// The APP SETTINGS rows of the game's Options page, each with the arrows that step it as Left and Right do in the
// game. Binding rows wait for a key in the game, so they stay there.
private final class SettingsController: UITableViewController {
    private var sections: [(name: String, rows: [ct.ios.SettingInfo])] = []

    init() {
        super.init(style: .insetGrouped)
        title = tr("tab.settings")
        reload()
    }

    required init?(coder: NSCoder) { nil }

    private func reload() {
        sections = []
        for index in 0..<ct.ios.SettingCount() {
            let setting = ct.ios.SettingAt(index)
            let name = String(setting.page_name)
            if sections.last?.name == name {
                sections[sections.count - 1].rows.append(setting)
            } else {
                sections.append((name, [setting]))
            }
        }
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        navigationItem.rightBarButtonItem = UIBarButtonItem(title: tr("ios.done"), style: .done, target: self,
                                                            action: #selector(done))
    }

    @objc private func done() { dismiss(animated: true) }

    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { .landscape }

    override func numberOfSections(in tableView: UITableView) -> Int { sections.count }

    override func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        sections[section].rows.count
    }

    override func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        sections[section].name
    }

    override func tableView(_ tableView: UITableView, titleForFooterInSection section: Int) -> String? {
        section + 1 == sections.count ? tr("settings.note") : nil
    }

    override func tableView(_ tableView: UITableView, cellForRowAt path: IndexPath) -> UITableViewCell {
        let cell = tableView.dequeueReusableCell(withIdentifier: "row")
            ?? UITableViewCell(style: .default, reuseIdentifier: "row")
        cell.selectionStyle = .none
        let setting = sections[path.section].rows[path.row]
        var content = UIListContentConfiguration.subtitleCell()
        content.text = String(setting.label)
        content.secondaryText = String(setting.help).replacingOccurrences(of: "\n", with: " ")
        content.secondaryTextProperties.color = .secondaryLabel
        cell.contentConfiguration = content

        let tag = path.section * 1000 + path.row
        let previous = UIButton(type: .system)
        previous.setImage(UIImage(systemName: "chevron.left"), for: .normal)
        previous.addTarget(self, action: #selector(stepBack(_:)), for: .touchUpInside)
        let next = UIButton(type: .system)
        next.setImage(UIImage(systemName: "chevron.right"), for: .normal)
        next.addTarget(self, action: #selector(stepOn(_:)), for: .touchUpInside)
        for button in [previous, next] {
            button.tag = tag
            button.tintColor = accent
            button.widthAnchor.constraint(equalToConstant: 40).isActive = true
        }
        let value = UILabel()
        value.text = String(setting.value)
        value.textAlignment = .center
        value.font = .monospacedDigitSystemFont(ofSize: 16, weight: .medium)
        value.adjustsFontSizeToFitWidth = true
        value.minimumScaleFactor = 0.6
        let stepper = UIStackView(arrangedSubviews: [previous, value, next])
        stepper.frame = CGRect(x: 0, y: 0, width: 190, height: 44)
        cell.accessoryView = stepper
        return cell
    }

    private func step(_ button: UIButton, _ direction: Int32) {
        let path = IndexPath(row: button.tag % 1000, section: button.tag / 1000)
        let setting = sections[path.section].rows[path.row]
        switch ct.ios.SettingsStep(setting.page, setting.row, direction) {
        case .Unchanged:
            return
        case .NotSaved:
            present(alert(tr("ios.not_saved_title"), tr("settings.not_saved")), animated: true)
        default:
            break
        }
        reload()
        tableView.reloadRows(at: [path], with: .none)
    }

    @objc private func stepBack(_ button: UIButton) { step(button, -1) }

    @objc private func stepOn(_ button: UIButton) { step(button, 1) }
}

// The first screen: the games, and the launcher's language.
private final class GameSelectController: UIViewController {
    private let root: String
    private let titleLabel = label(40, .heavy, accent)
    private let subtitle = label(17, .regular, .secondaryLabel)
    private let darkCloud = UIButton(configuration: .gray())
    private let darkCloud2 = UIButton(configuration: .filled())
    private let language = UIButton(configuration: .gray())

    init(root: String) {
        self.root = root
        super.init(nibName: nil, bundle: nil)
    }

    required init?(coder: NSCoder) { nil }

    override func viewDidLoad() {
        super.viewDidLoad()
        addBackdrop(view)
        titleLabel.text = "OpenCloud"
        titleLabel.textAlignment = .center
        subtitle.textAlignment = .center

        for button in [darkCloud, darkCloud2] {
            button.configuration?.cornerStyle = .large
            button.configuration?.titleAlignment = .center
            button.configuration?.contentInsets = NSDirectionalEdgeInsets(top: 24, leading: 20, bottom: 24,
                                                                          trailing: 20)
            button.widthAnchor.constraint(greaterThanOrEqualToConstant: 220).isActive = true
            button.heightAnchor.constraint(greaterThanOrEqualToConstant: 150).isActive = true
        }
        // Dark Cloud is not playable yet: its card stays grey.
        darkCloud.isEnabled = false
        darkCloud2.configuration?.baseBackgroundColor = UIColor(red: 0.20, green: 0.16, blue: 0.29, alpha: 1)
        darkCloud2.configuration?.baseForegroundColor = .white
        darkCloud2.addTarget(self, action: #selector(openDarkCloud2), for: .touchUpInside)

        let games = UIStackView(arrangedSubviews: [darkCloud, darkCloud2])
        games.spacing = 24
        games.distribution = .fillEqually
        let column = UIStackView(arrangedSubviews: [titleLabel, subtitle, games])
        column.axis = .vertical
        column.spacing = 10
        column.setCustomSpacing(28, after: subtitle)
        column.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(column)

        language.configuration?.image = UIImage(systemName: "globe")
        language.configuration?.imagePadding = 6
        language.configuration?.cornerStyle = .capsule
        language.showsMenuAsPrimaryAction = true
        language.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(language)

        let safe = view.safeAreaLayoutGuide
        NSLayoutConstraint.activate([
            column.centerXAnchor.constraint(equalTo: safe.centerXAnchor),
            column.centerYAnchor.constraint(equalTo: safe.centerYAnchor),
            column.widthAnchor.constraint(lessThanOrEqualTo: safe.widthAnchor, constant: -48),
            language.topAnchor.constraint(equalTo: safe.topAnchor, constant: 12),
            language.trailingAnchor.constraint(equalTo: safe.trailingAnchor, constant: -20),
        ])
        refresh()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        view.layer.sublayers?.first?.frame = view.bounds
    }

    override var prefersStatusBarHidden: Bool { true }

    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { .landscape }

    private func card(_ button: UIButton, _ title: String, _ subtitle: String) {
        var configuration = button.configuration
        var text = AttributedString(title)
        text.font = .systemFont(ofSize: 26, weight: .bold)
        configuration?.attributedTitle = text
        var detail = AttributedString(subtitle)
        detail.font = .systemFont(ofSize: 15, weight: .medium)
        configuration?.attributedSubtitle = detail
        button.configuration = configuration
    }

    private func refresh() {
        subtitle.text = tr("home.choose")
        card(darkCloud, "Dark Cloud", tr("home.coming_soon"))
        card(darkCloud2, "Dark Cloud 2", tr("home.dc2_subtitle"))
        let current = ct.ios.LanguageIndex()
        language.configuration?.title = String(ct.ios.LanguageName(current))
        language.menu = UIMenu(title: tr("language"), children: (0..<ct.ios.LanguageCount()).map { index in
            UIAction(title: String(ct.ios.LanguageName(index)), state: index == current ? .on : .off) { [weak self] _ in
                ct.ios.SetLanguageIndex(index)
                self?.refresh()
            }
        })
    }

    @objc private func openDarkCloud2() {
        navigationController?.pushViewController(LauncherController(root: root), animated: true)
    }
}

private final class LauncherController: UIViewController, UIDocumentPickerDelegate {
    private let root: String
    private var image: String?
    // A disc image chosen in Files stays readable only between these calls.
    private var scoped: URL?
    private var timer: Timer?
    private var background: UIBackgroundTaskIdentifier = .invalid

    private let statusIcon = UIImageView()
    private let statusTitle = label(18, .semibold, .white)
    private let statusBody = label(15, .regular, .secondaryLabel)
    private let progress = UIProgressView(progressViewStyle: .default)
    private let percent = label(13, .medium, .secondaryLabel)
    private let primary = makeButton("", "play.fill", primary: true)
    private let secondary = makeButton("", "folder", primary: false)
    private let back = UIButton(configuration: .plain())
    private var buttons = UIStackView()

    init(root: String) {
        self.root = root
        super.init(nibName: nil, bundle: nil)
    }

    required init?(coder: NSCoder) { nil }

    override func viewDidLoad() {
        super.viewDidLoad()
        addBackdrop(view)

        // The left column: the icon, the title and what the app needs.
        let icon = UIImageView(image: UIImage(named: "AppIcon60x60") ?? UIImage(systemName: "gamecontroller.fill"))
        icon.tintColor = accent
        icon.contentMode = .scaleAspectFit
        icon.layer.cornerRadius = 22
        icon.layer.masksToBounds = true
        icon.widthAnchor.constraint(equalToConstant: 96).isActive = true
        icon.heightAnchor.constraint(equalToConstant: 96).isActive = true
        let title = label(34, .bold, .white)
        title.text = "Dark Cloud 2"
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
        let subtitle = label(15, .regular, .secondaryLabel)
        subtitle.text = tr("ios.subtitle", deviceName, version)
        let notice = label(12, .regular, .tertiaryLabel)
        notice.text = tr("ios.notice")
        let left = UIStackView(arrangedSubviews: [icon, title, subtitle, notice])
        left.axis = .vertical
        left.alignment = .leading
        left.spacing = 10
        left.setCustomSpacing(18, after: icon)

        // The right column: the data's state, then what to do about it.
        statusIcon.contentMode = .scaleAspectFit
        statusIcon.widthAnchor.constraint(equalToConstant: 26).isActive = true
        statusIcon.heightAnchor.constraint(equalToConstant: 26).isActive = true
        let header = UIStackView(arrangedSubviews: [statusIcon, statusTitle])
        header.spacing = 10
        header.alignment = .center
        progress.progressTintColor = accent
        percent.font = .monospacedDigitSystemFont(ofSize: 13, weight: .medium)
        let card = UIStackView(arrangedSubviews: [header, statusBody, progress, percent])
        card.axis = .vertical
        card.spacing = 10
        card.isLayoutMarginsRelativeArrangement = true
        card.directionalLayoutMargins = NSDirectionalEdgeInsets(top: 16, leading: 16, bottom: 16, trailing: 16)
        card.backgroundColor = UIColor(white: 1, alpha: 0.07)
        card.layer.cornerRadius = 16

        primary.addTarget(self, action: #selector(primaryTapped), for: .touchUpInside)
        secondary.addTarget(self, action: #selector(secondaryTapped), for: .touchUpInside)
        let settings = makeButton(tr("tab.settings"), "gearshape", primary: false)
        settings.addTarget(self, action: #selector(showSettings), for: .touchUpInside)
        let saves = makeButton(tr("tab.saves"), "externaldrive", primary: false)
        saves.addTarget(self, action: #selector(showSaves), for: .touchUpInside)
        let album = makeButton(tr("tab.album"), "photo.on.rectangle", primary: false)
        album.addTarget(self, action: #selector(showAlbum), for: .touchUpInside)
        let help = makeButton(tr("ios.help"), "questionmark.circle", primary: false)
        help.addTarget(self, action: #selector(showHelp), for: .touchUpInside)
        let firstRow = UIStackView(arrangedSubviews: [settings, saves])
        let secondRow = UIStackView(arrangedSubviews: [album, help])
        for row in [firstRow, secondRow] {
            row.spacing = 10
            row.distribution = .fillEqually
        }
        buttons = UIStackView(arrangedSubviews: [primary, secondary, firstRow, secondRow])
        buttons.axis = .vertical
        buttons.spacing = 10

        let right = UIStackView(arrangedSubviews: [card, buttons])
        right.axis = .vertical
        right.spacing = 16

        // A small landscape iPhone is short: the right column scrolls rather than clip.
        let scroll = UIScrollView()
        scroll.alwaysBounceVertical = false
        scroll.showsVerticalScrollIndicator = false
        right.translatesAutoresizingMaskIntoConstraints = false
        scroll.addSubview(right)

        back.configuration?.title = tr("nav.back")
        back.configuration?.image = UIImage(systemName: "chevron.left")
        back.configuration?.imagePadding = 4
        back.tintColor = accent
        back.addTarget(self, action: #selector(goBack), for: .touchUpInside)
        back.translatesAutoresizingMaskIntoConstraints = false

        left.translatesAutoresizingMaskIntoConstraints = false
        scroll.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(left)
        view.addSubview(scroll)
        view.addSubview(back)
        let safe = view.safeAreaLayoutGuide
        NSLayoutConstraint.activate([
            back.topAnchor.constraint(equalTo: safe.topAnchor, constant: 8),
            back.leadingAnchor.constraint(equalTo: safe.leadingAnchor, constant: 16),
            left.leadingAnchor.constraint(equalTo: safe.leadingAnchor, constant: 32),
            left.centerYAnchor.constraint(equalTo: safe.centerYAnchor),
            left.widthAnchor.constraint(equalTo: safe.widthAnchor, multiplier: 0.38),
            scroll.leadingAnchor.constraint(equalTo: left.trailingAnchor, constant: 32),
            scroll.trailingAnchor.constraint(equalTo: safe.trailingAnchor, constant: -32),
            scroll.topAnchor.constraint(equalTo: safe.topAnchor),
            scroll.bottomAnchor.constraint(equalTo: safe.bottomAnchor),
            right.leadingAnchor.constraint(equalTo: scroll.contentLayoutGuide.leadingAnchor),
            right.trailingAnchor.constraint(equalTo: scroll.contentLayoutGuide.trailingAnchor),
            right.topAnchor.constraint(equalTo: scroll.contentLayoutGuide.topAnchor, constant: 16),
            right.bottomAnchor.constraint(equalTo: scroll.contentLayoutGuide.bottomAnchor, constant: -16),
            right.widthAnchor.constraint(equalTo: scroll.frameLayoutGuide.widthAnchor),
        ])

        // Back from the Files app, the disc image the player copied there is found without a tap.
        NotificationCenter.default.addObserver(self, selector: #selector(refresh),
                                               name: UIApplication.didBecomeActiveNotification, object: nil)
        refresh()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        view.layer.sublayers?.first?.frame = view.bounds
    }

    override var prefersStatusBarHidden: Bool { true }

    override var prefersHomeIndicatorAutoHidden: Bool { true }

    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { .landscape }

    private var dataMissing: Bool { ct.ios.DataMissing(std.string(root)) }

    private func setStatus(_ symbol: String, _ colour: UIColor, _ title: String, _ body: String) {
        statusIcon.image = UIImage(systemName: symbol)
        statusIcon.tintColor = colour
        statusTitle.text = title
        statusBody.text = body
    }

    @objc private func goBack() {
        if timer == nil {
            navigationController?.popViewController(animated: true)
        }
    }

    // Shows what the data's state calls for: Play once it is current, otherwise the way to it.
    @objc private func refresh() {
        if timer != nil {
            return
        }
        let found = String(ct.ios.DiscImage())
        image = found.isEmpty ? nil : found
        progress.isHidden = true
        percent.isHidden = true
        buttons.isHidden = false
        back.isHidden = false
        if !dataMissing {
            setStatus("checkmark.circle.fill", .systemGreen, tr("ios.ready_title"), tr("data.ready"))
            setTitle(primary, tr("nav.play"), "play.fill")
            setTitle(secondary, tr("ios.extract_again"), "arrow.clockwise")
        } else if let image {
            setStatus("opticaldisc", accent, tr("ios.found_title"), tr("ios.found_body", fileName(image)))
            setTitle(primary, tr("data.extract"), "square.and.arrow.down")
            setTitle(secondary, tr("ios.choose_other"), "folder")
        } else {
            setStatus("opticaldisc", accent, tr("ios.first_title"), tr("ios.first_body", deviceName))
            setTitle(primary, tr("ios.choose_image"), "folder")
            secondary.isHidden = true
            return
        }
        secondary.isHidden = false
    }

    private func fileName(_ path: String) -> String { (path as NSString).lastPathComponent }

    @objc private func primaryTapped() {
        if !dataMissing {
            launcherStarted = true
        } else if let image {
            extract(image)
        } else {
            chooseImage()
        }
    }

    @objc private func secondaryTapped() {
        if dataMissing {
            chooseImage()
            return
        }
        let sheet = UIAlertController(title: tr("ios.again_title"), message: tr("ios.again_body"),
                                      preferredStyle: .alert)
        if let image {
            sheet.addAction(UIAlertAction(title: tr("ios.from", fileName(image)), style: .default) { [weak self] _ in
                self?.extract(image)
            })
        }
        sheet.addAction(UIAlertAction(title: tr("ios.choose_image"), style: .default) { [weak self] _ in
            self?.chooseImage()
        })
        sheet.addAction(UIAlertAction(title: tr("confirm.cancel"), style: .cancel))
        present(sheet, animated: true)
    }

    private func chooseImage() {
        var types: [UTType] = [.diskImage]
        if let iso = UTType(filenameExtension: "iso") {
            types.insert(iso, at: 0)
        }
        // Opened in place: a copy of a disc image of several gigabytes would need the space twice.
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: types, asCopy: false)
        picker.delegate = self
        picker.allowsMultipleSelection = false
        present(picker, animated: true)
    }

    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else { return }
        if url.startAccessingSecurityScopedResource() {
            scoped = url
        }
        extract(url.withUnsafeFileSystemRepresentation { String(cString: $0!) })
    }

    private func extract(_ image: String) {
        ct.ios.ExtractBegin(std.string(image), std.string(root))
        // The screen must stay on through the extraction: a locked device suspends the app. A trip to the
        // background gets the little time iOS grants before it suspends it too.
        UIApplication.shared.isIdleTimerDisabled = true
        background = UIApplication.shared.beginBackgroundTask(withName: "Extract the game data") { [weak self] in
            MainActor.assumeIsolated { self?.endBackgroundTask() }
        }
        setStatus("gearshape.2.fill", accent, tr("ios.extracting_title", fileName(image)), tr("ios.extracting_body"))
        progress.isHidden = false
        percent.isHidden = false
        progress.progress = 0
        percent.text = tr("ios.starting")
        buttons.isHidden = true
        back.isHidden = true
        // The timer fires on the main run loop.
        let timer = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    private func endBackgroundTask() {
        if background != .invalid {
            UIApplication.shared.endBackgroundTask(background)
            background = .invalid
        }
    }

    private func tick() {
        let extraction = ct.ios.ExtractPoll()
        if extraction.running {
            progress.setProgress(extraction.fraction, animated: true)
            percent.text = "\(Int(extraction.fraction * 100))%  ·  \(String(extraction.stage))"
            return
        }
        timer?.invalidate()
        timer = nil
        scoped?.stopAccessingSecurityScopedResource()
        scoped = nil
        UIApplication.shared.isIdleTimerDisabled = false
        endBackgroundTask()
        refresh()
        let failure = String(extraction.failure)
        if failure.isEmpty {
            UINotificationFeedbackGenerator().notificationOccurred(.success)
            return
        }
        present(alert(tr("ios.failed_title"), tr("ios.failed_body", failure, deviceName)), animated: true)
    }

    @objc private func showSettings() { presentSheet(SettingsController(), from: self) }

    @objc private func showSaves() { presentSheet(SavesController(), from: self) }

    @objc private func showAlbum() { presentSheet(AlbumController(), from: self) }

    @objc private func showHelp() {
        present(alert(tr("ios.help_title"), tr("ios.help_body", deviceName, deviceName)), animated: true)
    }
}

@_cdecl("IosLauncherRunAt")
public func iosLauncherRunAt(_ dataRoot: UnsafePointer<CChar>) {
    let root = String(cString: dataRoot)
    // The game calls this on the main thread.
    MainActor.assumeIsolated { autoreleasepool {
        if let code = Locale.preferredLanguages.first {
            ct.ios.SetSystemLanguage(std.string(code))
        }
        let navigation = SheetNavigationController(rootViewController: GameSelectController(root: root))
        navigation.setNavigationBarHidden(true, animated: false)
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: UIScreen.main.bounds)
        window.overrideUserInterfaceStyle = .dark
        window.tintColor = accent
        // Above the window SDL may show while it starts.
        window.windowLevel = UIWindow.Level(rawValue: UIWindow.Level.normal.rawValue + 1)
        window.rootViewController = navigation
        window.makeKeyAndVisible()
        while !launcherStarted {
            CFRunLoopRunInMode(.defaultMode, 0.05, true)
        }
        window.isHidden = true
        window.rootViewController = nil
    } }
}
