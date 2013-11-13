﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Emit;

namespace Loader
{
    public class RoslynLoader : IAssemblyLoader
    {
        private readonly Dictionary<string, Tuple<Assembly, MetadataReference>> _compiledAssemblies = new Dictionary<string, Tuple<Assembly, MetadataReference>>(StringComparer.OrdinalIgnoreCase);
        private readonly string _solutionPath;
        private readonly IFileWatcher _watcher;
        private readonly IFrameworkReferenceResolver _resolver;

        public RoslynLoader(string solutionPath, IFileWatcher watcher, IFrameworkReferenceResolver resolver)
        {
            _solutionPath = solutionPath;
            _watcher = watcher;
            _resolver = resolver;
        }

        public Assembly Load(LoadOptions options)
        {
            string name = options.AssemblyName;

            Tuple<Assembly, MetadataReference> compiledAssembly;
            if (_compiledAssemblies.TryGetValue(name, out compiledAssembly))
            {
                return compiledAssembly.Item1;
            }

            string path = Path.Combine(_solutionPath, name);
            RoslynProject project;

            // Can't find a project file with the name so bail
            if (!RoslynProject.TryGetProject(path, out project))
            {
                return null;
            }

            _watcher.WatchDirectory(path, ".cs");
            _watcher.WatchFile(project.ProjectFilePath);

            var trees = new List<SyntaxTree>();

            foreach (var sourcePath in project.SourceFiles)
            {
                _watcher.WatchFile(sourcePath);
                trees.Add(SyntaxTree.ParseFile(sourcePath));
            }

            foreach (var directory in Directory.EnumerateDirectories(path, "*.*", SearchOption.AllDirectories))
            {
                _watcher.WatchDirectory(directory, ".cs");
            }


            List<MetadataReference> references = null;

            if (project.Dependencies.Count > 0)
            {
                Trace.TraceInformation("Loading dependencies for '{0}'", project.Name);

                references = project.Dependencies
                                .AsParallel()
                                .Select(d =>
                                {
                                    ExceptionDispatchInfo info = null;

                                    try
                                    {
                                        var loadedAssembly = Assembly.Load(d.Name);

                                        Tuple<Assembly, MetadataReference> compiledDependency;
                                        if (_compiledAssemblies.TryGetValue(d.Name, out compiledDependency))
                                        {
                                            return compiledDependency.Item2;
                                        }

                                        return new MetadataFileReference(loadedAssembly.Location);
                                    }
                                    catch (FileNotFoundException ex)
                                    {
                                        info = ExceptionDispatchInfo.Capture(ex);

                                        try
                                        {
                                            return MetadataFileReference.CreateAssemblyReference(d.Name);
                                        }
                                        catch
                                        {
                                            info.Throw();

                                            return null;
                                        }
                                    }
                                }).ToList();

                Trace.TraceInformation("Completed loading dependencies for '{0}'", project.Name);
            }
            else
            {
                references = new List<MetadataReference>();
            }

            references.AddRange(_resolver.GetFrameworkReferences(project.TargetFramework));

            // Create a compilation
            var compilation = Compilation.Create(
                name,
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                syntaxTrees: trees,
                references: references);

            if (options.OutputPath != null)
            {
                Directory.CreateDirectory(options.OutputPath);

                string assemblyPath = Path.Combine(options.OutputPath, name + ".dll");
                string pdbPath = Path.Combine(options.OutputPath, name + ".pdb");

                var result = compilation.Emit(assemblyPath, pdbPath);

                if (!result.Success)
                {
                    ReportCompilationError(result);

                    return null;
                }

                Trace.TraceInformation("{0} -> {1}", name, assemblyPath);

                return Assembly.LoadFile(assemblyPath);
            }

            return CompileToMemoryStream(name, compilation);
        }

        private Assembly CompileToMemoryStream(string name, Compilation compilation)
        {
            // Put symbols in a .symbols path
            var pdbPath = Path.Combine(_solutionPath, ".symbols", name + ".pdb");

            Directory.CreateDirectory(Path.GetDirectoryName(pdbPath));

            using (var fs = File.Create(pdbPath))
            using (var ms = new MemoryStream())
            {
                EmitResult result = compilation.Emit(ms, pdbStream: fs);

                if (!result.Success)
                {
                    ReportCompilationError(result);

                    return null;
                }

                var bytes = ms.ToArray();

                var assembly = Assembly.Load(bytes);
                MetadataReference reference = new MetadataImageReference(bytes);

                var compiled = Tuple.Create(assembly, reference);

                _compiledAssemblies[name] = compiled;

                return assembly;
            }
        }

        private static void ReportCompilationError(EmitResult result)
        {
            throw new InvalidDataException(String.Join(Environment.NewLine, result.Diagnostics.Select(d => d.GetMessage())));
        }
    }

}
