# Review: theme input from other people (fix for M2 and M3)

- **Verdict:** merge after fixes — **now:** all findings fixed (see below); merged into main
- **Build/tests:** 650/650; build 0 errors, no new warnings
- **Proof:**
  - Before: on `main`, 45 of 55 new tests failed (bad `ImagePath`/`FontFile` kept on import, network paths resolved, `NullReferenceException`, decimal overflow, screen size not clamped)
  - Before: review fixes: 18 of 21 failed on the reviewed branch (Save copied `\\localhost\C$\Windows\win.ini` into `assets\`, a null character failed the whole import, video asked for 256 MiB frames); tests using new API did not compile
  - After: all pass, 650/650

## Findings
- **Medium M1** — `Theme.Save` still opened UNC and device paths and copied the file in (`Theme.cs:165`, `Adopt`) → `Adopt` goes through `ResolveAsset`; a picked network file is copied in at pick time (`PickedFile.Localize`) — **fixed**
- **Low L1** — One unreadable path (null character, very long) failed the whole import with a raw .NET message (`ThemePackage.cs:141`) → that path is removed as "not a valid path", the theme still imports — **fixed**
- **Low L2** — `ResolveAsset` resolved `\x.png`, `C:x.png` and paths under a network base folder (`Theme.cs:272`) → only fully qualified paths; the combined path is checked too — **fixed** (by design, a theme stored on a share now shows no pictures)
- **Low L3** — The UI crash handler hid every error silently and without limit (`Program.cs:26`) → new `CrashGuard`: first error in the status bar, repeats logged 5 times, a storm shows a "Save and close" dialog — **fixed**
- **Low L4** — A video widget could ask for 256 MiB frames in the runner (`VideoSource.cs:32`) → decode capped at 1920 x 1080, aspect kept — **fixed**
- **Low L5** — A network image showed a blank box with no reason (`Theme.cs:272`) → `Theme.AssetProblem` text shown in orange under the path box — **fixed**
- Nits: N1 duplicate widget ids replaced, N2 test comment and a real `..` test case, N3 AIDA64 import normalized, N4 crash log lock (1200/1200 entries), N5 removed-path wording
- Still open (not review findings): network files picked for an unsaved theme stay in `%TEMP%\Glance\picked`; paths inside media files and reserved device names remain residual risks

## Manual checks for the user
- Open a `.glance` whose `theme.json` has a `\\server\...` picture path: the "cleaned theme" dialog lists it and the path is removed
- Pick a picture from a network share: the status bar says it was copied, and the preview shows it at once
- Type a `\\server\...` path in an image box: an orange line explains why nothing shows
- Open a theme stored on a network folder: no pictures, and the editor explains why
- The crash status text and "Save and close" dialog have not been seen by eye; check them if an error ever happens
