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
        private string _name;

        public Logger(string name)
        {
            _name = name;
        }

        public void Error(string message, params object[] args)
        {
            if (IsEnabled)
            {
                Console.WriteLine($"error: [{_name}] {string.Format(message, args)}");
            }
        }
        public void Info(string message, params object[] args)
        {
            if (IsEnabled)
            {
                Console.WriteLine($"info : [{_name}] {string.Format(message, args)}");
            }
        }
        public void Warning(string message, params object[] args)
        {
            if (IsEnabled)
            {
                Console.WriteLine($"warn : [{_name}] {string.Format(message, args)}");
            }
        }

        public static Logger For(string name)
        {
            return new Logger(name);
        } 
        
        [Obsolete("Use Logger.For to get a named logger instead")]
        public static void TraceError(string message, params object[] args)
        {
            if (IsEnabled)
            {
                Console.WriteLine("Error: " + message, args);
            }
        }

        [Obsolete("Use Logger.For to get a named logger instead")]
        public static void TraceInformation(string message, params object[] args)
        {
            if (IsEnabled)
            {
                Console.WriteLine("Information: " + message, args);
            }
        }

        [Obsolete("Use Logger.For to get a named logger instead")]
        public static void TraceWarning(string message, params object[] args)
        {
            if (IsEnabled)
            {
                Console.WriteLine("Warning: " + message, args);
            }
        }

        private static bool IsEnabled
        {
            get
            {
                return Environment.GetEnvironmentVariable(EnvironmentNames.Trace) == "1";
            }
        }
    }
}