// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Internal.Host;

namespace Microsoft.PowerShell.Commands
{
    /// <summary>
    /// Base class for a variety of commandlets that take color parameters.
    /// </summary>
    public
    class ConsoleColorCmdlet : PSCmdlet
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ConsoleColorCmdlet"/> class.
        /// </summary>
        public ConsoleColorCmdlet()
        {
            _consoleColorEnumType = typeof(ConsoleColor);
        }

        /// <summary>
        /// The -ForegroundColor parameter.
        /// </summary>
        /// <value></value>
        [Parameter]
        public
        ConsoleColor
        ForegroundColor
        {
            get
            {
                if (!_isFgColorSet)
                {
                    _fgColor = this.Host.UI.RawUI.ForegroundColor;
                    _isFgColorSet = true;
                }

                return _fgColor;
            }

            set
            {
                if (value >= (ConsoleColor)0 && value <= (ConsoleColor)15)
                {
                    _fgColor = value;
                    _isFgColorSet = true;
                }
                else
                {
                    ThrowTerminatingError(BuildOutOfRangeErrorRecord(value, "SetInvalidForegroundColor"));
                }
            }
        }

        /// <summary>
        /// </summary>
        /// <value></value>
        [Parameter]
        public
        ConsoleColor
        BackgroundColor
        {
            get
            {
                if (!_isBgColorSet)
                {
                    _bgColor = this.Host.UI.RawUI.BackgroundColor;
                    _isBgColorSet = true;
                }

                return _bgColor;
            }

            set
            {
                if (value >= (ConsoleColor)0 && value <= (ConsoleColor)15)
                {
                    _bgColor = value;
                    _isBgColorSet = true;
                }
                else
                {
                    ThrowTerminatingError(BuildOutOfRangeErrorRecord(value, "SetInvalidBackgroundColor"));
                }
            }
        }

        #region helper
        /// <summary>
        /// Resolves the two omitted colors together when supported by the host.
        /// This is used by Write-Host, not by the public getters: reading one public
        /// property must not eagerly capture the other property's default.
        /// </summary>
        internal void CacheConsoleColorSnapshot()
        {
            if (!_isFgColorSet && !_isBgColorSet &&
                Host.UI.RawUI is InternalHostRawUserInterface rawUI &&
                rawUI.TryGetConsoleColors(out ConsoleColor foregroundColor, out ConsoleColor backgroundColor))
            {
                _fgColor = foregroundColor;
                _bgColor = backgroundColor;
                _isFgColorSet = true;
                _isBgColorSet = true;
            }
        }

        private static ErrorRecord BuildOutOfRangeErrorRecord(object val, string errorId)
        {
            string msg = StringUtil.Format(HostStrings.InvalidColorErrorTemplate, val.ToString());
            ArgumentOutOfRangeException e = new("value", val, msg);
            return new ErrorRecord(e, errorId, ErrorCategory.InvalidArgument, null);
        }
        #endregion helper

        private ConsoleColor _fgColor;
        private ConsoleColor _bgColor;

        private bool _isFgColorSet = false;
        private bool _isBgColorSet = false;

        private readonly Type _consoleColorEnumType;
    }
}
