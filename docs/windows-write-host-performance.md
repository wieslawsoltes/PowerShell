# Windows Write-Host color-call optimization

Investigation and VM validation: 2026-09-28.

## Scope and design

This branch optimizes the Windows built-in ConsoleHost, not the `Write-Host`
cmdlet or the information stream. Both colored output helpers now obtain the
previous foreground/background together and apply each color pair with one
`SetConsoleTextAttribute` call. The existing lock, output method and `finally`
restoration remain in place. Unix and custom-host implementations are unchanged.

Restoration queries the active console buffer again. It preserves the non-color
bits observed **after** output instead of restoring an obsolete whole attribute
word. Setters still run when the 16-color values appear unchanged: those bits
cannot describe all VT state. Invalid enum values use the original individual
getters/setters, retaining validation order and partial-update behavior.

For a successful short colored write, the source-level model changes from ten
ConsoleHost color operations to four. Including two unspecified-color lookups
in a freshly invoked `Write-Host` and its mode/write calls gives approximately
14 native calls before versus eight after. These are source-derived counts,
not an ETW trace or a prediction of equivalent elapsed-time savings.

## Why retain this functionality?

- The [Write-Host contract](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/write-host?view=powershell-7.6)
  includes host output, colors, separators, newline control and information-stream
  capture/suppression. The patch does not bypass `MshCommandRuntime.WriteInformation`.
- The [official performance guidance](https://github.com/MicrosoftDocs/PowerShell-Docs/blob/main/reference/docs-conceptual/dev-cross-plat/performance/script-authoring-considerations.md#use-write-host-carefully)
  notes that direct console writes bypass transcription and are not supported
  by every host. They are a useful control, not a replacement for `Write-Host`.
- Earlier optimizations [#2674](https://github.com/PowerShell/PowerShell/pull/2674)
  and [#6882](https://github.com/PowerShell/PowerShell/pull/6882) already avoid
  unnecessary information metadata work and combine text/newline output.
  This patch does not claim those existing improvements as new work.
- [#21188](https://github.com/PowerShell/PowerShell/pull/21188) deliberately
  bypasses colored host overloads for `OutputRendering = 'PlainText'`.
  That condition remains unchanged and is covered by the regression tests.
- Windows Terminal's [console attribute implementation](https://github.com/microsoft/terminal/blob/main/src/host/getset.cpp)
  replaces attributes using a legacy attribute word. Its
  [VT forwarding implementation](https://github.com/microsoft/terminal/blob/main/src/host/VtIo.cpp)
  resets VT-only state when forwarding legacy setters. This is why an
  apparently redundant setter must not simply be omitted.
- [xterm.js SGR handling](https://github.com/xtermjs/xterm.js/blob/c58ea3637f3968e0e6e79cd92cf9aace7ef89ee2/src/common/InputHandler.ts#L2600)
  distinguishes resets, indexed colors and other attributes;
  [Ghostty's IO implementation](https://github.com/ghostty-org/ghostty/blob/b40acce58dcf77df52231c3798ea58e924647c89/src/termio/Exec.zig#L1264)
  addresses consumer-side stalls. Neither removes producer-side console calls.
  RoyalTerminal should continue consuming the shell's ordered VT output normally.

## Reproducible build and validation

Baseline: upstream commit `0b909d0eba6ce8a4a38b0d4a867208ff4296b1e5`.
Both builds use that source revision, with only this branch's ConsoleHost
changes in the candidate. The fork was fast-forwarded before cloning.

Environment: Windows 11 Pro ARM64, build 26200, Parallels VM, four vCPUs,
6 GiB RAM. Builds are self-contained Release `win-arm64`, using the repository's
required .NET SDK `11.0.100-rc.1.26425.128`, installed only in the task directory.
The machine's installed PowerShell 7.6.6 is not replaced.
The checkout's `git describe` gives `v7.6.0-preview.5-413-g0b909d0eb`, so both
experimental binaries display `7.6.0-preview.5`; the full source SHA above
identifies these builds, not that abbreviated version label.

Build each checkout into a different directory:

```powershell
Import-Module ./build.psm1
Start-PSBuild -Configuration Release -Runtime win-arm64 -Output C:\pwsh-experiment\baseline -UseNuGetOrg -NoPSModuleRestore -CI
# In the patched checkout, use a different output directory:
Start-PSBuild -Configuration Release -Runtime win-arm64 -Output C:\pwsh-experiment\candidate -UseNuGetOrg -NoPSModuleRestore -CI
```

If reusing a checkout after copying source files, invalidate incremental inputs
or rebuild; copying older source timestamps can leave a stale assembly. Confirm
the baseline and candidate `Microsoft.PowerShell.ConsoleHost.dll` hashes differ.
An initial stale-build comparison was detected this way and discarded.

Run Pester using each build's own `pwsh.exe`, with Pester 4.10.1 and the repository's
`test/tools/Modules` on `PSModulePath`; import `HelpersHostCS` for custom-host tests.
The selected files are:

- `Write-Host.Tests.ps1`
- `Write-Host.Console.Tests.ps1` (new)
- `Write-Stream.Tests.ps1`
- `Write-Error.Tests.ps1`
- `Write-Verbose.Tests.ps1`
- `Write-Debug.Tests.ps1`

These are under `test/powershell/Modules/Microsoft.PowerShell.Utility`.
The new tests launch a separate, non-redirected Windows console so that native
color operations actually run without altering the test runner's console.
They check all 256 foreground/background pairs, both newline modes for five
ANSI sequences, invalid colors including legacy partial updates, information
records and suppression, transcript capture, `InformationVariable` and the
PlainText bypass. ANSI restoration is compared with the original public-setter
sequence; hard-coded assumptions about VT-to-legacy underline bits are not used.

## Measurement method

The RoyalTerminal PowerShell output diagnostic launches each built executable
through its shipping Windows ConPTY implementation at 120 columns by 40 rows.
Its drain consumer reads output without concurrent VT parsing or UI rendering.
Child-side timing excludes startup. Each sample runs a fresh PowerShell process
and executes this workload:

```powershell
$PSStyle.OutputRendering = 'Ansi'
Measure-Command {
    1..100000 | ForEach-Object {
        $fg = $_ % 256
        $bg = ($_ * 7) % 256
        Write-Host "`e[38;5;${fg}m`e[48;5;${bg}m Line $_ `e[0m"
    }
}
```

For a manual check, run this in each build inside the same terminal, with
`-NoProfile`. Do not pipe output to a file or `$null`: that changes the measured
host path. The diagnostic additionally rejects redirected child handles and
verifies all 100,000 line markers, the final line and retained ANSI output.
Three stock/candidate pairs alternate order (stock first, candidate first,
stock first). No build or test suite runs concurrently with those measurements.

## Results

The correctly rebuilt candidate and stock build each passed **56 focused Pester
tests**, with **zero failures, skips or pending tests**. Both self-contained
ARM64 Release builds succeeded. The harness build reported zero warnings/errors.

| 100,000 colored `Write-Host` calls | Stock | Candidate |
| --- | ---: | ---: |
| Pair 1, stock first | 26.488 s | 16.443 s |
| Pair 2, candidate first | 26.717 s | 16.276 s |
| Pair 3, stock first | 26.023 s | 16.943 s |
| Median | **26.488 s** | **16.443 s** |

That is **37.9% less elapsed time**, or approximately **1.61x throughput**, for
this workload on this VM. Every valid sample captured all 100,000 line markers
and the final line, retained ANSI output, and reported nonredirected ARM64
children with VT enabled. This is not a 3-second `Write-Host` result, nor a
general-purpose PowerShell speedup. These timings are not compared against a
different released PowerShell or .NET version.

An additional **single pair in graphical Windows Terminal**, also at 120x40
with nonredirected output and VT enabled, took **30.141 s stock / 18.897 s
candidate** (37.3% less time). This is a separate end-to-end smoke/performance
check, not part of the three-sample drain-only medians. No RoyalTerminal parser
or renderer was involved in this graphical control.

## Compatibility boundary

This is a targeted optimization, not a claim of exhaustive Windows equivalence.
Combining API calls changes intermediate states visible to unrelated writers
and the number of native failure opportunities. The host lock does not serialize
other processes attached to the console. Native failure injection, external-writer
races, full prompt/progress interaction, Windows x64/x86, older Windows builds,
remoting integration and the full upstream suite require separate validation.
The unchanged custom-host path is covered by the existing custom-host tests.

No Avalonia, Ghostty, ConPTY, .NET or other dependency source is patched. No
upstream PowerShell pull request is implied by this experimental fork branch.
