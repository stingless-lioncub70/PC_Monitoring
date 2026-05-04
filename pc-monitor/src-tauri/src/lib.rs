use std::sync::Mutex;
use serde::{Deserialize, Serialize};
use tauri::{Manager, PhysicalPosition, RunEvent, WindowEvent};
use tauri_plugin_shell::process::CommandChild;
use tauri_plugin_shell::ShellExt;

struct SidecarState(Mutex<Option<CommandChild>>);

/// Kill the sidecar AND any process it spawned (e.g. monitor.exe → sensors.exe).
/// `child.kill()` alone uses TerminateProcess, which doesn't reach grandchildren;
/// `taskkill /F /T /PID <pid>` walks the process tree.
fn kill_sidecar_tree(child: CommandChild) {
    let pid = child.pid();
    let _ = child.kill();
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x08000000;
        let _ = std::process::Command::new("taskkill")
            .args(["/F", "/T", "/PID", &pid.to_string()])
            .creation_flags(CREATE_NO_WINDOW)
            .status();
    }
    #[cfg(not(target_os = "windows"))]
    {
        let _ = pid; // unused on non-Windows
    }
}

#[tauri::command]
fn set_overlay_visible(app: tauri::AppHandle, visible: bool) -> Result<(), String> {
    if let Some(overlay) = app.get_webview_window("overlay") {
        if visible {
            overlay.show().map_err(|e| e.to_string())?;
        } else {
            overlay.hide().map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

/// Toggle whether the overlay window accepts mouse input.
/// HUD mode (interactive=false): click-through, can't be dragged or right-clicked.
/// Edit mode (interactive=true): receives drags + right-click; user can move it
/// or pick a preset.
#[tauri::command]
fn set_overlay_interactive(app: tauri::AppHandle, interactive: bool) -> Result<(), String> {
    if let Some(overlay) = app.get_webview_window("overlay") {
        overlay
            .set_ignore_cursor_events(!interactive)
            .map_err(|e| e.to_string())?;
        // Setting focusable doesn't toggle mid-session on every Tauri build,
        // but it's safe to call and helps drag handling on some Windows
        // versions. Errors here are non-fatal.
        let _ = overlay.set_focusable(interactive);
    }
    Ok(())
}

#[derive(Serialize, Deserialize, Clone, Copy)]
struct TaskbarRect {
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
    /// 0=left, 1=top, 2=right, 3=bottom (matches Windows ABE_* constants).
    edge: u32,
}

#[cfg(target_os = "windows")]
fn query_taskbar_rect() -> Option<TaskbarRect> {
    use std::mem::{size_of, zeroed};
    use windows_sys::Win32::UI::Shell::{
        SHAppBarMessage, ABM_GETTASKBARPOS, APPBARDATA,
    };

    unsafe {
        let mut data: APPBARDATA = zeroed();
        data.cbSize = size_of::<APPBARDATA>() as u32;
        let ok = SHAppBarMessage(ABM_GETTASKBARPOS, &mut data);
        if ok == 0 {
            return None;
        }
        Some(TaskbarRect {
            left: data.rc.left,
            top: data.rc.top,
            right: data.rc.right,
            bottom: data.rc.bottom,
            edge: data.uEdge,
        })
    }
}

#[cfg(not(target_os = "windows"))]
fn query_taskbar_rect() -> Option<TaskbarRect> {
    None
}

#[tauri::command]
fn get_taskbar_rect() -> Result<TaskbarRect, String> {
    query_taskbar_rect().ok_or_else(|| "taskbar query unavailable".to_string())
}

/// Compute (x, y) for a named preset on the overlay's current monitor.
fn compute_preset_position(
    overlay: &tauri::WebviewWindow,
    preset: &str,
) -> Result<(i32, i32), String> {
    let size = overlay.outer_size().map_err(|e| e.to_string())?;
    let monitor = overlay
        .current_monitor()
        .map_err(|e| e.to_string())?
        .ok_or("no current monitor")?;
    let mon_pos = monitor.position();
    let mon_size = monitor.size();
    let w = size.width as i32;
    let h = size.height as i32;
    let mw = mon_size.width as i32;
    let mh = mon_size.height as i32;
    let mx = mon_pos.x;
    let my = mon_pos.y;
    let pad = 8;

    let (x, y) = match preset {
        "top-left" => (mx + pad, my + pad),
        "top-right" => (mx + mw - w - pad, my + pad),
        "bottom-left" => (mx + pad, my + mh - h - pad),
        "bottom-right" => (mx + mw - w - pad, my + mh - h - pad),
        "above-tray" => {
            // Place just above-and-left of the taskbar's tray area.
            // Edge constants: 0=left, 1=top, 2=right, 3=bottom.
            if let Some(tb) = query_taskbar_rect() {
                match tb.edge {
                    3 /* bottom */ => (mx + mw - w - pad, tb.top - h - 4),
                    1 /* top */    => (mx + mw - w - pad, tb.bottom + 4),
                    0 /* left */   => (tb.right + 4, my + mh - h - pad),
                    2 /* right */  => (tb.left - w - 4, my + mh - h - pad),
                    _              => (mx + mw - w - pad, my + mh - h - 48 - pad),
                }
            } else {
                // Fallback: assume 48px bottom taskbar.
                (mx + mw - w - pad, my + mh - h - 48 - pad)
            }
        }
        _ => return Err(format!("unknown preset: {}", preset)),
    };
    Ok((x, y))
}

#[tauri::command]
fn move_overlay_to_preset(app: tauri::AppHandle, preset: String) -> Result<(), String> {
    let overlay = app
        .get_webview_window("overlay")
        .ok_or("no overlay window")?;
    let (x, y) = compute_preset_position(&overlay, &preset)?;
    overlay
        .set_position(PhysicalPosition::new(x, y))
        .map_err(|e| e.to_string())?;
    save_overlay_position(&app, x, y);
    Ok(())
}

#[derive(Serialize, Deserialize, Default)]
struct OverlayPosition {
    x: i32,
    y: i32,
}

fn pos_file_path(app: &tauri::AppHandle) -> Option<std::path::PathBuf> {
    app.path().app_data_dir().ok().map(|p| p.join("overlay-pos.json"))
}

fn save_overlay_position(app: &tauri::AppHandle, x: i32, y: i32) {
    let Some(path) = pos_file_path(app) else { return };
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let _ = std::fs::write(
        &path,
        serde_json::to_string(&OverlayPosition { x, y }).unwrap_or_default(),
    );
}

fn load_overlay_position(app: &tauri::AppHandle) -> Option<OverlayPosition> {
    let path = pos_file_path(app)?;
    let bytes = std::fs::read(&path).ok()?;
    serde_json::from_slice::<OverlayPosition>(&bytes).ok()
}

/// Validate a saved (x, y) is on one of the user's current monitors. Prevents
/// "overlay disappeared" when you unplug the monitor it was on.
fn position_is_visible(app: &tauri::AppHandle, x: i32, y: i32) -> bool {
    let Some(monitors) = app.available_monitors().ok() else { return false };
    monitors.iter().any(|m| {
        let p = m.position();
        let s = m.size();
        let (mx, my, mw, mh) = (p.x, p.y, s.width as i32, s.height as i32);
        // require the overlay's top-left to land inside SOME monitor with a
        // small margin so it's still grabbable.
        x >= mx - 50 && x < mx + mw - 50 && y >= my - 20 && y < my + mh - 20
    })
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
  tauri::Builder::default()
    .plugin(tauri_plugin_shell::init())
    .manage(SidecarState(Mutex::new(None)))
    .invoke_handler(tauri::generate_handler![
        set_overlay_visible,
        set_overlay_interactive,
        get_taskbar_rect,
        move_overlay_to_preset,
    ])
    .setup(|app| {
      if cfg!(debug_assertions) {
        app.handle().plugin(
          tauri_plugin_log::Builder::default()
            .level(log::LevelFilter::Info)
            .build(),
        )?;
      }

      let sidecar = app
        .shell()
        .sidecar("monitor")
        .expect("failed to resolve `monitor` sidecar (is it bundled?)")
        .spawn()
        .expect("failed to spawn monitor sidecar");

      let state = app.state::<SidecarState>();
      *state.0.lock().unwrap() = Some(sidecar.1);

      // Configure the overlay: start in HUD mode (click-through) and place
      // it at either the saved position or the "above-tray" default.
      if let Some(overlay) = app.get_webview_window("overlay") {
        let _ = overlay.set_ignore_cursor_events(true);
        let _ = overlay.set_focusable(false);

        let handle = app.handle().clone();
        let saved = load_overlay_position(&handle);
        let placed = match saved {
            Some(p) if position_is_visible(&handle, p.x, p.y) => {
                overlay.set_position(PhysicalPosition::new(p.x, p.y)).is_ok()
            }
            _ => false,
        };
        if !placed {
            if let Ok((x, y)) = compute_preset_position(&overlay, "above-tray") {
                let _ = overlay.set_position(PhysicalPosition::new(x, y));
            }
        }

        // Persist position changes (debounce-light: write on every Moved
        // event; serde_json + small file means it's cheap).
        let app_for_event = app.handle().clone();
        overlay.on_window_event(move |event| {
            if let WindowEvent::Moved(pos) = event {
                save_overlay_position(&app_for_event, pos.x, pos.y);
            }
        });
      }

      Ok(())
    })
    .build(tauri::generate_context!())
    .expect("error while building tauri application")
    .run(|app_handle, event| {
      if let RunEvent::ExitRequested { .. } | RunEvent::Exit = event {
        if let Some(state) = app_handle.try_state::<SidecarState>() {
          if let Some(child) = state.0.lock().unwrap().take() {
            kill_sidecar_tree(child);
          }
        }
      }
    });
}
