// Read-only JXA probe. Does not launch Logic, activate windows, type or click.
function run(argv) {
    if (argv.length !== 1 || !argv[0]) {
        throw new Error("Pass the bundle identifier read from the selected Logic installation.");
    }
    const bundleIdentifier = argv[0];
    const events = Application("System Events");
    const matches = events.applicationProcesses.whose({bundleIdentifier: bundleIdentifier})();
    if (matches.length === 0) {
        return JSON.stringify({running: false, message: "Open a dedicated Logic test project manually, then repeat the probe."});
    }
    const logic = matches[0];
    function read(getter) {
        try { return getter(); } catch (error) { return {unavailable: String(error)}; }
    }
    return JSON.stringify({
        running: true,
        frontmost: read(() => logic.frontmost()),
        windows: read(() => logic.windows().slice(0, 20).map(window => ({
            title: read(() => window.name()), role: read(() => window.role()),
            subrole: read(() => window.subrole())
        }))),
        menu_titles: read(() => logic.menuBars[0].menuBarItems().slice(0, 30).map(item => item.name())),
        note: "Observed labels depend on Logic version and UI language. No render automation was performed."
    });
}
