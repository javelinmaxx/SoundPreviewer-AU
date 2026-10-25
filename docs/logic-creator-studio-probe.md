# Probing Logic Pro Creator Studio

The Logic render lab on `logic-render` recognizes both distributions:

- `com.apple.logic10`: legacy Mac Logic Pro.
- `com.apple.mobilelogic`: confirmed by the user from Logic Pro Creator Studio 12.0.

The app's filename is not its bundle identifier. Creator Studio can be installed
as `/Applications/Logic Pro Creator Studio.app`. Discovery checks the system and
user Applications directories and searches Spotlight for both identifiers.
Multiple matching installations require an explicit selection.

```bash
git switch logic-render
git pull --ff-only
./Logic-Render-Lab.command probe \
  --application "/Applications/Logic Pro Creator Studio.app"
```

Selecting `Contents/Info.plist`, `Contents`, or the executable inside
`Contents/MacOS` is also accepted: the tool resolves those paths to the enclosing
app bundle before reading metadata or calling `sdef`. A file named Logic alone
does not establish identity; the metadata must contain a recognized identifier.

For read-only UI discovery, open a dedicated test project manually and run:

```bash
./Logic-Render-Lab.command probe \
  --application "/Applications/Logic Pro Creator Studio.app" --accessibility
```

The detected bundle identifier is passed to the UI probe so it queries the
selected distribution's running process. It does not activate Logic, select a
patch, click or bounce. macOS may request Automation/Accessibility permission.
A missing scripting dictionary is recorded separately and does not prevent the
optional UI probe from running. Neither that error nor a successful window
inventory establishes whether automated patch rendering is available.

Tests cover path normalization, Creator Studio discovery and command routing
using fixtures/mocked processes. A JavaScript mock also checks bundle selection;
these do not replace running the probe against the actual Creator Studio UI.
