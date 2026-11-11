# Connectify — AI Reference Architecture & Codebase Guide

This document is the authoritative architectural reference for AI coding agents and human reviewers working on Connectify (`bluetooth_turnon`). It details the system layout, layering invariants, hardware data flows, threading model, and domain policies.

---

## 1. System Overview

Connectify is a high-reliability Windows Forms desktop application (.NET Framework 4.7.2) that monitors and manages Bluetooth adapter states, paired devices, battery levels, and audio/input connections using a Bunifu dark-themed UI.

Bluetooth access spans three subsystems:
- **Classic Bluetooth (BR/EDR)**: via `InTheHand.Net.Bluetooth` (32feet.NET).
- **Bluetooth Low Energy (BLE)**: via `Windows.Devices.Bluetooth` and `InTheHand.BluetoothLE`.
- **Windows Runtime & OS Interop**: via `Windows.Devices.Radios` (radio power), Core Audio `IKsControl` (audio reconnect/disconnect), and `SetupDi` (PnP battery queries).

---

## 2. Solution Structure & Directory Layout

```
bluetooth_turnon/
├── Connectify.Core/                     # Pure Domain Logic (.NET Framework 4.7.2, Zero WinForms/WinRT)
│   ├── DeviceClassifier.cs             # Device type classification (Appearance, CoD, heuristics)
│   ├── ConnectRoutingPolicy.cs         # 3-state transport capability & connection routing truth table
│   └── BatteryPolicy.cs                # Fast/slow budgets, exponential backoff, retention, MAC parsers
│
├── Connectify/                          # Main Application (.NET Framework 4.7.2 WinForms)
│   ├── BluetoothManager.cs             # Public Facade (Single source of truth)
│   ├── DevicePresenter.cs              # UI Presenter: DeviceOperationRecord, in-flight tracking, CTS
│   ├── DeviceViewModel.cs              # Decoupled UI view model per device card
│   ├── DeviceCardFactory.cs            # In-place card creation & diff reconciliation (no GDI churn)
│   ├── Form1.cs                        # UI Layer ONLY: WinForms event handling, Bunifu rendering
│   ├── Form1.Designer.cs               # Form layout definitions
│   ├── Program.cs                      # Application entry point & single-instance mutex
│   │
│   ├── Services/                       # Service Layer
│   │   ├── IRadioService.cs / RadioService.cs          # Adapter on/off, bounce protection, registry markers
│   │   ├── IBatteryService.cs / BatteryService.cs      # PnP query, fast/slow GATT discovery, backoffs
│   │   ├── IDeviceEnumerator.cs / DeviceEnumerator.cs  # Paired enumeration, 750ms cache, watchers, HID checks
│   │   ├── IConnectionService.cs / ConnectionService.cs# Per-device semaphores, cancellation, routing
│   │   └── IConnectionStrategy.cs
│   │       ├── BleConnectionStrategy.cs                # WinRT BLE direct connection & GATT sessions
│   │       ├── ClassicAudioConnectionStrategy.cs       # Baseband wake, Core Audio IKsControl, watchdog
│   │       └── ClassicHidConnectionStrategy.cs         # HID service cycling, IOCTL disconnections
│   │
│   ├── Native/                         # Native P/Invoke & COM Interop
│   │   ├── BluetoothNativeMethods.cs   # SetupDi radio enumeration, DeviceIoControl, ole32
│   │   └── CoreAudioInterop.cs         # IMMDevice, IDeviceTopology, IKsControl COM interfaces
│   │
│   ├── Logging/                        # Asynchronous File Logger
│   │   └── AsyncFileLogger.cs          # Thread-safe queue, 1MB rotation, regex MAC privacy masking
│   │
│   └── Common/                         # Async Task Adapters
│       └── WinRtAsyncExtensions.cs     # AsTask() for WinRT IAsyncOperation and IAsyncAction
│
└── argon/                              # AI Reference Codebase (Pure C# source files & documentation)
    ├── ARCHITECTURE_FOR_AI.md          # This reference architecture document
    ├── README.md                       # Reference overview
    ├── Connectify.Core/                # Domain logic files
    ├── Services/                       # Service implementations & interfaces
    ├── Native/                         # Native interop files
    ├── Logging/                        # Async file logger
    ├── Common/                         # WinRT task adapters
    └── Root files                      # BluetoothManager, Presenter, ViewModel, CardFactory, Form1, Program
```

---

## 3. Strict Layering Invariants (`GEMINI.md`)

```
   ┌────────────────────────────────────────────────────────┐
   │         Form1.cs / Form1.Designer.cs (UI ONLY)         │
   │  - Zero Bluetooth APIs (No 32feet, No WinRT, No P/Invoke│
   │  - Bunifu dark-theme rendering and event subscriptions │
   └───────────────────────────┬────────────────────────────┘
                               │ UI events / Data binding
                               ▼
   ┌────────────────────────────────────────────────────────┐
   │                   DevicePresenter                      │
   │  - DeviceOperationRecord (OperationId, Intent, CTS)    │
   │  - Decoupled in-flight tracking from status text       │
   │  - Bounded 1-second fresh hardware state check         │
   └───────────────────────────┬────────────────────────────┘
                               │ Async requests
                               ▼
   ┌────────────────────────────────────────────────────────┐
   │             BluetoothManager (Public Facade)           │
   │  - Single source of truth for all Bluetooth operations │
   │  - Exposes thread-safe events marshaled to Form1       │
   └─────────────┬───────────────────────────┬──────────────┘
                 │                           │
                 ▼                           ▼
   ┌───────────────────────────┐ ┌───────────────────────────┐
   │      Connectify.Core      │ │      Services Layer       │
   │ - DeviceClassifier        │ │ - RadioService            │
   │ - ConnectRoutingPolicy    │ │ - ConnectionService       │
   │ - BatteryPolicy           │ │ - DeviceEnumerator        │
   │ (Pure Domain, Zero UI)    │ │ - BatteryService          │
   └───────────────────────────┘ └───────────────────────────┘
```

1. **Form1 is strictly UI**: Never call 32feet.NET, WinRT, or native Bluetooth APIs in `Form1.cs`. All operations go through `DevicePresenter` or `BluetoothManager`.
2. **Threading & Marshaling**: Background events (watchers, adapter changes, battery updates) fire on thread-pool threads. All UI updates MUST be marshaled via `SafeBeginInvoke`/`SafeBeginInvokeAsync` or `InvokeRequired`. Never call `.Result` or `.Wait()` on UI paths.
3. **Card Reconciliation Without GDI Churn**: Device cards in `panelDeviceList` are reconciled in-place diffing by address (`ulong`). Control handles are preserved across refreshes; never clear and recreate controls unnecessarily.
4. **Intent Preservation**: User intent (`userIntentConnect`) is captured at click time. A bounded 1s fresh-check confirms whether the device is already in the target state. Intent is **never flipped** to the opposite action.
5. **No Blind Radio Bounces**: Automatic radio bouncing is disabled by default (`AllowAutomaticRadioBounce = false`, registry-backed). Manual resets run an asynchronous peripheral scan (`CheckConnectedPeripheralsAsync`) to warn the user before disconnecting active mice/keyboards.

---

## 4. Hardware Data Flow Pipelines

### 4.1 Device Toggle Pipeline (Connect / Disconnect)
```mermaid
sequenceDiagram
    participant User
    participant Form1
    participant Presenter as DevicePresenter
    participant ConnSvc as ConnectionService
    participant Strategy as Strategy (Audio/HID/BLE)

    User->>Form1: Clicks Device Card
    Form1->>Presenter: ExecuteDeviceToggleAsync(device)
    Note over Presenter: Capture userIntentConnect = !device.IsConnected<br/>Assign unique OperationId & CTS<br/>TryAdd to _activeOperations
    Presenter->>Presenter: Bounded 1s Fresh-Check (IsDeviceConnectedAsync)
    alt Already in desired state
        Presenter-->>Form1: DeviceOperationResult.AlreadyInDesiredState()
    else Action required
        Presenter->>ConnSvc: ConnectDeviceWithResultAsync / DisconnectDeviceWithResultAsync
        Note over ConnSvc: Acquire device semaphore lock<br/>Read CoD (codReadSuccess)<br/>Evaluate ConnectRoutingPolicy
        ConnSvc->>Strategy: Route to ClassicAudio, ClassicHid, or BleStrategy
        Strategy-->>ConnSvc: DeviceOperationResult (Success, Failed, etc.)
        ConnSvc-->>Presenter: Return typed result
        Presenter-->>Form1: Return typed result
        Note over Presenter: finally: remove record if OperationId matches;<br/>dispose linked CTS
    end
    Form1->>Form1: Update card UI & schedule RefreshDevicesIfIdleAsync
```

### 4.2 3-State Connection Routing Truth Table
Classified by `ConnectRoutingPolicy.ClassifyTransportCapability`:
- **Inputs**: `isBlePaired`, `installedReadSuccess`, `installedServices`, `codMajor`, `codReadSuccess`.
- **Capability Output**:
  - `ClassicCapable`: Audio sink, Handsfree, or HID service GUID present in Classic installed services.
  - `BleOnly`: Empty services + BLE paired + non-Classic CoD (not Audio 0x04 or Peripheral 0x05).
  - `Unknown`: CoD read failed, or empty services with Classic Audio/Peripheral CoD (preserves Classic routing).
- **Route Determination**:
  - `BleOnly` -> `ConnectionRoute.BleDirect` (`BleConnectionStrategy`).
  - `ClassicCapable` or `Unknown` + Peripheral -> `ConnectionRoute.ClassicWithHid` (`ClassicHidConnectionStrategy`).
  - `ClassicCapable` or `Unknown` + Audio/Other -> `ConnectionRoute.ClassicWithAudio` (`ClassicAudioConnectionStrategy`).
  - *Audio Fallback*: If Classic Audio returns `Failed` or `TimedOut` (and is not 0x04 Audio, cancelled, or radio off), falls back to BLE GATT.

### 4.3 Core Audio Watchdog & Circuit Breaker
- **Execution**: `ClassicAudioConnectionStrategy.TrySendCoreAudioBtCommand` invokes `IKsControl.KsProperty` on a dedicated background thread (`CoreAudio-KsProperty-Watchdog`).
- **Timeout**: 5000ms watchdog (`CoreAudioWatchdogTimeoutMs`).
- **Single Reconnect Rule**: Loop breaks immediately (`break;`) upon triggering reconnect on the first matching endpoint, eliminating duplicate reconnect commands across Stereo and Hands-Free endpoints.
- **Circuit Breaker**: Trips open at `>= 3` consecutive timeouts. Features a **60-second cooldown** after which it transitions automatically to a half-open trial.
- **Resource Safety**: Leaks on abandoned `ManualResetEventSlim` instances are prevented by background joining and disposal.

### 4.4 Destructive Bounce Protection Pipeline
- Controlled radio bounces cycle Bluetooth adapter power as a last-resort recovery mechanism.
- **Safety Checks**:
  1. Default state: `AllowAutomaticRadioBounce = false` (HKCU registry `Software\Connectify\AllowAutomaticRadioBounce`).
  2. In-flight marker: HKCU registry `Software\Connectify\BounceInProgress`. Recovered on next app startup if abnormal shutdown occurs.
  3. Peripheral Guard: `CheckConnectedPeripheralsAsync` detects active connection handles, cached connections, paired BLE HIDs (`System.Devices.Aep.IsConnected`), and Classic paired HIDs without blocking the UI thread.
  4. User Intent Guard: If the user explicitly turns the adapter off, `_userRequestedAdapterOff` aborts any active bounce retry loops and forces radio power OFF. Flag resets automatically when the radio is re-enabled externally.
  5. Internal Power-Down Suppression: During an active bounce, `BluetoothRadio_StateChanged` suppresses `RadioTurnedOff` unless `_userRequestedAdapterOff` is true. `BluetoothManager` guards `CancelDeviceOperations` against `IsRadioBounceInProgress`, ensuring the bounce's own 1-second power-down does not cancel `_deviceOpCts` or abort itself mid-reset.

### 4.5 Battery Hydration & Caching Pipeline
- **Budgets**:
  - Fast cached budget: **2000ms**
  - Slow uncached discovery budget: **10000ms**
- **Levels of Resolution**:
  1. SetupDi PnP Property Cache (`DEVPKEY_Device_BatteryPercentage`).
  2. GATT Battery Service (`0x180F` / `0x2A19`). Values > 100% are rejected.
  3. Last-Good Value Retention: Preserved for **30 minutes** across transient failures.
- **Backoff & Retries**:
  - Exponential backoff: Failure #1 = 30s, Failure #2 = 2m, Failure #3+ = capped at 5m.
  - Maximum retries: **5**.
  - Unsupported devices (Classic-only without PnP/GATT): Cached for **24 hours** (left alone).
  - Successful devices: Supported for periodic refresh every **5 minutes**.
  - Disconnect: Clears in-flight queries and resets failure count to 0 for immediate retry upon reconnect.

### 4.6 Snapshot Cache & Invalidation Pipeline
- **750ms Paired Device Snapshot Cache**: Maintained in `DeviceEnumerator.cs` to eliminate UI stutter and redundant WinRT/SetupDi enumeration during rapid event bursts.
- **Cache Invalidation Triggers (`IDeviceEnumerator.InvalidateDeviceSnapshotCache()`)**:
  1. *Battery Updates*: `BatteryService.BatteryUpdated` invalidates the snapshot cache before invoking `DeviceConnectionChanged`, ensuring newly read battery percentages render immediately on UI cards without waiting for the 60s refresh timer.
  2. *Connection/Disconnection Completion*: `ConnectionService` invalidates the snapshot cache in the `finally` blocks of both `ConnectDeviceWithResultAsync` and `DisconnectDeviceWithResultAsync`, ensuring card post-click refreshes reflect fresh hardware states even on sub-750ms toggles.
- **Race Condition Prevention**: `InvalidateDeviceSnapshotCache()` resets `_ongoingSnapshotTask = null` under `_snapshotLock`. When an awaiting enumeration task finishes, `GetPairedDevicesAsync` only commits `_cachedPairedDevicesSnapshot` if `_ongoingSnapshotTask == taskToAwait`, discarding stale background results that were superseded during invalidation.

---

## 5. Test Suite Verification Reference

Both pre-compiled characterization test binaries run in `Connectify\bin\Debug\` and validate domain logic and solution integration:

1. **`CoreTests.exe` (Pure Domain Suite)**:
   - Target: `Connectify.Core.dll` (Pure C#, zero UI, zero WinRT).
   - Verifies: `DeviceClassifier` (34 appearance/CoD/name heuristics), `ConnectRoutingPolicy` (10 truth-table routes), `BatteryPolicy` (27 budget/backoff/cache checks).
   - Result: **71 / 71 Passed (100%)**.

2. **`SuiteTests.exe` (Full Verification Suite)**:
   - Target: `Connectify.exe` + `Connectify.Core.dll`.
   - Verifies: Classification, Presenter In-Flight Tracking, Address Masking, Startup Validation, Card Reconciliation & Hover State, Bounce Midway Recovery, Handle Recreation Lifecycle, Intent Preservation, 3-State Routing, Core Audio Watchdog & Circuit Breaker, Bounce Safety & HID Detection, Battery GATT Budgets.
   - Result: **201 / 201 Passed (100%)**.
