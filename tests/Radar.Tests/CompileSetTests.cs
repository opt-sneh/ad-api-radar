using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Build.Evaluation;
using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class CompileSetTests
{
    [TestMethod]
    public void SdkDefaultsIncludeSourceButNotGeneratedFolders()
    {
        using var fixture = new Fixture();
        fixture.Write(
            "App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        fixture.Write("App/a.cs", "class A { }");
        fixture.Write("App/sub/b.cs", "class B { }");
        fixture.Write("App/obj/x.cs", "class X { }");
        fixture.Write("App/bin/y.cs", "class Y { }");
        fixture.Solution("App/App.csproj");

        var (exitCode, result) = fixture.Run();

        Assert.AreEqual(0, exitCode);
        var project = result.Projects.Single();
        Assert.IsTrue(project.Success);
        CollectionAssert.AreEquivalent(
            new[] { "a.cs", "b.cs" },
            project.Files.Select(file => Path.GetFileName(file.FullPath)).ToArray()
        );
        Assert.AreEqual(2, project.CompiledFileCount);
        Assert.IsFalse(
            string.Equals(
                "true",
                fixture.EvaluatedProperty("App/App.csproj", "ManagePackageVersionsCentrally"),
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

    [TestMethod]
    public void MultiTargetProjectListsTheUnionOfEveryFramework()
    {
        using var fixture = new Fixture();
        // Before the fix the outer (no TargetFramework) evaluation listed 0 files for this shape.
        fixture.Write(
            "App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' != 'net8.0'"><Compile Remove="Net8Only.cs" /></ItemGroup>
            </Project>
            """
        );
        fixture.Write("App/Shared.cs", "class Shared { }");
        fixture.Write("App/Net8Only.cs", "class Net8Only { }");
        fixture.Solution("App/App.csproj");

        var (exitCode, result) = fixture.Run();

        Assert.AreEqual(0, exitCode);
        var project = result.Projects.Single();
        Assert.IsTrue(project.Success, project.Error);
        CollectionAssert.AreEquivalent(
            new[] { "Shared.cs", "Net8Only.cs" },
            project.Files.Select(file => Path.GetFileName(file.FullPath)).ToArray()
        );
        Assert.AreEqual(0, project.DuplicateCount, "files shared by both frameworks are listed once, not counted as duplicates");
    }

    [TestMethod]
    public void ExplicitCompileItemsPreserveLinksAndExcludeUnlistedFiles()
    {
        using var fixture = new Fixture();
        fixture.Write(
            "App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Listed.cs" />
                <Compile Include="..\shared\Linked.cs"><Link>Shared\Linked.cs</Link></Compile>
              </ItemGroup>
            </Project>
            """
        );
        fixture.Write("App/Listed.cs", "class Listed { }");
        fixture.Write("App/Unlisted.cs", "class Unlisted { }");
        fixture.Write("shared/Linked.cs", "class Linked { }");
        fixture.Solution("App/App.csproj");

        var (exitCode, result) = fixture.Run();

        Assert.AreEqual(0, exitCode);
        var project = result.Projects.Single();
        Assert.IsTrue(project.Success);
        CollectionAssert.AreEquivalent(
            new[] { "Listed.cs", "Linked.cs" },
            project.Files.Select(file => Path.GetFileName(file.FullPath)).ToArray()
        );
        var linked = project.Files.Single(file => Path.GetFileName(file.FullPath) == "Linked.cs");
        Assert.IsTrue(linked.Linked);
        Assert.AreEqual("Shared\\Linked.cs", linked.Link);
        Assert.IsTrue(linked.RelativePath.Contains("shared", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            project.Files.Single(file => Path.GetFileName(file.FullPath) == "Listed.cs").Linked
        );
    }

    [TestMethod]
    public void MalformedProjectDoesNotHideValidProjectAndReturnsFailure()
    {
        using var fixture = new Fixture();
        fixture.Write(
            "Good/Good.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        fixture.Write("Good/Good.cs", "class Good { }");
        fixture.Write("Bad/Bad.csproj", "<Project><Broken></Project>");
        fixture.Solution("Good/Good.csproj", "Bad/Bad.csproj");

        var (exitCode, result) = fixture.Run();

        Assert.AreNotEqual(0, exitCode);
        Assert.HasCount(2, result.Projects);
        Assert.IsTrue(
            result.Projects.Single(project => project.ProjectPath.Contains("Good.csproj")).Success
        );
        var bad = result.Projects.Single(project => project.ProjectPath.Contains("Bad.csproj"));
        Assert.IsFalse(bad.Success);
        Assert.IsFalse(string.IsNullOrWhiteSpace(bad.Error));
    }

    [TestMethod]
    public void DuplicateCompileItemsCountOneFile()
    {
        using var fixture = new Fixture();
        fixture.Write(
            "App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Listed.cs"><Link>First.cs</Link></Compile>
                <Compile Include="Listed.cs"><Link>Second.cs</Link></Compile>
              </ItemGroup>
            </Project>
            """
        );
        fixture.Write("App/Listed.cs", "class Listed { }");
        fixture.Solution("App/App.csproj");

        var (exitCode, result) = fixture.Run();

        Assert.AreEqual(0, exitCode);
        var project = result.Projects.Single();
        Assert.IsTrue(project.Success);
        Assert.HasCount(1, project.Files);
        Assert.AreEqual(1, project.CompiledFileCount);
        Assert.AreEqual(1, project.DuplicateCount);
        Assert.AreEqual("First.cs", project.Files.Single().Link);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root;
        private readonly string output;

        public Fixture()
        {
            var repo = FindRepoRoot();
            root = Path.Combine(repo, "data", "test-fixtures", Guid.NewGuid().ToString("N"));
            output = root + ".json";
            Directory.CreateDirectory(root);
            Write("Directory.Build.props", "<Project />");
            Write(
                "Directory.Packages.props",
                "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>"
            );
        }

        public void Write(string relativePath, string content)
        {
            string path = Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)
            );
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Solution(params string[] projects)
        {
            const string csharpProjectType = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
            var lines = new List<string>
            {
                "Microsoft Visual Studio Solution File, Format Version 12.00",
                "# Visual Studio Version 17",
            };
            foreach (string project in projects)
            {
                string name = Path.GetFileNameWithoutExtension(project);
                lines.Add(
                    $"Project(\"{csharpProjectType}\") = \"{name}\", \"{project.Replace('/', '\\')}\", \"{{{Guid.NewGuid():D}}}\""
                );
                lines.Add("EndProject");
            }
            lines.Add("Global");
            lines.Add("EndGlobal");
            Write("Fixture.sln", string.Join(Environment.NewLine, lines));
        }

        public (int ExitCode, CompileSetResult Result) Run()
        {
            int exitCode = Program.Main([
                "compile-set",
                "--repo",
                root,
                "--sln",
                "Fixture.sln",
                "--out",
                output,
            ]);
            Assert.IsTrue(
                File.Exists(output),
                "The command should write JSON even when a project fails."
            );
            var result = JsonSerializer.Deserialize<CompileSetResult>(File.ReadAllText(output));
            Assert.IsNotNull(result);
            return (exitCode, result);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string EvaluatedProperty(string project, string property)
        {
            using var collection = new ProjectCollection();
            string path = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            return collection.LoadProject(path).GetPropertyValue(property);
        }

        public void Dispose()
        {
            File.Delete(output);
            Directory.Delete(root, recursive: true);
        }

        private static string FindRepoRoot()
        {
            for (
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                directory is not null;
                directory = directory.Parent
            )
            {
                if (File.Exists(Path.Combine(directory.FullName, "AdApiRadar.slnx")))
                    return directory.FullName;
            }
            throw new DirectoryNotFoundException("Repository root not found");
        }
    }
}
