# Original Mac desktop UI startup

The original desktop UI is an Avalonia application, separate from the new
localhost web app. It already selects Avalonia's native macOS backend. Its
Apple Silicon UI CI checks have passed. An Intel CI run showed Skia/Metal shader
compilation timing out (`Compilation took longer than 300 ms`), followed by the
installed-storage window test timing out. This is evidence of a graphics/startup
failure on that runner, not proof of the cause on every user's Mac.

For a running process with no visible window, try the updated source launcher:

```bash
./Start-SoundPreviewer-Software.command
# Equivalent:
./Start-SoundPreviewer.command --software-rendering
```

For an updated packaged app containing this startup option:

```bash
bash scripts/Start-MacApp-Software.sh "/path/SoundPreviewer.app"
```

The helper calls the bundle's executable directly with the software-rendering
flag and environment setting, so Terminal displays startup errors. **An old
`.app` cannot gain this option just by updating the repository**: rebuild it or
use a new app bundle first. The helper does not modify the app bundle or bypass
macOS security checks. Normal launching retains the existing graphics defaults.

New desktop builds create a unique startup log under:

```text
~/Library/Logs/SoundPreviewer/startup-<time>-<pid>.log
```

The log records architecture, requested rendering mode, Avalonia initialization,
storage preparation, main-window construction/opening and startup failures. Its
location is printed to stderr before Avalonia starts, so it also helps diagnose
failures that occur before any window exists. Logs never overwrite an old log.
An unwritable log destination does not prevent launching.

For an isolated diagnostic location, set `SOUNDPREVIEWER_STARTUP_LOG` to a new
filename. If the app still fails in software mode, its startup log and Terminal
error output are needed to distinguish a graphics failure from storage migration,
permissions, missing native libraries or another initialization error. Existing
Preview and Config folders should be preserved.

The Mac CI software-rendering job opens a real isolated desktop window and runs
the application's existing UI smoke suite on both Apple Silicon and Intel.
