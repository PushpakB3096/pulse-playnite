#!/usr/bin/env python3
"""Update packaging/installer.yaml: prepend a Packages entry for a new release (idempotent by Version)."""
from __future__ import annotations

import os
import sys
from pathlib import Path

import yaml


def main() -> None:
    addon_id = os.environ["ADDON_ID"]
    version = os.environ["VERSION"].strip()
    api = os.environ.get("REQUIRED_API_VERSION", "6.2.0").strip()
    release_date = os.environ["RELEASE_DATE"].strip()
    package_url = os.environ["PACKAGE_URL"].strip()
    changelog = os.environ.get("CHANGELOG", "").strip()
    changelog_lines = (
        [line.strip() for line in changelog.splitlines() if line.strip()]
        if changelog
        else [f"Release {version}"]
    )

    path = Path(os.environ.get("INSTALLER_YAML", "packaging/installer.yaml"))

    data: dict = {}
    if path.exists():
        with path.open(encoding="utf-8") as f:
            raw = yaml.safe_load(f)
            if isinstance(raw, dict):
                data = raw

    if data.get("AddonId") != addon_id:
        data["AddonId"] = addon_id

    packages = data.get("Packages")
    if packages is None:
        packages = []
    if not isinstance(packages, list):
        packages = []

    existing = {
        p.get("Version")
        for p in packages
        if isinstance(p, dict) and p.get("Version") is not None
    }
    if version in existing:
        print(f"Version {version} already listed in {path}; nothing to do.", file=sys.stderr)
        sys.exit(0)

    new_pkg = {
        "Version": version,
        "RequiredApiVersion": api,
        "ReleaseDate": release_date,
        "PackageUrl": package_url,
        "Changelog": changelog_lines,
    }
    packages.insert(0, new_pkg)
    data["Packages"] = packages

    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as f:
        yaml.dump(
            data,
            f,
            default_flow_style=False,
            allow_unicode=True,
            sort_keys=False,
            width=120,
        )

    print(f"Updated {path} (added {version}).")


if __name__ == "__main__":
    main()
