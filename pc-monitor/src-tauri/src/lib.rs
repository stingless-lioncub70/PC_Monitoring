use std::sync::Mutex;
use tauri::{Manager, RunEvent};
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

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
  tauri::Builder::default()
    .plugin(tauri_plugin_shell::init())
    .manage(SidecarState(Mutex::new(None)))
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
