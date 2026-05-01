# SonicFlow / KhurramAudioRoute — Working Notes for Claude

WPF .NET 10 audio mixer using ManagedBass / NAudio / WPF-UI. System tray supported via `H.NotifyIcon.Wpf` (closing the window hides to tray; exit only via tray menu).

## MCP Workflow — Always Use These

This project is wired up to two MCPs that must be consulted before doing exploration by hand. They give faster, cheaper answers than `Grep` / `Read` over a 1500-node graph.

### 1. `codebase-memory` MCP — primary code-knowledge source

Project ID: `C-Users-KhurramShafique-OneDrive - Continental Expedited Services, Inc-Desktop-audio-audio-router-src-AudioRouterV2`

Use this **first** for anything that asks "where is X / what calls Y / what does this class do":

| Need | Tool |
| --- | --- |
| Find a symbol, class, or function | `mcp__codebase-memory__search_graph` |
| Read a function's source by qualified name | `mcp__codebase-memory__get_code_snippet` |
| High-level structure / packages / dependencies | `mcp__codebase-memory__get_architecture` |
| Free-text search across the code | `mcp__codebase-memory__search_code` |
| Trace dependencies between two symbols | `mcp__codebase-memory__trace_path` |
| Impact of a change since a ref | `mcp__codebase-memory__detect_changes` |
| Re-index after large edits | `mcp__codebase-memory__index_repository` (mode: `moderate` is the default) |

Re-index whenever the file count or class count changes meaningfully (new feature, large refactor). Skip for one-line fixes.

### 2. `repomix` MCP — bulk codebase packing

Use when you need a single consolidated view of the project (code review prep, sharing context with another tool, generating docs):

| Need | Tool |
| --- | --- |
| Pack the local repo into one XML file | `mcp__repomix__pack_codebase` |
| Pack a remote GitHub repo | `mcp__repomix__pack_remote_repository` |
| Read previously packed output | `mcp__repomix__read_repomix_output` |
| Grep inside packed output | `mcp__repomix__grep_repomix_output` |

If `mcp__repomix__*` tools are missing from the available tool list, the MCP is enabled but Claude Code was started before it was registered. Restart Claude Code (or run `/mcp` and reconnect) to surface the tools. Server health can be confirmed with `claude mcp list`.

## Default Flow For New Tasks

1. **Plan** — call `mcp__codebase-memory__get_architecture` if you don't already know the layout.
2. **Locate** — `search_graph` (symbol) or `search_code` (free text) before falling back to `Grep`.
3. **Read** — `get_code_snippet` for definitions; only use `Read` for whole-file context.
4. **Edit** — `Edit` / `Write` as usual.
5. **After non-trivial edits** — re-run `index_repository` so the graph stays current.

## Project-Specific Build Notes

- Solution: `KhurramAudioRoute.sln` (also `AudioRouterV2.sln`). Csproj: `KhurramAudioRoute.csproj`. Target: `net10.0-windows`, `WinExe`, WPF.
- Build: `dotnet build KhurramAudioRoute.csproj`.
- Native deps (`bass.dll`, `bassmix.dll`, `bass_fx.dll`) under `Native/` are copied to output via `<Content>` items.
- App icon: `Assets/app_icon.ico` is embedded as `<Resource>` and set as `<ApplicationIcon>`. The tray icon and window icon both reference it via the relative path `Assets/app_icon.ico` (resolves as a pack URI at runtime).
- If a build fails with "file is locked by KhurramAudioRoute (PID …)", the previous run is hidden in the system tray. Right-click the tray icon → Exit, or `Stop-Process -Name KhurramAudioRoute -Force`, then rebuild.

## System Tray Behavior

- `MainWindow.OnClosing` cancels the close and calls `Hide()` unless `_isExplicitExit` is set.
- `MainWindow.ExitApplication()` is the only path that fully shuts the app down — invoked from the tray context menu's "Exit" item.
- `TrayIcon.Dispose()` runs in `OnClosed` so the icon is removed cleanly.
