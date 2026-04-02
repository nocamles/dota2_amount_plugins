[English](README_EN.md) | [中文](README.md)

# Dota 2 Real-time Net Worth Overlay

A lightweight real-time Net Worth overlay for Dota 2 players. It retrieves live game data via the official **Game State Integration (GSI)** and displays the hero's total net worth (gold, item value, Aghanim's Shard, and Scepter bonuses) in a sleek, floating window.

## 📖 Background

In Dota 2, players often need to manually calculate or check the scoreboard to estimate their economic status. To provide better intuition for key item timings and buyback management, this project provides a real-time overlay. It offers data-driven support and automatically toggles visibility based on game state to ensure zero interference with gameplay.

## 📊 Data Sources

1.  **Dota 2 GSI (Game State Integration)**: The core data source. The application automatically configures the GSI interface in your Dota 2 client when launched, and receives JSON packets containing:
    *   `player`: Real-time gold.
    *   `items`: Item names in all slots.
    *   `hero`: Status of Aghanim's Shard and Scepter buffs.
2.  **`item_price.json`**: An internal item price database. The app maps item names from GSI to this JSON to calculate the total value of all equipment.

## 🛠️ Tech Stack & Development

*   **UI Framework**: Built with **C# WPF** for high-performance, transparent, and click-through Windows overlays.
*   **Backend Service**: Uses C# built-in networking libraries to run a local HTTP server (Port 3000) to receive POST data from Dota 2.
*   **State Management**:
    *   **GSI Cache Pool**: Solves the data fluctuation issue caused by GSI's delta updates.
    *   **Multi-threading**: Separates the HTTP server, UI thread, and Dota 2 process monitor.
*   **Interaction**:
    *   Global hotkey support (Default: `Ctrl+Alt+F10`).
    *   Persistent configuration (`config.txt`) for window position, lock state, and hotkeys.

## ✨ Features

*   **Real-time Display**: Auto-calculates Gold + Items + Shard/Scepter buffs.
*   **Smart Visibility**: 
    *   Automatically hides when Dota 2 is not running or the player is in the main menu.
    *   Only shows during matches or Demo mode.
*   **Custom Positioning**: Supports dragging when unlocked; supports click-through when locked to avoid interfering with game clicks.
*   **Global Hotkey**: Toggle Lock/Unlock state instantly with a key combination.
*   **Auto GSI Configuration**: No manual setup required. GSI configs are automatically deployed upon running.
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

### 1. Run the Plugin
No need to manually configure the Dota 2 GSI; the code configures it automatically.
Simply place the downloaded `exe` file and `item_price.json` in the **same directory** and double-click to run the executable.

---
*This project is for educational purposes. Please comply with Dota 2's Terms of Service.*