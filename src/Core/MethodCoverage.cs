﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fettle.Core.Internal;
using Fettle.Core.Internal.NUnit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.MSBuild;

namespace Fettle.Core
{
    public class MethodCoverage : IMethodCoverage
    {
        private readonly ITestFinder testFinder;
        private readonly ITestRunner testRunner;

        public MethodCoverage() : this(new NUnitTestEngine(), new NUnitTestEngine())
        {
        }

        internal MethodCoverage(ITestFinder testFinder, ITestRunner testRunner)
        {
            this.testFinder = testFinder;
            this.testRunner = testRunner;
        }

        public async Task<CoverageAnalysisResult> AnalyseMethodCoverage(Config config)
        {
            var baseTempDirectory = TempDirectory.Create();

            try
            {
                var copiedTestAssemblyFilePaths = 
                    CopyTestAssembliesToTempDirectories(
                        config.TestAssemblyFilePaths, 
                        baseTempDirectory)
                    .ToList();

                using (var workspace = MSBuildWorkspace.Create())
                {
                    var solution = await workspace.OpenSolutionAsync(config.SolutionFilePath);

                    await InstrumentThenCompileMultipleProjects(
                        solution.Projects.Where(p => Filtering.ShouldMutateProject(p, config)),
                        baseTempDirectory,
                        copiedTestAssemblyFilePaths);

                    var methodsAndCoveringTests = new Dictionary<string, ImmutableHashSet<string>>();

                    foreach (var copiedTestAssemblyFilePath in copiedTestAssemblyFilePaths)
                    {
                        var tests = testFinder.FindTests(new [] { copiedTestAssemblyFilePath });

                        if (!RunTestsAndCollectExecutedMethods(
                            tests,
                            copiedTestAssemblyFilePath,
                            methodsAndCoveringTests))
                        {
                            return CoverageAnalysisResult.Error("A test failed");
                        }    
                    }

                    return CoverageAnalysisResult.Success(methodsAndCoveringTests);
                }
            }
            finally
            {
                Directory.Delete(baseTempDirectory, recursive: true);
            }
        }

        private bool RunTestsAndCollectExecutedMethods(
            IEnumerable<string> tests,
            string copiedTestAssemblyFilePath, 
            IDictionary<string, ImmutableHashSet<string>> methodsAndCoveringTests)
        {
            var runResult = testRunner.RunTestsAndCollectExecutedMethods(
                new [] { copiedTestAssemblyFilePath }, tests, methodsAndCoveringTests);

            // todo: remove
            var result = new List<string>();
            foreach (var methodsAndCoveringTest in methodsAndCoveringTests)
            {
                result.Add($"{methodsAndCoveringTest.Key}");
                methodsAndCoveringTest.Value.ToList().ForEach(x => result.Add($"\t{x}"));
                result.Add(Environment.NewLine);
            }
            File.WriteAllLines(@"c:\temp\coverage.txt", result);

            if (runResult.Status != TestRunStatus.AllTestsPassed)
            {
                return false;
            }

            return true;
        }

        private static async Task InstrumentThenCompileMultipleProjects(
            IEnumerable<Project> projects,
            string baseTempDirectory,
            IList<string> copiedTestAssemblyFilePaths)
        {
            foreach (var project in projects)
            {
                var outputFilePath = Path.Combine(baseTempDirectory, $@"{project.AssemblyName}.dll");

                await InstrumentThenCompileProject(project, outputFilePath);

                CopyInstrumentedAssemblyIntoTempTestAssemblyDirectories(
                    outputFilePath, 
                    copiedTestAssemblyFilePaths.Select(Path.GetDirectoryName));
            }
        }

        private static async Task InstrumentThenCompileProject(
            Project project, 
            string outputFilePath)
        {
            var originalSyntaxTrees = new List<SyntaxTree>();
            var modifiedSyntaxTrees = new List<SyntaxTree>();

            var namespaceSuffix = project.Name.Replace(".", "");

            foreach (var originalClass in project.Documents)
            {
                var originalSyntaxTree = await originalClass.GetSyntaxTreeAsync().ConfigureAwait(false);
                originalSyntaxTrees.Add(originalSyntaxTree);
                modifiedSyntaxTrees.Add(
                    await InstrumentDocument(originalSyntaxTree, originalClass, namespaceSuffix));
            }

            var compilation = (await project.GetCompilationAsync().ConfigureAwait(false))
                .RemoveSyntaxTrees(originalSyntaxTrees)
                .AddSyntaxTrees(modifiedSyntaxTrees)
                .AddSyntaxTrees(GenerateInstrumentationCollectorDocument(namespaceSuffix));

            var compilationResult = ProjectCompilation.CompileProject(
                outputFilePath,
                compilation);
            if (!compilationResult.Success)
            {
                var diagnostics = string.Join(Environment.NewLine, compilationResult.Diagnostics);
                throw new Exception(
                    $"Failed to compile project {compilation.AssemblyName}{Environment.NewLine}{diagnostics}");
            }
        }

        private static SyntaxTree GenerateInstrumentationCollectorDocument(string namespaceSuffix)
        {
            string classContents = $@"
            
            using System.Text;
            using System.Net.Sockets;

            namespace _FettleInstrumentation_{namespaceSuffix}
            {{
                public static class Coverage
                {{
                    public static void ReportMethodExecuted(string fullMethodName)
                    {{
                        using (var client = new TcpClient(""127.0.0.1"", 4444))
                        {{
                            var messageAsBytes = Encoding.UTF8.GetBytes($""{{fullMethodName}}\n"");
                            client.GetStream().Write(messageAsBytes, 0, messageAsBytes.Length);
                            client.Close();
                        }}
                    }}
                }}
            }}";

            return SyntaxFactory.ParseCompilationUnit(classContents).SyntaxTree;
        }

        private static async Task<SyntaxTree> InstrumentDocument(
            SyntaxTree originalSyntaxTree,
            Document document,
            string namespaceSuffix)
        {
            var root = await originalSyntaxTree.GetRootAsync();
            var semanticModel = await document.GetSemanticModelAsync();
            var documentEditor = DocumentEditor.CreateAsync(document).Result;

            foreach (var classNode in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                foreach (var methodNode in classNode.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var fullMethodName = methodNode.ChildNodes().First().NameOfContainingMethod(semanticModel);

                    var newNode = SyntaxFactory.ParseStatement(
                        $"_FettleInstrumentation_{namespaceSuffix}.Coverage.ReportMethodExecuted(\"{fullMethodName}\");");

                    var firstChildNode = methodNode.Body.ChildNodes().FirstOrDefault();
                    if (firstChildNode != null)
                    {
                        documentEditor.InsertBefore(firstChildNode, newNode);
                    }
                    else
                    {
                        // the method is empty
                        documentEditor.ReplaceNode(
                            methodNode, 
                            methodNode.WithBody(SyntaxFactory.Block(newNode)));
                    }
                }
            }

            return await documentEditor.GetChangedDocument().GetSyntaxTreeAsync();
        }

        private static void CopyInstrumentedAssemblyIntoTempTestAssemblyDirectories(
            string instrumentedAssemblyFilePath,
            IEnumerable<string> copiedTestAssemblyDirectories)
        {
            foreach (var copiedTestAssemblyDirectory in copiedTestAssemblyDirectories)
            {
                File.Copy(
                    instrumentedAssemblyFilePath, 
                    Path.Combine(copiedTestAssemblyDirectory, Path.GetFileName(instrumentedAssemblyFilePath)),
                    overwrite: true);
            }
        }

        private static IEnumerable<string> CopyTestAssembliesToTempDirectories(
            IEnumerable<string> testAssemblyFilePaths,
            string baseTempDirectory)
        {
            foreach (var testAssemblyFilePath in testAssemblyFilePaths)
            {
                var fromDir = Path.GetDirectoryName(testAssemblyFilePath);
                var toDir = Path.Combine(baseTempDirectory, Path.GetFileNameWithoutExtension(testAssemblyFilePath));
                Directory.CreateDirectory(toDir);
                DirectoryUtils.CopyDirectoryContents(fromDir, toDir);

                yield return Path.Combine(toDir, Path.GetFileName(testAssemblyFilePath));
            }
        }
    }
}