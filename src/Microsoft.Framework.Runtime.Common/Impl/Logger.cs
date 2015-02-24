// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace Microsoft.Framework.Runtime
{
    /// <summary>
    /// Extremely simple low-level logging facility for runtime
    /// </summary>
    internal class Logger
    {
        private static int? _level;
        private const int OffLevel = 0;
        private const int InfoLevel = 1;
        private const int TraceLevel = 2;

        private string _name;

        public Logger(string name)
        {
            _name = name;
        }

        public void Error(string message, params object[] args)
        {
            if (IsErrorEnabled)
            {
                Console.WriteLine($"error: [{_name}] {string.Format(message, args)}");
            }
        }
        public void Trace(string message, params object[] args)
        {
            if (IsTraceEnabled)
            {
                Console.WriteLine($"trace: [{_name}] {string.Format(message, args)}");
            }
        }
        public void Info(string message, params object[] args)
        {
            if (IsInfoEnabled)
            {
                Console.WriteLine($"info : [{_name}] {string.Format(message, args)}");
            }
        }
        public void Warning(string message, params object[] args)
        {
            if (IsWarningEnabled)
            {
                Console.WriteLine($"warn : [{_name}] {string.Format(message, args)}");
            }
        }

        public static Logger For(string name)
        {
            return new Logger(name);
        }

        private static bool IsErrorEnabled { get { return Level >= InfoLevel; } }
        private static bool IsWarningEnabled { get { return Level >= InfoLevel; } }
        private static bool IsInfoEnabled { get { return Level >= InfoLevel; } }
        private static bool IsTraceEnabled { get { return Level >= TraceLevel; } }

        private static int Level
        {
            get
            {
                if(_level == null)
                {
                    string levelStr = Environment.GetEnvironmentVariable(EnvironmentNames.Trace);
                    int newLevel;
                    if(string.IsNullOrEmpty(levelStr) || !int.TryParse(levelStr, out newLevel))
                    {
                        newLevel = OffLevel;
                    }
                    _level = newLevel;
                }
                return _level.Value;
            }
        }
    }
}