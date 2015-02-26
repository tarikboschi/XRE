// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Framework.PackageManager.Bundle;
using Microsoft.Framework.PackageManager.Restore.NuGet;
using Microsoft.Framework.Runtime;
using NuGet;
using Microsoft.Framework.Runtime.DependencyManagement;

namespace Microsoft.Framework.PackageManager
{
    public class RestoreCommand
    {
        public RestoreCommand(IApplicationEnvironment env)
        {
            ApplicationEnvironment = env;
            FileSystem = new PhysicalFileSystem(Directory.GetCurrentDirectory());
            MachineWideSettings = new CommandLineMachineWideSettings();
            ScriptExecutor = new ScriptExecutor();
        }

        public FeedOptions FeedOptions { get; set; }

        public string RestoreDirectory { get; set; }
        public string NuGetConfigFile { get; set; }
        public IEnumerable<string> Sources { get; set; }
        public IEnumerable<string> FallbackSources { get; set; }
        public bool NoCache { get; set; }
        public bool Lock { get; set; }
        public bool Unlock { get; set; }
        public string PackageFolder { get; set; }

        public string RestorePackageId { get; set; } 
        public string RestorePackageVersion { get; set; }

        public string AppInstallPath { get; private set; }

        public ScriptExecutor ScriptExecutor { get; private set; }

        public IApplicationEnvironment ApplicationEnvironment { get; private set; }
        public IMachineWideSettings MachineWideSettings { get; set; }
        public IFileSystem FileSystem { get; set; }
        public Reports Reports { get; set; }

        protected internal ISettings Settings { get; set; }
        protected internal IPackageSourceProvider SourceProvider { get; set; }

        public async Task<bool> ExecuteCommand()
        {
            try
            {
                var sw = Stopwatch.StartNew();

                // If the root argument is a project.json file
                if (string.Equals(
                    Runtime.Project.ProjectFileName,
                    Path.GetFileName(RestoreDirectory),
                    StringComparison.OrdinalIgnoreCase))
                {
                    RestoreDirectory = Path.GetDirectoryName(Path.GetFullPath(RestoreDirectory));
                }
                else if (!Directory.Exists(RestoreDirectory) && !string.IsNullOrEmpty(RestoreDirectory))
                {
                    throw new InvalidOperationException("The given root is invalid.");
                }

                var restoreDirectory = RestoreDirectory ?? Directory.GetCurrentDirectory();

                var rootDirectory = ProjectResolver.ResolveRootDirectory(restoreDirectory);
                ReadSettings(rootDirectory);

                string packagesDirectory = FeedOptions.TargetPackagesFolder;

                if (string.IsNullOrEmpty(packagesDirectory))
                {
                    packagesDirectory = NuGetDependencyResolver.ResolveRepositoryPath(rootDirectory);
                }

                var packagesFolderFileSystem = CreateFileSystem(packagesDirectory);
                var pathResolver = new DefaultPackagePathResolver(packagesFolderFileSystem, useSideBySidePaths: true);

                int restoreCount = 0;
                int successCount = 0;

                if (!string.IsNullOrEmpty(RestorePackageId))
                {
                    restoreCount = 1;
                    var success = await RestoreForInstall(packagesDirectory);
                    if (success)
                    {
                        successCount = 1;
                    }
                }
                else
                {
                    var projectJsonFiles = Directory.EnumerateFiles(
                        restoreDirectory,
                        Runtime.Project.ProjectFileName,
                        SearchOption.AllDirectories);
                    Func<string, Task> restorePackage = async projectJsonPath =>
                    {
                        Interlocked.Increment(ref restoreCount);
                        var success = await RestoreForProject(projectJsonPath, rootDirectory, packagesDirectory);
                        if (success)
                        {
                            Interlocked.Increment(ref successCount);
                        }
                    };

                    if (PlatformHelper.IsMono)
                    {
                        // Restoring in parallel on Mono throws native exception
                        foreach (var projectJsonFile in projectJsonFiles)
                        {
                            await restorePackage(projectJsonFile);
                        }
                    }
                    else
                    {
                        await ForEachAsync(
                            projectJsonFiles,
                            maxDegreesOfConcurrency: Environment.ProcessorCount,
                            body: restorePackage);
                    }
                }

                if (restoreCount > 1)
                {
                    Reports.Information.WriteLine(string.Format("Total time {0}ms", sw.ElapsedMilliseconds));
                }

                return restoreCount == successCount;
            }
            catch (Exception ex)
            {
                Reports.Information.WriteLine("----------");
                Reports.Information.WriteLine(ex.ToString());
                Reports.Information.WriteLine("----------");
                Reports.Information.WriteLine("Restore failed");
                Reports.Information.WriteLine(ex.Message);
                return false;
            }
        }

        private async Task<bool> RestoreForInstall(string packagesDirectory)
        {
            var success = true;

            var sw = new Stopwatch();
            sw.Start();

            var restoreOperations = new RestoreOperations(Reports.Verbose);
            var projectProviders = new List<IWalkProvider>();
            var localProviders = new List<IWalkProvider>();
            var remoteProviders = new List<IWalkProvider>();

            localProviders.Add(
                new LocalWalkProvider(
                    new NuGetDependencyResolver(
                        new PackageRepository(
                            packagesDirectory))));

            var effectiveSources = PackageSourceUtils.GetEffectivePackageSources(
                SourceProvider,
                FeedOptions.Sources,
                FeedOptions.FallbackSources);

            AddRemoteProvidersFromSources(remoteProviders, effectiveSources);

            var restoreContext = new RestoreContext()
            {
                FrameworkName = ApplicationEnvironment.RuntimeFramework,
                ProjectLibraryProviders = projectProviders,
                LocalLibraryProviders = localProviders,
                RemoteLibraryProviders = remoteProviders
            };

            if (RestorePackageVersion == null)
            {
                var packageFeeds = new List<IPackageFeed>();

                foreach (var source in effectiveSources)
                {
                    var feed = PackageSourceUtils.CreatePackageFeed(
                        source,
                        FeedOptions.NoCache,
                        FeedOptions.IgnoreFailedSources,
                        Reports);
                    if (feed != null)
                    {
                        packageFeeds.Add(feed);
                    }
                }

                var package = await PackageSourceUtils.FindLatestPackage(packageFeeds, RestorePackageId);

                if (package == null)
                {
                    Reports.Error.WriteLine("Unable to locate the package {0}".Red(), RestorePackageId);
                    return false;
                }
                RestorePackageVersion = package.Version.ToString();
            }

            Reports.Information.WriteLine("Installing package {0} {1}", RestorePackageId.Bold(), RestorePackageVersion.Bold());

            var graphNode = await restoreOperations.CreateGraphNode(
                restoreContext,
                new Library()
                {
                    Name = RestorePackageId,
                    Version = SemanticVersion.Parse(RestorePackageVersion)
                },
                _ => true);

            Reports.Information.WriteLine(string.Format("{0}, {1}ms elapsed", "Resolving complete".Green(), sw.ElapsedMilliseconds));

            var installItems = new List<GraphItem>();
            var missingItems = new HashSet<LibraryRange>();

            ForEach(new GraphNode[] { graphNode }, node =>
            {
                if (node == null || node.LibraryRange == null)
                {
                    return;
                }
                if (node.Item == null || node.Item.Match == null)
                {
                    if (!node.LibraryRange.IsGacOrFrameworkReference &&
                         node.LibraryRange.VersionRange != null &&
                         missingItems.Add(node.LibraryRange))
                    {
                        Reports.Error.WriteLine(string.Format("Unable to locate {0} >= {1}", node.LibraryRange.Name.Red().Bold(), node.LibraryRange.VersionRange));
                        success = false;
                    }
                    return;
                }
                // "kpm restore" is case-sensitive
                if (!string.Equals(node.Item.Match.Library.Name, node.LibraryRange.Name, StringComparison.Ordinal))
                {
                    if (missingItems.Add(node.LibraryRange))
                    {
                        Reports.Error.WriteLine(
                            "Unable to locate {0} >= {1}. Do you mean {2}?",
                            node.LibraryRange.Name.Red().Bold(),
                            node.LibraryRange.VersionRange,
                            node.Item.Match.Library.Name.Bold());
                        success = false;
                    }
                    return;
                }
                var isRemote = remoteProviders.Contains(node.Item.Match.Provider);
                var isAdded = installItems.Any(item => item.Match.Library == node.Item.Match.Library);
                if (!isAdded && isRemote)
                {
                    installItems.Add(node.Item);
                }
            });

            await InstallPackages(installItems, packagesDirectory, packageFilter: (library, nupkgSHA) => true);

            Reports.Information.WriteLine("{0}, {1}ms elapsed", "Install complete".Green().Bold(), sw.ElapsedMilliseconds);

            Library appLib = graphNode?.Item?.Match?.Library;

            if (appLib == null)
            {
                return false;
            }

            var resolver = new DefaultPackagePathResolver(packagesDirectory);

            // The call to GetFullPath is important because GetInstallPath returns a path with a "\.\" in it
            AppInstallPath = Path.GetFullPath(resolver.GetInstallPath(appLib.Name, appLib.Version));

            return success;
        }

        private async Task<bool> RestoreForProject(string projectJsonPath, string rootDirectory, string packagesDirectory)
        {
            var success = true;

            Reports.Information.WriteLine(string.Format("Restoring packages for {0}", projectJsonPath.Bold()));

            var sw = new Stopwatch();
            sw.Start();

            var projectFolder = Path.GetDirectoryName(projectJsonPath);
            var projectLockFilePath = Path.Combine(projectFolder, LockFileFormat.LockFileName);

            Runtime.Project project;
            if (!Runtime.Project.TryGetProject(projectJsonPath, out project))
            {
                throw new Exception("TODO: project.json parse error");
            }

            var lockFile = await ReadLockFile(projectLockFilePath);

            var useLockFile = false;
            if (Lock == false &&
                Unlock == false &&
                lockFile != null &&
                lockFile.Islocked)
            {
                useLockFile = true;
            }

            Func<string, string> getVariable = key =>
            {
                return null;
            };

            if (!ScriptExecutor.Execute(project, "prerestore", getVariable))
            {
                Reports.Error.WriteLine(ScriptExecutor.ErrorMessage);
                return false;
            }

            var projectDirectory = project.ProjectDirectory;
            var packageRepository = new PackageRepository(packagesDirectory);
            var restoreOperations = new RestoreOperations(Reports.Verbose);
            var projectProviders = new List<IWalkProvider>();
            var localProviders = new List<IWalkProvider>();
            var remoteProviders = new List<IWalkProvider>();
            var contexts = new List<RestoreContext>();

            projectProviders.Add(
                new LocalWalkProvider(
                    new ProjectReferenceDependencyProvider(
                        new ProjectResolver(
                            projectDirectory,
                            rootDirectory))));

            localProviders.Add(
                new LocalWalkProvider(
                    new NuGetDependencyResolver(packageRepository)));

            var effectiveSources = PackageSourceUtils.GetEffectivePackageSources(
                SourceProvider,
                FeedOptions.Sources,
                FeedOptions.FallbackSources);

            AddRemoteProvidersFromSources(remoteProviders, effectiveSources);

            var tasks = new List<Task<GraphNode>>();

            if (useLockFile)
            {
                Reports.Information.WriteLine(string.Format("Following lock file {0}", projectLockFilePath.White().Bold()));

                var context = new RestoreContext
                {
                    FrameworkName = ApplicationEnvironment.RuntimeFramework,
                    ProjectLibraryProviders = projectProviders,
                    LocalLibraryProviders = localProviders,
                    RemoteLibraryProviders = remoteProviders,
                };

                contexts.Add(context);

                foreach (var lockFileLibrary in lockFile.Libraries)
                {
                    var projectLibrary = new LibraryRange
                    {
                        Name = lockFileLibrary.Name,
                        VersionRange = new SemanticVersionRange
                        {
                            MinVersion = lockFileLibrary.Version,
                            MaxVersion = lockFileLibrary.Version,
                            IsMaxInclusive = true,
                            VersionFloatBehavior = SemanticVersionFloatBehavior.None,
                        }
                    };
                    tasks.Add(restoreOperations.CreateGraphNode(context, projectLibrary, _ => false));
                }
            }
            else
            {
                foreach (var configuration in project.GetTargetFrameworks())
                {
                    var context = new RestoreContext
                    {
                        FrameworkName = configuration.FrameworkName,
                        ProjectLibraryProviders = projectProviders,
                        LocalLibraryProviders = localProviders,
                        RemoteLibraryProviders = remoteProviders,
                    };
                    contexts.Add(context);
                }

                if (!contexts.Any())
                {
                    contexts.Add(new RestoreContext
                    {
                        FrameworkName = ApplicationEnvironment.RuntimeFramework,
                        ProjectLibraryProviders = projectProviders,
                        LocalLibraryProviders = localProviders,
                        RemoteLibraryProviders = remoteProviders,
                    });
                }

                foreach (var context in contexts)
                {
                    var projectLibrary = new LibraryRange
                    {
                        Name = project.Name,
                        VersionRange = new SemanticVersionRange(project.Version)
                    };

                    tasks.Add(restoreOperations.CreateGraphNode(context, projectLibrary, _ => true));
                }
            }
            var graphs = await Task.WhenAll(tasks);

            var graphItems = new List<GraphItem>();
            var installItems = new List<GraphItem>();
            var missingItems = new HashSet<LibraryRange>();

            ForEach(graphs, node =>
            {
                if (node == null || node.LibraryRange == null)
                {
                    return;
                }

                if (node.Item == null || node.Item.Match == null)
                {
                    if (!node.LibraryRange.IsGacOrFrameworkReference &&
                         node.LibraryRange.VersionRange != null &&
                         missingItems.Add(node.LibraryRange))
                    {
                        Reports.Error.WriteLine(string.Format("Unable to locate {0} {1}", node.LibraryRange.Name.Red().Bold(), node.LibraryRange.VersionRange));
                        success = false;
                    }

                    return;
                }

                // "kpm restore" is case-sensitive
                if (!string.Equals(node.Item.Match.Library.Name, node.LibraryRange.Name, StringComparison.Ordinal))
                {
                    if (missingItems.Add(node.LibraryRange))
                    {
                        Reports.Error.WriteLine("Unable to locate {0} {1}. Do you mean {2}?",
                            node.LibraryRange.Name.Red().Bold(), node.LibraryRange.VersionRange, node.Item.Match.Library.Name.Bold());

                        success = false;
                    }

                    return;
                }

                var isRemote = remoteProviders.Contains(node.Item.Match.Provider);
                var isInstallItem = installItems.Any(item => item.Match.Library == node.Item.Match.Library);

                if (!isInstallItem && isRemote)
                {
                    installItems.Add(node.Item);
                }

                var isGraphItem = graphItems.Any(item => item.Match.Library == node.Item.Match.Library);
                if (!isGraphItem)
                {
                    graphItems.Add(node.Item);
                }
            });

            await InstallPackages(installItems, packagesDirectory, packageFilter: (library, nupkgSHA) => true);

            if (success && !useLockFile)
            {
                Reports.Information.WriteLine(string.Format("Writing lock file {0}", projectLockFilePath.White().Bold()));
                WriteLockFile(projectLockFilePath, project, graphItems, new PackageRepository(packagesDirectory));
            }

            if (!ScriptExecutor.Execute(project, "postrestore", getVariable))
            {
                Reports.Error.WriteLine(ScriptExecutor.ErrorMessage);
                return false;
            }

            if (!ScriptExecutor.Execute(project, "prepare", getVariable))
            {
                Reports.Error.WriteLine(ScriptExecutor.ErrorMessage);
                return false;
            }

            Reports.Information.WriteLine(string.Format("{0}, {1}ms elapsed", "Restore complete".Green().Bold(), sw.ElapsedMilliseconds));

            return success;
        }

        private async Task InstallPackages(List<GraphItem> installItems, string packagesDirectory,
            Func<Library, string, bool> packageFilter)
        {
            using (var sha512 = SHA512.Create())
            {
                foreach (var item in installItems)
                {
                    var library = item.Match.Library;

                    var memStream = new MemoryStream();
                    await item.Match.Provider.CopyToAsync(item.Match, memStream);
                    memStream.Seek(0, SeekOrigin.Begin);
                    var nupkgSHA = Convert.ToBase64String(sha512.ComputeHash(memStream));

                    bool shouldInstall = packageFilter(library, nupkgSHA);
                    if (!shouldInstall)
                    {
                        continue;
                    }

                    Reports.Information.WriteLine("Installing {0} {1}", library.Name.Bold(), library.Version);
                    memStream.Seek(0, SeekOrigin.Begin);
                    await NuGetPackageUtils.InstallFromStream(memStream, library, packagesDirectory, sha512);
                }
            }
        }

        private Task<LockFile> ReadLockFile(string projectLockFilePath)
        {
            if (!File.Exists(projectLockFilePath))
            {
                return Task.FromResult(default(LockFile));
            }
            var lockFileFormat = new LockFileFormat();
            return Task.FromResult(lockFileFormat.Read(projectLockFilePath));
        }

        private void WriteLockFile(string projectLockFilePath, Runtime.Project project, List<GraphItem> graphItems,
            PackageRepository repository)
        {
            var lockFile = new LockFile();
            lockFile.Islocked = Lock;

            using (var sha512 = SHA512.Create())
            {
                foreach (var item in graphItems.OrderBy(x => x.Match.Library, new LibraryComparer()))
                {
                    var library = item.Match.Library;
                    var packageInfo = repository.FindPackagesById(library.Name)
                        .FirstOrDefault(p => p.Version == library.Version);

                    if (packageInfo == null)
                    {
                        continue;
                    }

                    var package = packageInfo.Package;

                    using (var nupkgStream = package.GetStream())
                    {
                        var lockFileLib = new LockFileLibrary();
                        lockFileLib.Name = package.Id;
                        lockFileLib.Version = package.Version;
                        lockFileLib.Sha = Convert.ToBase64String(sha512.ComputeHash(nupkgStream));
                        lockFileLib.DependencySets = package.DependencySets.ToList();
                        lockFileLib.FrameworkAssemblies = package.FrameworkAssemblies.ToList();
                        lockFileLib.PackageAssemblyReferences = package.PackageAssemblyReferences.ToList();
                        lockFileLib.Files = package.GetFiles().ToList();

                        lockFile.Libraries.Add(lockFileLib);
                    }
                }
            }

            // Use empty string as the key of dependencies shared by all frameworks
            lockFile.FrameworkDependencies.Add(new KeyValuePair<string, IEnumerable<string>>(
                string.Empty,
                project.Dependencies.Select(x => x.LibraryRange.ToString())));
            foreach (var frameworkInfo in project.GetTargetFrameworks())
            {
                lockFile.FrameworkDependencies.Add(new KeyValuePair<string, IEnumerable<string>>(
                    frameworkInfo.FrameworkName.ToString(),
                    frameworkInfo.Dependencies.Select(x => x.LibraryRange.ToString())));
            }

            var lockFileFormat = new LockFileFormat();
            lockFileFormat.Write(projectLockFilePath, lockFile);
        }


        private void AddRemoteProvidersFromSources(List<IWalkProvider> remoteProviders, List<PackageSource> effectiveSources)
        {
            foreach (var source in effectiveSources)
            {
                var feed = PackageSourceUtils.CreatePackageFeed(
                    source,
                    FeedOptions.NoCache,
                    FeedOptions.IgnoreFailedSources,
                    Reports);
                if (feed != null)
                {
                    remoteProviders.Add(new RemoteWalkProvider(feed));
                }
            }
        }

        private static void ExtractPackage(string targetPath, FileStream stream)
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var packOperations = new BundleOperations();
                packOperations.ExtractNupkg(archive, targetPath);
            }
        }

        void ForEach(IEnumerable<GraphNode> nodes, Action<GraphNode> callback)
        {
            foreach (var node in nodes)
            {
                callback(node);
                ForEach(node.Dependencies, callback);
            }
        }

        void Display(string indent, IEnumerable<GraphNode> graphs)
        {
            foreach (var node in graphs)
            {
                Reports.Information.WriteLine(indent + node.Item.Match.Library.Name + "@" + node.Item.Match.Library.Version);
                Display(indent + " ", node.Dependencies);
            }
        }


        private void ReadSettings(string solutionDirectory)
        {
            Settings = SettingsUtils.ReadSettings(solutionDirectory, NuGetConfigFile, FileSystem, MachineWideSettings);

            // Recreate the source provider and credential provider
            SourceProvider = PackageSourceBuilder.CreateSourceProvider(Settings);
            //HttpClient.DefaultCredentialProvider = new SettingsCredentialProvider(new ConsoleCredentialProvider(Console), SourceProvider, Console);

        }

        private IFileSystem CreateFileSystem(string path)
        {
            path = FileSystem.GetFullPath(path);
            return new PhysicalFileSystem(path);
        }

        // Based on http://blogs.msdn.com/b/pfxteam/archive/2012/03/05/10278165.aspx
        private static Task ForEachAsync<TVal>(IEnumerable<TVal> source,
                                               int maxDegreesOfConcurrency,
                                               Func<TVal, Task> body)
        {
            var tasks = Partitioner.Create(source)
                                   .GetPartitions(maxDegreesOfConcurrency)
                                   .AsParallel()
                                   .Select(async partition =>
                                   {
                                       using (partition)
                                       {
                                           while (partition.MoveNext())
                                           {
                                               await body(partition.Current);
                                           }
                                       }
                                   });

            return Task.WhenAll(tasks);
        }
        class LibraryComparer : IComparer<Library>
        {
            public int Compare(Library x, Library y)
            {
                int compare = string.Compare(x.Name, y.Name);
                if (compare == 0)
                {
                    if (x.Version == null && y.Version == null)
                    {
                        // NOOP;
                    }
                    else if (x.Version == null)
                    {
                        compare = -1;
                    }
                    else if (y.Version == null)
                    {
                        compare = 1;
                    }
                    else
                    {
                        compare = x.Version.CompareTo(y.Version);
                    }
                }
                return compare;
            }
        }
    }
}