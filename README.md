# 🦊 FoxSense — CS2 External Cheat

A fully external Counter-Strike 2 cheat built in C# / WPF. Operates entirely in userland with no DLL injection, no hooks, and no kernel drivers.

## Features

### 👁 ESP (Extra Sensory Perception)
- **Box ESP** — 2D bounding boxes around players
- **Skeleton ESP** — Full bone skeleton rendering
- **Health Bar** — Color-coded health indicators
- **Name ESP** — Player name tags
- **Distance** — Distance in meters
- **Snap Lines** — Lines from crosshair to targets
- **Enemy-only filtering**
- **Custom RGB color** per-element

### 🎯 Soft Aimbot
- Configurable **FOV radius** (20–250)
- Adjustable **smoothing** (1–15)
- Bone target selection (Head / Neck / Chest)
- Custom aim key binding
- FOV circle visualization
- Enemy-only targeting

### 🎨 Skin Changer
- **Real-time skin database** fetched from community API
- Skin preview images in selection list
- Supports **legacy model skins** (Asiimov, Dragon Lore, etc.)
- Aggressive mesh mask override for instant application
- Attribute injection via `RegenerateWeaponSkins`
- No team switch required — skins apply instantly

## Architecture

```
FoxSense/
├── Core/
│   ├── Memory.cs          # Process memory R/W via Win32 API
│   ├── Offsets.cs          # Game offsets + bone indices
│   ├── SkinOffsets.cs      # Skin changer specific offsets
│   └── OffsetUpdater.cs    # Auto-update offsets from API
├── Features/
│   ├── EspRenderer.cs      # WPF overlay rendering
│   ├── SoftAim.cs          # Aimbot logic + mouse movement
│   ├── SkinChanger.cs      # Paint kit injection engine
│   └── SkinDatabase.cs     # Skin catalog from API
├── Game/
│   ├── GameState.cs        # Entity list + bone reading
│   ├── ViewMatrix.cs       # World-to-screen projection
│   └── PlayerData.cs       # Player data structures
├── Overlay/
│   └── OverlayWindow.cs    # Transparent WPF overlay
└── UI/
    └── MainWindow.xaml      # Professional sidebar GUI
```

## GUI

Professional dark-themed interface with:
- **Left sidebar** navigation (ESP / Aimbot / Skins / Settings)
- **Pill-style toggle switches**
- **Grouped settings** in dark card sections
- **Deep navy color palette**
- Draggable, borderless window
- GUI toggle hotkey (default: LAlt)

## Requirements

- Windows 10/11
- .NET 8.0 Runtime
- Counter-Strike 2 (Steam)
- Run as **Administrator**

## Building

```bash
dotnet build --configuration Release
```

## Anti-Cheat Notes

- **Fully external** — no DLL injection
- **Userland only** — no kernel drivers
- Reads game memory via `ReadProcessMemory`
- Skin changer uses temporary remote memory allocation
- No persistent hooks or code patches

## Disclaimer

This project is for **educational purposes only**. Use at your own risk. The authors are not responsible for any bans or consequences resulting from the use of this software.

## License

MIT
