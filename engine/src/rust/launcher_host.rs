//! The bridge between the desktop launcher and the game that implements its `LauncherHost`.
#[cfg(feature = "desktop-ui")]
use crate::launcher::{Backend, LegacyPacks, Photo, Save, Setting};

// The C++ half stays out of unit tests, which link no C++ bridge.
// Without the desktop window (iOS) nothing calls the host.
#[cfg_attr(not(feature = "desktop-ui"), allow(dead_code))]
#[cxx::bridge(namespace = "opencloud")]
mod ffi {
    /// One steppable settings row, with its text in the launcher's language.
    struct LauncherSetting {
        page: i32,
        row: i32,
        page_name: String,
        label: String,
        help: String,
        value: String,
    }

    struct LauncherSave {
        file: String,
        number: i32,
        autosave: bool,
        readable: bool,
        status: String,
        chapter: String,
        play_seconds: u64,
        /// Local date and time of the last write.
        modified: String,
    }

    /// One photo of the memory card album, as RGBA8.
    struct LauncherPhoto {
        card: i32,
        slot: i32,
        hd: bool,
        width: u32,
        height: u32,
        rgba: Vec<u8>,
    }

    unsafe extern "C++" {
        include!("opencloud/launcher_host.hpp");

        type LauncherHost;
        fn text(self: &LauncherHost, key: &str) -> String;
        fn language(self: &LauncherHost) -> i32;
        fn language_count(self: &LauncherHost) -> i32;
        fn language_name(self: &LauncherHost, index: i32) -> String;
        fn set_language(self: &LauncherHost, index: i32);
        fn data_root(self: &LauncherHost) -> String;
        fn data_ready(self: &LauncherHost) -> bool;
        fn settings(self: &LauncherHost) -> Vec<LauncherSetting>;
        fn step_setting(self: &LauncherHost, page: i32, row: i32, direction: i32) -> i32;
        fn saves(self: &LauncherHost) -> Vec<LauncherSave>;
        fn saves_protected(self: &LauncherHost) -> bool;
        fn saves_folder(self: &LauncherHost) -> String;
        fn delete_save(self: &LauncherHost, file: &str) -> String;
        fn duplicate_save(self: &LauncherHost, file: &str) -> String;
        fn export_save(self: &LauncherHost, file: &str, destination: &str) -> String;
        fn import_save(self: &LauncherHost, source: &str) -> String;
        fn legacy_save_count(self: &LauncherHost) -> i32;
        fn legacy_folder(self: &LauncherHost) -> String;
        fn import_legacy(self: &LauncherHost, imported: &mut i32) -> String;
        fn legacy_datapacks_folder(self: &LauncherHost) -> String;
        fn legacy_datapack_bytes(self: &LauncherHost) -> u64;
        fn datapacks_folder(self: &LauncherHost) -> String;
        fn datapacks_room(self: &LauncherHost, bytes: u64) -> String;
        fn photos(self: &LauncherHost) -> Vec<LauncherPhoto>;
        fn export_photo(self: &LauncherHost, card: i32, slot: i32, destination: &str) -> String;
        fn delete_photo(self: &LauncherHost, card: i32, slot: i32) -> String;
    }

    extern "Rust" {
        /// Shows the launcher on the main thread until the player starts a game (true) or closes it (false).
        fn run_launcher(title: &str, host: &LauncherHost) -> Result<bool>;
    }
}

fn run_launcher(title: &str, host: &ffi::LauncherHost) -> Result<bool, crate::core::CoreError> {
    #[cfg(not(feature = "desktop-ui"))]
    {
        let _ = (title, host);
        Ok(false)
    }
    #[cfg(feature = "desktop-ui")]
    {
        crate::launcher::run(title, Box::new(Host(host)))
    }
}

/// The game's host, for the length of the launcher's window.
#[cfg(feature = "desktop-ui")]
struct Host<'a>(&'a ffi::LauncherHost);

#[cfg(feature = "desktop-ui")]
fn outcome(error: String) -> Result<(), String> {
    if error.is_empty() { Ok(()) } else { Err(error) }
}

#[cfg(feature = "desktop-ui")]
impl Backend for Host<'_> {
    fn text(&self, key: &str) -> String {
        self.0.text(key)
    }

    fn language(&self) -> usize {
        self.0.language().max(0) as usize
    }

    fn languages(&self) -> Vec<String> {
        (0..self.0.language_count())
            .map(|index| self.0.language_name(index))
            .collect()
    }

    fn set_language(&self, index: usize) {
        self.0.set_language(index as i32);
    }

    fn data_root(&self) -> String {
        self.0.data_root()
    }

    fn data_ready(&self) -> bool {
        self.0.data_ready()
    }

    fn settings(&self) -> Vec<Setting> {
        self.0
            .settings()
            .into_iter()
            .map(|row| Setting {
                page: row.page,
                row: row.row,
                page_name: row.page_name,
                label: row.label,
                help: row.help,
                value: row.value,
            })
            .collect()
    }

    fn step_setting(&self, page: i32, row: i32, direction: i32) -> i32 {
        self.0.step_setting(page, row, direction)
    }

    fn saves(&self) -> Vec<Save> {
        self.0
            .saves()
            .into_iter()
            .map(|save| Save {
                file: save.file,
                number: save.number,
                autosave: save.autosave,
                readable: save.readable,
                status: save.status,
                chapter: save.chapter,
                play_seconds: save.play_seconds,
                modified: save.modified,
            })
            .collect()
    }

    fn saves_protected(&self) -> bool {
        self.0.saves_protected()
    }

    fn saves_folder(&self) -> String {
        self.0.saves_folder()
    }

    fn delete_save(&self, file: &str) -> Result<(), String> {
        outcome(self.0.delete_save(file))
    }

    fn duplicate_save(&self, file: &str) -> Result<(), String> {
        outcome(self.0.duplicate_save(file))
    }

    fn export_save(&self, file: &str, destination: &str) -> Result<(), String> {
        outcome(self.0.export_save(file, destination))
    }

    fn import_save(&self, source: &str) -> Result<(), String> {
        outcome(self.0.import_save(source))
    }

    fn legacy_save_count(&self) -> usize {
        self.0.legacy_save_count().max(0) as usize
    }

    fn legacy_folder(&self) -> String {
        self.0.legacy_folder()
    }

    fn import_legacy(&self) -> Result<usize, String> {
        let mut imported = 0;
        outcome(self.0.import_legacy(&mut imported)).map(|()| imported.max(0) as usize)
    }

    fn legacy_datapacks(&self) -> Option<LegacyPacks> {
        let from = self.0.legacy_datapacks_folder();
        (!from.is_empty()).then(|| LegacyPacks {
            from,
            to: self.0.datapacks_folder(),
            bytes: self.0.legacy_datapack_bytes(),
        })
    }

    fn datapacks_room(&self, bytes: u64) -> Result<(), String> {
        outcome(self.0.datapacks_room(bytes))
    }

    fn photos(&self) -> Vec<Photo> {
        self.0
            .photos()
            .into_iter()
            .map(|photo| Photo {
                card: photo.card,
                slot: photo.slot,
                hd: photo.hd,
                width: photo.width,
                height: photo.height,
                rgba: photo.rgba,
            })
            .collect()
    }

    fn export_photo(&self, card: i32, slot: i32, destination: &str) -> Result<(), String> {
        outcome(self.0.export_photo(card, slot, destination))
    }

    fn delete_photo(&self, card: i32, slot: i32) -> Result<(), String> {
        outcome(self.0.delete_photo(card, slot))
    }
}
