//! Çalan müzik bilgisi ve medya kontrolleri.
//!
//! Windows: Sistem Medya Taşıma Kontrolleri (SMTC). Spotify, tarayıcıda
//! YouTube, Apple Music, VLC gibi SMTC'ye kayıt olan her uygulama görünür.
//! macOS: Faz 3'te eklenecek; şimdilik hiçbir şey yayınlanmaz.
//!
//! Frontend'e iki olay gider:
//! - `media-update`  : her saniye (değiştiyse) şarkı bilgisi ve ilerleme
//! - `media-artwork` : yalnızca şarkı değişince, kapak (data URL)

use serde::Serialize;

#[cfg_attr(not(windows), allow(dead_code))]
#[derive(Clone, Serialize, PartialEq, Default, Debug)]
#[serde(rename_all = "camelCase")]
pub struct MediaState {
    /// Kontrol edilebilir bir medya oturumu var mı?
    pub active: bool,
    pub playing: bool,
    pub title: String,
    pub artist: String,
    pub album: String,
    /// Kaynak uygulama (ör. "Spotify.exe")
    pub source: String,
    pub position_ms: u64,
    pub duration_ms: u64,
    /// Şarkıyı ayırt etmek için anahtar (kaynak + sanatçı + başlık)
    pub track_key: String,
}

#[cfg_attr(not(windows), allow(dead_code))]
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MediaArtwork {
    pub track_key: String,
    /// `data:image/...;base64,...` ya da kapak yoksa boş
    pub data_url: String,
}

#[derive(Clone, Copy)]
pub enum MediaAction {
    PlayPause,
    Next,
    Previous,
}

#[tauri::command]
pub async fn media_control(action: String) -> Result<(), String> {
    let action = match action.as_str() {
        "playPause" => MediaAction::PlayPause,
        "next" => MediaAction::Next,
        "previous" => MediaAction::Previous,
        other => return Err(format!("bilinmeyen medya eylemi: {other}")),
    };
    // WinRT'nin bloklayan çağrıları ana iş parçacığını kilitlemesin.
    tauri::async_runtime::spawn_blocking(move || platform::control(action))
        .await
        .map_err(|e| e.to_string())?
}

pub fn start_watcher(app: tauri::AppHandle) {
    platform::start_watcher(app);
}

#[cfg(windows)]
mod platform {
    use super::{MediaAction, MediaArtwork, MediaState};
    use base64::Engine;
    use std::{thread, time::Duration};
    use tauri::{AppHandle, Emitter};
    use windows::Media::Control::{
        GlobalSystemMediaTransportControlsSession as Session,
        GlobalSystemMediaTransportControlsSessionManager as Manager,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus as Status,
    };
    use windows::Storage::Streams::DataReader;
    use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize};

    const POLL: Duration = Duration::from_millis(1000);
    const ARTWORK_TRIES: u8 = 3;
    /// TimeSpan 100 ns biriminde.
    const TICKS_PER_MS: i64 = 10_000;

    fn manager() -> windows::core::Result<Manager> {
        Manager::RequestAsync()?.join()
    }

    fn current_session() -> Option<Session> {
        manager().ok()?.GetCurrentSession().ok()
    }

    fn read_state(session: &Session) -> windows::core::Result<MediaState> {
        let props = session.TryGetMediaPropertiesAsync()?.join()?;
        let playback = session.GetPlaybackInfo()?;
        let timeline = session.GetTimelineProperties()?;

        let title = props.Title()?.to_string();
        let artist = props.Artist()?.to_string();
        let album = props.AlbumTitle()?.to_string();
        let source = session.SourceAppUserModelId()?.to_string();

        let start = timeline.StartTime()?.Duration;
        let end = timeline.EndTime()?.Duration;
        let pos = timeline.Position()?.Duration;
        let to_ms = |t: i64| (t.max(0) / TICKS_PER_MS) as u64;

        Ok(MediaState {
            active: true,
            playing: playback.PlaybackStatus()? == Status::Playing,
            track_key: format!("{source}|{artist}|{title}"),
            title,
            artist,
            album,
            source,
            position_ms: to_ms(pos - start),
            duration_ms: to_ms(end - start),
        })
    }

    fn read_artwork(session: &Session) -> windows::core::Result<String> {
        let props = session.TryGetMediaPropertiesAsync()?.join()?;
        let Ok(thumb) = props.Thumbnail() else {
            return Ok(String::new());
        };
        let stream = thumb.OpenReadAsync()?.join()?;
        let size = stream.Size()? as u32;
        if size == 0 {
            return Ok(String::new());
        }
        let content_type = stream.ContentType()?.to_string();
        let reader = DataReader::CreateDataReader(&stream)?;
        reader.LoadAsync(size)?.join()?;
        let mut bytes = vec![0u8; size as usize];
        reader.ReadBytes(&mut bytes)?;

        let mime = if content_type.is_empty() { "image/png".into() } else { content_type };
        let b64 = base64::engine::general_purpose::STANDARD.encode(bytes);
        Ok(format!("data:{mime};base64,{b64}"))
    }

    pub fn start_watcher(app: AppHandle) {
        thread::spawn(move || {
            unsafe {
                let _ = RoInitialize(RO_INIT_MULTITHREADED);
            }
            let mut last = MediaState::default();
            let mut artwork_key = String::new();
            let mut artwork_tries = 0u8;

            loop {
                let session = current_session();
                let state = session
                    .as_ref()
                    .and_then(|s| read_state(s).ok())
                    .unwrap_or_default();

                if state != last {
                    let _ = app.emit("media-update", &state);
                }

                // Bazı uygulamalar kapağı şarkıdan biraz sonra yayınlar;
                // boş gelirse birkaç tur daha dener, sonra vazgeçer.
                if state.active
                    && state.track_key != artwork_key
                    && let Some(s) = &session
                {
                    let data_url = read_artwork(s).unwrap_or_default();
                    artwork_tries += 1;
                    if !data_url.is_empty() || artwork_tries >= ARTWORK_TRIES {
                        artwork_key = state.track_key.clone();
                        artwork_tries = 0;
                    }
                    let _ = app.emit(
                        "media-artwork",
                        MediaArtwork { track_key: state.track_key.clone(), data_url },
                    );
                }

                last = state;
                thread::sleep(POLL);
            }
        });
    }

    pub fn control(action: MediaAction) -> Result<(), String> {
        let run = || -> windows::core::Result<()> {
            let Some(session) = current_session() else { return Ok(()) };
            match action {
                MediaAction::PlayPause => session.TryTogglePlayPauseAsync()?.join()?,
                MediaAction::Next => session.TrySkipNextAsync()?.join()?,
                MediaAction::Previous => session.TrySkipPreviousAsync()?.join()?,
            };
            Ok(())
        };
        run().map_err(|e| e.to_string())
    }
}

#[cfg(not(windows))]
mod platform {
    use super::MediaAction;
    use tauri::AppHandle;

    pub fn start_watcher(_app: AppHandle) {}

    pub fn control(_action: MediaAction) -> Result<(), String> {
        Ok(())
    }
}
