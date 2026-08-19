using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhatsBox;

namespace Tests;

public class WhatsBoxPackTests
{
    static readonly string[] SupportedRids =
    [
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64",
    ];

    [Fact]
    public void ResolveBinaryPath_finds_project_reference_native_and_version_runs()
    {
        var path = WhatsBoxHost.ResolveBinaryPath();
        Assert.True(File.Exists(path), path);

        var start = new ProcessStartInfo(path, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Failed to start '{path}'.");
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(15_000));
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("whatsbox", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteWhatsBoxRuntimeJson_maps_six_rids_to_rid_packages()
    {
        var repo = FindRepoRoot();
        var project = Path.Combine(repo, "src", "WhatsBox", "WhatsBox.csproj");
        var intermediate = Path.Combine(repo, "src", "WhatsBox", "obj", "Debug", "net10.0");
        Directory.CreateDirectory(intermediate);

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-restore");
        start.ArgumentList.Add("-t:WriteWhatsBoxRuntimeJson");
        start.ArgumentList.Add("-p:DesignTimeBuild=true");
        start.ArgumentList.Add("-p:GeneratePackageOnBuild=false");
        start.ArgumentList.Add("-nologo");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start dotnet msbuild.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));
        Assert.True(process.ExitCode == 0, stdout + Environment.NewLine + stderr);

        var runtimeJson = Path.Combine(intermediate, "runtime.json");
        Assert.True(File.Exists(runtimeJson), runtimeJson);
        using var doc = JsonDocument.Parse(File.ReadAllText(runtimeJson));
        var runtimes = doc.RootElement.GetProperty("runtimes");
        foreach (var rid in SupportedRids)
        {
            var range = runtimes
                .GetProperty(rid)
                .GetProperty("WhatsBox")
                .GetProperty("WhatsBox." + rid)
                .GetString();
            Assert.False(string.IsNullOrWhiteSpace(range));
            Assert.StartsWith("[", range);
            Assert.EndsWith(", )", range);
        }
    }

    [Fact]
    public void Pointer_and_rid_csproj_use_calc_pack_split()
    {
        var repo = FindRepoRoot();
        var csproj = File.ReadAllText(Path.Combine(repo, "src", "WhatsBox", "WhatsBox.csproj"));
        Assert.Contains("<PackageId>WhatsBox</PackageId>", csproj);
        Assert.Contains("WhatsBox.$(RuntimeIdentifier)", csproj);
        Assert.Contains("<IncludeBuildOutput Condition=\"'$(RuntimeIdentifier)' == ''\">true</IncludeBuildOutput>", csproj);
        Assert.Contains("<IncludeBuildOutput Condition=\"'$(RuntimeIdentifier)' != ''\">false</IncludeBuildOutput>", csproj);
        Assert.DoesNotContain("<PackAsTool", csproj);
        foreach (var rid in SupportedRids)
            Assert.Contains(rid, csproj);

        var template = File.ReadAllText(Path.Combine(repo, "src", "WhatsBox", "runtime.json"));
        using var doc = JsonDocument.Parse(template);
        Assert.True(doc.RootElement.TryGetProperty("runtimes", out _));
    }

    [Fact]
    public void Workflows_have_os_matrix_rid_pack_and_pointer_collect()
    {
        var repo = FindRepoRoot();
        var osMatrix = File.ReadAllText(Path.Combine(repo, ".github", "workflows", "os-matrix.json"));
        using (var doc = JsonDocument.Parse(osMatrix))
        {
            var oses = doc.RootElement.EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains("ubuntu-latest", oses);
            Assert.Contains("windows-latest", oses);
            Assert.Contains("macos-latest", oses);
        }

        var build = File.ReadAllText(Path.Combine(repo, ".github", "workflows", "build.yml"));
        Assert.Contains("os-matrix.json", build);
        Assert.Contains("dotnet pack src/WhatsBox/WhatsBox.csproj", build);
        Assert.Contains("name: package-${{ steps.rid.outputs.rid }}", build);

        var publish = File.ReadAllText(Path.Combine(repo, ".github", "workflows", "publish.yml"));
        Assert.Contains("rid: win-x64", publish);
        Assert.Contains("rid: win-arm64", publish);
        Assert.Contains("rid: linux-x64", publish);
        Assert.Contains("rid: linux-arm64", publish);
        Assert.Contains("rid: osx-x64", publish);
        Assert.Contains("rid: osx-arm64", publish);
        Assert.Contains("windows-11-arm", publish);
        Assert.Contains("ubuntu-24.04-arm", publish);
        Assert.Contains("macos-15-intel", publish);
        Assert.Contains("name: package-${{ matrix.rid }}", publish);
        Assert.Contains("pattern: package-*", publish);
        Assert.Contains("dotnet pack src/WhatsBox/WhatsBox.csproj", publish);
        Assert.DoesNotContain("PackAsTool", publish);
        Assert.Matches(new Regex(@"pointerPackages"), publish);
    }

    [Fact]
    public void Current_runtime_identifier_is_a_supported_pack_rid()
    {
        Assert.Contains(RuntimeInformation.RuntimeIdentifier, SupportedRids);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WhatsBox.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find WhatsBox.slnx from " + AppContext.BaseDirectory);
    }
}
