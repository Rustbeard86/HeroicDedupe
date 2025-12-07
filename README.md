# HeroicDedupe

A smart deduplication tool for [Heroic Games Launcher](https://heroicgameslauncher.com/) that identifies and hides duplicate games across your Epic, GOG, and Amazon Gaming libraries.

![.NET 10](https://img.shields.io/badge/.NET-10.0-purple)
![License](https://img.shields.io/badge/license-MIT-green)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-blue)

## Features

- **Multi-Store Support**: Scans Epic Games (Legendary), GOG, and Amazon Gaming (Nile) libraries
- **Smart Matching**: Groups games by normalized titles, handling trademark symbols, edition suffixes, and naming variations
- **Intelligent Prioritization**: Configurable store priority with optional preference for enhanced editions (Remastered, Definitive, GOTY)
- **IGDB Integration**: Optional metadata enrichment via IGDB API for accurate release dates
- **Safe by Default**: Dry-run mode previews changes before applying
- **Local Caching**: IGDB lookups are cached locally for fast subsequent runs

## Quick Start

### Prerequisites

- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- [Heroic Games Launcher](https://heroicgameslauncher.com/) installed

### Installation

1. Download the latest release from [Releases](https://github.com/Rustbeard86/HeroicDedupe/releases)
2. Extract to a folder of your choice
3. Edit `appsettings.json` to configure your preferences

### Usage

```bash
# Preview duplicates (safe mode - no changes made)
./HeroicDedupe.exe

# Apply changes (hides duplicates in Heroic)
./HeroicDedupe.exe --live
```

## Configuration

Edit `appsettings.json` to customize behavior:

```json
{
  "DryRun": true,
  "Priority": ["Gog", "Epic", "Amazon"],
  "PreferEnhancedEditions": true,

  "HeroicConfigPath": "%AppData%\\heroic\\store\\config.json",
  "LegendaryLibraryPath": "%AppData%\\heroic\\store_cache\\legendary_library.json",
  "GogLibraryPath": "%AppData%\\heroic\\store_cache\\gog_library.json",
  "NileLibraryPath": "%AppData%\\heroic\\store_cache\\nile_library.json",

  "Igdb": {
    "Enabled": false,
    "ClientId": "",
    "ClientSecret": ""
  }
}
```

### Options

| Setting | Description | Default |
|---------|-------------|---------|
| `DryRun` | Preview mode - no changes written | `true` |
| `Priority` | Store preference order (first = highest priority) | `["Gog", "Epic", "Amazon"]` |
| `PreferEnhancedEditions` | Prefer Remastered/Definitive editions over store priority | `true` |
| `HeroicConfigPath` | Path to Heroic's config.json | Windows AppData path |
| `Igdb.Enabled` | Enable IGDB metadata enrichment | `false` |
| `Igdb.ClientId` | Twitch API Client ID | - |
| `Igdb.ClientSecret` | Twitch API Client Secret | - |

### Command Line Overrides

```bash
--dry-run    # Force dry-run mode (safe preview)
--live       # Force live mode (apply changes)
```

### Linux Configuration

For Linux users, update paths in `appsettings.json`:

```json
{
  "HeroicConfigPath": "~/.config/heroic/store/config.json",
  "LegendaryLibraryPath": "~/.config/heroic/store_cache/legendary_library.json",
  "GogLibraryPath": "~/.config/heroic/store_cache/gog_library.json",
  "NileLibraryPath": "~/.config/heroic/store_cache/nile_library.json"
}
```

## IGDB Integration (Optional)

For more accurate release date matching, you can enable IGDB metadata enrichment:

1. Create a Twitch Developer application at https://dev.twitch.tv/console/apps
   - **Name**: Any name (e.g., "HeroicDedupe")
   - **OAuth Redirect URLs**: `http://localhost`
   - **Category**: Application Integration
   - **Client Type**: Confidential

2. Copy your **Client ID** and generate a **Client Secret**

3. Update `appsettings.json`:
   ```json
   "Igdb": {
     "Enabled": true,
     "ClientId": "your-client-id",
     "ClientSecret": "your-client-secret"
   }
   ```

> **Note**: First run with IGDB enabled will take several minutes to fetch metadata. Results are cached locally in `igdb_cache.json` for 30 days.

## How It Works

1. **Reads** game libraries from Heroic's cache files
2. **Normalizes** titles by removing trademark symbols (®™©), edition suffixes, and special characters
3. **Groups** games with matching normalized titles
4. **Sorts** each group by priority:
   - Enhanced editions first (if `PreferEnhancedEditions` is true)
   - Store priority order
   - Release date (newest first)
5. **Marks** all but the winner as hidden in Heroic's config

### Example Output

```
Match Group [key: bioshock2]
  [KEEP] Gog    | BioShock™ 2 Remastered (1482265668) [Remastered, 2016-09-14]
  [HIDE] Gog    | BioShock® 2 (1806891286) [2010-02-09]
  [HIDE] Epic   | BioShock 2 Remastered (b22ce34b...) [Remastered, 2016-09-14]
--------------------------------------------------
```

## Building from Source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- PowerShell (for build script)

### Build

```powershell
# Clone the repository
git clone https://github.com/Rustbeard86/HeroicDedupe.git
cd HeroicDedupe

# Build debug
dotnet build

# Build release (single-file executable)
./build-release.ps1 -Clean

# Build for Linux
./build-release.ps1 -Runtime linux-x64
```

### Output

Release builds are output to the `release/` folder:
- `HeroicDedupe.exe` (~700 KB)
- `appsettings.json`

## Project Structure

```
HeroicDedupe/
|-- Program.cs              # Application entry point
|-- Models/
|   +-- Models.cs           # Data models (LocalGame, AppConfig, etc.)
|-- Readers/
|   +-- Readers.cs          # Store library readers (Epic, GOG, Amazon)
|-- Services/
|   |-- Services.cs         # Core services (Deduplication, Config modifier)
|   +-- IgdbService.cs      # IGDB API integration with caching
|-- appsettings.json        # Configuration file
|-- build-release.ps1       # Release build script
+-- icon.ico                # Application icon
```

## FAQ

**Q: Is this safe? Will it delete my games?**  
A: HeroicDedupe only modifies Heroic's `config.json` to mark games as hidden. Your actual game files and store libraries are never modified. Hidden games can be unhidden in Heroic's settings.

**Q: Why is my preferred store's version being hidden?**  
A: Check your `Priority` setting in `appsettings.json`. If `PreferEnhancedEditions` is true, a Remastered/Definitive edition from a lower-priority store may win.

**Q: The IGDB lookup is taking forever!**  
A: First run needs to query IGDB for each unique game (~4 requests/second due to rate limits). Subsequent runs use the local cache and are nearly instant.

**Q: Can I undo the changes?**  
A: Yes! In Heroic, go to Settings > Games > Show Hidden Games, then unhide any games you want back.

## Contributing

Contributions are welcome! Feel free to:
- Report bugs or suggest features via [Issues](https://github.com/Rustbeard86/HeroicDedupe/issues)
- Submit pull requests

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- [Heroic Games Launcher](https://heroicgameslauncher.com/) - The awesome open-source launcher this tool supports
- [IGDB](https://www.igdb.com/) - Game metadata API
- [Twitch](https://dev.twitch.tv/) - IGDB API authentication
