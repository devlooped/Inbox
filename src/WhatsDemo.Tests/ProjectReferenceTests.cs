namespace WhatsDemo.Tests;

public class ProjectReferenceTests
{
    [Fact]
    public void Demo_package_references_whatsbox_star_and_does_not_project_reference_it()
    {
        var csproj = FindDemoProject();
        var xml = File.ReadAllText(csproj);
        Assert.Contains("""<PackageReference Include="WhatsBox" Version="*" />""", xml);
        Assert.Contains("<PackageId>wd</PackageId>", xml);
        Assert.Contains("<PackAsTool>true</PackAsTool>", xml);
        Assert.Contains("<PublishAot>true</PublishAot>", xml);
        Assert.Contains("<ToolCommandName>wd</ToolCommandName>", xml);
        Assert.Contains("<ToolPackageRuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64</ToolPackageRuntimeIdentifiers>", xml);
        Assert.DoesNotContain("wd.$(RuntimeIdentifier)", xml);
        Assert.Contains("RestoreSources", xml);
        Assert.Contains("$(PackageOutputPath)", xml);
        Assert.Contains("Exists('$(PackageOutputPath)')", xml);
        Assert.Contains("<RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>", xml);
        var nuget = File.ReadAllText(Path.Combine(Path.GetDirectoryName(csproj)!, "nuget.config"));
        Assert.Contains("ignoreFailedSources", nuget);
        Assert.Contains("packageSourceMapping", nuget);
        Assert.Contains("key=\"local\"", nuget);
        Assert.Contains("../../bin", nuget);
        Assert.DoesNotContain("WhatsBox\\WhatsBox.csproj", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WhatsBox/WhatsBox.csproj", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Program_wires_store_device_connect_qr_pair_self_chat_and_slash_commands()
    {
        var src = File.ReadAllText(FindProgram());
        Assert.Contains("Path.GetFullPath(Environment.CurrentDirectory)", src);
        Assert.Contains(".store", src);
        Assert.Contains("Directory.CreateDirectory(store)", src);
        Assert.Contains("DeviceName.Current()", src);
        Assert.Contains("Connect = true", src);
        Assert.Contains("QrRenderer.Render(qr.Code)", src);
        Assert.Contains("Paired", src);
        Assert.Contains("Subscribe = sync.InitialSubscribe", src);
        Assert.Contains("DirectorySync.Load", src);
        Assert.Contains("sync.NoteSelf", src);
        Assert.Contains("icon: false", src);
        Assert.Contains("session.BeginSend(text)", src);
        Assert.Contains("session.RememberSent(sent.Id, text)", src);
        Assert.Contains("session.FormatOutbound(text, DateTimeOffset.Now, sent.Topic)", src);
        Assert.Contains("text.Handle, text.ByName", src);
        Assert.Contains("AtMentions.TryParse", src);
        Assert.Contains("Completions.Complete", src);
        Assert.Contains("RecentChats", src);
        Assert.Contains("recents.ReplyTo(mention.ReplyId)", src);
        var beginSend = src.IndexOf("session.BeginSend(text)", StringComparison.Ordinal);
        var sendAsync = src.IndexOf("box.SendAsync(to, text: text, reply: reply", StringComparison.Ordinal);
        Assert.True(beginSend >= 0 && sendAsync > beginSend);
        Assert.Contains("box.ReadAsync(text.Topic, [id]", src);
        Assert.Contains("TopicResolver.IsGroup(text.Topic) ? text.By : null", src);
        Assert.Contains("SlashCommands.TryParse", src);
        Assert.Contains("LogoutAsync", src);
        Assert.Contains("DisconnectAsync", src);
        Assert.Contains("ConnectAsync", src);
        Assert.Contains("GetDirectoryAsync", src);
        Assert.Contains("UnsubscribeAsync", src);
        Assert.Contains("cannot unsubscribe self-chat", src);
        Assert.Contains("OnSubscribeAsync", src);
        Assert.Contains("WhatsJsonContext.Default.DirectoryRow", src);
        Assert.Contains("JsonPanel.Render", src);
        Assert.Contains("ReadArgumentAsync", src);
        Assert.Contains("ReadTopicAsync", src);
        Assert.Contains("TopicResolver.ResolveAsync", src);
        Assert.Contains("ListDirectoryAsync", src);
        Assert.Contains("DirectoryBook.NormalizeTopic", src);
    }

    static string FindDemoProject() => FindUnderDemo("WhatsDemo.csproj");

    static string FindProgram() => FindUnderDemo("Program.cs");

    static string FindUnderDemo(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;
            var sibling = Path.Combine(dir.FullName, "WhatsDemo", fileName);
            if (File.Exists(sibling))
                return sibling;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
