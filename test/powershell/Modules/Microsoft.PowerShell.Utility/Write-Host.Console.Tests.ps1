# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# Run in a separate real console: redirected native output does not exercise the
# same ConsoleHost write path. Never change the test runner's console state.
Describe 'Write-Host Windows console color transactions' -Tags 'Feature' {
    BeforeAll {
        if (!$IsWindows) { return }

        $resultFile = Join-Path $TestDrive 'console-result.json'
        $child = {
            param($resultFile)
            $ErrorActionPreference = 'Stop'
            try {
                if ([Console]::IsOutputRedirected) { throw 'A real console is required' }
                $PSStyle.OutputRendering = 'Ansi'
                Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class WriteHostConsoleState
{
    public static ConsoleColor Color(int value) => (ConsoleColor)value;
    [StructLayout(LayoutKind.Sequential)]
    private struct BufferInfo
    {
        public short Width, Height, X, Y;
        public ushort Attributes;
        public short Left, Top, Right, Bottom, MaxWidth, MaxHeight;
    }
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out BufferInfo info);
    public static ushort Attributes()
    {
        if (!GetConsoleScreenBufferInfo(GetStdHandle(-11), out BufferInfo info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.Attributes;
    }
}
'@
                $raw = $Host.UI.RawUI
                $raw.ForegroundColor = 'Yellow'
                $raw.BackgroundColor = 'DarkBlue'
                $original = [WriteHostConsoleState]::Attributes() -band 0xff
                $defaultCases = 0
                foreach ($explicitForeground in @($false, $true)) {
                    foreach ($explicitBackground in @($false, $true)) {
                        $parameters = @{}
                        if ($explicitForeground) { $parameters.ForegroundColor = 'Cyan' }
                        if ($explicitBackground) { $parameters.BackgroundColor = 'DarkRed' }
                        $message = (Write-Host 'defaults' @parameters 6>&1).MessageData
                        $expectedForeground = if ($explicitForeground) { 'Cyan' } else { 'Yellow' }
                        $expectedBackground = if ($explicitBackground) { 'DarkRed' } else { 'DarkBlue' }
                        if ($message.ForegroundColor -ne $expectedForeground -or $message.BackgroundColor -ne $expectedBackground) { throw 'Default color snapshot changed the information record' }
                        $defaultCases++
                    }
                }
                $pipelineRecords = @(& {
                    'first'
                    $raw.ForegroundColor = 'Red'
                    $raw.BackgroundColor = 'Green'
                    'second'
                } | Write-Host 6>&1)
                if ($pipelineRecords.Count -ne 2) { throw 'Missing pipeline information records' }
                foreach ($record in $pipelineRecords) {
                    if ($record.MessageData.ForegroundColor -ne 'Yellow' -or $record.MessageData.BackgroundColor -ne 'DarkBlue') { throw 'Defaults were not cached per cmdlet' }
                }
                $next = (Write-Host 'next invocation' 6>&1).MessageData
                if ($next.ForegroundColor -ne 'Red' -or $next.BackgroundColor -ne 'Green') { throw 'Defaults were cached across invocations' }
                $raw.ForegroundColor = 'Yellow'
                $raw.BackgroundColor = 'DarkBlue'
                $colorCases = 0
                foreach ($foreground in 0..15) {
                    foreach ($background in 0..15) {
                        $raw.CursorPosition = [System.Management.Automation.Host.Coordinates]::new(0, 0)
                        Write-Host 'X' -ForegroundColor $foreground -BackgroundColor $background -NoNewline
                        $cell = $raw.GetBufferContents([System.Management.Automation.Host.Rectangle]::new(0, 0, 0, 0))[0, 0]
                        if ($cell.Character -ne 'X' -or [int]$cell.ForegroundColor -ne $foreground -or [int]$cell.BackgroundColor -ne $background) {
                            throw "Incorrect cell colors: $foreground / $background"
                        }
                        if (([WriteHostConsoleState]::Attributes() -band 0xff) -ne $original) { throw 'Colors not restored' }
                        $colorCases++
                    }
                }

                $ansiCases = 0
                foreach ($text in @("`e[38;5;197m`e[48;5;21mX", "`e[38;2;1;127;254mX", "`e[31mX`e[0m", "`e[4mX", "`e[7mX")) {
                    foreach ($noNewline in @($true, $false)) {
                        # Use the public individual setters as the legacy oracle. The
                        # mapping of VT attributes to legacy bits depends on the console.
                        [Console]::Write("`e[0m")
                        $raw.ForegroundColor = 'Yellow'
                        $raw.BackgroundColor = 'DarkBlue'
                        $savedForeground = $raw.ForegroundColor
                        $savedBackground = $raw.BackgroundColor
                        $raw.ForegroundColor = $savedForeground
                        $raw.BackgroundColor = $savedBackground
                        try {
                            if ($noNewline) { $Host.UI.Write($text) }
                            else { $Host.UI.WriteLine($text) }
                        }
                        finally {
                            $raw.ForegroundColor = $savedForeground
                            $raw.BackgroundColor = $savedBackground
                        }
                        $expectedAttributes = [WriteHostConsoleState]::Attributes()

                        [Console]::Write("`e[0m")
                        $raw.ForegroundColor = 'Yellow'
                        $raw.BackgroundColor = 'DarkBlue'
                        Write-Host $text -NoNewline:$noNewline
                        $attributes = [WriteHostConsoleState]::Attributes()
                        if (($attributes -band 0xff) -ne $original) { throw 'ANSI output leaked colors' }
                        if ($attributes -ne $expectedAttributes) { throw "ANSI attribute restoration changed: $attributes / $expectedAttributes" }
                        $ansiCases++
                    }
                }

                [Console]::Write("`e[0m")
                $raw.ForegroundColor = 'Yellow'
                $raw.BackgroundColor = 'DarkBlue'
                # Public host calls accept enum casts, unlike cmdlet parameter binding.
                try { $Host.UI.Write([WriteHostConsoleState]::Color(-1), [ConsoleColor]0, 'invalid'); throw 'Missing foreground error' }
                catch [System.Management.Automation.MethodInvocationException] {
                    if ($_.Exception.InnerException -isnot [ArgumentException]) { throw }
                }
                if ($raw.ForegroundColor -ne 'Yellow' -or $raw.BackgroundColor -ne 'DarkBlue') { throw 'Invalid foreground changed colors' }
                try { $Host.UI.Write([ConsoleColor]12, [WriteHostConsoleState]::Color(16), 'invalid'); throw 'Missing background error' }
                catch [System.Management.Automation.MethodInvocationException] {
                    if ($_.Exception.InnerException -isnot [ArgumentException]) { throw }
                }
                if ($raw.ForegroundColor -ne 'Red' -or $raw.BackgroundColor -ne 'DarkBlue') { throw 'Invalid background changed legacy partial-update behavior' }

                $records = @(Write-Host 'a','b' -Separator '+' -ForegroundColor Cyan -BackgroundColor DarkRed -NoNewline 6>&1)
                if ($records.Count -ne 1 -or $records[0] -isnot [System.Management.Automation.InformationRecord]) { throw 'Information record missing' }
                $record = $records[0]
                if ($record.MessageData.Message -ne 'a+b' -or !$record.MessageData.NoNewLine -or
                    $record.MessageData.ForegroundColor -ne 'Cyan' -or $record.MessageData.BackgroundColor -ne 'DarkRed' -or
                    $record.Tags -notcontains 'PSHOST') { throw 'Information record changed' }
                $position = $raw.CursorPosition
                Write-Host 'must not appear' 6>$null
                Write-Host 'must not appear' -InformationAction Ignore
                if (!$raw.CursorPosition.Equals($position)) { throw 'Suppressed output reached the console' }

                $transcriptFile = $resultFile + '.transcript'
                Start-Transcript -Path $transcriptFile | Out-Null
                Write-Host 'write-host-transcript-sentinel' -ForegroundColor Cyan -InformationVariable capturedInformation
                Stop-Transcript | Out-Null
                if ((Get-Content $transcriptFile -Raw) -notmatch 'write-host-transcript-sentinel') { throw 'Transcript missed host output' }
                if ($capturedInformation.Count -ne 1 -or $capturedInformation[0].MessageData.Message -ne 'write-host-transcript-sentinel') { throw 'InformationVariable missed host output' }
                Remove-Item -LiteralPath $transcriptFile

                $PSStyle.OutputRendering = 'PlainText'
                $beforePlainText = [WriteHostConsoleState]::Attributes()
                $raw.CursorPosition = [System.Management.Automation.Host.Coordinates]::new(0, 0)
                Write-Host "`e[31mPlain`e[0m" -ForegroundColor Cyan -BackgroundColor DarkRed -NoNewline
                $cell = $raw.GetBufferContents([System.Management.Automation.Host.Rectangle]::new(0, 0, 0, 0))[0, 0]
                if ($cell.Character -ne 'P' -or [int]$cell.ForegroundColor -ne ($beforePlainText -band 0x0f) -or
                    [int]$cell.BackgroundColor -ne (($beforePlainText -band 0xf0) -shr 4) -or
                    [WriteHostConsoleState]::Attributes() -ne $beforePlainText) { throw 'PlainText output changed colors or retained ANSI sequences' }

                @{ColorCases=$colorCases; AnsiCases=$ansiCases; InvalidColors=$true; Information=$true; Transcript=$true; PlainText=$true; DefaultCases=$defaultCases; CachedDefaults=$true} |
                    ConvertTo-Json -Compress | Set-Content -LiteralPath $resultFile
            }
            catch {
                @{Error=$_ | Out-String} | ConvertTo-Json -Compress | Set-Content -LiteralPath $resultFile
                exit 1
            }
        }
        $childFile = Join-Path $TestDrive 'console-child.ps1'
        $child.ToString() | Set-Content -LiteralPath $childFile
        $process = Start-Process (Join-Path $PSHOME 'pwsh.exe') -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-File',('"' + $childFile + '"'),('"' + $resultFile + '"') -WindowStyle Hidden -PassThru
        try {
            if (!$process.WaitForExit(60000)) {
                $process.Kill()
                throw 'Console test child timed out'
            }
            $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
            if ($process.ExitCode -ne 0 -or $result.Error) { throw "Console test failed: $($result.Error)" }
        }
        finally { $process.Dispose() }
    }

    It 'renders all 256 color pairs and restores caller colors' -Skip:(!$IsWindows) {
        $result.ColorCases | Should -Be 256
    }

    It 'restores colors without discarding post-write ANSI attributes' -Skip:(!$IsWindows) {
        $result.AnsiCases | Should -Be 10
    }

    It 'preserves invalid-color errors and partial updates' -Skip:(!$IsWindows) {
        $result.InvalidColors | Should -BeTrue
    }

    It 'preserves information records and suppression' -Skip:(!$IsWindows) {
        $result.Information | Should -BeTrue
    }

    It 'preserves transcript and information-variable capture' -Skip:(!$IsWindows) {
        $result.Transcript | Should -BeTrue
    }

    It 'preserves the PlainText rendering bypass' -Skip:(!$IsWindows) {
        $result.PlainText | Should -BeTrue
    }

    It 'resolves omitted and explicit colors in information records' -Skip:(!$IsWindows) {
        $result.DefaultCases | Should -Be 4
    }

    It 'caches default colors per cmdlet rather than across invocations' -Skip:(!$IsWindows) {
        $result.CachedDefaults | Should -BeTrue
    }
}
