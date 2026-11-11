# Connectify — AI Architecture & Codebase Guide

This document is specifically crafted for AI assistants to rapidly understand the Connectify codebase, its architecture, design decisions, threading model, and device operation pipelines.

---

## 1. What Connectify Is
Connectify is a high-reliability Windows Forms desktop application (.NET Framework 4.7.2) that monitors and manages Bluetooth adapter states, paired/connected devices, battery levels, and audio/input connections using a Bunifu dark-themed UI.

---

## 2. Solution Structure & Layering

```
bluetooth_turnon/
├── Connectify.Core/                     # Pure Domain Logic (.NET Framework 4.7.2)
│   ├── DeviceClassifier.cs             # Device type classification (Appearance, CoD, heuristics)
│   ├── ConnectRoutingPolicy.cs         # 3-state transport capability & connection routing truth table
│   └── BatteryPolicy.cs                # Cached & uncached query budgets, exponential backoff, retention
│
├── Connectify/ (and mirrored in argon/)
│   ├── BluetoothManager.cs             # Public Facade (Single source of truth, API-compatible)
│   ├── DevicePresenter.cs              # UI Presenter: Intent, bounded checks, in-flight tracking
│   ├── DeviceViewModel.cs              # Decoupled UI presentation model per device card
│   ├── DeviceCardFactory.cs            # In-place card creation & diff reconciliation (no GDI churn)
│   ├── Form1.cs                        # UI Layer ONLY: WinForms event handling, Bunifu rendering
│   │
│   ├── Services/                       # Service Layer (Strangler Pattern)
│   │   ├── IRadioService.cs / RadioService.cs          # Adapter on/off, manual reset bounce, registry markers
│   │   ├── IBatteryService.cs / BatteryService.cs      # PnP query, fast/slow GATT discovery, backoffs
│   │   ├── IDeviceEnumerator.cs / DeviceEnumerator.cs  # Paired enumeration, watchers, peripheral checks
│   │   ├── IConnectionService.cs / ConnectionService.cs# Per-device semaphore locks, cancellation, routing
│   │   └── IConnectionStrategy.cs
│   │       ├── BleConnectionStrategy.cs                # WinRT BLE direct connection & GATT sessions
│   │       ├── ClassicAudioConnectionStrategy.cs       # Baseband wake, Core Audio IKsControl, service cycles
│   │       └── ClassicHidConnectionStrategy.cs         # HID service cycling, IOCTL disconnections
│   │
│   ├── Native/                         # Native P/Invoke & COM Interop
│   │   ├── BluetoothNativeMethods.cs   # SetupDi radio enumeration, DeviceIoControl, ole32
│   │   └── CoreAudioInterop.cs         # IMMDevice, IDeviceTopology, IKsControl COM interfaces
│   │
│   ├── Logging/                        # Asynchronous File Logger
│   │   └── AsyncFileLogger.cs          # Thread-safe queue, 1MB rotation, MAC address regex masking
│   │
│   └── Common/                         # Async Task Adapters
│       └── WinRtAsyncExtensions.cs     # AsTask() for WinRT IAsyncOperation and IAsyncAction
```

---

## 3. Key Architectural Principles

1. **UI / Hardware Separation (`GEMINI.md`)**:
   - `Form1.cs` never interacts with 32feet.NET, WinRT, or native Bluetooth APIs directly.
   - All connection logic, routing, and hardware I/O flow through `DevicePresenter` -> `BluetoothManager` -> `ConnectionService`.
2. **Strict Thread Marshaling**:
   - OS events (BLE watchers, adapter state notifications) fire on thread-pool background threads.
   - Controls are never touched from background callbacks. UI updates always marshal back via `if (InvokeRequired) { Invoke(...); }` or `SafeBeginInvoke`.
3. **Card Reconciliation Without GDI Churn**:
   - Device cards in `panelDeviceList` are reconciled in-place diffing by MAC address. Existing panels and labels are updated in place, preventing cursor freezing, hover flicker, and WinForms handle recreation.
4. **Intent Preservation (Bug 7 Fix)**:
   - User intent is determined at click time (`userIntentConnect = !device.IsConnected`).
   - Fresh hardware check is bounded to 1 second. If the device is already in the desired state, it is treated as immediate success. Intent is **never flipped** to the opposite action on stale cards.
5. **Typed Operation Results**:
   - Connection strategies return `DeviceOperationResult` with specific `DeviceOperationStatus` reason codes:
     - `Success`, `AlreadyInDesiredState`, `Cancelled`, `AccessDenied`, `RadioOff`, `TimedOut`, `DeviceNotFound`, `PeripheralInputProtected`, `Failed`.
6. **Destructive Bounce Protection**:
   - Controlled radio bounces (adapter toggle fallback) are **disabled by default**.
   - Even if manually enabled, bounces are aborted if any connected HID input device (mouse, keyboard, controller) is detected to prevent dropping user peripherals.

---

## 4. Dependencies
- `InTheHand.Net.Bluetooth` (32feet.NET) for Classic Bluetooth.
- `InTheHand.BluetoothLE` for BLE wrappers.
- `Windows.Devices.Bluetooth` & `Windows.Devices.Radios` (WinRT) for BLE GATT and radio power.
- `Bunifu UI Framework` for the desktop dark theme.
