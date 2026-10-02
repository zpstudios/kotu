# A380 investigation — 2026-10-02

The original process termination remains unexplained. This change adds evidence for a future occurrence; it does not establish or fix the cause of the 11:41:59 KST crash.

## Evidence

- QA observed both KOTU windows disappear during MP3 pause → F11 list → invalid ZIP in All Readable on v0.371.0. Short comparisons did not reproduce it. See `QA-REPORT-2026-10-02.md` §3.
- `artifacts/qa-20261002/crash-events.json`: Application Error 1000, Microsoft.UI.Xaml.dll 3.1.8.0, exception 0xc000027b, offset 0x3a7515, report ID `4002cd59-c31e-4493-a53d-1e473ad133a3`.
- The Windows WER archive contains only `Report.wer`, with the same IntegratorReportIdentifier. Its APPCRASH signature lists StackHash12_ed3, exception 8000ffff, offset 0. These are different reporting signatures for the same report, not an exception stack or proof of a particular code defect.
- No matching dump or exception stack was available in the QA artifacts, the inspected temporary KOTU log directory, or the user CrashDumps directory. The WER report contains no additional stack/stowed-exception detail.

## Code audit

- `ChildContentHost.Detach` invalidates and disposes the contract session before removing the bottom bar and center. Queued contract notifications recheck session identity. Existing tests cover obsolete callbacks, reentrant state notification, and detach ordering.
- `AudioPlayerView.Dispatch` checks `_tornDown` before enqueue and again inside the callback. `OnUnloaded` sets that flag, stops timers/VU capture, unsubscribes player events, then posts native Stop/Dispose to its worker. Async player creation checks teardown before attachment.
- `ArchiveView` checks attachment/generation when asynchronous opening returns and restores empty state before notifying `ContentOpenFailed`.
- Native media/view teardown, deferred XAML/layout callbacks, previous module history, multiple windows and completed Jobs remain hypotheses. No inspected code path or available crash record proves one is the original cause. Runtime reproduction was not performed in this implementation pass.

## Added diagnostic behavior

Installed builds record All Readable transition boundaries in `%TEMP%\KOTU\content-transitions.log`. Logging works without the optional detailed trace switch. It has no timer or recurring worker. Each rare transition stage flushes a small line through a reused writer. Current and rotated `.log.1` files each stay within 64 KiB; only those two files are retained.

Records contain UTC time, assembly version, process/thread ID, view ID, transition generation, a fixed module-kind whitelist, stage and optional exception type/HResult. File paths, filenames, exception messages, content, and stacks are omitted. Exceptions are recorded and rethrown without changing the application exception policy. Journal I/O errors stop this journal instance and do not escape into the app. Standalone distribution policy prevents all journal writes, including when an explicit path is injected.

`Begin` identifies the target module. `DetachBegin` through `Detached` identify the previous child (or `none`). `CreateBegin` and subsequent attachment/completion/failure stages identify the target. `OpenFailed` can follow `Completed` asynchronously; it means the current child reported failure, not that the whole transition threw. View IDs distinguish multiple All Readable windows; generations distinguish successive transitions in one view. The final line identifies the last recorded boundary, not necessarily the faulting operation.

The installed app registers a fixed AppInstance key before creating App; subsequent processes redirect activation and return. The shared writer therefore has one owning process and serializes multiple views with a lock. External file locks or unsupported concurrent writers may stop diagnostics safely. Persistence favors evidence across process termination; small synchronous writes add some storage-dependent transition cost. No hot-path media event logging was added.

## Validation and recovery

- Related normal Core tests: 26 passed (5 new journal tests plus existing ChildContentHost/ContentContractSession tests).
- Standalone compilation and filtered journal tests: 5 passed, checking that enabled journals cannot create files.
- New tests check immediately flushed lines, omission of private message/module values, concurrent view identity, two-file size limits and latest generation retention, disabled/disposed behavior, and nonthrowing permanent shutdown after I/O failure.
- All Readable WinUI module build passed with 0 warnings and 0 errors. Neither build nor these tests prove runtime crash resolution.
- Final Release/x64 solution build: 0 warnings, 0 errors. All 503 tests passed with normal Windows permissions; the sandbox run had 27 FileOperations failures, which all passed on the same binaries outside sandbox restrictions. Evidence: `artifacts/a380-build.log`, `artifacts/a380-tests-unrestricted.log`, `TestResults/a380-final-unrestricted`.

For a recurrence, preserve both transition files and the corresponding Application Error/WER record before additional navigation can rotate them. A dump or reproducible scenario is still needed for a root-cause repair. Minimal code recovery is to remove the AllReadable journal hooks and `ContentTransitionJournal` (no playback, routing, or archive behavior was modified).

## Release verification

The user explicitly authorized release on 2026-10-02. v0.375.0 was published at 15:25:55 KST with
tag/source `72895f4d4bb7a4653273414c1d5a46abe5b724b7`. GitHub build `36972832177` and release
`36972832219` succeeded. Setup, Portable, Standalone, full/delta packages and three update metadata
files were present (8 assets); the release is neither draft nor prerelease. Installation, installed-file
consistency, startup and Standalone gates passed. This release still adds diagnostics only and does
not establish resolution of the original crash.
