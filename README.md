# Rear Mirror Cruiser

Lethal Company mod that shows a live rear and inside-view camera feed of the Cruiser when sitting in the driver seat

## Features

- Rear and inside camera view when sitting in cruiser driver seat
- Hides instantly when leaving driver seat

## Config

- enable/disable camera views
- camera FOVs
- FPS per camera
- UI opacity

## Installation

- Install BepInEx for Lethal Company.
- Place `RearMirrorCruiser.dll` into `BepInEx/plugins`.

## Thunderstore Packaging (Maintainers)

This repository includes an automated Thunderstore packaging and publish flow.

- Package script: `scripts/package-thunderstore.ps1`
- GitHub Actions workflow: `.github/workflows/publish-thunderstore.yml`

The generated zip contains:

- `RearMirrorCruiser.dll`
- `manifest.json`
- `README.md`
- `icon.png`
