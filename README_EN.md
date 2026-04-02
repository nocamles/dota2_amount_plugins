[English](README_EN.md) | [中文](README.md)

# Dota 2 Real-time Net Worth Overlay

A lightweight real-time Net Worth overlay for Dota 2 players. It retrieves live game data via the official **Game State Integration (GSI)** and displays the hero's total net worth (gold, item value, Aghanim's Shard, and Scepter bonuses) in a sleek, floating window.

## 📖 Background

In Dota 2, players often need to manually calculate or check the scoreboard to estimate their economic status. To provide better intuition for key item timings and buyback management, this project provides a real-time overlay. It offers data-driven support and automatically toggles visibility based on game state to ensure zero interference with gameplay.

## 📊 Data Sources

1.  **Dota 2 GSI (Game State Integration)**: The core data source. By configuring a GSI file in the Dota 2 client, the app receives JSON packets containing:
    *   `player`: Real-time gold.
    *   `items`: Item names in all slots.
    *   `hero`: Status of Aghanim's Shard and Scepter buffs.
2.  **`item_price.json`**: An internal item price database. The app maps item names from GSI to this JSON to calculate the total value of all equipment.

## 🛠️ Tech Stack & Development

*   **UI Framework**: Built with **PyQt6** for high-performance, transparent, and click-through Windows overlays.
*   **Backend Service**: **Flask** runs a local HTTP server (Port 3000) to receive POST data from Dota 2.
*   **State Management**:
    *   **GSI Cache Pool**: Solves the data fluctuation issue caused by GSI's delta updates.
    *   **Multi-threading**: Separates the Flask server, UI thread, and Dota 2 process monitor.
*   **Interaction**:
    *   Global hotkey support via the `keyboard` library (Default: `Ctrl+Alt+F10`).
    *   Persistent configuration (`overlay_config.json`) for window position, lock state, and hotkeys.

## ✨ Features

*   **Real-time Display**: Auto-calculates Gold + Items + Shard/Scepter buffs.
*   **Smart Visibility**: 
    *   Automatically hides when Dota 2 is not running or the player is in the main menu.
    *   Only shows during matches or Demo mode.
*   **Custom Positioning**: Supports dragging when unlocked; supports click-through when locked to avoid interfering with game clicks.
*   **Global Hotkey**: Toggle Lock/Unlock state instantly with a key combination.
*   **System Tray Support**: Run in background with tray icon for quick settings and exit.

## 📸 Screenshots

### 1. In-game Overlay
![Default Style](img/默认状态样式.png)
*Figure: Default display during a match.*

### 2. Dragging & Layout
![Dragging Style](img/拖动状态样式.png)
*Figure: The window becomes draggable after unlocking.*

### 3. Hotkey Settings
![Hotkey Settings](img/设置拖动全局快捷键.png)
*Figure: Customize any key combination as the global toggle.*

### 4. Tray Management
![Tray Menu](img/右键任务栏图标.png)
*Figure: Right-click the tray icon to modify settings or quit.*

## 🚀 Quick Start

### 1. Configure Dota 2 GSI
1.  Navigate to your Dota 2 installation folder: `game\dota\cfg\`.
2.  Create a folder named `gamestate_integration` (if it doesn't exist).
3.  Create a file named `gamestate_integration_networth.cfg` and paste the content from `GSI_Config.txt`.

### 2. Run the Plugin
1.  Ensure Python is installed.
2.  Install dependencies:
    ```bash
    pip install PyQt6 Flask keyboard
    ```
3.  Run the application:
    ```bash
    python main.py
    ```

---
*This project is for educational purposes. Please comply with Dota 2's Terms of Service.*
