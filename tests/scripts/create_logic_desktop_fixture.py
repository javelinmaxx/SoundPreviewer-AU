"""Wrap self-test Apple AU states in a synthetic .patch container for native CI."""
from pathlib import Path
import plistlib
import sys

from test_logic_patch_inspector import block, cst


def create(root: Path):
    patch = root / "TEST ONLY desktop.patch"
    patch.mkdir()
    metadata = {"VersionPatches": 40014, "channels": [{"Filename": "#Root.cst", "Root": True,
        "Channel_inputIndex_1": -1, "Channel_inputIsBus": False, "Channel_outputIsBus": False,
        "Channel_outputIsStereo": True, "Channel_outputIndex": 0, "Channel_sends": [{}, {}]}]}
    (patch / "data.plist").write_bytes(plistlib.dumps(metadata))
    (patch / "#Root.cst").write_bytes(cst([
        block((root / "wet/instrument.aupreset").read_bytes()),
        block((root / "wet/effect.aupreset").read_bytes(), slot=3, role=0)]))


if __name__ == "__main__":
    create(Path(sys.argv[1]).resolve())
