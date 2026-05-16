# FoxSense | CS2 External ESP & Aimbot

FoxSense is a high-performance, undetectable external multi-cheat for Counter-Strike 2. Built in C# with a focus on stealth and performance, it leverages direct syscalls and a low-latency threading model to provide a silky-smooth experience.

## ✨ Features

- **Professional ESP:**
  - **Box & Skeleton:** High-fidelity player boxes and skeleton bone rendering.
  - **Health & Info:** Dynamic health bars, player names, and distance tracking.
  - **Snaplines:** Tactical lines pointing to enemy locations.
- **Precision Soft Aimbot:**
  - **FOV-Based:** Configurable field of view with on-screen visualization.
  - **Smooth Smoothing:** Exponential convergence curve for human-like movement.
  - **Bone Targeting:** Selectable target bones (Head, Neck, Chest).
- **Stealth Architecture:**
  - **Direct Syscalls:** Bypasses userland hooks by reading `ntdll.dll` from disk.
  - **Read-Only Access:** Minimal process rights (`PROCESS_VM_READ` only) to minimize the detection surface.
  - **External Drawing:** High-performance WPF `DrawingVisual` overlay.

## 🚀 Performance

- **Game Thread:** Polling at 1000Hz for near-real-time entity synchronization.
- **Aimbot Thread:** High-priority 500Hz loop for precision corrections.
- **Rendering:** VSync-locked overlay using `CompositionTarget.Rendering`.

## 🛠️ Installation & Usage

1. **Clone the repository:**
   ```bash
   git clone git@github.com:dragonblz/CS2-ESP.git
   ```
2. **Build:** Open the solution in Visual Studio 2022 and build as **Release x64**.
3. **Run CS2:** Set game mode to **Windowed** or **Windowed Fullscreen**.
4. **Launch FoxSense:** Run the executable. Use the GUI to configure your settings.
5. **Hotkey:** Use the Left Alt key (default) to toggle the GUI.

## ⚖️ Disclaimer

This software is for educational purposes only. Use at your own risk. The developers are not responsible for any bans or consequences resulting from the use of this software.

---

*Engineered for performance. Designed for stealth.*
