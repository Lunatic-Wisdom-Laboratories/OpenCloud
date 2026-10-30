//! OpenCloud's desktop launcher: the window that opens before a game, to choose the game, extract its data from the
//! player's disc, and manage its settings, saves and photo album. The game answers through `Backend`
//! (launcher_host.rs bridges the C++ one).

/// One steppable settings row.
pub struct Setting {
    pub page: i32,
    pub row: i32,
    pub page_name: String,
    pub label: String,
    pub help: String,
    pub value: String,
}

pub struct Save {
    pub file: String,
    pub number: i32,
    pub autosave: bool,
    pub readable: bool,
    pub status: String,
    pub chapter: String,
    pub play_seconds: u64,
    pub modified: String,
}

/// One photo of the memory card album, as RGBA8.
pub struct Photo {
    pub card: i32,
    pub slot: i32,
    pub hd: bool,
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
}

/// A data packs folder to copy into this installation's.
pub struct LegacyPacks {
    pub from: String,
    pub to: String,
    pub bytes: u64,
}

/// What the launcher asks of the game; errors are the text to show.
pub trait Backend {
    fn text(&self, key: &str) -> String;
    fn language(&self) -> usize;
    fn languages(&self) -> Vec<String>;
    fn set_language(&self, index: usize);
    fn data_root(&self) -> String;
    fn data_ready(&self) -> bool;
    fn settings(&self) -> Vec<Setting>;
    /// 0: unchanged, 1: saved, 2: applied but not saved.
    fn step_setting(&self, page: i32, row: i32, direction: i32) -> i32;
    fn saves(&self) -> Vec<Save>;
    fn saves_protected(&self) -> bool;
    fn saves_folder(&self) -> String;
    fn delete_save(&self, file: &str) -> Result<(), String>;
    fn duplicate_save(&self, file: &str) -> Result<(), String>;
    fn export_save(&self, file: &str, destination: &str) -> Result<(), String>;
    fn import_save(&self, source: &str) -> Result<(), String>;
    /// Converts PS2 saves in from a memory card, a .psu or a save folder; Ok is what was imported.
    fn import_ps2(&self, source: &str) -> Result<String, String>;
    fn legacy_save_count(&self) -> usize;
    fn legacy_folder(&self) -> String;
    fn import_legacy(&self) -> Result<usize, String>;
    /// ChronicleTwo's data packs, when there are some to copy.
    fn legacy_datapacks(&self) -> Option<LegacyPacks>;
    fn datapacks_room(&self, bytes: u64) -> Result<(), String>;
    fn photos(&self) -> Vec<Photo>;
    fn export_photo(&self, card: i32, slot: i32, destination: &str) -> Result<(), String>;
    fn delete_photo(&self, card: i32, slot: i32) -> Result<(), String>;
}

/// The boot executable of Dark Chronicle's PAL disc, the one the game's data comes from.
pub const DC2_PAL_SERIAL: &str = "SCES_511.90";

/// Whether a disc's boot executable is the PAL Dark Chronicle; a source without SYSTEM.CNF is left to the
/// extraction to judge.
pub fn accepts_serial(serial: Option<&str>) -> bool {
    serial.is_none_or(|serial| serial.eq_ignore_ascii_case(DC2_PAL_SERIAL))
}

/// "12:05" for a play time in seconds, hours first.
pub fn play_time(seconds: u64) -> String {
    let minutes = seconds / 60;
    format!("{}:{:02}", minutes / 60, minutes % 60)
}

/// Replaces the first "{}" of a translated text.
pub fn fill(text: &str, value: impl std::fmt::Display) -> String {
    text.replacen("{}", &value.to_string(), 1)
}

#[cfg(feature = "desktop-ui")]
pub use ui::run;

#[cfg(feature = "desktop-ui")]
mod ui {
    use super::*;
    use crate::core::{self, CoreError, CoreResult, ScriptPublicationPolicy};
    use eframe::egui::{self, Color32, CornerRadius, Margin, RichText, Stroke, Vec2};
    use std::path::{Path, PathBuf};
    use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
    use std::sync::{Arc, Mutex};
    use std::thread::JoinHandle;
    use std::time::Duration;

    const ACCENT: Color32 = Color32::from_rgb(237, 189, 92);
    const BACKGROUND: Color32 = Color32::from_rgb(16, 15, 24);
    const PANEL: Color32 = Color32::from_rgb(28, 25, 40);
    const CARD: Color32 = Color32::from_rgb(40, 35, 56);
    const MUTED: Color32 = Color32::from_rgb(150, 145, 165);

    #[derive(Clone, Copy, PartialEq, Eq)]
    enum Screen {
        Home,
        Dc2,
    }

    #[derive(Clone, Copy, PartialEq, Eq)]
    enum Tab {
        Data,
        Settings,
        Saves,
        Album,
    }

    enum Confirm {
        DeleteSave(String),
        DeletePhoto(i32, i32),
    }

    /// Latest extraction progress; parallel completions can arrive out of order, so it never goes backwards.
    #[derive(Default)]
    struct Progress {
        fraction: f32,
        message: &'static str,
    }

    struct AlbumPhoto {
        card: i32,
        slot: i32,
        hd: bool,
        texture: egui::TextureHandle,
    }

    struct Launcher<'a> {
        backend: Box<dyn Backend + 'a>,
        title: String,
        screen: Screen,
        tab: Tab,
        play: Arc<AtomicBool>,
        // Game data.
        source: String,
        progress: Arc<Mutex<Progress>>,
        worker: Option<JoinHandle<CoreResult<core::ExtractReport>>>,
        data_ready: bool,
        // ChronicleTwo's data packs: what there is to copy (read once), and the copy under way.
        packs: Option<Option<LegacyPacks>>,
        packs_copied: Arc<AtomicU64>,
        packs_worker: Option<JoinHandle<std::io::Result<usize>>>,
        // What was last done, or went wrong.
        notice: Option<(String, bool)>,
        confirm: Option<Confirm>,
        saves: Option<Vec<Save>>,
        album: Option<Vec<AlbumPhoto>>,
        selected_photo: Option<(i32, i32)>,
    }

    impl<'a> Launcher<'a> {
        fn new(title: &str, backend: Box<dyn Backend + 'a>, play: Arc<AtomicBool>) -> Self {
            let data_ready = backend.data_ready();
            Self {
                backend,
                title: title.into(),
                screen: Screen::Home,
                tab: if data_ready { Tab::Settings } else { Tab::Data },
                play,
                source: String::new(),
                progress: Arc::new(Mutex::new(Progress::default())),
                worker: None,
                data_ready,
                packs: None,
                packs_copied: Arc::new(AtomicU64::new(0)),
                packs_worker: None,
                notice: None,
                confirm: None,
                saves: None,
                album: None,
                selected_photo: None,
            }
        }

        fn t(&self, key: &str) -> String {
            self.backend.text(key)
        }

        fn ok(&mut self, text: String) {
            self.notice = Some((text, false));
        }

        fn fail(&mut self, text: String) {
            self.notice = Some((text, true));
        }

        fn report(&mut self, result: Result<(), String>, done: &str) {
            match result {
                Ok(()) => {
                    let text = self.t(done);
                    self.ok(text)
                }
                Err(error) => self.fail(error),
            }
        }

        // --- Extraction, as the first-run setup window runs it ---

        fn start_extraction(&mut self) {
            let mut source = PathBuf::from(self.source.trim());
            if let Some(image) = lone_disc_image(&source) {
                self.source = image.to_string_lossy().into_owned();
                source = image;
            }
            if !source.is_dir() && !source.is_file() {
                let text = self.t("data.no_source");
                return self.fail(text);
            }
            match core::archive::disc_serial(&source) {
                Ok(serial) if !accepts_serial(serial.as_deref()) => {
                    let text = fill(&self.t("data.not_pal"), serial.unwrap_or_default());
                    return self.fail(text);
                }
                Err(error) => return self.fail(error.to_string()),
                Ok(_) => {}
            }
            self.notice = None;
            *self.progress.lock().unwrap() = Progress::default();
            let progress = self.progress.clone();
            let output = PathBuf::from(self.backend.data_root());
            let jobs = std::thread::available_parallelism().map_or(1, usize::from);
            match std::thread::Builder::new()
                .name("game-data-extraction".into())
                .stack_size(16 * 1024 * 1024)
                .spawn(move || {
                    core::extract_with_progress_policy(
                        &source,
                        &output,
                        jobs,
                        &|fraction, message| {
                            let mut progress = progress.lock().unwrap();
                            if fraction >= progress.fraction {
                                progress.fraction = fraction;
                                progress.message = message;
                            }
                        },
                        ScriptPublicationPolicy::Retain,
                    )
                }) {
                Ok(worker) => self.worker = Some(worker),
                Err(error) => self.fail(error.to_string()),
            }
        }

        /// Whether game files are being written, which holds the window open and the game back.
        fn busy(&self) -> bool {
            self.worker.is_some() || self.packs_worker.is_some()
        }

        fn start_packs_copy(&mut self) {
            let Some(Some(packs)) = &self.packs else {
                return;
            };
            if let Err(error) = self.backend.datapacks_room(packs.bytes) {
                self.fail(error);
                return;
            }
            let (from, to) = (PathBuf::from(&packs.from), PathBuf::from(&packs.to));
            let copied = Arc::clone(&self.packs_copied);
            copied.store(0, Ordering::Relaxed);
            self.notice = None;
            match std::thread::Builder::new()
                .name("launcher-packs".into())
                .spawn(move || copy_new_files(&from, &to, &copied))
            {
                Ok(worker) => self.packs_worker = Some(worker),
                Err(error) => self.fail(error.to_string()),
            }
        }

        fn poll_packs_copy(&mut self) {
            if !self
                .packs_worker
                .as_ref()
                .is_some_and(|worker| worker.is_finished())
            {
                return;
            }
            match self.packs_worker.take().unwrap().join() {
                Ok(Ok(count)) => {
                    let text = fill(&self.t("data.packs_done"), count);
                    self.ok(text)
                }
                Ok(Err(error)) => {
                    let text = format!("{}\n{error}", self.t("error.copy"));
                    self.fail(text)
                }
                Err(_) => {
                    let text = self.t("error.copy");
                    self.fail(text)
                }
            }
            self.packs = None;
        }

        fn poll_extraction(&mut self) {
            if !self
                .worker
                .as_ref()
                .is_some_and(|worker| worker.is_finished())
            {
                return;
            }
            let result = self.worker.take().unwrap().join();
            self.data_ready = self.backend.data_ready();
            match result {
                Ok(Ok(_)) if self.data_ready => {
                    let text = self.t("data.done");
                    self.ok(text)
                }
                Ok(Ok(_)) => {
                    let text = self.t("data.incomplete");
                    self.fail(text)
                }
                Ok(Err(error)) => self.fail(error.to_string()),
                Err(_) => {
                    let text = self.t("data.stopped");
                    self.fail(text)
                }
            }
        }

        // --- Screens ---

        fn home(&mut self, ui: &mut egui::Ui) {
            ui.add_space(24.0);
            ui.vertical_centered(|ui| {
                ui.label(RichText::new("OpenCloud").size(40.0).strong().color(ACCENT));
                ui.label(RichText::new(self.t("home.choose")).size(18.0).color(MUTED));
            });
            ui.add_space(32.0);
            let width = ((ui.available_width() - 64.0) / 2.0).clamp(220.0, 360.0);
            let height = 220.0;
            let left = (ui.available_width() - width * 2.0 - 32.0).max(0.0) / 2.0;
            ui.horizontal(|ui| {
                ui.add_space(left);
                game_card(
                    ui,
                    Vec2::new(width, height),
                    "Dark Cloud",
                    &self.t("home.coming_soon"),
                    false,
                );
                ui.add_space(32.0);
                let subtitle = self.t("home.dc2_subtitle");
                if game_card(
                    ui,
                    Vec2::new(width, height),
                    "Dark Cloud 2",
                    &subtitle,
                    true,
                )
                .clicked()
                {
                    self.screen = Screen::Dc2;
                    self.notice = None;
                }
            });
        }

        fn header(&mut self, ui: &mut egui::Ui) {
            ui.horizontal(|ui| {
                if self.screen == Screen::Dc2 {
                    let back = ui.add_enabled(
                        self.worker.is_none(),
                        egui::Button::new(format!("‹  {}", self.t("nav.back"))),
                    );
                    if back.clicked() {
                        self.screen = Screen::Home;
                        self.notice = None;
                    }
                    ui.add_space(8.0);
                    ui.label(RichText::new("Dark Cloud 2").size(20.0).strong());
                } else {
                    ui.label(RichText::new(&self.title).size(16.0).color(MUTED));
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    let languages = self.backend.languages();
                    let current = self
                        .backend
                        .language()
                        .min(languages.len().saturating_sub(1));
                    let mut chosen = current;
                    egui::ComboBox::new("language", "")
                        .selected_text(languages.get(current).cloned().unwrap_or_default())
                        .show_ui(ui, |ui| {
                            for (index, name) in languages.iter().enumerate() {
                                ui.selectable_value(&mut chosen, index, name);
                            }
                        });
                    ui.label(RichText::new(self.t("language")).color(MUTED));
                    if chosen != current {
                        self.backend.set_language(chosen);
                        // Texts built from the old language.
                        self.saves = None;
                        self.notice = None;
                    }
                });
            });
        }

        fn sidebar(&mut self, ui: &mut egui::Ui) {
            ui.add_space(8.0);
            let busy = self.busy();
            for (tab, key) in [
                (Tab::Data, "tab.data"),
                (Tab::Settings, "tab.settings"),
                (Tab::Saves, "tab.saves"),
                (Tab::Album, "tab.album"),
            ] {
                let button = egui::Button::new(RichText::new(self.t(key)).size(16.0))
                    .selected(self.tab == tab)
                    .min_size(Vec2::new(ui.available_width(), 40.0))
                    .corner_radius(CornerRadius::same(8));
                if ui.add_enabled(!busy || tab == Tab::Data, button).clicked() && self.tab != tab {
                    self.tab = tab;
                    self.notice = None;
                    self.selected_photo = None;
                }
                ui.add_space(4.0);
            }
            ui.with_layout(egui::Layout::bottom_up(egui::Align::Center), |ui| {
                ui.add_space(8.0);
                let ready = self.data_ready && !busy;
                let play = egui::Button::new(
                    RichText::new(format!("▶  {}", self.t("nav.play")))
                        .size(20.0)
                        .strong()
                        .color(if ready { Color32::BLACK } else { MUTED }),
                )
                .fill(if ready { ACCENT } else { CARD })
                .min_size(Vec2::new(ui.available_width(), 52.0))
                .corner_radius(CornerRadius::same(10));
                let response = ui.add_enabled(ready, play);
                if response.clicked() {
                    self.play.store(true, Ordering::Release);
                    ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
                }
                if !self.data_ready {
                    ui.label(RichText::new(self.t("data.need_data")).color(MUTED).small());
                }
            });
        }

        fn data_tab(&mut self, ui: &mut egui::Ui) {
            let busy = self.worker.is_some();
            section_title(ui, &self.t("tab.data"));
            let (icon, colour, text) = if self.data_ready {
                ("✔", Color32::from_rgb(110, 200, 120), self.t("data.ready"))
            } else {
                ("●", ACCENT, self.t("data.missing"))
            };
            ui.horizontal_wrapped(|ui| {
                ui.label(RichText::new(icon).color(colour).size(18.0));
                ui.label(RichText::new(text).size(15.0));
            });
            ui.add_space(16.0);
            ui.add_enabled_ui(!busy, |ui| {
                ui.label(RichText::new(self.t("data.source")).color(MUTED));
                ui.horizontal(|ui| {
                    ui.add(
                        egui::TextEdit::singleline(&mut self.source)
                            .desired_width((ui.available_width() - 330.0).max(160.0)),
                    );
                    if ui.button(self.t("data.choose_iso")).clicked() {
                        let mut dialog = rfd::FileDialog::new()
                            .set_title(self.t("data.choose_iso"))
                            .add_filter("ISO", &["iso"]);
                        if let Some(folder) = Path::new(&self.source)
                            .parent()
                            .filter(|folder| folder.is_dir())
                        {
                            dialog = dialog.set_directory(folder);
                        }
                        if let Some(path) = dialog.pick_file() {
                            self.source = path.to_string_lossy().into_owned();
                            self.notice = None;
                        }
                    }
                    if ui.button(self.t("data.choose_folder")).clicked()
                        && let Some(path) = rfd::FileDialog::new()
                            .set_title(self.t("data.choose_folder"))
                            .pick_folder()
                    {
                        self.source = path.to_string_lossy().into_owned();
                        self.notice = None;
                    }
                });
                ui.label(RichText::new(self.t("data.hint")).color(MUTED).small());
            });
            ui.add_space(12.0);
            if busy {
                let progress = self.progress.lock().unwrap();
                ui.add(
                    egui::ProgressBar::new(progress.fraction)
                        .show_percentage()
                        .fill(ACCENT),
                );
                ui.label(if progress.message.is_empty() {
                    "…"
                } else {
                    progress.message
                });
                drop(progress);
                ui.label(RichText::new(self.t("data.keep_open")).color(MUTED).small());
            } else {
                let label = if self.data_ready {
                    self.t("data.extract_again")
                } else {
                    self.t("data.extract")
                };
                let button = egui::Button::new(RichText::new(label).size(16.0))
                    .min_size(Vec2::new(200.0, 36.0));
                if ui
                    .add_enabled(
                        !self.source.trim().is_empty() && self.packs_worker.is_none(),
                        button,
                    )
                    .clicked()
                {
                    self.start_extraction();
                }
            }
            self.packs_card(ui);
        }

        /// Offers ChronicleTwo's data packs (upscaled textures), copied on request for their size.
        fn packs_card(&mut self, ui: &mut egui::Ui) {
            if self.packs.is_none() {
                self.packs = Some(self.backend.legacy_datapacks());
            }
            let Some(Some(packs)) = &self.packs else {
                return;
            };
            let (from, bytes) = (packs.from.clone(), packs.bytes);
            ui.add_space(20.0);
            egui::Frame::new()
                .fill(CARD)
                .corner_radius(8)
                .inner_margin(Margin::same(12))
                .show(ui, |ui| {
                    ui.label(RichText::new(self.t("data.packs")).size(16.0).strong());
                    ui.label(fill(&self.t("data.packs_body"), size_text(bytes)));
                    ui.label(RichText::new(from).color(MUTED).small());
                    ui.add_space(6.0);
                    if self.packs_worker.is_some() {
                        let copied = self.packs_copied.load(Ordering::Relaxed);
                        ui.add(
                            egui::ProgressBar::new(copied as f32 / bytes.max(1) as f32)
                                .show_percentage()
                                .fill(ACCENT),
                        );
                    } else if ui
                        .add_enabled(
                            self.worker.is_none(),
                            egui::Button::new(self.t("data.packs_copy")),
                        )
                        .clicked()
                    {
                        self.start_packs_copy();
                    }
                });
        }

        fn settings_tab(&mut self, ui: &mut egui::Ui) {
            section_title(ui, &self.t("tab.settings"));
            ui.label(RichText::new(self.t("settings.note")).color(MUTED));
            ui.add_space(8.0);
            let settings = self.backend.settings();
            let mut step = None;
            egui::ScrollArea::vertical()
                .auto_shrink([false, false])
                .show(ui, |ui| {
                    let mut page = None;
                    for setting in &settings {
                        if page != Some(setting.page) {
                            page = Some(setting.page);
                            ui.add_space(10.0);
                            ui.label(
                                RichText::new(&setting.page_name)
                                    .strong()
                                    .color(ACCENT)
                                    .size(15.0),
                            );
                            ui.separator();
                        }
                        ui.horizontal(|ui| {
                            ui.vertical(|ui| {
                                ui.set_width((ui.available_width() - 240.0).max(200.0));
                                ui.label(RichText::new(&setting.label).size(15.0));
                                ui.label(
                                    RichText::new(setting.help.replace('\n', " "))
                                        .color(MUTED)
                                        .small(),
                                );
                            });
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    if ui
                                        .add(egui::Button::new("›").min_size(Vec2::new(32.0, 28.0)))
                                        .clicked()
                                    {
                                        step = Some((setting.page, setting.row, 1));
                                    }
                                    ui.add_sized(
                                        [150.0, 28.0],
                                        egui::Label::new(RichText::new(&setting.value).strong()),
                                    );
                                    if ui
                                        .add(egui::Button::new("‹").min_size(Vec2::new(32.0, 28.0)))
                                        .clicked()
                                    {
                                        step = Some((setting.page, setting.row, -1));
                                    }
                                },
                            );
                        });
                        ui.add_space(4.0);
                    }
                });
            if let Some((page, row, direction)) = step
                && self.backend.step_setting(page, row, direction) == 2
            {
                let text = self.t("settings.not_saved");
                self.fail(text);
            }
        }

        fn saves_tab(&mut self, ui: &mut egui::Ui) {
            section_title(ui, &self.t("tab.saves"));
            let protected = self.backend.saves_protected();
            ui.horizontal(|ui| {
                if ui.button(self.t("saves.import")).clicked()
                    && let Some(path) = rfd::FileDialog::new()
                        .add_filter("JSON", &["json"])
                        .pick_file()
                {
                    let result = self.backend.import_save(&path.to_string_lossy());
                    self.report(result, "saves.imported_one");
                    self.saves = None;
                }
                if ui.button(self.t("saves.open_folder")).clicked() {
                    open_folder(&self.backend.saves_folder());
                }
            });
            if !protected {
                ui.add_space(6.0);
                egui::Frame::new()
                    .fill(CARD)
                    .corner_radius(8)
                    .inner_margin(Margin::same(10))
                    .show(ui, |ui| {
                        let mut source = None;
                        ui.horizontal_wrapped(|ui| {
                            if ui.button(self.t("saves.ps2_import")).clicked() {
                                source = rfd::FileDialog::new()
                                    .set_title(self.t("saves.ps2_import"))
                                    .add_filter("PS2", &["ps2", "psu"])
                                    .pick_file();
                            }
                            if ui.button(self.t("saves.ps2_import_folder")).clicked() {
                                source = rfd::FileDialog::new()
                                    .set_title(self.t("saves.ps2_import_folder"))
                                    .pick_folder();
                            }
                        });
                        ui.label(RichText::new(self.t("saves.ps2_help")).color(MUTED).small());
                        if let Some(path) = source {
                            match self.backend.import_ps2(&path.to_string_lossy()) {
                                Ok(report) => self.ok(report),
                                Err(error) => self.fail(error),
                            }
                            self.saves = None;
                            self.album = None;
                        }
                    });
            }
            let legacy = self.backend.legacy_save_count();
            if legacy > 0 {
                ui.add_space(6.0);
                egui::Frame::new()
                    .fill(CARD)
                    .corner_radius(8)
                    .inner_margin(Margin::same(10))
                    .show(ui, |ui| {
                        ui.horizontal_wrapped(|ui| {
                            ui.label(fill(&self.t("saves.legacy"), legacy));
                            if ui.button(self.t("saves.legacy_import")).clicked() {
                                match self.backend.import_legacy() {
                                    Ok(count) => {
                                        let text = fill(&self.t("saves.legacy_done"), count);
                                        self.ok(text)
                                    }
                                    Err(error) => self.fail(error),
                                }
                                self.saves = None;
                                self.album = None;
                            }
                        });
                        ui.label(
                            RichText::new(self.backend.legacy_folder())
                                .color(MUTED)
                                .small(),
                        );
                    });
            }
            if protected {
                ui.add_space(6.0);
                ui.label(RichText::new(self.t("saves.protected")).color(ACCENT));
            }
            ui.add_space(8.0);
            if self.saves.is_none() {
                self.saves = Some(self.backend.saves());
            }
            let saves = self.saves.take().unwrap_or_default();
            if saves.is_empty() {
                ui.label(RichText::new(self.t("saves.none")).color(MUTED));
            }
            let mut action: Option<(&'static str, String)> = None;
            egui::ScrollArea::vertical()
                .auto_shrink([false, false])
                .show(ui, |ui| {
                    for save in &saves {
                        egui::Frame::new()
                            .fill(PANEL)
                            .corner_radius(8)
                            .inner_margin(Margin::same(10))
                            .show(ui, |ui| {
                                ui.set_width(ui.available_width());
                                ui.horizontal(|ui| {
                                    ui.vertical(|ui| {
                                        let name = if save.autosave {
                                            self.t("saves.autosave")
                                        } else {
                                            fill(&self.t("saves.slot"), save.number + 1)
                                        };
                                        ui.label(RichText::new(name).strong().size(16.0));
                                        if save.readable {
                                            ui.label(format!(
                                                "{}  ·  {} {}",
                                                save.chapter,
                                                self.t("saves.play_time"),
                                                play_time(save.play_seconds)
                                            ));
                                        } else {
                                            ui.label(
                                                RichText::new(&save.status)
                                                    .color(Color32::LIGHT_RED),
                                            );
                                        }
                                        ui.label(
                                            RichText::new(format!(
                                                "{} {}",
                                                self.t("saves.modified"),
                                                save.modified
                                            ))
                                            .color(MUTED)
                                            .small(),
                                        );
                                    });
                                    ui.with_layout(
                                        egui::Layout::right_to_left(egui::Align::Center),
                                        |ui| {
                                            if ui
                                                .add_enabled(
                                                    !protected,
                                                    egui::Button::new(self.t("saves.delete")),
                                                )
                                                .clicked()
                                            {
                                                action = Some(("delete", save.file.clone()));
                                            }
                                            if ui.button(self.t("saves.export")).clicked() {
                                                action = Some(("export", save.file.clone()));
                                            }
                                            if ui.button(self.t("saves.duplicate")).clicked() {
                                                action = Some(("duplicate", save.file.clone()));
                                            }
                                        },
                                    );
                                });
                            });
                        ui.add_space(6.0);
                    }
                });
            self.saves = Some(saves);
            match action {
                Some(("delete", file)) => self.confirm = Some(Confirm::DeleteSave(file)),
                Some(("export", file)) => {
                    if let Some(path) = rfd::FileDialog::new()
                        .set_file_name(&file)
                        .add_filter("JSON", &["json"])
                        .save_file()
                    {
                        let result = self.backend.export_save(&file, &path.to_string_lossy());
                        self.report(result, "saves.exported");
                    }
                }
                Some((_, file)) => {
                    let result = self.backend.duplicate_save(&file);
                    self.report(result, "saves.duplicated");
                    self.saves = None;
                }
                None => {}
            }
        }

        fn album_tab(&mut self, ui: &mut egui::Ui) {
            section_title(ui, &self.t("tab.album"));
            if self.album.is_none() {
                let photos = self.backend.photos();
                self.album = Some(
                    photos
                        .into_iter()
                        .map(|photo| AlbumPhoto {
                            card: photo.card,
                            slot: photo.slot,
                            hd: photo.hd,
                            texture: ui.ctx().load_texture(
                                format!("photo-{}-{}", photo.card, photo.slot),
                                egui::ColorImage::from_rgba_unmultiplied(
                                    [photo.width as usize, photo.height as usize],
                                    &photo.rgba,
                                ),
                                egui::TextureOptions::LINEAR,
                            ),
                        })
                        .collect(),
                );
            }
            let protected = self.backend.saves_protected();
            let album = self.album.take().unwrap_or_default();
            if album.is_empty() {
                ui.label(RichText::new(self.t("album.none")).color(MUTED));
            } else {
                ui.horizontal(|ui| {
                    ui.label(RichText::new(fill(&self.t("album.count"), album.len())).color(MUTED));
                    if ui.button(self.t("album.export_all")).clicked()
                        && let Some(folder) = rfd::FileDialog::new().pick_folder()
                    {
                        let mut result = Ok(());
                        for photo in &album {
                            let path = folder.join(photo_name(photo.card, photo.slot));
                            result = result.and(self.backend.export_photo(
                                photo.card,
                                photo.slot,
                                &path.to_string_lossy(),
                            ));
                        }
                        self.report(result, "album.exported");
                    }
                });
            }
            ui.add_space(8.0);
            let mut action: Option<(&'static str, i32, i32)> = None;
            let selected = self.selected_photo;
            let preview_width = if selected.is_some() { 300.0 } else { 0.0 };
            ui.horizontal_top(|ui| {
                egui::ScrollArea::vertical()
                    .max_width(ui.available_width() - preview_width)
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        ui.horizontal_wrapped(|ui| {
                            for photo in &album {
                                let image = egui::Image::from_texture(&photo.texture)
                                    .fit_to_exact_size(Vec2::splat(112.0))
                                    .corner_radius(6);
                                let chosen = selected == Some((photo.card, photo.slot));
                                let response = ui.add(egui::Button::image(image).selected(chosen));
                                if response.clicked() {
                                    action = Some(("select", photo.card, photo.slot));
                                }
                            }
                        });
                    });
                if let Some(photo) = selected.and_then(|(card, slot)| {
                    album
                        .iter()
                        .find(|photo| photo.card == card && photo.slot == slot)
                }) {
                    ui.vertical(|ui| {
                        ui.set_width(preview_width - 12.0);
                        ui.add(
                            egui::Image::from_texture(&photo.texture)
                                .fit_to_exact_size(Vec2::splat(256.0))
                                .corner_radius(8),
                        );
                        ui.label(
                            RichText::new(format!(
                                "{}  ·  {}{}",
                                fill(&self.t("album.card"), photo.card + 1),
                                fill(&self.t("album.photo"), photo.slot + 1),
                                if photo.hd { "  ·  HD" } else { "" }
                            ))
                            .color(MUTED),
                        );
                        ui.horizontal(|ui| {
                            if ui.button(self.t("album.export")).clicked() {
                                action = Some(("export", photo.card, photo.slot));
                            }
                            if ui
                                .add_enabled(!protected, egui::Button::new(self.t("album.delete")))
                                .clicked()
                            {
                                action = Some(("delete", photo.card, photo.slot));
                            }
                        });
                    });
                }
            });
            self.album = Some(album);
            match action {
                Some(("select", card, slot)) => self.selected_photo = Some((card, slot)),
                Some(("export", card, slot)) => {
                    if let Some(path) = rfd::FileDialog::new()
                        .set_file_name(photo_name(card, slot))
                        .add_filter("PNG", &["png"])
                        .save_file()
                    {
                        let result = self
                            .backend
                            .export_photo(card, slot, &path.to_string_lossy());
                        self.report(result, "album.exported");
                    }
                }
                Some((_, card, slot)) => self.confirm = Some(Confirm::DeletePhoto(card, slot)),
                None => {}
            }
        }

        fn confirm_dialog(&mut self, ctx: &egui::Context) {
            let Some(confirm) = &self.confirm else { return };
            let question = match confirm {
                Confirm::DeleteSave(_) => self.t("saves.confirm_delete"),
                Confirm::DeletePhoto(..) => self.t("album.confirm_delete"),
            };
            let mut answer = None;
            let modal = egui::Modal::new(egui::Id::new("confirm")).show(ctx, |ui| {
                ui.set_width(360.0);
                ui.label(RichText::new(question).size(16.0));
                ui.add_space(12.0);
                ui.horizontal(|ui| {
                    if ui
                        .add(
                            egui::Button::new(
                                RichText::new(self.t("confirm.delete")).color(Color32::WHITE),
                            )
                            .fill(Color32::from_rgb(170, 50, 50)),
                        )
                        .clicked()
                    {
                        answer = Some(true);
                    }
                    if ui.button(self.t("confirm.cancel")).clicked() {
                        answer = Some(false);
                    }
                });
            });
            if modal.should_close() && answer.is_none() {
                answer = Some(false);
            }
            match (answer, self.confirm.take()) {
                (Some(true), Some(Confirm::DeleteSave(file))) => {
                    let result = self.backend.delete_save(&file);
                    self.report(result, "saves.deleted");
                    self.saves = None;
                }
                (Some(true), Some(Confirm::DeletePhoto(card, slot))) => {
                    let result = self.backend.delete_photo(card, slot);
                    self.report(result, "album.deleted");
                    self.album = None;
                    self.selected_photo = None;
                }
                (Some(_), _) => {}
                (None, pending) => self.confirm = pending,
            }
        }
    }

    impl eframe::App for Launcher<'_> {
        fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
            let ctx = ui.ctx().clone();
            self.poll_extraction();
            self.poll_packs_copy();
            let busy = self.busy();
            if busy && ctx.input(|input| input.viewport().close_requested()) {
                ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
            }
            egui::Panel::top("header")
                .frame(
                    egui::Frame::new()
                        .fill(PANEL)
                        .inner_margin(Margin::symmetric(16, 10)),
                )
                .show(ui, |ui| self.header(ui));
            if let Some((text, error)) = self.notice.clone() {
                egui::Panel::bottom("notice")
                    .frame(
                        egui::Frame::new()
                            .fill(if error {
                                Color32::from_rgb(90, 30, 34)
                            } else {
                                Color32::from_rgb(30, 70, 44)
                            })
                            .inner_margin(Margin::symmetric(16, 8)),
                    )
                    .show(ui, |ui| {
                        ui.horizontal(|ui| {
                            ui.label(RichText::new(text).color(Color32::WHITE));
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    if ui.small_button("✕").clicked() {
                                        self.notice = None;
                                    }
                                },
                            );
                        });
                    });
            }
            if self.screen == Screen::Dc2 {
                egui::Panel::left("tabs")
                    .exact_size(220.0)
                    .resizable(false)
                    .frame(
                        egui::Frame::new()
                            .fill(PANEL)
                            .inner_margin(Margin::same(12)),
                    )
                    .show(ui, |ui| self.sidebar(ui));
            }
            egui::CentralPanel::default()
                .frame(
                    egui::Frame::new()
                        .fill(BACKGROUND)
                        .inner_margin(Margin::same(20)),
                )
                .show(ui, |ui| match self.screen {
                    Screen::Home => self.home(ui),
                    Screen::Dc2 => match self.tab {
                        Tab::Data => self.data_tab(ui),
                        Tab::Settings => self.settings_tab(ui),
                        Tab::Saves => self.saves_tab(ui),
                        Tab::Album => self.album_tab(ui),
                    },
                });
            self.confirm_dialog(&ctx);
            if busy {
                ctx.request_repaint_after(Duration::from_millis(100));
            }
        }
    }

    impl Drop for Launcher<'_> {
        fn drop(&mut self) {
            // A graphics or event-loop error must not leave a detached publishing worker.
            if let Some(worker) = self.worker.take() {
                let _ = worker.join();
            }
            if let Some(worker) = self.packs_worker.take() {
                let _ = worker.join();
            }
        }
    }

    /// A size in MB, or GB from one up.
    fn size_text(bytes: u64) -> String {
        const MB: f64 = 1024.0 * 1024.0;
        let megabytes = bytes as f64 / MB;
        if megabytes >= 1024.0 {
            format!("{:.1} GB", megabytes / 1024.0)
        } else {
            format!("{:.0} MB", megabytes.max(1.0))
        }
    }

    /// Copies every file under from that is not yet under to, counting the bytes into copied; files already there
    /// are kept. Returns how many files were copied.
    fn copy_new_files(from: &Path, to: &Path, copied: &AtomicU64) -> std::io::Result<usize> {
        let mut count = 0;
        let mut folders = vec![from.to_path_buf()];
        while let Some(folder) = folders.pop() {
            let target = to.join(folder.strip_prefix(from).unwrap_or(Path::new("")));
            std::fs::create_dir_all(&target)?;
            for entry in std::fs::read_dir(&folder)? {
                let entry = entry?;
                let kind = entry.file_type()?;
                if kind.is_dir() {
                    folders.push(entry.path());
                } else if kind.is_file() {
                    let destination = target.join(entry.file_name());
                    let length = entry.metadata()?.len();
                    if !destination.exists() {
                        // Written aside first so an interrupted copy never leaves a short file in place.
                        let mut partial = destination.clone().into_os_string();
                        partial.push(".partial");
                        let partial = PathBuf::from(partial);
                        std::fs::copy(entry.path(), &partial)?;
                        std::fs::rename(&partial, &destination)?;
                        count += 1;
                    }
                    copied.fetch_add(length, Ordering::Relaxed);
                }
            }
        }
        Ok(count)
    }

    fn section_title(ui: &mut egui::Ui, text: &str) {
        ui.label(RichText::new(text).size(24.0).strong());
        ui.add_space(8.0);
    }

    /// A game's tile on the home screen; a game not yet playable is drawn grey and does not respond.
    fn game_card(
        ui: &mut egui::Ui,
        size: Vec2,
        title: &str,
        subtitle: &str,
        enabled: bool,
    ) -> egui::Response {
        let sense = if enabled {
            egui::Sense::click()
        } else {
            egui::Sense::hover()
        };
        let (rect, response) = ui.allocate_exact_size(size, sense);
        let hovered = enabled && response.hovered();
        let painter = ui.painter();
        let (fill, stroke, text) = match (enabled, hovered) {
            (false, _) => (
                Color32::from_rgb(34, 33, 38),
                Color32::from_rgb(60, 58, 66),
                Color32::from_rgb(95, 93, 102),
            ),
            (true, false) => (CARD, Color32::from_rgb(90, 78, 120), Color32::WHITE),
            (true, true) => (Color32::from_rgb(52, 45, 74), ACCENT, Color32::WHITE),
        };
        painter.rect(
            rect,
            CornerRadius::same(16),
            fill,
            Stroke::new(2.0, stroke),
            egui::StrokeKind::Inside,
        );
        painter.text(
            rect.center() - Vec2::new(0.0, 18.0),
            egui::Align2::CENTER_CENTER,
            title,
            egui::FontId::proportional(30.0),
            text,
        );
        painter.text(
            rect.center() + Vec2::new(0.0, 24.0),
            egui::Align2::CENTER_CENTER,
            subtitle,
            egui::FontId::proportional(15.0),
            if enabled { ACCENT } else { text },
        );
        if enabled {
            response.on_hover_cursor(egui::CursorIcon::PointingHand)
        } else {
            response
        }
    }

    fn photo_name(card: i32, slot: i32) -> String {
        format!("dark-cloud-2-album-{}-{:02}.png", card + 1, slot + 1)
    }

    fn open_folder(folder: &str) {
        #[cfg(windows)]
        let program = "explorer";
        #[cfg(target_os = "macos")]
        let program = "open";
        #[cfg(not(any(windows, target_os = "macos")))]
        let program = "xdg-open";
        let _ = std::process::Command::new(program).arg(folder).spawn();
    }

    /// The disc image a folder stands for: its only `.iso` file, when the folder holds no `DATA.DAT` of its own.
    fn lone_disc_image(folder: &Path) -> Option<PathBuf> {
        let mut disc_files = false;
        let mut images = Vec::new();
        for path in std::fs::read_dir(folder)
            .ok()?
            .filter_map(|entry| Some(entry.ok()?.path()))
        {
            disc_files |= path
                .file_name()
                .is_some_and(|name| name.eq_ignore_ascii_case("DATA.DAT"));
            if path.is_file()
                && path
                    .extension()
                    .is_some_and(|ext| ext.eq_ignore_ascii_case("iso"))
            {
                images.push(path);
            }
        }
        match images.as_slice() {
            [image] if !disc_files => Some(image.clone()),
            _ => None,
        }
    }

    fn style(ctx: &egui::Context) {
        ctx.set_theme(egui::ThemePreference::Dark);
        ctx.all_styles_mut(|style| {
            style.spacing.button_padding = Vec2::new(12.0, 6.0);
            style.spacing.item_spacing = Vec2::new(8.0, 6.0);
            let visuals = &mut style.visuals;
            visuals.panel_fill = BACKGROUND;
            visuals.window_fill = PANEL;
            visuals.selection.bg_fill = ACCENT.linear_multiply(0.45);
            visuals.selection.stroke = Stroke::new(1.0, ACCENT);
            visuals.hyperlink_color = ACCENT;
            for widget in [
                &mut visuals.widgets.inactive,
                &mut visuals.widgets.hovered,
                &mut visuals.widgets.active,
            ] {
                widget.corner_radius = CornerRadius::same(6);
            }
        });
    }

    /// Runs on the main thread and returns once a game is started (true) or the window is closed (false).
    pub fn run(title: &str, backend: Box<dyn Backend + '_>) -> Result<bool, CoreError> {
        let play = Arc::new(AtomicBool::new(false));
        let app = Launcher::new(title, backend, play.clone());
        let options = eframe::NativeOptions {
            viewport: egui::ViewportBuilder::default()
                .with_inner_size([1040.0, 660.0])
                .with_min_inner_size([820.0, 520.0])
                .with_icon(crate::setup::icon().unwrap_or_default()),
            centered: true,
            run_and_return: true,
            ..Default::default()
        };
        eframe::run_native(
            title,
            options,
            Box::new(move |creation| {
                style(&creation.egui_ctx);
                Ok(Box::new(app))
            }),
        )
        .map_err(|error| CoreError(format!("Could not open the launcher: {error}")))?;
        Ok(play.load(Ordering::Acquire))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_the_pal_disc_is_accepted() {
        assert!(accepts_serial(Some("SCES_511.90")));
        assert!(accepts_serial(Some("sces_511.90")));
        assert!(accepts_serial(None));
        assert!(!accepts_serial(Some("SLUS_207.08")));
        assert!(!accepts_serial(Some("SCPS_150.31")));
    }

    #[test]
    fn play_time_is_hours_and_minutes() {
        assert_eq!(play_time(0), "0:00");
        assert_eq!(play_time(59), "0:00");
        assert_eq!(play_time(3600 + 5 * 60), "1:05");
        assert_eq!(play_time(100 * 3600), "100:00");
    }

    #[test]
    fn fill_replaces_the_first_placeholder() {
        assert_eq!(fill("Save {}", 3), "Save 3");
        assert_eq!(fill("No placeholder", 3), "No placeholder");
    }
}
