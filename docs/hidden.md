# Window Hide Debug Log (macOS Elevation Flow)

Date: 2026-02-12

## Goal

When user clicks "Relaunch as Administrator" on macOS:

1. Launch elevated app instance.
2. Hide original (non-elevated) window, but keep original process alive.
3. When elevated instance exits, original process exits.

## What already works

- Elevated instance launches.
- Original process exits when elevated process closes.

## Problem still open

- Original non-elevated window is not hidden during elevated session.

## Attempt 1: Generic `Window` hide/show via reflection

Code path:

- `TrySetCurrentWindowVisible(bool visible)` attempted:
  - `Window.Current`
  - direct `Show()` / `Hide()` method reflection
  - `AppWindow` reflection fallback

Result:

- No runtime errors.
- Window still visible in testing.

Analysis:

- Likely wrong window instance (`Window.Current` can be null/non-primary on Uno desktop).
- Or selected API is no-op on this platform target.

## Attempt 2: Use real main window from `App`

Change:

- Added `App.CurrentMainWindow => (Current as App)?.MainWindow`
- Fallback from `Window.Current` to `App.CurrentMainWindow`.

Result:

- Still not hidden.

Analysis:

- Correct window reference alone is insufficient; hide API likely unsupported/no-op.

## Attempt 3: Direct `AppWindow.Hide()` on Uno desktop

Change:

- Prioritized `window.AppWindow.Hide()` / `Show()`.

Result:

- Build warning proved root issue:
  - `Uno0001: Microsoft.UI.Windowing.AppWindow.Hide() is not implemented in Uno`
- At runtime, behavior stayed unchanged (visible window).

Analysis:

- `AppWindow.Hide()` cannot be relied on for this target (macOS/Uno desktop).

## Attempt 4: Multi-layer fallback strategy

Change:

- Added helper chain:
  - NativeWindow/NativeWrapper reflection (`Hide`, `IsVisible`)
  - CoreWindow visibility setter
  - AppWindow reflection
  - direct Show/Hide reflection

Result:

- Build clean.
- User still reports not hidden.

Analysis:

- Either the runtime wrapper type/method names differ from assumed names.
- Or the invoked methods exist but are not wired to the visible host window.
- Need concrete runtime type/member inspection from active process/runtime assemblies.

## Runtime inspection done so far

Observed by assembly scanning from built output:

- `Microsoft.UI.Xaml.Window` found in `Uno.UI.dll`.
- `Window` surface includes `Visible` getter, `Activate()`, `Close()`, `AppWindow`, `CoreWindow`, `NativeWindow`, `NativeWrapper`.
- `Microsoft.UI.Windowing.AppWindow.Hide()` exists in metadata but is not implemented on Uno target.
- Uno macOS internals present in assemblies:
  - `Uno.UI.Runtime.Skia.MacOS.MacOSWindowWrapper`
  - `Uno.UI.Xaml.Controls.NativeWindowWrapperBase`
  - methods like `Hide`, `set_IsVisible`, etc. exist in types.
- However, there is no confirmation these methods are bound to runtime-visible behavior or affect the actual NSWindow instance.

## Inference

- Most likely fix is to call the exact active native wrapper instance method for the current window (macOS Skia wrapper), not `AppWindow.Hide()`.
- We need precise binding to the concrete runtime object behind `Window.NativeWindow`/`Window.NativeWrapper` and confirm the method is callable and affects window visibility in the current runtime context, particularly on the primary NSWindow.

## Next debug steps planned

0. During elevation launch, log the full type name and method list of `Window.NativeWindow`, `Window.NativeWrapper`, and the actual instance returned at runtime. This helps confirm we are not mistakenly working with a wrapper stub or an inactive instance.
1. Inspect concrete runtime type names + method signatures for:
   - `Window.NativeWindow`
   - `Window.NativeWrapper`
   - `Window.CoreWindow`

3. If wrapper hide is ineffective, use macOS-native fallback (`osascript` / NSApp hide) scoped to parent process only.

## Native macOS Workarounds Considered

If Uno APIs and wrappers fail to control window visibility at runtime, native macOS solutions may be used as a fallback:

### 1. Use `NSApp hide:nil` to hide the entire app (and optionally remove Dock icon)
This can be invoked from native code or via AppleScript:
```bash
osascript -e 'tell application "YourAppName" to hide'
```

Limitations: This hides all windows, not just the main one, and the Dock icon remains visible.

To fully suppress Dock presence, macOS also allows changing the app activation policy:
```objective-c
[NSApp hide:nil];
[NSApp setActivationPolicy:NSApplicationActivationPolicyProhibited];
```

This removes the app from the Dock and prevents UI reactivation until restored.

To restore later:
```objective-c
[NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
[NSApp activateIgnoringOtherApps:YES];
```

Note: This approach is useful for elevation workflows or helper-style behavior, but must be used carefully if the app needs to return to the foreground.

### 2. Use `orderOut:` on the NSWindow instance
If Uno exposes the native `NSWindow`, call:
```objective-c
[window orderOut:nil];
```
However, this requires bridging into native code. Uno may not expose this handle directly.

### 3. Use a native helper binary
A separate Objective-C/Swift tool could:
- Locate your app's main window
- Call `orderOut:` or `[window setIsVisible:NO]`
- Be triggered by your .NET code during elevation launch

### 4. Use dynamic interop (advanced)
If NSWindow pointer can be obtained from Uno internals, P/Invoke + ObjCRuntime calls might allow direct control from managed code. Risky and maintenance-heavy.

These workarounds are last resorts if Uno wrappers remain disconnected from visible NSWindow behavior.

## Attempt 5 (In Progress): Native macOS hide/unhide via Objective-C runtime + trace log

Implemented:

- Added macOS native fallback in `MainViewModel`:
  - `NSApplication.sharedApplication`
  - `hide:` when `visible == false`
  - `unhide:` when `visible == true`
  - using P/Invoke to `/usr/lib/libobjc.A.dylib`.
- Added visibility-path tracing to:
  - `<appdata>/dotnet-uninstall-ui/visibility-debug.log`
- Trace includes which path succeeded/failed:
  - native wrapper
  - core window
  - app window
  - mac native application hide/unhide
  - direct reflection fallback

Reasoning:

- `AppWindow.Hide()` is confirmed unimplemented on Uno target.
- Calling `NSApplication hide:` should hide the app regardless of Uno wrapper behavior.
- Trace log gives concrete runtime evidence instead of inference.

## Attempt 6: Dock icon suppression while hidden (activation policy switch)

Implemented:

- Extended native macOS fallback to also call:
  - `setActivationPolicy:` with `NSApplicationActivationPolicyProhibited` after `hide:`
  - `setActivationPolicy:` with `NSApplicationActivationPolicyRegular` before `unhide:`
  - `activateIgnoringOtherApps:` after restoring.

Reasoning:

- `hide:` alone can leave Dock icon visible.
- `Prohibited` removes Dock presence while parent process remains alive in background.
- `Regular` restores normal app behavior if elevation fails and parent window needs to return.
