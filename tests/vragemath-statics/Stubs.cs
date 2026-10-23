using System;
using System.Collections.Generic;
using Shared.Logging;

namespace VRage.Scripting
{
    // Stand-in for the game's enum, same members as VRage.Scripting.MyApiTarget
    public enum MyApiTarget
    {
        None,
        Mod,
        Ingame,
    }
}

namespace VRageMathStaticsTests
{
    // Collects the Info lines, the only level the rewriter writes
    internal sealed class ListLogger : IPluginLogger
    {
        public readonly List<string> Lines = new();

        public bool IsTraceEnabled => true;
        public bool IsDebugEnabled => true;
        public bool IsInfoEnabled => true;
        public bool IsWarningEnabled => true;
        public bool IsErrorEnabled => true;
        public bool IsCriticalEnabled => true;

        public void Trace(Exception ex, string message, params object[] data) => Lines.Add(message);

        public void Debug(Exception ex, string message, params object[] data) => Lines.Add(message);

        public void Info(Exception ex, string message, params object[] data) => Lines.Add(message);

        public void Warning(Exception ex, string message, params object[] data) =>
            Lines.Add(message);

        public void Error(Exception ex, string message, params object[] data) => Lines.Add(message);

        public void Critical(Exception ex, string message, params object[] data) =>
            Lines.Add(message);

        public void Trace(string message, params object[] data) => Lines.Add(message);

        public void Debug(string message, params object[] data) => Lines.Add(message);

        public void Info(string message, params object[] data) => Lines.Add(message);

        public void Warning(string message, params object[] data) => Lines.Add(message);

        public void Error(string message, params object[] data) => Lines.Add(message);

        public void Critical(string message, params object[] data) => Lines.Add(message);
    }
}
