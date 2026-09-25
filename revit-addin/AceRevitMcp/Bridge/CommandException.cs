using System;

namespace AceRevitMcp.Bridge
{
    /// <summary>An expected, user-facing failure (bad arguments, no document open, ...).</summary>
    internal sealed class CommandException : Exception
    {
        public CommandException(string message) : base(message) { }
    }
}
