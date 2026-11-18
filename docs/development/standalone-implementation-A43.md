# A43 standalone executable implementation

The release adds `KOTU-win-Standalone.exe`, separate from the installed executable
and Velopack's `KOTU-win-Portable.zip`. The standalone distribution uses the
compile-time `KotuStandalone=true` property in Core. Filename, directory, marker
files and environment variables never enable standalone mode in an installed build.

## Packaging decision

The official .NET single-file probe was published locally against the current
Windows App SDK 1.8 and v0.372.0 dependencies. Its output contained a 367,791,162-byte
`KOTU.exe` and a separate 1,449,048-byte `resources.pri`, plus PDBs. The existing PRI
copy runs after Publish. Passing single-file properties globally also initially
failed because referenced WinUI module projects did not enable MSIX tooling.
The probe with global `EnableMsixTooling=true` built, but was not claimed to be a
verified one-file application. It was not run as an installed distribution and no
claim is made that PRI was missing inside its bundle: the loose PRI proves the
published folder alone cannot establish that contract.

The chosen launcher instead embeds a ZIP of the complete ordinary self-contained
publish. It uses the Windows .NET Framework 4.x runtime; the app contains .NET 8.
No new runtime, driver or extraction utility is installed. `csc.exe` from Windows'
Framework64 directory builds the launcher. This makes local and CI packaging
independent of a C++ toolchain, IExpress or a downloaded SFX stub.

`eng/Build-Standalone.ps1` publishes with the explicit standalone property, checks
PRI, 7z, ScreenRecorderLib, icons, sample media, recorder license and the VLC engine
and plugin tree, adds LICENSE, THIRD-PARTY-NOTICES and the standalone policy, omits
PDBs from the embedded ZIP, then emits only the launcher as the release asset.
It never takes its input from a Velopack package. Fresh GUID build directories
prevent files from an older publish becoming stale payload entries.

The build locates the latest licensed Visual Studio x64 `Microsoft.VC14x.CRT`
redistribution directory using VCToolsRedistDir, VCINSTALLDIR, vswhere and standard
VS installation paths. `-VcRuntimePath` supports an explicit redistribution source.
Missing sources fail before publishing; Windows system directories are rejected.
All DLLs from that CRT directory are copied app-locally after Microsoft signature
and x64 PE checks. PE import tables from ScreenRecorderLib and every copied CRT
are audited for missing VC runtime dependencies. A Microsoft redistribution notice
ships in `licenses/Microsoft-VC-Runtime-NOTICE.txt`. The app does not install a
shared VC runtime. Windows Media Foundation remains an OS prerequisite; N/KN needs
Microsoft's Media Feature Pack and Windows Server needs its Media Foundation feature.

## Runtime boundary

The launcher creates a new version/GUID folder in the user's temporary directory
with an ACL limited to the current user. It extracts into `app`, gives the child
`scratch` as TEMP/TMP and the WebView2 user-data directory, forwards correctly
quoted arguments and the caller's working directory, and waits for its exit.
ZIP entries must remain below `app`; absolute paths, escaping paths, ADS colons
and duplicate files fail. Cleanup never follows reparse points. Child exit status
is preserved. Cleanup retries for up to 30 seconds for delayed browser/antivirus
handles. Administrator restart uses the outer launcher and the original temporary
base, so the new session cannot be deleted with the old session's scratch folder.
The normal standalone instance key is separate from the installed instance key;
the smoke test uses a fresh key to avoid redirecting into a user's running session.
For smoke runs the launcher generates a fresh nonce and an exact receipt path inside
scratch. The app accepts the smoke flag only with that launcher context and atomically
records the nonce receipt after all policy/UI/native checks finish. The launcher
requires matching receipt contents and no startup-error log before returning success.
Elapsed time is not used as evidence of success. Normal launches clear inherited
smoke receipt variables.

Settings and playback history use thread-safe memory serialization. Direct creation
of persistent JSON settings fails in a standalone build. Velopack startup handling,
all update service entry points, Explorer registration and removal entry points,
legacy cleanup, shell selection files, restart-session read/write/delete and tray
promotion are blocked. Settings UI presents an English session-only explanation;
registration and settings-file controls are absent and Updates has no active UI or
watch timer. Administrator restart starts a fresh session without window restore.
Wallpaper conversion uses TEMP; explicit wallpaper/default-microphone changes and
saved user files/recordings remain normal feature outputs.

This guarantees no KOTU-created persistent settings/session files in AppData and
no KOTU Explorer/tray-promotion registry writes. It does not erase Windows execution
records, Prefetch, antivirus records, OS-maintained tray history or records of
existing runtime/driver services. Killing the launcher, power loss or persistent
locks can leave its private temporary folder. There is no persistent cleanup job
or registry-based reboot deletion. Policy text ships inside the extracted payload.

## Verification and remaining device checks

The locally verified executable with app-local CRT is 162,291,712 bytes. The publish
before PDB exclusion contains 1,072 files totaling 394,390,895 bytes. The embedded ZIP
contains 1,059 files totaling 378,068,059 uncompressed bytes; ZIP extraction preserves
the complete resource/native subdirectory layout. The user downloads one exe.

Local verification passed with warnings treated as errors: standalone publish,
Framework launcher compile, three memory-settings tests, architecture validation,
the release workflow contract (seven bypass regressions) and actionlint. The standalone
packaging regressions and nonce-based smoke rerun also passed after independent review.
This PC has no Visual Studio installation: local packaging verification used the
Microsoft-signed x64 runtime CAB from its existing official VC redist package cache,
extracted only into ignored artifacts. Release CI obtains redistribution files from
its Visual Studio installation. The actual
launcher smoke test reached WinUI and built Settings UI, wrote/saved memory settings,
checked updates were unavailable, initialized libvlc, created/disposed a native
ScreenRecorderLib recorder without recording, and read a ZIP using 7z. It then
closed normally and passed KOTU AppData/product-registry snapshot comparison and
session-folder removal. The expanded snapshot includes product AppData roots (even
empty ones), legacy product folders, file-association values, RegisteredApplications,
shell verbs, the three product COM classes and product-owned UserChoice entries.
OS-maintained MRU, execution and NotifyIconSettings history are deliberately excluded.
Packaging regressions check missing CRT dependencies, prohibited system redistribution
sources, empty AppData root detection, registry ownership filters and nonce receipts.
Two earlier snapshot comparisons detected an AppData change
while an installed KOTU process was also running; those runs do not establish which
process wrote it. An unchanged-state rerun passed.
The snapshot check deliberately fails if concurrent software changes those files.

CI builds and runs the same executable after installer verification and before
upload/publication. Failure blocks release. CI has not been run for these unpushed
changes. Physical-device checks still include real video/audio decoding, actual
screen/microphone recording, HTML rendering/WebView2 shutdown, UAC cancellation and
restart, repeated simultaneous starts, a custom TEMP path, and antivirus/SmartScreen
handling of the unsigned large self-extracting executable. Framework 4.x availability
is an explicit prerequisite for the launcher; no unsupported-machine claim is made.
