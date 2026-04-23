# Configuration Manager - Release Notes

## Version 2.0.1

### Bug Fixes
- Minor UI refinements and fixes
- Improved configuration file validation
- Better error handling for corrupted config files

### Improvements
- Enhanced compatibility with latest CastleForge framework
- Optimized performance when loading large configuration files
- Clearer error messages for configuration issues

---

## Version 2.0.0

### New Features
- **Unified Configuration Interface**: Single UI for configuring all mods
- **Multi-Type Support**: Handle text, numeric, boolean, and selection-based settings
- **Profile Management**: Save and load different configuration profiles
- **Real-Time Settings**: Change settings and see effects without restart
- **Validation System**: Built-in validation to prevent invalid settings

### Features
- User-friendly configuration editor
- Support for custom setting types
- Configuration persistence across sessions
- Integration with mod loader system
- Support for default configurations

### Technical
- JSON-based configuration storage
- Mod integration API for developers
- Schema validation for mod configurations

---

## Installation & Update

To update to v2.0.1:
1. Download the latest Config.dll
2. Replace the old DLL in your `!Mods\Config\` folder
3. Restart CastleForge

Configuration files are automatically compatible with newer versions.

---

## For Mod Developers

To add configuration support to your mod:
1. Define a configuration schema in JSON format
2. Use the Config mod API to expose settings
3. Call the Config mod's methods to load/save settings

Example configuration:
```json
{
  "settingName": "value",
  "numericSetting": 42,
  "booleanSetting": true
}
```

---

## Support

For issues or questions, visit: https://github.com/unknowghost0/config_castleminerZ
