// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace System.Management.Automation.Host
{
    /// <summary>
    /// Internal capability for hosts that can read both console colors in one operation.
    /// </summary>
    internal interface IConsoleColorSnapshotProvider
    {
        /// <summary>
        /// Reads the current colors without changing console state or caching across calls.
        /// </summary>
        void GetConsoleColors(out ConsoleColor foregroundColor, out ConsoleColor backgroundColor);
    }
}
