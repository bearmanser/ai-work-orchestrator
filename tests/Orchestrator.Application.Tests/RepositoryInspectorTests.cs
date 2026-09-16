using Orchestrator.Application.Repositories;
using Orchestrator.Application.Repositories.Detectors;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class RepositoryInspectorTests : IDisposable
{
    private const string CsprojXml = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="Dapper" Version="2.1.35" />
            <ProjectReference Include="..\\Shared\\Shared.csproj" />
          </ItemGroup>
        </Project>
        """;

    private readonly List<string> _roots = [];

    private static readonly RepositoryInspector Inspector = new(
        new DotNetDetector(), new NodeDetector(), new InstructionFileDetector());

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Inspect_DotNetSolution_SuggestsRestoreBuildTest()
    {
        var root = TempRepoWith(("Api.sln", ""), ("src/Api/Api.csproj", CsprojXml), ("tests/Api.Tests/Api.Tests.csproj", CsprojXml));

        var result = await Inspector.InspectAsync(root, default);

        result.SuggestedCommands!.Restore.ShouldBe("dotnet restore");
        result.SuggestedCommands.Build.ShouldBe("dotnet build --no-restore");
        result.SuggestedCommands.Test.ShouldBe("dotnet test --no-build");
    }

    [Fact]
    public async Task Inspect_NodePackageWithTestScript_SuggestsNpmCommands()
    {
        var root = TempRepoWith(("package.json", """{"scripts":{"build":"vite build","test":"vitest run"}}"""));

        var result = await Inspector.InspectAsync(root, default);

        result.SuggestedCommands!.Build.ShouldBe("npm run build");
        result.SuggestedCommands.Test.ShouldBe("npm run test");
    }

    [Fact]
    public async Task Inspect_NodePackageWithoutTestScript_LeavesTestNull()
    {
        var root = TempRepoWith(("package.json", """{"scripts":{"build":"vite build"}}"""));
        (await Inspector.InspectAsync(root, default)).SuggestedCommands!.Test.ShouldBeNull();
    }

    [Fact]
    public async Task Inspect_FindsInstructionFiles()
    {
        var root = TempRepoWith(("CLAUDE.md", "# rules"), ("CONTRIBUTING.md", "# contributing"), ("README.md", "# readme"));

        var files = (await Inspector.InspectAsync(root, default)).InstructionFiles;

        files.ShouldContain("CLAUDE.md");
        files.ShouldContain("CONTRIBUTING.md");
    }

    [Fact]
    public async Task Inspect_HttpBaseUrlInConfiguration_IsReportedWithItsSourcePath()
    {
        var root = TempRepoWith(("src/Web/appsettings.json",
            """{"Services":{"BackendApi":{"BaseUrl":"https://backend.internal.kameleon.no"}}}"""));

        var dependency = (await Inspector.InspectAsync(root, default))
            .Dependencies.Single(d => d.Kind == "http-base-url");

        dependency.Identifier.ShouldBe("https://backend.internal.kameleon.no");
        dependency.SourcePath.ShouldBe("src/Web/appsettings.json");
    }

    [Fact]
    public async Task Inspect_NeverInfersFromDirectoryNameAlone()
    {
        // A folder called "backend-client" proves nothing; only file contents count.
        var root = TempRepoWith(("src/backend-client/.gitkeep", ""));
        (await Inspector.InspectAsync(root, default)).Dependencies.ShouldBeEmpty();
    }

    [Fact]
    public async Task Inspect_IgnoresNodeModulesAndBinObj()
    {
        var root = TempRepoWith(("node_modules/pkg/package.json", """{"scripts":{"test":"x"}}"""),
                                ("src/bin/Debug/App.deps.json", "{}"));
        (await Inspector.InspectAsync(root, default)).Projects.ShouldBeEmpty();
    }

    [Fact]
    public async Task Inspect_DotNetProjects_ReportsNugetAndProjectReferenceDependencies()
    {
        var root = TempRepoWith(("src/Api/Api.csproj", CsprojXml));

        var dependencies = (await Inspector.InspectAsync(root, default)).Dependencies;

        var nuget = dependencies.Single(d => d.Kind == "nuget");
        nuget.Identifier.ShouldBe("Dapper");
        nuget.Version.ShouldBe("2.1.35");
        nuget.SourcePath.ShouldBe("src/Api/Api.csproj");

        var projectRef = dependencies.Single(d => d.Kind == "project-reference");
        projectRef.SourcePath.ShouldBe("src/Api/Api.csproj");
    }

    [Fact]
    public async Task Inspect_OpenApiFile_IsReportedAsApiSurface()
    {
        var root = TempRepoWith(("api/openapi.json", """{"openapi":"3.0.0"}"""));

        var surfaces = (await Inspector.InspectAsync(root, default)).ApiSurfaces;

        surfaces.Select(s => s.Path).ShouldContain("api/openapi.json");
    }

    [Fact]
    public async Task Inspect_GitignoredDirectory_IsSkipped()
    {
        var root = TempRepoWith(
            (".gitignore", "generated/\n"),
            ("generated/pkg/package.json", """{"scripts":{"test":"x"}}"""));

        (await Inspector.InspectAsync(root, default)).Projects.ShouldBeEmpty();
    }

    private string TempRepoWith(params (string RelativePath, string Content)[] files)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "repo-inspect-" + Guid.NewGuid().ToString("N"))).FullName;
        _roots.Add(root);

        foreach (var (relativePath, content) in files)
        {
            var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return root;
    }
}
