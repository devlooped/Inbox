namespace WhatsDemo.Tests;

public class DeviceNameTests
{
    [Fact]
    public void Current_is_whatsbox_demo_by_username_on_machine()
    {
        var name = DeviceName.Current();
        Assert.Equal($"whatsbox demo by {Environment.UserName} on {Environment.MachineName}", name);
    }
}
