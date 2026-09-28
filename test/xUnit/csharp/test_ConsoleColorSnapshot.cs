// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Internal.Host;
using System.Management.Automation.Runspaces;
using System.Security;
using Microsoft.PowerShell.Commands;
using Xunit;

namespace PSTests.Parallel
{
    public class ConsoleColorSnapshotTests
    {
        [Theory]
        [InlineData(false, false, 1, 0, 0, ConsoleColor.Yellow, ConsoleColor.DarkBlue)]
        [InlineData(true, false, 0, 0, 1, ConsoleColor.Cyan, ConsoleColor.DarkBlue)]
        [InlineData(false, true, 0, 1, 0, ConsoleColor.Yellow, ConsoleColor.DarkRed)]
        [InlineData(true, true, 0, 0, 0, ConsoleColor.Cyan, ConsoleColor.DarkRed)]
        public void WriteHostUsesSnapshotOnlyForTwoOmittedColors(
            bool explicitForeground, bool explicitBackground, int snapshots, int foregroundReads, int backgroundReads,
            ConsoleColor foreground, ConsoleColor background)
        {
            var raw = new SnapshotRawUI();
            var host = new ColorHost(raw);
            using Runspace runspace = OpenRunspace(host);
            using PowerShell ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Write-Host").AddArgument("value");
            if (explicitForeground) ps.AddParameter("ForegroundColor", ConsoleColor.Cyan);
            if (explicitBackground) ps.AddParameter("BackgroundColor", ConsoleColor.DarkRed);
            ps.Invoke();

            Assert.False(ps.HadErrors);
            Assert.Equal(snapshots, raw.Snapshots);
            Assert.Equal(foregroundReads, raw.ForegroundReads);
            Assert.Equal(backgroundReads, raw.BackgroundReads);
            var message = Assert.IsType<HostInformationMessage>(Assert.Single(ps.Streams.Information).MessageData);
            Assert.Equal(foreground, message.ForegroundColor);
            Assert.Equal(background, message.BackgroundColor);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DefaultsAreCachedPerCmdletNotAcrossInvocations(bool supportsSnapshot)
        {
            CountingRawUI raw = supportsSnapshot ? new SnapshotRawUI() : new CountingRawUI();
            var host = new ColorHost(raw);
            host.UserInterface.AfterInformation = () =>
            {
                raw.ForegroundColor = ConsoleColor.Red;
                raw.BackgroundColor = ConsoleColor.Green;
            };
            using Runspace runspace = OpenRunspace(host);
            using PowerShell ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Write-Host");
            ps.Invoke(new[] { "first", "second" });

            Assert.False(ps.HadErrors);
            Assert.Equal(2, ps.Streams.Information.Count);
            foreach (InformationRecord record in ps.Streams.Information)
            {
                var message = Assert.IsType<HostInformationMessage>(record.MessageData);
                Assert.Equal(ConsoleColor.Yellow, message.ForegroundColor);
                Assert.Equal(ConsoleColor.DarkBlue, message.BackgroundColor);
            }

            if (raw is SnapshotRawUI snapshot) Assert.Equal(1, snapshot.Snapshots);
            else Assert.Equal(new[] { "foreground", "background" }, raw.Reads);

            ps.Commands.Clear();
            ps.Streams.ClearStreams();
            ps.AddCommand("Write-Host").AddArgument("third").Invoke();
            Assert.False(ps.HadErrors);
            var next = Assert.IsType<HostInformationMessage>(Assert.Single(ps.Streams.Information).MessageData);
            Assert.Equal(ConsoleColor.Red, next.ForegroundColor);
            Assert.Equal(ConsoleColor.Green, next.BackgroundColor);
            if (raw is SnapshotRawUI nextSnapshot) Assert.Equal(2, nextSnapshot.Snapshots);
            else Assert.Equal(new[] { "foreground", "background", "foreground", "background" }, raw.Reads);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void CustomHostExceptionsRetainPartialMessageAndReadOrder(bool failForeground)
        {
            var raw = new CountingRawUI { FailForeground = failForeground, FailBackground = !failForeground };
            using Runspace runspace = OpenRunspace(new ColorHost(raw));
            using PowerShell ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Write-Host").AddArgument("value").Invoke();

            Assert.False(ps.HadErrors);
            var message = Assert.IsType<HostInformationMessage>(Assert.Single(ps.Streams.Information).MessageData);
            Assert.Equal(failForeground ? null : (ConsoleColor?)ConsoleColor.Yellow, message.ForegroundColor);
            Assert.Null(message.BackgroundColor);
            Assert.Equal(failForeground ? new[] { "foreground" } : new[] { "foreground", "background" }, raw.Reads);
        }

        [Fact]
        public void UnsupportedSnapshotDoesNotReadIndividualProperties()
        {
            var raw = new CountingRawUI();
            var wrapper = new InternalHostRawUserInterface(raw, null);
            Assert.False(wrapper.TryGetConsoleColors(out _, out _));
            Assert.Empty(raw.Reads);
            Assert.False(new InternalHostRawUserInterface(null, null).TryGetConsoleColors(out _, out _));
        }

        [Fact]
        public void SnapshotFailureDoesNotCacheOrFallBackToIndividualReads()
        {
            var raw = new SnapshotRawUI { FailSnapshot = true };
            var host = new ColorHost(raw);
            host.UserInterface.AfterInformation = () => raw.FailSnapshot = false;
            using Runspace runspace = OpenRunspace(host);
            using PowerShell ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Write-Host").Invoke(new[] { "first", "second" });

            Assert.False(ps.HadErrors);
            Assert.Equal(2, raw.Snapshots);
            Assert.Empty(raw.Reads);
            Assert.Equal(2, ps.Streams.Information.Count);
            var first = Assert.IsType<HostInformationMessage>(ps.Streams.Information[0].MessageData);
            Assert.Null(first.ForegroundColor);
            Assert.Null(first.BackgroundColor);
            var second = Assert.IsType<HostInformationMessage>(ps.Streams.Information[1].MessageData);
            Assert.Equal(ConsoleColor.Yellow, second.ForegroundColor);
            Assert.Equal(ConsoleColor.DarkBlue, second.BackgroundColor);
        }

        private static Runspace OpenRunspace(PSHost host)
        {
            InitialSessionState state = InitialSessionState.CreateDefault2();
            state.Commands.Add(new SessionStateCmdletEntry("Write-Host", typeof(WriteHostCommand), null));
            Runspace runspace = RunspaceFactory.CreateRunspace(host, state);
            runspace.Open();
            return runspace;
        }

        private class CountingRawUI : PSHostRawUserInterface
        {
            protected ConsoleColor Foreground = ConsoleColor.Yellow;
            protected ConsoleColor Background = ConsoleColor.DarkBlue;
            internal readonly List<string> Reads = new();
            internal int ForegroundReads;
            internal int BackgroundReads;
            internal bool FailForeground;
            internal bool FailBackground;

            public override ConsoleColor ForegroundColor
            {
                get
                {
                    ForegroundReads++;
                    Reads.Add("foreground");
                    if (FailForeground) throw new HostException("Foreground unavailable");
                    return Foreground;
                }
                set => Foreground = value;
            }

            public override ConsoleColor BackgroundColor
            {
                get
                {
                    BackgroundReads++;
                    Reads.Add("background");
                    if (FailBackground) throw new HostException("Background unavailable");
                    return Background;
                }
                set => Background = value;
            }

            public override Coordinates CursorPosition { get; set; }

            public override Coordinates WindowPosition { get; set; }

            public override int CursorSize { get; set; }

            public override Size BufferSize { get; set; }

            public override Size WindowSize { get; set; }

            public override Size MaxWindowSize => new Size(120, 40);

            public override Size MaxPhysicalWindowSize => MaxWindowSize;

            public override string WindowTitle { get; set; }

            public override bool KeyAvailable => false;

            public override BufferCell[,] GetBufferContents(Rectangle rectangle) => throw new NotImplementedException();

            public override void SetBufferContents(Rectangle rectangle, BufferCell fill) => throw new NotImplementedException();

            public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) => throw new NotImplementedException();

            public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) => throw new NotImplementedException();

            public override KeyInfo ReadKey(ReadKeyOptions options) => throw new NotImplementedException();

            public override void FlushInputBuffer() => throw new NotImplementedException();
        }

        private sealed class SnapshotRawUI : CountingRawUI, IConsoleColorSnapshotProvider
        {
            internal int Snapshots;
            internal bool FailSnapshot;

            public void GetConsoleColors(out ConsoleColor foregroundColor, out ConsoleColor backgroundColor)
            {
                Snapshots++;
                if (FailSnapshot) throw new HostException("Snapshot unavailable");
                foregroundColor = Foreground;
                backgroundColor = Background;
            }
        }

        private sealed class ColorHost : PSHost
        {
            internal ColorHost(PSHostRawUserInterface raw) => UserInterface = new ColorUI(raw);

            internal ColorUI UserInterface { get; }

            public override PSHostUserInterface UI => UserInterface;

            public override Guid InstanceId { get; } = Guid.NewGuid();

            public override string Name => "Color snapshot test";

            public override Version Version => new Version(1, 0);

            public override CultureInfo CurrentCulture => CultureInfo.InvariantCulture;

            public override CultureInfo CurrentUICulture => CultureInfo.InvariantCulture;

            public override void SetShouldExit(int exitCode) => throw new NotImplementedException();

            public override void EnterNestedPrompt() => throw new NotImplementedException();

            public override void ExitNestedPrompt() => throw new NotImplementedException();

            public override void NotifyBeginApplication() { }

            public override void NotifyEndApplication() { }
        }

        private sealed class ColorUI : PSHostUserInterface
        {
            internal ColorUI(PSHostRawUserInterface raw) => RawUI = raw;

            internal Action AfterInformation { get; set; }

            public override PSHostRawUserInterface RawUI { get; }

            public override void WriteInformation(InformationRecord record) => AfterInformation?.Invoke();

            public override void Write(string value) { }

            public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) { }

            public override void WriteLine(string value) { }

            public override void WriteErrorLine(string value) { }

            public override void WriteDebugLine(string message) { }

            public override void WriteProgress(long sourceId, ProgressRecord record) { }

            public override void WriteVerboseLine(string message) { }

            public override void WriteWarningLine(string message) { }

            public override string ReadLine() => throw new NotImplementedException();

            public override SecureString ReadLineAsSecureString() => throw new NotImplementedException();

            public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions) => throw new NotImplementedException();

            public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice) => throw new NotImplementedException();

            public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) => throw new NotImplementedException();

            public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options) => throw new NotImplementedException();
        }
    }
}
