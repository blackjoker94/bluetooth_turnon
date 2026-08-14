# GEMINI.md — Connectify

<!--
This file loads into context on EVERY message in this project.
Apply the Golden Test before adding any rule:
"Would removing this cause the agent to make mistakes?" If not — cut it.
Do not restate C#/.NET defaults the agent already knows. Only write rules
that override defaults or encode decisions specific to this project.
-->

---

## Project Overview

Connectify (`bluetooth_turnon`) is a Windows Forms desktop app (.NET Framework 4.7.2)
that monitors and manages Bluetooth adapter state and connected devices, using a
Bunifu dark-themed UI. Bluetooth access goes through `InTheHand.Net.Bluetooth`
(32feet.NET) for Classic, `InTheHand.BluetoothLE` for BLE, and Windows Runtime
APIs (`Windows.Devices.Bluetooth`) where needed for adapter state and battery info.

---

# Section A — Architecture & Separation of Concerns (YOU MUST FOLLOW)

## 1) Layering

- `Form1.cs` / `Form1.Designer.cs` = UI ONLY: layout, event wiring, rendering
  state onto Bunifu controls. **Zero Bluetooth API calls or business logic here.**
- `BluetoothManager.cs` = all Bluetooth logic: adapter on/off, device discovery,
  connection state, battery queries. This is the single source of truth for
  Bluetooth state — the form never talks to 32feet.NET, BLE APIs, or WinRT directly.
- If logic doesn't fit either bucket cleanly (e.g. mapping a raw device object to
  a UI-friendly view model), put it in a small dedicated class
  (`DeviceViewModel.cs`, `BluetoothStateMapper.cs`) — don't let it leak into
  `Form1.cs` "just this once."

## 2) Threading (IMPORTANT — this is the #1 source of bugs in this app)

- Bluetooth adapter/device events (from 32feet.NET, BLE watchers, or WinRT) fire
  on background threads — **never touch a Bunifu control directly from those
  callbacks.**
- Always marshal back to the UI thread before updating controls:
  `if (InvokeRequired) { Invoke(...); return; }` or `SynchronizationContext.Post`.
- Never block the UI thread on Bluetooth I/O (device queries, battery reads,
  adapter toggling). Use `async`/`await` with `Task.Run` for blocking/native calls;
  keep `Form1`'s event handlers `async void` only at the top-level UI entry point.

## 3) Error Handling

- `BluetoothManager` catches all exceptions from the underlying Bluetooth APIs
  (adapter not present, permission denied, device unreachable, BLE service not
  supported) and surfaces them as a typed result/event — never let a raw
  exception from 32feet.NET or WinRT propagate into `Form1`.
- Explicitly handle: adapter absent, adapter off, no paired devices, device
  battery unavailable (many BLE devices don't expose battery service — don't
  assume every device has one). No silent failures — surface a status string
  or state enum the UI can render.

## 4) Change Discipline

- Make the smallest change that solves the problem.
- Don't refactor `Form1.Designer.cs` layout code unless explicitly asked —
  designer files are fragile and hand-edits can break the WinForms designer.
- Never change existing Bunifu control names/types casually — the designer
  file and code-behind must stay in sync.

## 5) Dependencies

- Don't add new NuGet packages without justification.
- Any new Bluetooth-related package must be checked for BLE + Classic support
  and .NET Framework 4.7.2 compatibility before adding — many modern Bluetooth
  libraries target .NET 6+/8+ only and won't build here.

## 6) Security

- Never hardcode device MAC addresses, pairing PINs, or tokens.
- Validate any external input (paired device list, service UUIDs) before use.

---

# Section B — C# / WinForms / Bunifu Specific Rules

## 1) UI Controls

- Use Bunifu equivalents (`BunifuButton`, `BunifuToggleSwitch`, `BunifuCards`,
  etc.) consistently — don't mix in stock WinForms controls for the same
  purpose (e.g. don't add a plain `Button` next to `BunifuButton`s).
- Device list/status cards are dynamically generated — when adding a new card
  type, follow the existing card-creation pattern in `Form1.cs` rather than
  hand-placing controls in the designer.

## 2) Bluetooth State Handling

- Adapter on/off toggle must read the *actual* current adapter state on form
  load and after every toggle — never assume the UI toggle state matches
  reality; Bluetooth can be turned off from Windows settings outside the app.
- Device discovery/refresh should be re-entrant-safe: guard against overlapping
  scans if the user mashes "refresh" or toggles Bluetooth rapidly.
- Battery percentage: treat as optional/nullable per device. Render "N/A" or
  hide the metric rather than showing a stale or zero value when unavailable.

## 3) Async Patterns

- `BluetoothManager` methods that hit hardware/OS APIs return `Task<T>` (or
  raise events for ongoing state like device-connected/disconnected).
- No `.Result` or `.Wait()` on async calls anywhere — this deadlocks WinForms
  UI-thread callers. Always `await`.

## 4) Disposal

- Dispose Bluetooth watchers/listeners (BLE advertisement watchers, adapter
  event subscriptions) in `Form1`'s `FormClosing`/`Dispose` — leaving them
  running leaks handles and can keep the process alive after the window closes.

## 5) Workflow

- Before marking any task done, verify: no Bluetooth call happens on the UI
  thread's synchronous path, no unhandled exception path from
  `BluetoothManager` reaches `Form1` unguarded, and any new device metric
  handles the "not supported by this device" case.