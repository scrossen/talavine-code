#!/usr/bin/env python3
"""Resolve provisioned local signing from installed public signing metadata."""

import argparse
import datetime
import fnmatch
import hashlib
import pathlib
import plistlib
import re
import subprocess
import sys
import xml.etree.ElementTree as ET


def keychain_groups(profile, bundle_id):
    """Primary write identity, then only provision-authorized shipped Dev aliases."""
    entitlements = profile.get("Entitlements", {})
    primary = entitlements.get("com.apple.application-identifier", "")
    team = next((value for value in profile.get("TeamIdentifier", []) if primary in
                 (f"{value}.{bundle_id}", f"{value}.maccatalyst.{bundle_id}")), None)
    wildcard_team = wildcard_development_team(profile)
    if not team and wildcard_team:
        # A device-bound team wildcard authorizes exactly one concrete group for
        # this bundle; the historical Dev aliases are never inferred from it.
        primary = f"{wildcard_team}.{bundle_id}"
        if not any(fnmatch.fnmatchcase(primary, pattern)
                   for pattern in entitlements.get("keychain-access-groups", [])):
            raise ValueError("Provisioning profile does not authorize the app's primary Keychain group.")
        return [primary]
    allowed = entitlements.get("keychain-access-groups", [])
    authorized = lambda group: any(fnmatch.fnmatchcase(group, pattern) for pattern in allowed)
    if not team or not authorized(primary):
        raise ValueError("Provisioning profile does not authorize the app's primary Keychain group.")
    groups = [primary]
    # These are the two historical identities produced by the local Dev build
    # script and Apple's Catalyst namespace, never arbitrary team-wide groups.
    if not bundle_id.endswith(".Dev"):
        for historical in (f"{team}.{bundle_id}.Dev", f"{team}.maccatalyst.{bundle_id}.Dev"):
            if authorized(historical):
                groups.append(historical)
    return groups


def wildcard_development_team(profile):
    """Team id of a device-bound `TEAM.*` development profile, else None.

    Developer ID and store profiles must name the app explicitly; a wildcard is
    accepted only for local development signing tied to this Mac's device list.
    """
    app_id = profile.get("Entitlements", {}).get("com.apple.application-identifier", "")
    team = next((value for value in profile.get("TeamIdentifier", [])
                 if app_id == f"{value}.*"), None)
    if team and not profile.get("ProvisionsAllDevices") and profile.get("ProvisionedDevices"):
        return team
    return None


def apply_signing_metadata(profile, bundle_id, entitlements_path, app_info_path):
    groups = keychain_groups(profile, bundle_id)
    with app_info_path.open("rb") as source:
        info = plistlib.load(source)
    if info.get("CFBundleIdentifier") != bundle_id:
        raise ValueError("Staged bundle identity does not match the signing request.")
    with entitlements_path.open("rb") as source:
        entitlements = plistlib.load(source)
    entitlements["com.apple.application-identifier"] = groups[0]
    entitlements["keychain-access-groups"] = groups
    # This ordered list is sealed by the app signature. The host uses its first
    # group for writes and only reads existing KEKs from the remaining groups.
    info["TalavineKeychainAccessGroups"] = groups
    entitlements_path.write_bytes(plistlib.dumps(entitlements))
    app_info_path.write_bytes(plistlib.dumps(info))


def candidate(profile, path, identities, bundle_id, device_id, now):
    entitlements = profile.get("Entitlements", {})
    app_id = entitlements.get("com.apple.application-identifier", "")
    teams = profile.get("TeamIdentifier", [])
    team = next((team for team in teams if app_id in
                 (f"{team}.{bundle_id}", f"{team}.maccatalyst.{bundle_id}")), None)
    wildcard = False
    if not team:
        team = wildcard_development_team(profile)
        if team:
            wildcard, app_id = True, f"{team}.{bundle_id}"
    if not team or "OSX" not in profile.get("Platform", []):
        return None
    expiry = profile.get("ExpirationDate")
    if not expiry or expiry.replace(tzinfo=datetime.timezone.utc) <= now:
        return None
    if not any(fnmatch.fnmatchcase(app_id, group) for group in
               entitlements.get("keychain-access-groups", [])):
        return None
    # Store-only profiles cannot run a local build. Development profiles must
    # authorize this device; Developer ID profiles authorize all devices.
    if not profile.get("ProvisionsAllDevices") and device_id.upper() not in {
            value.upper() for value in profile.get("ProvisionedDevices", [])}:
        return None
    matches = []
    for certificate in profile.get("DeveloperCertificates", []):
        fingerprint = hashlib.sha1(certificate).hexdigest().upper()
        name = identities.get(fingerprint)
        if name and (name.startswith("Developer ID Application:")
                     or name.startswith("Apple Development:")):
            matches.append((0 if name.startswith("Developer ID Application:") else 1,
                            fingerprint, name))
    if not matches:
        return None
    rank, fingerprint, identity = sorted(matches)[0]
    return {"identity": identity, "fingerprint": fingerprint, "profile": str(path),
            "team": team, "application_id": app_id,
            "debugger_allowed": bool(entitlements.get("get-task-allow", False)),
            "rank": rank, "wildcard": wildcard, "expiry": expiry.isoformat()}


def resolve(candidates):
    # An explicit app profile always wins; the team wildcard is only a fallback.
    explicit = [value for value in candidates if not value.get("wildcard")]
    candidates = explicit or candidates
    if not candidates:
        raise ValueError("No installed provisioning profile and available signing identity "
                         "authorize this bundle's Keychain access on this Mac. Set "
                         "MACOS_PROVISIONING_PROFILE_PATH and MACOS_CODESIGN_IDENTITY "
                         "to a matching Developer ID or development pair.")
    if len({value["application_id"] for value in candidates}) != 1:
        raise ValueError("Multiple Keychain identities match this bundle. Set "
                         "MACOS_PROVISIONING_PROFILE_PATH to the profile used for "
                         "the existing saved credentials; choosing another can hide them.")
    return sorted(candidates, key=lambda value: (value["rank"],
                  -datetime.datetime.fromisoformat(value["expiry"]).timestamp(),
                  value["profile"]))[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle-id")
    parser.add_argument("--project", type=pathlib.Path)
    parser.add_argument("--profile", type=pathlib.Path)
    parser.add_argument("--identity")
    parser.add_argument("--apply-entitlements", type=pathlib.Path)
    parser.add_argument("--app-info", type=pathlib.Path)
    args = parser.parse_args()
    bundle_id = args.bundle_id
    if not bundle_id and args.project:
        bundle_id = ET.parse(args.project).findtext("./PropertyGroup/ApplicationId")
    if not bundle_id:
        raise ValueError("Specify --bundle-id or a project with a concrete ApplicationId.")
    if args.apply_entitlements or args.app_info:
        if not (args.apply_entitlements and args.app_info and args.profile):
            raise ValueError("Applying signing metadata requires a profile, entitlements, and app Info.plist.")
        decoded = subprocess.run(["/usr/bin/security", "cms", "-D", "-i", str(args.profile)],
                                 check=True, capture_output=True)
        apply_signing_metadata(plistlib.loads(decoded.stdout), bundle_id,
                               args.apply_entitlements, args.app_info)
        return
    raw = subprocess.run(["/usr/bin/security", "find-identity", "-v", "-p", "codesigning"],
                         check=True, capture_output=True, text=True).stdout
    identities = dict(re.findall(r'\) ([A-F0-9]{40}) "([^"\n]+)"', raw))
    if args.identity:
        identities = {key: value for key, value in identities.items()
                      if args.identity in (key, value)}
    raw = subprocess.run(["/usr/sbin/ioreg", "-rd1", "-c", "IOPlatformExpertDevice"],
                         check=True, capture_output=True, text=True).stdout
    device = re.search(r'"IOPlatformUUID"\s*=\s*"([^"\n]+)"', raw)
    device_id = device.group(1) if device else ""
    if args.profile:
        if not args.profile.is_file():
            raise ValueError(f"Provisioning profile does not exist: {args.profile}")
        profiles = [args.profile]
    else:
        roots = [pathlib.Path.home() / "Library/MobileDevice/Provisioning Profiles",
                 pathlib.Path.home() / "Library/Developer/Xcode/UserData/Provisioning Profiles"]
        profiles = [path for root in roots if root.exists()
                    for path in root.iterdir() if path.suffix == ".provisionprofile"]
    candidates = []
    now = datetime.datetime.now(datetime.timezone.utc)
    for path in profiles:
        decoded = subprocess.run(["/usr/bin/security", "cms", "-D", "-i", str(path)],
                                 capture_output=True)
        if decoded.returncode:
            continue
        try:
            value = candidate(plistlib.loads(decoded.stdout), path, identities,
                              bundle_id, device_id, now)
            if value:
                candidates.append(value)
        except (ValueError, TypeError, plistlib.InvalidFileException):
            continue
    result = resolve(candidates)
    # Plain lines consumed as data, never evaluated as shell commands.
    for value in (bundle_id, result["identity"], result["profile"], result["team"],
                  result["application_id"], "true" if result["debugger_allowed"] else "false"):
        if "\n" in value or "\r" in value:
            raise ValueError("Signing metadata contains an unsupported newline.")
        print(value)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        print(f"Error: {error}", file=sys.stderr)
        sys.exit(1)
