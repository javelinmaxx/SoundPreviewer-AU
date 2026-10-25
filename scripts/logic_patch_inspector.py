#!/usr/bin/env python3
"""Inspect Logic .patch packages without loading plug-ins or changing source files.

The reverse-engineered OCuA/UCuA layout is informed by LogicFiles (MIT):
https://github.com/CraigStuntz/LogicFiles/tree/3234062e16f8b5dc10d9ddcc68454013d97c44c0
See third-party/logicfiles/LICENSE. Unknown blocks remain explicit in the report.
This tool inventories saved state; it does not render or certify AU compatibility.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import plistlib
import re
import stat
import struct
import subprocess
import sys
from xml.parsers.expat import ExpatError

MAX_FILE_BYTES = 64 * 1024 * 1024
MAX_PACKAGE_BYTES = 256 * 1024 * 1024
MAX_CHANNELS = 128
MAX_MEMBERS = 1024
MAX_BLOCKS = 4096
MAX_PLIST_BYTES = 8 * 1024 * 1024
FORMAT_REFERENCE = "LogicFiles OCuA/UCuA observations; not an Apple format specification"


class NativeInstrumentSkipped(ValueError):
    """Recognized Logic-native instrument: no external preview is possible."""


class InspectionError(ValueError):
    """The package is missing required files or contains invalid bounded input."""


class _PlistDictionary(dict):
    def __setitem__(self, key, value):
        if not isinstance(key, str) or key in self:
            raise InspectionError("Plist dictionary keys must be unique strings.")
        super().__setitem__(key, value)


def _summary(data: bytes) -> dict:
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def _json_value(value, depth=0, budget=None):
    """Retain unknown plist fields without expanding opaque data or UID archives."""
    if budget is None:
        budget = [100000]
    budget[0] -= 1
    if budget[0] < 0 or depth > 32:
        raise InspectionError("Plist exceeds the node/depth limit or contains a cycle.")
    if isinstance(value, bytes):
        return {"opaque_data": _summary(value)}
    if isinstance(value, plistlib.UID):
        return {"plist_uid": value.data}
    if isinstance(value, datetime.datetime):
        return {"plist_date": value.isoformat()}
    if isinstance(value, dict):
        return {key: _json_value(item, depth + 1, budget) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_value(item, depth + 1, budget) for item in value]
    if isinstance(value, float) and not math.isfinite(value):
        return {"nonfinite_real": str(value)}
    return value


def _plist(data: bytes) -> dict:
    if len(data) > MAX_PLIST_BYTES:
        raise InspectionError("Plist exceeds the 8 MiB inspection limit.")
    try:
        value = plistlib.loads(data, dict_type=_PlistDictionary)
        if not isinstance(value, dict):
            raise InspectionError("Expected a plist dictionary.")
        _json_value(value)  # Reject cyclic or deeply nested binary plists, too.
        return value
    except (ValueError, TypeError, OverflowError, RecursionError, ExpatError, plistlib.InvalidFileException) as error:
        raise InspectionError(f"Invalid plist: {error}") from error


def _read_member(root: Path, name: str, remaining: int) -> bytes:
    # Treat plist filenames as data. Never follow references outside the package,
    # links to other libraries, or special files such as FIFOs.
    if not name or name in (".", "..") or any(char in name for char in ("/", "\\", ":", "\0")):
        raise InspectionError(f"Unsafe package member name: {name!r}")
    path = root / name
    mode = path.lstat().st_mode
    if not stat.S_ISREG(mode):
        raise InspectionError(f"Package member must be a regular, unlinked file: {name}")
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0)
    with os.fdopen(os.open(path, flags), "rb") as source:
        size = os.fstat(source.fileno()).st_size
        if not stat.S_ISREG(os.fstat(source.fileno()).st_mode) or size > min(MAX_FILE_BYTES, remaining):
            raise InspectionError(f"Package member exceeds the file/package limit: {name}")
        data = source.read(min(MAX_FILE_BYTES, remaining) + 1)
    if len(data) > min(MAX_FILE_BYTES, remaining):
        raise InspectionError(f"Package member grew beyond the inspection limit: {name}")
    return data


def _u32(data: bytes, offset: int) -> int:
    if offset < 0 or offset + 4 > len(data):
        raise InspectionError("Truncated OCuA/UCuA length field.")
    return struct.unpack_from("<I", data, offset)[0]


def _text(data: bytes) -> str:
    return data.split(b"\0", 1)[0].decode("utf-8", errors="replace")


def _fourcc(value) -> str | None:
    if type(value) is not int or not 0 <= value <= 0xFFFFFFFF:
        return None
    raw = value.to_bytes(4, "big")
    return raw.decode("ascii") if all(32 <= byte < 127 for byte in raw) else None


def _component(plist: dict) -> dict | None:
    fields = {key: _fourcc(plist.get(key)) for key in ("type", "subtype", "manufacturer")}
    return fields if all(fields.values()) else None


def _payload(block: bytes, subheader: int) -> tuple[str, int, bytes, dict | None] | None:
    """Find a bounded state envelope, not arbitrary strings inside plug-in state.

    Named wrappers vary in prefix length. Only search the first 4 KiB for a
    validated envelope. Once a PST is found, its nested plists aren't other FX.
    Binary AU wrappers store an explicit length immediately before their plist.
    """
    candidates = []
    prefix = block[:4096]
    for magic, adjustment in ((b"GAMETSPP", -12), (b"PPSTEMAG", -12), (b"<?xml", 0), (b"bplist00", 0)):
        position = prefix.find(magic)
        if position >= 0:
            candidates.append(position + adjustment)
    if subheader == 64:
        candidates = [56] if block[56:64] == b"bplist00" else []
    for start in sorted(set(candidates)):
        if start < 40 or start >= len(block):
            continue
        if block[start + 12:start + 20] in (b"GAMETSPP", b"PPSTEMAG"):
            size = _u32(block, start)
            if size < 24 or size > len(block) - start:
                raise InspectionError("PST payload length escapes its UCuA block.")
            return "logic_pst", start, block[start:start + size], None
        if block[start:start + 8] == b"bplist00":
            size = _u32(block, start - 4)
            if size < 40 or size > len(block) - start:
                raise InspectionError("Binary plist length escapes its UCuA block.")
            raw = block[start:start + size]
        elif block[start:start + 5] == b"<?xml":
            end = block.find(b"</plist>", start)
            if end < 0:
                raise InspectionError("Unterminated embedded XML plist.")
            raw = block[start:end + len(b"</plist>")]
        else:
            continue
        value = _plist(raw)
        kind = "keyed_archive" if value.get("$archiver") == "NSKeyedArchiver" else "au_preset"
        return kind, start, raw, value
    return None


def inspect_cst(data: bytes) -> dict:
    """Describe recognized saved state in container order; never infer DSP support."""
    if len(data) < 41 or data[:4] != b"OCuA":
        raise InspectionError("Not a supported OCuA channel-strip container.")
    position = _u32(data, 28) + 36
    if not 41 <= position <= len(data):
        raise InspectionError("Invalid OCuA header length.")
    result = {"channel_type": {0x40: "audio", 0x42: "bus", 0x43: "instrument"}.get(data[40], "unknown"),
              "plugins": [], "opaque_blocks": [], "warnings": []}
    count = 0
    while data[position:position + 4] == b"UCuA":
        count += 1
        if count > MAX_BLOCKS:
            raise InspectionError("Too many UCuA blocks.")
        size = _u32(data, position + 28) + 36
        if size < 40 or size > len(data) - position:
            raise InspectionError("Invalid or truncated UCuA block length.")
        block = data[position:position + size]
        slot = struct.unpack_from("<H", block, 18)[0]
        subheader = _u32(block, 36)
        state = _payload(block, subheader) if slot < 12 and subheader in (64, 424) else None
        if state is None or state[0] == "keyed_archive":
            reason = "logic_internal_metadata" if state else "unrecognized_block"
            result["opaque_blocks"].append({"slot": slot, "kind": reason, **_summary(block)})
        else:
            kind, start, raw, value = state
            named = subheader == 424 and start >= 188
            role_byte = block[187] if named else None
            component = _component(value) if value else None
            wrapper = _component({"manufacturer": _u32(block, 168), "type": _u32(block, 172),
                                  "subtype": _u32(block, 176)}) if named and kind == "au_preset" else None
            if role_byte is not None:
                role = "instrument" if role_byte & 0x08 else "midi_fx" if role_byte & 0x02 else "audio_fx"
                role_source = "wrapper_bits"
            else:
                role = {"aumu": "instrument", "aumi": "midi_fx", "aufx": "audio_fx", "aumf": "audio_fx"}.get(
                    component["type"] if component else None, "unknown")
                role_source = "au_type" if role != "unknown" else "unknown"
            plugin = {"slot": slot, "role": role, "role_source": role_source, "state_format": kind,
                      "preset_filename": (_text(block[50:110]) or None) if named else None,
                      "display_name": (_text(block[156:168]) or None) if named else None,
                      "component": component, "saved_state": _summary(raw), "availability": "not_checked"}
            if kind == "au_preset":
                plugin["wrapper_component"] = wrapper
                plugin["identity_conflict"] = wrapper is not None and component is not None and wrapper != component
                if plugin["identity_conflict"]:
                    plugin["availability"] = "identity_conflict"
                    result["warnings"].append(f"Slot {slot}: wrapper and AU saved-state identities disagree; dependency matching is unsafe.")
                if named and wrapper is None:
                    result["warnings"].append(f"Slot {slot}: wrapper component identity is unrecognized; only the saved-state identity is available.")
                expected_role = {"aumu": "instrument", "aumi": "midi_fx", "aufx": "audio_fx", "aumf": "audio_fx"}.get(
                    component["type"] if component else None)
                if expected_role is not None and role != "unknown" and role != expected_role:
                    result["warnings"].append(f"Slot {slot}: wrapper role disagrees with the saved-state AU type.")
            if value:
                plugin["preset_name"] = value.get("name") if isinstance(value.get("name"), str) else None
                plugin["state_keys"] = sorted(value)
            if kind == "logic_pst":
                plugin["availability"] = "logic_native_hostability_unknown"
                if named:
                    plugin["logic_plugin_id"] = _u32(block, 176)
                result["warnings"].append(f"Slot {slot}: Logic-native PST state needs a compatible Logic host; no AU identity is assumed.")
            elif component is None:
                result["warnings"].append(f"Slot {slot}: AU state has no complete printable component identity.")
            if role == "unknown":
                result["warnings"].append(f"Slot {slot}: plug-in role could not be established.")
            result["plugins"].append(plugin)
        position += size
    # Known examples have a 50-byte OCuA footer. Preserve other tails explicitly
    # rather than treating an unsupported container version as a complete parse.
    tail = data[position:]
    if tail:
        result["container_tail"] = _summary(tail)
        if len(tail) != 50 or tail[:4] != b"OCuA":
            result["warnings"].append("Unrecognized container tail; further slots may be unreadable.")
    if result["opaque_blocks"]:
        result["warnings"].append("Opaque blocks may contain keyboard zones, Smart Controls, bypass or other Logic-specific state; they are not interpreted.")
    return result


def parse_au_inventory(text: str) -> set[tuple[str, str, str]]:
    """Read `auval -a` listings. Registration does not prove loadability/licensing."""
    components = set()
    for line in text.splitlines():
        # auval prints three four-byte codes separated by one space. Codes may
        # themselves contain spaces (for example the manufacturer ' kHs').
        match = re.match(r"^\s*(au.{2})[ \t](.{4})[ \t](.{4})[ \t]+\S.*$", line)
        if match and all(32 <= ord(char) < 127 for group in match.groups()[:3] for char in group):
            components.add(match.groups()[:3])
        elif line.lstrip().startswith("au"):
            raise InspectionError("Unrecognized AU inventory entry; refusing to infer missing dependencies from a partial listing.")
    if not components:
        raise InspectionError("AU inventory contains no recognizable `auval -a` entries; missing dependencies cannot be established.")
    return components


def resolve_patch_path(path: str | Path) -> Path:
    """Accept a package or a directly selected CST/plist inside that package.

    Do not infer packages from unrelated parent folders or follow package links.
    Expand a quoted ~/ path, but leave shell escaping to the caller's shell.
    """
    root = Path(path).expanduser().absolute()
    if root.is_file() and root.parent.suffix.lower() == ".patch" and (root.name == "data.plist" or root.suffix.lower() == ".cst"):
        root = root.parent
    if root.is_symlink():
        raise InspectionError(f"Selected path is a symbolic link: {str(root)!r}. Select the actual .patch package folder instead.")
    if not root.exists():
        hint = " Check the path; quote paths containing spaces."
        if "\\ " in str(path):
            hint += " Backslash-escaped spaces were passed literally; inside double quotes use spaces without backslashes."
        if str(path).endswith(" "):
            hint += " The supplied path ends with a space; remove it if unintended."
        raise InspectionError(f"Selected path does not exist: {str(root)!r}." + hint)
    if root.suffix.lower() != ".patch":
        description = "folder" if root.is_dir() else f"{root.suffix or 'extensionless'} file"
        raise InspectionError(f"Selected {description}: {str(root)!r}. Choose a Logic Library .patch package, not a standalone .cst/.pst/.aupreset or its parent library folder.")
    if not root.is_dir():
        kind = "a file"
        if root.is_file():
            with root.open("rb") as source:
                header = source.read(8)
            if header.startswith(b"PK\x03\x04"):
                kind = "a ZIP archive; extract it first"
            elif header == b"bplist00":
                kind = "a binary-plist file (this single-file patch variant is not supported yet)"
        raise InspectionError(f"Selected .patch is {kind}, not a package directory: {str(root)!r}. This reader expects a folder containing data.plist and #Root.cst.")
    return root


def inspect_patch(path: str | Path, inventory: set[tuple[str, str, str]] | None = None) -> dict:
    root = resolve_patch_path(path)
    remaining = MAX_PACKAGE_BYTES
    files = {}

    def read(name):
        nonlocal remaining
        if name not in files:
            files[name] = _read_member(root, name, remaining)
            remaining -= len(files[name])
        return files[name]

    metadata = _plist(read("data.plist"))
    settings = metadata.get("channels")
    if type(metadata.get("VersionPatches")) is not int or not isinstance(settings, list) or not settings or len(settings) > MAX_CHANNELS:
        raise InspectionError("data.plist must contain VersionPatches and 1–128 channel dictionaries.")
    by_filename = {}
    filenames = set()
    for setting in settings:
        if not isinstance(setting, dict) or not isinstance(setting.get("Filename"), str):
            raise InspectionError("Each channel needs a Filename string.")
        name = setting["Filename"]
        if name.casefold() in filenames or not name.lower().endswith(".cst"):
            raise InspectionError("Channel filenames must be distinct .cst members.")
        by_filename[name] = setting
        filenames.add(name.casefold())
        if "Channel_sends" in setting and not isinstance(setting["Channel_sends"], list):
            raise InspectionError("Channel_sends must be an array.")
        read(name)
    read("#Root.cst")
    names = set(by_filename) | {"#Root.cst"}
    # Include orphan strips in the report so absent metadata cannot hide a dependency.
    uninspected = []
    for member_count, member in enumerate(root.iterdir(), 1):
        if member_count > MAX_MEMBERS:
            raise InspectionError("Too many package directory members.")
        if member.suffix.lower() == ".cst":
            names.add(member.name)
            if len(names) > MAX_CHANNELS:
                raise InspectionError("Too many channel strips in the package.")
            read(member.name)
        elif member.name != "data.plist":
            uninspected.append(member.name)
    warnings = ["Inspection only: saved state, routing and AU registration do not establish renderability."]
    if metadata["VersionPatches"] != 40014:
        warnings.append(f"Unverified patch format version: {metadata['VersionPatches']}.")
    if uninspected:
        warnings.append("Other package members were not inspected or fingerprinted; they may contain additional dependencies.")
    if sum(setting.get("Root") is True for setting in settings) != 1 or not by_filename.get("#Root.cst", {}).get("Root") is True:
        warnings.append("Root channel metadata does not identify #Root.cst uniquely; routing is uncertain.")
    channels = []
    for name in sorted(names, key=lambda item: (item != "#Root.cst", item)):
        setting = by_filename.get(name)
        channel = {"filename": name, "settings": _json_value(setting) if setting else None,
                   "source": _summary(read(name))}
        if setting is None:
            warnings.append(f"{name}: missing channel metadata; routing is unknown.")
        if setting and any(setting.get("Channel_sends", [])):
            warnings.append(f"{name}: populated sends are preserved in settings but their routing semantics are not interpreted.")
        try:
            channel.update(inspect_cst(read(name)))
        except InspectionError as error:
            channel.update(plugins=[], opaque_blocks=[], warnings=[f"Unreadable channel strip: {error}"])
        for plugin in channel["plugins"]:
            component = plugin["component"]
            if component and inventory is not None and not plugin.get("identity_conflict"):
                identity = tuple(component[key] for key in ("type", "subtype", "manufacturer"))
                plugin["availability"] = "registered" if identity in inventory else "not_in_inventory"
        channels.append(channel)
    digest = hashlib.sha256()
    for name, data in sorted(files.items()):
        encoded = name.encode("utf-8")
        digest.update(struct.pack("<I", len(encoded)) + encoded + struct.pack("<Q", len(data)) + data)
    return {"schema_version": 1, "path": str(root), "name": root.stem,
            "format_reference": FORMAT_REFERENCE, "patch_format_version": metadata["VersionPatches"],
            "inspected_state_sha256": digest.hexdigest(),
            "fingerprint_scope": "data.plist and inspected CST files; excludes external assets and other package members",
            "uninspected_members": sorted(uninspected), "metadata": _json_value(metadata),
            "inventory_checked": inventory is not None, "channels": channels, "warnings": warnings,
            "rendering_supported": False}


def format_report(report: dict) -> str:
    lines = [f"Logic patch: {report['name']}", f"Package: {report['path']}", f"Saved-state fingerprint: {report['inspected_state_sha256']}",
             "Rendering: not implemented (read-only inspection)"]
    for channel in report["channels"]:
        setting = channel["settings"] or {}
        lines.append(f"\n{channel['filename']} — {setting.get('Channel_name', '(unnamed)')} [{channel.get('channel_type', 'unreadable')}]")
        routing = {key: value for key, value in setting.items() if key.startswith(("Channel_input", "Channel_output")) or key == "Channel_sends"}
        lines.append("  Routing (saved values): " + json.dumps(routing, ensure_ascii=False))
        for plugin in channel["plugins"]:
            component = plugin["component"]
            identity = "/".join(component[key] for key in ("type", "subtype", "manufacturer")) if component else "AU identity unknown"
            name = plugin["display_name"] or plugin.get("preset_name") or plugin["preset_filename"] or "(unnamed)"
            lines.append(f"  Slot {plugin['slot']} {plugin['role']}: {name} — {identity} — {plugin['availability']}")
        if channel["opaque_blocks"]:
            lines.append("  Opaque blocks: " + ", ".join(str(block["slot"]) for block in channel["opaque_blocks"]))
        lines.extend("  Warning: " + warning for warning in channel["warnings"])
    lines.extend("Warning: " + warning for warning in report["warnings"])
    return "\n".join(lines)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("patch", help="Path to a Logic .patch package (source files are never written)")
    parser.add_argument("--json", action="store_true", help="Print a structured report instead of a summary")
    inventory = parser.add_mutually_exclusive_group()
    inventory.add_argument("--au-inventory", type=Path, help="Compare dependencies with a text listing captured using auval -a")
    inventory.add_argument("--check-installed", action="store_true", help="On macOS, run /usr/bin/auval -a to list registered AUs (no validation/rendering)")
    args = parser.parse_args(argv)
    try:
        listing = None
        if args.au_inventory:
            with args.au_inventory.open("rb") as source:
                raw = source.read(MAX_PLIST_BYTES + 1)
            if len(raw) > MAX_PLIST_BYTES:
                raise InspectionError("AU inventory exceeds 8 MiB.")
            listing = raw.decode("utf-8")
        elif args.check_installed:
            if sys.platform != "darwin":
                raise InspectionError("--check-installed requires macOS; use --au-inventory for a captured listing.")
            completed = subprocess.run(["/usr/bin/auval", "-a"], check=True, capture_output=True, text=True, timeout=30)
            listing = completed.stdout
            if len(listing.encode("utf-8")) > MAX_PLIST_BYTES:
                raise InspectionError("AU inventory exceeds 8 MiB.")
        report = inspect_patch(args.patch, parse_au_inventory(listing) if listing is not None else None)
        print(json.dumps(report, indent=2, ensure_ascii=False, allow_nan=False) if args.json else format_report(report))
        return 0
    except (InspectionError, OSError, UnicodeError, subprocess.SubprocessError) as error:
        print(f"Inspection failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
