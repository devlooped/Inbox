namespace WhatsDemo;

/// <summary>Linked-device label shown in WhatsApp → Linked devices.</summary>
public static class DeviceName
{
    public static string Current() => Format(Environment.UserName, Environment.MachineName);

    public static string Format(string userName, string machineName)
        => $"whatsbox demo by {userName} on {machineName}";
}
