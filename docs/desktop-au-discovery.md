# Desktop Audio Unit discovery

Version 1.3.1 adds AUv2 bundle discovery to the desktop plugin scanner.
Rebuild the desktop app, then click Scan Plugins. Both
`/Library/Audio/Plug-Ins/Components` and
`~/Library/Audio/Plug-Ins/Components` are checked, including when an existing
installation has saved VST-only scan folders. Saved custom directories are
preserved. New installations also list these locations by default.

Discovered `.component` bundles appear as AU with their bundle version and
Mach-O architecture (Intel, ARM64 or Universal). Both instrument and effect
bundles are included. XML and binary Info.plist metadata are supported.
Scanning reads files without loading or executing the plugins. Malformed
bundles are reported as warnings; valid bundles continue to appear.

Discovery does not prove that macOS registered the AU, that its license is
available, or that it can be instantiated. Use `/usr/bin/auval -a` to inspect
macOS registration. Built-in Apple units and AUv3 app extensions are not
inventoried by this bundle scanner.

The desktop synthesizer preset backends still require their supported VST or
standalone formats. AU bundles are excluded from automatic association, and
manual association explains this limitation. Existing VST selections are
preserved. To render supported Logic `.patch` AU chains, run
`./Start-LogicPreview-Web.command` and open `http://localhost:5002`.
The native preview worker checks the requested registered AU at render time;
it does not depend on the desktop scanner's inventory.
