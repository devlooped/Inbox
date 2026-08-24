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
    public void WriteInboxRuntimeJson_maps_six_rids_to_rid_packages()
    {
        var repo = FindRepoRoot();
        var project = Path.Combine(repo, "src", "WhatsBox", "WhatsBox.csproj");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var intermediate = Path.Combine(repo, "src", "WhatsBox", "obj", configuration, "net10.0");
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
        start.ArgumentList.Add("-t:WriteInboxRuntimeJson");
        start.ArgumentList.Add("-p:Configuration=" + configuration);
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
        var slnx = File.ReadAllText(Path.Combine(repo, "Inbox.slnx"));
        Assert.Contains("src/Inbox/Inbox.csproj", slnx);
        var inbox = File.ReadAllText(Path.Combine(repo, "src", "Inbox", "Inbox.csproj"));
        Assert.Contains("<PackageId>Inbox</PackageId>", inbox);
        Assert.Contains("<AssemblyName>Inbox</AssemblyName>", inbox);
        Assert.Contains("<RootNamespace>Inbox</RootNamespace>", inbox);
        Assert.Contains(@"PackagePath=""build\Inbox.targets""", inbox);
        Assert.Contains(@"<None Update=""build\Inbox.targets""", inbox);
        Assert.DoesNotContain("<Import ", inbox);
        Assert.DoesNotContain("buildTransitive", inbox, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<IsPackable>false</IsPackable>", inbox);
        Assert.DoesNotContain("whatsbox.exe", inbox, StringComparison.OrdinalIgnoreCase);

        var targets = File.ReadAllText(Path.Combine(repo, "src", "Inbox", "build", "Inbox.targets"));
        Assert.Contains("<InboxTargetsImported>true</InboxTargetsImported>", targets);
        Assert.Contains("buildTransitive", targets);
        Assert.Contains("Condition=\"'$(RuntimeIdentifiers)' != ''\"", targets);
        Assert.Contains("$(InboxPackageId).$(RuntimeIdentifier)", targets);
        Assert.Contains("WriteInboxRuntimeJson", targets);
        Assert.Contains("PackInboxNativeBinary", targets);
        Assert.DoesNotContain("go build", targets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("whatsbox.exe", targets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<RuntimeIdentifiers Condition=", targets);
        Assert.DoesNotContain("win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64", targets);

        var csproj = File.ReadAllText(Path.Combine(repo, "src", "WhatsBox", "WhatsBox.csproj"));
        Assert.Contains("<PackageId>WhatsBox</PackageId>", csproj);
        Assert.Contains(@"..\Inbox\Inbox.csproj", csproj);
        Assert.Contains(@"..\Inbox\build\Inbox.targets", csproj);
        Assert.Contains("<RuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64</RuntimeIdentifiers>", csproj);
        Assert.DoesNotContain("PrivateAssets=\"all\"", csproj.Substring(csproj.IndexOf(@"..\Inbox\Inbox.csproj", StringComparison.Ordinal)));
        Assert.DoesNotContain("WhatsBox.$(RuntimeIdentifier)", csproj);
        Assert.DoesNotContain("IncludeBuildOutput", csproj);
        Assert.DoesNotContain("WriteWhatsBoxRuntimeJson", csproj);
        Assert.DoesNotContain("PackWhatsBoxNativeBinary", csproj);
        Assert.DoesNotContain("PackInboxReferenceOutput", csproj);
        Assert.DoesNotContain("<PackAsTool", csproj);
        Assert.Contains("InboxNativeBinary", csproj);
        Assert.Contains("BuildWhatsBoxRidNative", csproj);
        foreach (var rid in SupportedRids)
            Assert.Contains(rid, csproj);

        Assert.False(File.Exists(Path.Combine(repo, "src", "WhatsBox", "runtime.json")));
    }

    [Fact]
    public void Wd_tool_csproj_uses_math_pointer_and_rid_pack_split()
    {
        var repo = FindRepoRoot();
        var csproj = File.ReadAllText(Path.Combine(repo, "src", "WhatsDemo", "WhatsDemo.csproj"));
        Assert.Contains("<PackageId>wd</PackageId>", csproj);
        Assert.Contains("<PackAsTool>true</PackAsTool>", csproj);
        Assert.Contains("<PublishAot>true</PublishAot>", csproj);
        Assert.Contains("<ToolCommandName>wd</ToolCommandName>", csproj);
        Assert.Contains(
            "<ToolPackageRuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64</ToolPackageRuntimeIdentifiers>",
            csproj);
        Assert.DoesNotContain("wd.$(RuntimeIdentifier)", csproj);
        Assert.DoesNotContain("runtime.json", csproj);
        foreach (var rid in SupportedRids)
            Assert.Contains(rid, csproj);
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
            Assert.DoesNotContain("macos-latest", oses);
            Assert.DoesNotContain("macos-15-intel", oses);
        }

        var build = File.ReadAllText(Path.Combine(repo, ".github", "workflows", "build.yml"));
        Assert.Contains("os-matrix.json", build);
        Assert.Contains("dotnet pack src/Inbox/Inbox.csproj", build);
        Assert.Contains("dotnet pack src/WhatsBox/WhatsBox.csproj", build);
        Assert.True(build.IndexOf("dotnet pack src/Inbox/Inbox.csproj", StringComparison.Ordinal) <
            build.IndexOf("dotnet pack src/WhatsBox/WhatsBox.csproj", StringComparison.Ordinal));
        Assert.Contains("dotnet pack src/WhatsDemo/WhatsDemo.csproj", build);
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
        Assert.Contains("dotnet pack src/Inbox/Inbox.csproj", publish);
        Assert.Contains("dotnet pack src/WhatsBox/WhatsBox.csproj", publish);
        Assert.True(publish.IndexOf("dotnet pack src/Inbox/Inbox.csproj", StringComparison.Ordinal) <
            publish.IndexOf("dotnet pack src/WhatsBox/WhatsBox.csproj", StringComparison.Ordinal));
        Assert.Contains("dotnet pack src/WhatsDemo/WhatsDemo.csproj", publish);
        Assert.DoesNotContain("<PackAsTool", File.ReadAllText(Path.Combine(repo, "src", "WhatsBox", "WhatsBox.csproj")));
        Assert.Matches(new Regex(@"pointerPackages"), publish);

        var demo = File.ReadAllText(Path.Combine(repo, "demo.ps1"));
        Assert.Contains("src/WhatsDemo/WhatsDemo.csproj", demo);
        Assert.Contains("tool', 'install', 'wd'", demo);
        Assert.Contains("--configfile", demo);
        Assert.Contains("src/WhatsDemo/nuget.config", demo);
    }

    [Fact]
    public void Current_runtime_identifier_is_a_supported_pack_rid()
    {
        Assert.Contains(PortablePackRid(RuntimeInformation.RuntimeIdentifier), SupportedRids);
    }

    // Distro RIDs such as ubuntu.24.04-x64 pack as linux-x64.
    static string PortablePackRid(string rid)
    {
        if (rid.StartsWith("ubuntu", StringComparison.OrdinalIgnoreCase))
        {
            var dash = rid.LastIndexOf('-');
            if (dash >= 0)
                return "linux" + rid[dash..];
        }

        return rid;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Inbox.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find Inbox.slnx from " + AppContext.BaseDirectory);
    }
}
