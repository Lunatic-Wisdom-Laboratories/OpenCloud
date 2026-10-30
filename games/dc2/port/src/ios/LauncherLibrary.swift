// The launcher's saves and photo album sheets (Launcher.swift presents them). Saves are shared and imported as their
// JSON files; the album's photos as PNG copies the bridge writes. ChronicleTwo's own folder is only ever copied from.

import ChronicleBridge
import CxxStdlib
import UIKit
import UniformTypeIdentifiers

// Shares files through the system sheet, anchored where iPad needs it.
@MainActor private func share(_ urls: [URL], from controller: UIViewController, anchor: UIView?) {
    let sheet = UIActivityViewController(activityItems: urls, applicationActivities: nil)
    if let popover = sheet.popoverPresentationController {
        popover.sourceView = anchor ?? controller.view
        popover.sourceRect = (anchor ?? controller.view).bounds
    }
    controller.present(sheet, animated: true)
}

// Asks before deleting, then runs action; its error, if any, is shown.
@MainActor private func confirmDelete(_ question: String, from controller: UIViewController,
                                      _ action: @escaping () -> String, done: @escaping () -> Void) {
    let sheet = UIAlertController(title: question, message: nil, preferredStyle: .alert)
    sheet.addAction(UIAlertAction(title: tr("confirm.delete"), style: .destructive) { _ in
        let error = action()
        if !error.isEmpty {
            controller.present(alert(tr("error.delete"), error), animated: true)
        }
        done()
    })
    sheet.addAction(UIAlertAction(title: tr("confirm.cancel"), style: .cancel))
    controller.present(sheet, animated: true)
}

final class SavesController: UITableViewController, UIDocumentPickerDelegate {
    private var saves: [ct.ios.SaveInfo] = []
    private var legacy: Int32 = 0
    private var protected = false
    // Whether the open picker is choosing a PS2 memory card, .psu or save folder rather than a JSON save.
    private var pickingPs2 = false

    init() {
        super.init(style: .insetGrouped)
        title = tr("tab.saves")
    }

    required init?(coder: NSCoder) { nil }

    override func viewDidLoad() {
        super.viewDidLoad()
        navigationItem.rightBarButtonItem = UIBarButtonItem(title: tr("ios.done"), style: .done, target: self,
                                                            action: #selector(done))
        navigationItem.leftBarButtonItems = [
            UIBarButtonItem(title: tr("saves.import"), style: .plain, target: self, action: #selector(importSave)),
            UIBarButtonItem(title: tr("saves.ps2_import"), style: .plain, target: self, action: #selector(importPs2)),
        ]
        reload()
    }

    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { .landscape }

    @objc private func done() { dismiss(animated: true) }

    private func reload() {
        saves = (0..<ct.ios.RefreshSaves()).map { ct.ios.SaveAt($0) }
        legacy = ct.ios.LegacySaveCount()
        protected = ct.ios.SavesProtected()
        tableView.reloadData()
    }

    // The ChronicleTwo import comes first while there is one to offer.
    private var legacySection: Bool { legacy > 0 }

    override func numberOfSections(in tableView: UITableView) -> Int { legacySection ? 2 : 1 }

    private func isLegacy(_ section: Int) -> Bool { legacySection && section == 0 }

    override func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        isLegacy(section) ? 1 : saves.count
    }

    override func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        isLegacy(section) ? "ChronicleTwo" : tr("tab.saves")
    }

    override func tableView(_ tableView: UITableView, titleForFooterInSection section: Int) -> String? {
        if isLegacy(section) {
            return tr("saves.legacy", legacy)
        }
        if saves.isEmpty {
            return tr("saves.none")
        }
        return protected ? tr("saves.protected") : nil
    }

    override func tableView(_ tableView: UITableView, cellForRowAt path: IndexPath) -> UITableViewCell {
        let cell = tableView.dequeueReusableCell(withIdentifier: "save")
            ?? UITableViewCell(style: .default, reuseIdentifier: "save")
        var content = UIListContentConfiguration.subtitleCell()
        if isLegacy(path.section) {
            content.text = tr("saves.legacy_import")
            content.image = UIImage(systemName: "square.and.arrow.down.on.square")
            content.textProperties.color = accent
        } else {
            let save = saves[path.row]
            content.text = String(save.title)
            content.secondaryText = "\(String(save.detail))\n\(tr("saves.modified")) \(String(save.modified))"
            content.secondaryTextProperties.color = save.readable ? .secondaryLabel : .systemRed
            content.image = UIImage(systemName: "externaldrive.fill")
            content.imageProperties.tintColor = accent
        }
        cell.contentConfiguration = content
        cell.accessoryType = isLegacy(path.section) ? .none : .disclosureIndicator
        return cell
    }

    override func tableView(_ tableView: UITableView, didSelectRowAt path: IndexPath) {
        tableView.deselectRow(at: path, animated: true)
        if isLegacy(path.section) {
            let outcome = ct.ios.ImportLegacy()
            present(alert(tr("saves.legacy_import"), String(outcome.message)), animated: true)
            reload()
            return
        }
        let save = saves[path.row]
        let file = save.file
        let anchor = tableView.cellForRow(at: path)
        let sheet = UIAlertController(title: String(save.title), message: String(save.detail),
                                      preferredStyle: .actionSheet)
        sheet.addAction(UIAlertAction(title: tr("ios.share"), style: .default) { [weak self] _ in
            guard let self else { return }
            share([URL(fileURLWithPath: String(save.path))], from: self, anchor: anchor)
        })
        sheet.addAction(UIAlertAction(title: tr("saves.duplicate"), style: .default) { [weak self] _ in
            let error = String(ct.ios.DuplicateSave(file))
            if !error.isEmpty {
                self?.present(alert(tr("error.copy"), error), animated: true)
            }
            self?.reload()
        })
        if !protected {
            sheet.addAction(UIAlertAction(title: tr("saves.delete"), style: .destructive) { [weak self] _ in
                guard let self else { return }
                confirmDelete(tr("saves.confirm_delete"), from: self, { String(ct.ios.DeleteSave(file)) }) {
                    [weak self] in self?.reload()
                }
            })
        }
        sheet.addAction(UIAlertAction(title: tr("confirm.cancel"), style: .cancel))
        if let popover = sheet.popoverPresentationController {
            popover.sourceView = anchor ?? tableView
            popover.sourceRect = (anchor ?? tableView).bounds
        }
        present(sheet, animated: true)
    }

    @objc private func importSave() {
        // A copy: the picked file stays where it is.
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.json], asCopy: true)
        picker.delegate = self
        pickingPs2 = false
        present(picker, animated: true)
    }

    // A memory card or .psu file, or a folder (a folder memory card or a save folder), read where it is.
    @objc private func importPs2() {
        let sheet = UIAlertController(title: tr("saves.ps2_import"), message: tr("saves.ps2_help"),
                                      preferredStyle: .alert)
        sheet.addAction(UIAlertAction(title: tr("ios.continue"), style: .default) { [weak self] _ in
            guard let self else { return }
            let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.data, .folder], asCopy: false)
            picker.delegate = self
            self.pickingPs2 = true
            self.present(picker, animated: true)
        })
        sheet.addAction(UIAlertAction(title: tr("confirm.cancel"), style: .cancel))
        present(sheet, animated: true)
    }

    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else { return }
        if pickingPs2 {
            let access = url.startAccessingSecurityScopedResource()
            let outcome = ct.ios.ImportPs2(std.string(url.path))
            if access {
                url.stopAccessingSecurityScopedResource()
            }
            present(alert(tr("saves.ps2_import"), String(outcome.message)), animated: true)
            reload()
            return
        }
        let error = String(ct.ios.ImportSave(std.string(url.path)))
        present(alert(tr("saves.import"), error.isEmpty ? tr("saves.imported_one") : error), animated: true)
        reload()
    }
}

private final class PhotoCell: UICollectionViewCell {
    let image = UIImageView()

    override init(frame: CGRect) {
        super.init(frame: frame)
        image.frame = contentView.bounds
        image.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        image.contentMode = .scaleAspectFill
        image.clipsToBounds = true
        image.layer.cornerRadius = 8
        image.layer.magnificationFilter = .nearest
        contentView.addSubview(image)
    }

    required init?(coder: NSCoder) { nil }
}

final class AlbumController: UICollectionViewController {
    private var photos: [ct.ios.PhotoInfo] = []
    private let empty = label(15, .regular, .secondaryLabel)

    init() {
        let layout = UICollectionViewFlowLayout()
        layout.itemSize = CGSize(width: 112, height: 112)
        layout.minimumInteritemSpacing = 10
        layout.minimumLineSpacing = 10
        layout.sectionInset = UIEdgeInsets(top: 16, left: 16, bottom: 16, right: 16)
        super.init(collectionViewLayout: layout)
        title = tr("tab.album")
    }

    required init?(coder: NSCoder) { nil }

    override func viewDidLoad() {
        super.viewDidLoad()
        collectionView.register(PhotoCell.self, forCellWithReuseIdentifier: "photo")
        navigationItem.rightBarButtonItem = UIBarButtonItem(title: tr("ios.done"), style: .done, target: self,
                                                            action: #selector(done))
        navigationItem.leftBarButtonItem = UIBarButtonItem(title: tr("album.export_all"), style: .plain,
                                                           target: self, action: #selector(shareAll))
        empty.text = tr("album.none")
        empty.textAlignment = .center
        collectionView.backgroundView = empty
        reload()
    }

    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        reload()
    }

    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { .landscape }

    @objc private func done() { dismiss(animated: true) }

    func reload() {
        photos = (0..<ct.ios.RefreshPhotos()).map { ct.ios.PhotoAt($0) }
        empty.isHidden = !photos.isEmpty
        navigationItem.leftBarButtonItem?.isEnabled = !photos.isEmpty
        collectionView.reloadData()
    }

    @objc private func shareAll() {
        share(photos.map { URL(fileURLWithPath: String($0.png)) }, from: self, anchor: nil)
    }

    override func collectionView(_ collectionView: UICollectionView, numberOfItemsInSection section: Int) -> Int {
        photos.count
    }

    override func collectionView(_ collectionView: UICollectionView,
                                 cellForItemAt path: IndexPath) -> UICollectionViewCell {
        let cell = collectionView.dequeueReusableCell(withReuseIdentifier: "photo", for: path)
        (cell as? PhotoCell)?.image.image = UIImage(contentsOfFile: String(photos[path.item].png))
        return cell
    }

    override func collectionView(_ collectionView: UICollectionView, didSelectItemAt path: IndexPath) {
        navigationController?.pushViewController(PhotoController(photos[path.item]), animated: true)
    }
}

private final class PhotoController: UIViewController {
    private let photo: ct.ios.PhotoInfo

    init(_ photo: ct.ios.PhotoInfo) {
        self.photo = photo
        super.init(nibName: nil, bundle: nil)
        title = "\(tr("album.card", photo.card + 1))  ·  \(tr("album.photo", photo.slot + 1))\(photo.hd ? "  ·  HD" : "")"
    }

    required init?(coder: NSCoder) { nil }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        let image = UIImageView(image: UIImage(contentsOfFile: String(photo.png)))
        image.contentMode = .scaleAspectFit
        image.layer.magnificationFilter = photo.hd ? .linear : .nearest
        image.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(image)
        let safe = view.safeAreaLayoutGuide
        NSLayoutConstraint.activate([
            image.topAnchor.constraint(equalTo: safe.topAnchor, constant: 12),
            image.bottomAnchor.constraint(equalTo: safe.bottomAnchor, constant: -12),
            image.leadingAnchor.constraint(equalTo: safe.leadingAnchor),
            image.trailingAnchor.constraint(equalTo: safe.trailingAnchor),
        ])
        var items = [UIBarButtonItem(barButtonSystemItem: .action, target: self, action: #selector(sharePhoto))]
        if !ct.ios.SavesProtected() {
            items.append(UIBarButtonItem(barButtonSystemItem: .trash, target: self, action: #selector(deletePhoto)))
        }
        navigationItem.rightBarButtonItems = items
    }

    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { .landscape }

    @objc private func sharePhoto() {
        share([URL(fileURLWithPath: String(photo.png))], from: self, anchor: nil)
    }

    @objc private func deletePhoto() {
        let card = photo.card, slot = photo.slot
        confirmDelete(tr("album.confirm_delete"), from: self, { String(ct.ios.DeletePhoto(card, slot)) }) {
            [weak self] in self?.navigationController?.popViewController(animated: true)
        }
    }
}
