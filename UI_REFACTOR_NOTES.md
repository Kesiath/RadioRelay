# RadioRelay Modern UI Refactor — v1.5.0

## What changed

- Reworked the main window into a wider, responsive card-based layout.
- Replaced native light-themed WinForms track bars with a custom dark `ModernSlider`.
- Added rounded `ModernButton`, `ModernCard`, `ModernPanel`, and `StatusBadge` controls.
- Standardized typography on Segoe UI and Consolas for clean Windows compatibility.
- Improved dark combo boxes, field borders, focus states, spacing, and DPI behavior.
- Preserved the activity rail, status indicators, radio readouts, encryption controls,
  PTT bindings, HUD controls, device selectors, audio controls, import/export actions,
  connection controls, logging, and all existing event-handler wiring.
- Radio volume accents now follow each radio's configured HUD color.

## Validation performed

- The complete client source was compiled successfully as a .NET 8 Windows Forms assembly.
- All C# files were parsed for syntax errors.
- `git diff --check` completed without whitespace errors.
- The UI and layout test set compiled successfully, including the new slider/button tests.

The uploaded repository already contains `RelayClientNetworkQualityTests.cs`, which references
`NetworkQualityTracker`, `NetworkQualitySnapshot`, and `RelayConnectionState`. Those classes are
not present anywhere in the uploaded Client, Shared, or Server source, so that unrelated test file
cannot compile against either the original source or this UI refactor. It was not altered.

## Testing the packaged build

1. Extract `RadioRelay-ModernUI-v1.5.0-win-x64.zip` completely.
2. Run `RadioRelay.exe` from the extracted folder.
3. Existing configuration files remain compatible; keep a backup before replacing an older build.

The packaged build is self-contained for 64-bit Windows and includes the existing runtime files
from the supplied release output with the newly compiled v1.5.0 client assembly substituted in.

## 1.5.1 startup hotfix

- Enabled `ControlStyles.SupportsTransparentBackColor` on `ModernSlider` before assigning `Color.Transparent`.
- Fixes the startup exception: `Control does not support transparent background colors.`
- Added a regression test covering the slider's transparent background contract.

## 1.5.2 resize and field-edge hotfix

- Coalesces live resize messages to a 25 ms UI-thread layout cadence instead of recursively relaying out and invalidating the entire form for every `WM_SIZE` message.
- Skips nested layout entirely when a wide window only needs the fixed-width page recentered.
- Removes duplicate input-host resize/layout handlers and avoids simulated transparent input-host backgrounds.
- Insets rounded input borders by one physical pixel so the right and bottom strokes remain inside the control clip rectangle.
- Preserves the TX/RX/IDLE status pill and its activity color on the far-right side of each radio card.

## 1.5.7 fixed-window render hotfix

- Restores the mandatory one-time post-handle layout pass after DPI scaling.
- Keeps the window non-resizable; no live resize timer or resize event relayout is restored.
- Explicitly reapplies the fixed page width to each top-level page row, performs the TableLayoutPanel layout, and refreshes AutoScroll bounds once when the form is shown.
- Fixes the 1.5.6 regression where only the RadioRelay wordmark rendered and the page content remained blank.

