// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Framework.ApplicationHost.Impl.Syntax;
using Microsoft.Framework.Runtime;
using Microsoft.Framework.Runtime.Common.CommandLine;
using Microsoft.Framework.Runtime.Hosting;
using Microsoft.Framework.Runtime.Hosting.DependencyProviders;
using Newtonsoft.Json.Linq;
using NuGet.Frameworks;
using NuGet.ProjectModel;

namespace Microsoft.Framework.ApplicationHost
{
    public class Program
    {
        private readonly IAssemblyLoaderContainer _container;
        private readonly IApplicationEnvironment _environment;
        private readonly IServiceProvider _serviceProvider;

        public Program(IAssemblyLoaderContainer container, IApplicationEnvironment environment, IServiceProvider serviceProvider)
        {
            _container = container;
            _environment = environment;
            _serviceProvider = serviceProvider;
        }

        public Task<int> Main(string[] args)
        {
            Logger.TraceInformation("[ApplicationHost] Application Host Starting");
            ApplicationHostOptions options;
            string[] programArgs;
            int exitCode;

            bool shouldExit = ParseArgs(args, out options, out programArgs, out exitCode);
            if (shouldExit)
            {
                return Task.FromResult(exitCode);
            }


            // Construct the necessary context for hosting the application
            var builder = new RuntimeHostBuilder(
                options.ApplicationBaseDirectory,
                _container)
            {
                TargetFramework = NuGetFramework.ParseFrameworkName(
                    _environment.RuntimeFramework.FullName, 
                    DefaultFrameworkNameProvider.Instance)
            };
            var projectResolver = new ProjectResolver(
                builder.ApplicationBaseDirectory,
                builder.RootDirectory);
            builder.DependencyProviders.Add(new ProjectReferenceDependencyProvider(projectResolver));
            var referenceResolver = new FrameworkReferenceResolver();
            builder.DependencyProviders.Add(new ReferenceAssemblyDependencyResolver(referenceResolver));

            // Boot the runtime
            var host = builder.Build();

            // Check for a project
            if (host.Project == null)
            {
                // No project was found. We can't start the app.
                Logger.TraceError($"[ApplicationHost] A project.json file was not found in '{options.ApplicationBaseDirectory}'");
                return Task.FromResult(1);
            }

            // Get the project and print some information from it
            Logger.TraceInformation($"[ApplicationHost] Created runtime host ({host.TargetFramework}) for {host.Project.Name} in {host.ApplicationBaseDirectory}");

            // Determine the command to be executed
            var command = string.IsNullOrEmpty(options.ApplicationName) ? "run" : options.ApplicationName;
            string replacementCommand;
            if (host.Project.Commands.TryGetValue(command, out replacementCommand))
            {
                var replacementArgs = CommandGrammar.Process(
                    replacementCommand,
                    GetVariable).ToArray();
                options.ApplicationName = replacementArgs.First();
                programArgs = replacementArgs.Skip(1).Concat(programArgs).ToArray();
            }

            if (string.IsNullOrEmpty(options.ApplicationName) ||
                string.Equals(options.ApplicationName, "run", StringComparison.Ordinal))
            {
                options.ApplicationName = host.Project.EntryPoint ?? host.Project.Name;
            }

            try
            {
                host.LaunchApplication(
                    options.ApplicationName,
                    programArgs);
            }
            catch (Exception ex)
            {
                Logger.TraceError($"[ApplicationHost] {ex.GetType().Name} launching application: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Logger.TraceError($"[ApplicationHost] Caused by {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                throw;
            }

            return Task.FromResult(0);
        }

        private string GetVariable(string key)
        {
            if (string.Equals(key, "env:ApplicationBasePath", StringComparison.OrdinalIgnoreCase))
            {
                return _environment.ApplicationBasePath;
            }
            if (string.Equals(key, "env:ApplicationName", StringComparison.OrdinalIgnoreCase))
            {
                return _environment.ApplicationName;
            }
            if (string.Equals(key, "env:Version", StringComparison.OrdinalIgnoreCase))
            {
                return _environment.Version;
            }
            if (string.Equals(key, "env:TargetFramework", StringComparison.OrdinalIgnoreCase))
            {
                return _environment.RuntimeFramework.Identifier;
            }
            return Environment.GetEnvironmentVariable(key);
        }

        private bool ParseArgs(string[] args, out ApplicationHostOptions options, out string[] outArgs, out int exitCode)
        {
            var app = new CommandLineApplication(throwOnUnexpectedArg: false);
            app.Name = typeof(Program).Namespace;
            var optionWatch = app.Option("--watch", "Watch file changes", CommandOptionType.NoValue);
            var optionPackages = app.Option("--packages <PACKAGE_DIR>", "Directory containing packages",
                CommandOptionType.SingleValue);
            var optionConfiguration = app.Option("--configuration <CONFIGURATION>", "The configuration to run under", CommandOptionType.SingleValue);
            var optionCompilationServer = app.Option("--port <PORT>", "The port to the compilation server", CommandOptionType.SingleValue);
            var runCmdExecuted = false;
            app.HelpOption("-?|-h|--help");
            app.VersionOption("--version", GetVersion());
            var runCmd = app.Command("run", c =>
            {
                // We don't actually execute "run" command here
                // We are adding this command for the purpose of displaying correct help information
                c.Description = "Run application";
                c.OnExecute(() =>
                {
                    runCmdExecuted = true;
                    return 0;
                });
            },
            addHelpCommand: false,
            throwOnUnexpectedArg: false);
            app.Execute(args);

            options = null;
            outArgs = null;
            exitCode = 0;

            if (app.IsShowingInformation)
            {
                // If help option or version option was specified, exit immediately with 0 exit code
                return true;
            }
            else if (!(app.RemainingArguments.Any() || runCmdExecuted))
            {
                // If no subcommand was specified, show error message
                // and exit immediately with non-zero exit code
                Logger.TraceError("Please specify the command to run");
                exitCode = 2;
                return true;
            }

            options = new ApplicationHostOptions();
            options.WatchFiles = optionWatch.HasValue();
            options.PackageDirectory = optionPackages.Value();

            options.Configuration = optionConfiguration.Value() ?? _environment.Configuration ?? "Debug";
            options.ApplicationBaseDirectory = _environment.ApplicationBasePath;
            var portValue = optionCompilationServer.Value() ?? Environment.GetEnvironmentVariable(EnvironmentNames.CompilationServerPort);

            int port;
            if (!string.IsNullOrEmpty(portValue) && int.TryParse(portValue, out port))
            {
                options.CompilationServerPort = port;
            }

            var remainingArgs = new List<string>();
            if (runCmdExecuted)
            {
                // Later logic will execute "run" command
                // So we put this argment back after it was consumed by parser
                remainingArgs.Add("run");
                remainingArgs.AddRange(runCmd.RemainingArguments);
            }
            else
            {
                remainingArgs.AddRange(app.RemainingArguments);
            }

            if (remainingArgs.Any())
            {
                options.ApplicationName = remainingArgs[0];
                outArgs = remainingArgs.Skip(1).ToArray();
            }
            else
            {
                outArgs = remainingArgs.ToArray();
            }

            return false;
        }

        private static string GetVersion()
        {
            var assembly = typeof(Program).GetTypeInfo().Assembly;
            var assemblyInformationalVersionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return assemblyInformationalVersionAttribute.InformationalVersion;
        }
    }
}