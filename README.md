# Configuration Manager

Universal configuration manager for CastleForge mods. Provides an intuitive interface for managing and organizing mod settings.

## What it does
- Adds `Configuration Manager` to the main menu and pause menu.
- Creates and manages `mod.json` configuration files for CastleForge mods.
- Provides a user-friendly interface for viewing and editing mod settings.
- Integrates with the CastleForge mod loader framework for persistent configuration storage.

## Configuration

Config mod stores settings in:

`<GameRoot>\!Mods\Config\<ModName>.json`

Each configuration file follows the standard CastleForge mod metadata format.

## Community repo workflow

The community repo is:

`https://github.com/RussDev7/CastleForge-CommunityMods`

Each community entry lives in its own folder inside a category:

- `Mods/<ProjectName>/`
- `TexturePacks/<ProjectName>/`
- `WeaponAddons/<ProjectName>/`

Each entry folder should include:

- `mod.json`
- `README.md`
- `preview.png` or `preview.gif`

## How to publish

1. Prepare your configuration mod files:
   - Ensure `mod.json` contains valid metadata (name, version, author, description)
   - Create a meaningful `README.md` explaining what your config manages
   - Provide a `preview.png` or `preview.gif` showing the interface or use case

2. Fork `RussDev7/CastleForge-CommunityMods`

3. Copy your submission into the matching category folder:
   - `Mods/<YourConfigName>/`

4. Include:
   - `mod.json`
   - `README.md`
   - `preview.png` or `preview.gif`

5. Commit your changes

6. Open a pull request

## Notes
- Config mod works alongside other CastleForge mods to provide centralized settings management
- The community catalog system will automatically discover and index your mod once the PR is merged
- Configuration files are human-readable JSON for easy sharing and version control
