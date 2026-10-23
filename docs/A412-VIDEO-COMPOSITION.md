# A412 — video composition and genuine layout ordering

## Scope and current status

Selected by the user's 2026-10-23 continuous development instruction, following A398.
Version **0.405.0**, baseline product `4c65d21` (v0.404.0), with main-owned documentation
commit `e34a34c` already present. Main coordinates and reviews; GPT-6 Astra/high implements
the bounded batch. **Main accepted the bounded normal-entry rendered playback gates and cleanup
on2026-10-23 after reviewing source, checks and decisive saved screenshots.** Release verification
is pending. The limits in the final acceptance section remain; this is not exhaustive A412 completion.

## Evidence and implementation

The prior main observations in `A411-VIDEO-FIT.md` reproduced half-size output after app100→200,
and approximately three-quarter output after system-default150→200, including baseline v0.398.0.
These are main observations, not a new user report. The installed LibVLCSharp3.10.0 diagnostic
then traced genuine logical SizeChanged before CompositionScaleChanged. The former updates
native size metadata using the previous composition scale; the latter updates only the inverse
matrix. Source and trace evidence is retained under `artifacts/a412/agent/`.

`CompositionSynchronizedVideoHost` holds only the last genuinely arranged child rectangle when
a new positive final rectangle arrives before its target composition scale. The public composition
event invalidates arrange once; the next layout pass uses the latest actual final rectangle and
current target. No queued scale or rectangle from an earlier transition is replayed.
The approved lifecycle augmentation also arms one100ms worker deadline per pending hold;
its guarded UI callback releases the latest genuine final rectangle when a public composition
callback is missing. Rapid requests do not move that deadline. If scale still mismatches,
the failed target is latched within the current surface lifetime instead of retrying.

The single existing `Vlc` control becomes a small `CompositionVideoView` subclass. Its supported
OnApplyTemplate override calls the library first and reports template completion. The host
discovers at most128 visual-tree nodes on actual lifecycle notifications, accepts exactly one
SwapChainPanel, and removes named subscriptions when the child/template/host lifetime changes.
No permanent LayoutUpdated discovery, periodic timer, polling, forced layout, fabricated SizeChanged,
size nudge, private API, reflection, pointer patch, control/player recreation, or package update
is part of the product implementation.

## Coordinate precision correction

Initial numeric tests exposed float storage in Windows.Foundation.Point: translated unit samples
produced2.0000038147 instead of2. Native testing of the first implementation then established a
real convergence failure: at translation320, app100 held675×375 instead of1350×750; at1200,
app200 and app100 held900×500. This failed evidence is retained in
`artifacts/a412-implementation/agent/probe/trace-before-precision.json`.

The approved correction samples the origin and two1024-unit axis vectors, then normalizes their
differences to per-unit scale. Only coordinate sampling changes; actual child geometry is untouched.
Each axis must be positive, finite, and axis aligned. Root rasterization is multiplied once.
Composition matching retains `1e-5 × max(1, |target|, |actual|)` on each axis.

Float-coordinate uncertainty is conservatively bounded by the sum of the endpoint magnitudes
times2^-23 plus two minimum float subnormals, divided by1024 and multiplied by rasterization.
This overestimates round-to-nearest coordinate error rather than assuming exact double precision.
If it exceeds one quarter of `1e-5 × max(1, |target|)`, the host uses immediate normal arrange.
Extreme translations therefore fail open instead of retaining a stale rectangle indefinitely.

## Contract and preservation review

| Case | Behavior |
| --- | --- |
| Composition already matches / early event | Immediate genuine final arrange |
| Late or missing composition event | Previous genuine rectangle until matching public event or one-shot deadline; latest layout wins |
| Rapid return to original target | Current target and latest size are re-read; no stale transition queue |
| First positive layout | Normal arrange permits surface/options/player initialization |
| Zero dimension after a prior positive arrangement | Zero commits zero; subsequent positive mismatch can hold that genuine zero until progress/deadline |
| Infinite measure constraint | Native child.Measure receives constraint; returned child.DesiredSize is finite |
| Invalid root, transform, scale, or API availability | Immediate normal arrange |
| Absent, ambiguous, oversized template tree | Synchronization disabled; normal arrange |
| Unload, child replacement, template replacement | Old subscriptions removed, timer disposed, generation advanced, geometry/positive-history/failed-target cleared |
| Identical logical size with composition-only change | Explicitly unsupported by this mechanism; no artificial resize |

The current LibVLC, player, media, audio, time, pause, rate, tracks, resume/history, EOF/loop
decisions and A411 fit guards are unchanged. `VideoPlayerView.xaml.cs` is unchanged. A411's
SurfacePixelSize reads the **outer VideoSurface grid**, so held inner Vlc geometry does not
enter its fit calculation. Its low-priority callback still checks current mode and teardown.
EOF Clear currently runs on the outer grid's SizeChanged and may precede final inner resize;
normal rendered EOF acceptance must resolve that risk before claiming it passed.

## Verification boundary

Focused numeric tests cover translated320/1200 axes, anisotropic positive scale, rotation/shear/
reflection rejection, invalid/overflow values, exact relative-tolerance boundaries, and extreme
coordinate fallback. Earlier failed test output is retained; it is not counted as a passing run.

The artifact-only native probe instantiates the **actual production host and subclass**, without
decoding a video. It exercises late/early ordering, rapid return, spaced rapid transitions,
translated surfaces, zero/restore, unsupported shear, valid/ambiguous/recovered templates,
unload/reload, and infinite measurement. Public size-event inputs infer library metadata according
to the installed implementation. This is event/metadata evidence, not rendered playback proof.

Normal Program.Main production verification remains required: same fixed viewport100→200,
150→200 and back/default, playing/paused bordered video, small-video shrink-only, fit modes,
Original/manual zoom, seek/time, EOF/resume, independent windows and close/lifecycle. Audio
continuity must not be claimed from visual evidence alone. OS-DPI-only, other monitors, and
composition-only changes with unchanged final logical dimensions remain outside this proof.
The native probe uses a fresh settings backup and does not open user media.

Build, TRX, static checks, lifecycle evidence and preservation records are kept under
`artifacts/a412-implementation/agent/`. Final local counts and native results will be appended
after they finish and main reviews the batch, before normal UI work.

## Pre-UI review checkpoint — 2026-10-23 21:57 KST

**Release build0 warnings/errors; focused65/65; complete801/801 across eight assemblies;
architecture20; release gates7. Native convergence gate is not fully satisfied.** Product
source is frozen at this checkpoint, pending main's design decision; normal UI has not started.

The final production-host probe exits0 and produces no managed exception. It verifies finite
infinite-constraint measurement, late/early transitions, rapid return, spaced rapid transitions,
all320-offset transitions, template replacement/recovery, zero geometry and unload/reload.
The final event summary is `agent/probe/event-summary-final.json`; raw trace is
`agent/probe/trace-final.json`. These results establish only the observed public layout behavior.

Two important limitations surfaced:

1. At translated1200→app200, the root was moved outside the current window viewport. The public
   composition property changed to2, but **no CompositionScaleChanged callback was delivered**
   in the trace. The host remained900×500 while its real final bounds were675×375. This differs
   from the earlier coordinate-rounding problem; no retry, timer or watchdog was added. Later
   genuine layout recovered. An off-viewport missing-event case requires a main design decision.
2. Zero→positive combined with app200→100 commits positive geometry immediately, as the approved
   initialization contract requires. The size callback sees old composition2; composition then
   becomes1 with no later genuine size delta. Inferred metadata is2700×1500 against1350×750.
   Normal geometry convergence does not establish native-output convergence for this transition.

Unsupported shear and ambiguous-panel paths correctly arrange immediately, but their inferred
metadata can remain stale; they are explicitly safe layout fallbacks, not refresh successes.
Extreme-coordinate fallback likewise commits real geometry without claiming a universal repair.
The first1200-offset missing-event case prevents treating this diagnostic as a complete native pass.

Check reruns were tied to changes: initial focused/full failed an unrealistic10-decimal assertion;
`full-final` followed its float-aware test correction; `full-precision` followed the1024-sample and
uncertainty behavior change; `full-accepted` followed tightening the conservative coordinate
error bound from2^-24 to2^-23. The accepted run is the final evidence; no additional unchanged
whole-suite runs are planned.

At21:57:04, no owned KOTU process remained; fresh Sky inventory found no KOTU/probe window.
Current settings and the fresh backup both hash
`F11284486319B6C38F988A3556CF6076B3CD2163F2156040E2109AE8242C73D9`; no restoration write was needed.
No user media or fixtures were opened or modified by the diagnostic.

## Approved lifecycle transplant checkpoint — 2026-10-23 22:25 KST

Main approved transfer of the artifact lifecycle candidate. Only the product host and this batch
report changed during the augmentation. Numeric helper, subclass, tests, XAML, version0.405.0
and player/fit/EOF code retain their previous hashes. The artifact-only injection, counters,
trace calls, deferred callback and public state inspectors were removed; the normal callback
is directly enqueued with the same generation, loaded-state and exact-owner guards.

The worker one-shot timer is armed once per pending genuine size hold, with100ms due time and
infinite period. Normal progress/commit and detach dispose it and advance the generation.
Failed enqueue disposes the captured timer without worker access to UI properties. An accepted
deadline forces the next genuine final arrangement once, then only a still-mismatching current
target is latched. A distinct target or a new bound surface lifetime clears the latch. First
positive layout remains normal; prior positive history allows a later genuine zero to be held.
The deadline bounds scheduling plus UI/layout latency, not hard real-time completion.

New-source verification: **Release build0 warnings/errors; focused65/65; full801/801 across eight
assemblies; architecture20; release gates7; seam/escape/XML/brace checks pass.** The first focused
test invocation stalled in the sandbox without a result; its exact owned process tree was stopped
and its log retained. The unchanged focused command passed outside the sandbox, followed by the
serial full suite. No failed test is relabeled as a pass.

Evidence: `artifacts/a412-final/agent/` contains build/test logs, TRX summaries, source preservation,
`transplant.diff` against the prior product and `prototype-strip.diff` against the approved artifact.
Lifecycle evidence remains the original37/37 geometry samples,6 accepted deadlines and20 normal
cancellations under `artifacts/a412-lifecycle/agent/`; it is not a rendered-media result and was
not redundantly rerun. Four metadata-inference boundaries remain documented there.

Normal production publish/UI has not started. Main review of this exact transplant/checkpoint is
required next. The rendered EOF gate must run before proposing any additional EOF notification;
no new EOF hook was added. First-positive cold startup, actual frames, playback state and other
native acceptance items remain open. No commit/push/release is claimed.

## Normal production acceptance — 2026-10-23 22:59 KST

Main accepted the exact lifecycle transplant and new-source checks, then authorized normal
production publish/native testing. The normal `Program.Main`, `Standalone=false` package has no
artifact harness/seams; package/VLC checks passed. Source remains frozen at host SHA256
`06245925398DCD45C50CDFDCA9B3344B12D0030B4CEDBB3D642F172B3054BF36`.

Actual bordered media passed the observed fixed-viewport paused200→100→200→150→200 transitions,
playing200→100/100→200 and decisive large playing150→200 repeat (time1:55→2:12). Cold100 shows
all four borders before any resize/scale input; no anomaly required a v404 comparison. The
playing repeat exposes correct top/left/full-width extent, with lower/right portions occluded
by Settings/toolbar; it is not an all-four-border claim. The original black medium capture is
retained and excluded from rendered-pass evidence; its cause remains unresolved.

EOF observation: the final frame is still present at3:00, then200→100 clears it;100→200 remains
clear, including the final unoccluded view. No additional EOF hook was needed. Portrait fit
modes and Original at fixed200, small native cap, built-in MP4 playback/pause, Next/Prev,
synthetic resume and independent window close were exercised. Two paused video windows alive
during100→200 retain large0:17 and small1:42 with correct exposed frame geometry. Alt+Escape
changes stacking for inspection without resizing either maximized video viewport.

Limits remain explicit: no live Original scale-change claim, manual modifier-wheel/audio
audibility untested, exact first reopened small-frame time uncaptured; Prev portrait shows1:11
matching saved71.126s at UI precision. Rapid/culling deadlines retain artifact-only coverage.
One background capture contains the foreground window's pixels, so decisive concurrent evidence
uses foreground snapshots; this does not establish the earlier black capture's cause.

Detailed evidence: `artifacts/a412-final/agent/native-observations.md`, `native/`, package logs
and `preservation-after.json`. Main independently reviewed decisive PNGs. At22:59:02 no KOTU
process/window remained; fresh settings were restored exactly to SHA256
`F11284486319B6C38F988A3556CF6076B3CD2163F2156040E2109AE8242C73D9`, and all four AVI fixtures plus
manifest retained their hashes. No user media was opened. Final release/reporting belongs to
main; no commit, push or release is claimed by this delegate.
