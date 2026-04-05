# 🔥 FurnaceManager

FurnaceManager is a unified, high-performance furnace plugin for Rust that combines the functionality of both **QuickSmelt** and **FurnaceSplitter** into a single, clean system.

Built for performance, accuracy, and simplicity, FurnaceManager provides **exact smelting control**, **accurate ETA**, **intelligent fuel management**, and **seamless item splitting** — all without the conflicts and inconsistencies of running multiple plugins.

---

## 🚀 Features

### ⚙️ Smelting Control

* Configurable smelt speed multiplier (e.g. 5x)
* Configurable fuel burn rate
* Configurable output multiplier
* Per-oven overrides
* Fully deterministic smelting (no estimation)

---

### 📊 Accurate ETA

* Exact ETA based on real smelt rate
* No guessing, no desync
* Updates smoothly in real-time

---

### ⛽ Fuel Management

* Accurate fuel requirement calculation
* Auto fuel balancing
* Trim excess fuel back to player inventory
* Prevents overfilling

---

### 📦 Smart Splitting (FurnaceSplitter-style)

* Even distribution across all input slots
* Respects existing stacks
* Works with right-click splitting
* Per-player stack count control
* Supports all oven types

---

### 🖥️ UI

* Clean, familiar layout (based on FurnaceSplitter)
* Same position and controls
* Displays:

  * ETA
  * Fuel required
  * Stack controls
* Buttons:

  * **Off** – disable FurnaceManager per player
  * **Trim** – remove excess fuel
  * **< / >** – adjust stack count

---

### 🔐 Permissions

* `furnacemanager.use` — required to use plugin (if enabled)

---

## ⚡ Performance

FurnaceManager is designed to be lightweight and efficient:

* Single unified system (no plugin conflicts)
* Minimal hooks
* Batched UI updates
* No polling-heavy logic
* Efficient per-oven state tracking

Suitable for both small servers and high-population environments.

---

## ⚙️ Configuration

```json
{
  "UsePermission": true,
  "SavePlayerData": true,
  "Ui": {
    "Position": {
      "x": 0.6505,
      "y": 0.022
    },
    "RefreshInterval": 1.0
  },
  "Splitter": {
    "Enabled": true
  },
  "Runtime": {
    "UseObservedEta": true,
    "SampleInterval": 1.0,
    "Smoothing": 0.55,
    "ResetIncreaseThreshold": 5.0
  },
  "Ovens": {
    "*": {
      "Enabled": true,
      "AllowSmelting": true,
      "AllowSplitting": true,
      "AllowAutoFuel": true,
      "AllowTrimFuel": true,
      "ShowUi": true,
      "SpeedMultiplier": 5.0,
      "FuelUsageSpeedMultiplier": 1.0,
      "FuelUsageMultiplier": 1,
      "OutputMultiplier": 1.0,
      "FuelMultiplier": 1.0
    }
  }
}
```

### 🔧 Notes

* `SpeedMultiplier`: Controls smelting speed (e.g. 5.0 = 5x)
* `FuelUsageSpeedMultiplier`: Controls how fast fuel burns
* `FuelUsageMultiplier`: Controls how much fuel is consumed per cycle
* `FuelMultiplier`: Adjusts calculated fuel requirement
* Per-oven overrides are supported

---

## 🧪 Supported Ovens

FurnaceManager supports all `BaseOven` types, including:

* Furnaces (small / large / legacy)
* Electric furnaces
* BBQs
* Campfires
* Fireplaces
* Cauldrons
* Refineries
* Hobo barrels
* Skull fire pits
* And more

Ovens are automatically detected and added to config.

---

## 💬 Commands

### Chat

```
/fm           → Show status
/fm on        → Enable FurnaceManager
/fm off       → Disable FurnaceManager
/fm trim      → Trim excess fuel
```

### Console

```
furnacemanager.enabled true/false
furnacemanager.totalstacks <number>
furnacemanager.trim
```

---

## 🧠 How It Works

Unlike traditional setups:

* ❌ No dependency between multiple plugins
* ❌ No estimated ETA
* ❌ No conflicting logic

FurnaceManager:

* Owns smelting logic
* Tracks real workload
* Calculates exact timing and fuel

Result:

* Perfect synchronization
* Stable performance
* Predictable gameplay

---

## 🏁 Why FurnaceManager?

Instead of:

* Running QuickSmelt + FurnaceSplitter
* Dealing with desync issues
* Fixing broken ETA and fuel calculations

You get:

* One plugin
* One system
* Everything working together perfectly

---

## 🙏 Credits

* Inspired by functionality from:

  * QuickSmelt
  * FurnaceSplitter
* Designed and integrated by **Milestorme**

---

## 📌 Version

**v0.1.0**

Initial unified release of FurnaceManager

---

## 🛠️ Support

If you encounter issues or want new features:

* Test changes on a staging server
* Provide clear reproduction steps
* Keep configs updated after changes

---

Enjoy a clean, accurate, and powerful furnace system 🔥
