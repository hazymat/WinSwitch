# WinSwitch: working notes

Read `SPEC.md` before any work. It is the source of truth; propose changes to it rather than silently diverging.

Read `docs/DESIGN.md` too: the design rules shared by all four apps (tray notifications, dot colours, cards). It is identical in every repo; change it in all four together.

UI and UX across all of Mat's apps: follow Rules for every app in https://github.com/hazymat/sift/blob/main/docs/ui-consistency.md (undo and history, the selection bar, press-hold to choose, dragging, keys, hints). A change here that adds or alters a shared pattern updates that section in the same piece of work.

## Dev environment

- Build and test on Windows (the app can't run on Linux; it compiles there with `-p:EnableWindowsTargeting=true`). The test PC and its details live in the project's private notes, not in this public repo.
- Shell only, no web UI: `dotnet build app/WinSwitch.csproj`. Settings and the wizard are plain WPF built in code from `Power/Catalog.cs`.
- A `portable.txt` next to the exe keeps settings, the original-settings snapshot and logs beside it (handy for test copies).
- Never test restore or uninstall on a PC whose original settings you care about without reading `original-settings.json` first.

## Git identity (required)

Never credit Claude/Anthropic anywhere: no `Co-Authored-By` trailers, no "Generated with" lines, no session links, no Claude author. Before the first commit in any clone run:

```
git config user.name hazymat && git config user.email 7063284+hazymat@users.noreply.github.com
```

The `No AI attribution` workflow fails any PR or push whose commits credit Claude; fix the commits, don't merge past it.

## Releases

Every push to main publishes a dev pre-release (Velopack, `.github/workflows/build.yml`). A stable release is promoted by running the workflow by hand with `stable_version` set to an existing dev version. A commit trailer `Impact: feature` or `Impact: improvement` puts its line first in What's new.

## Code style

- Compact code, descriptive variable names (no single-letter names), concise useful comments only.
- Windows' documented APIs only; undocumented ones (power mode overlay) are read back and dropped if they don't stick.

## Layout

- `app/Power/`: `PowerApi` (powrprof), `Catalog` (every setting and the five dials), `Direct` (brightness, refresh rate, radios, volume, device power flags), `Snapshot` (original settings, reader, writer, restorer).
- `app/Rules/`: `Engine` (situations, tweaks, applying, changed-in-Windows questions, new docks), `Inventory` (what's connected), `Templates` (Starting profile, wizard profiles, Battery or speed presets).
- `app/Shell/`: tray, notification cards, power actions and Keep awake, updater, crash log, monitor ids.
- `app/SettingsWindow.*`, `app/WizardWindow.cs`.
