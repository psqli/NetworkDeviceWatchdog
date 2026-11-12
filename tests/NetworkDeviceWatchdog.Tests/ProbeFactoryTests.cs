using NetworkDeviceWatchdog.Checks;
using NetworkDeviceWatchdog.Configuration;

namespace NetworkDeviceWatchdog.Tests;

public class ProbeFactoryTests
{
    [Fact]
    public void Factory_builds_the_expected_checks_per_device_type()
    {
        var services = new ProbeServices { PrintQueues = new NoQueues() };
        var timeouts = new TimeoutConfig();

        string[] Names(DeviceConfig d)
        {
            using var probe = DeviceProbeFactory.Create(d, timeouts, services);
            return [.. probe.Checks.Select(c => c.Name)];
        }

        Assert.Equal(new[] { "busy:9100", "ping", "mac", "escpos:9100", "queue" },
            Names(new DeviceConfig { Name = "R", Type = "escpos-printer", Host = "10.0.0.1", Mac = "00-11-22-33-44-55", WindowsQueue = "Q" }));
        Assert.Equal(new[] { "ping", "ipp:631" },
            Names(new DeviceConfig { Name = "O", Type = "printer", Host = "10.0.0.2" }));
        Assert.Equal(new[] { "ping", "tcp:9100" },
            Names(new DeviceConfig { Name = "O", Type = "printer", Host = "10.0.0.2", Ipp = new IppConfig { Enabled = false } }));
        Assert.Equal(new[] { "tcp:1883,80", "http" },
            Names(new DeviceConfig { Name = "G", Type = "generic", Host = "10.0.0.3", Ping = false, TcpPorts = [1883, 80, 1883], Http = new HttpCheckConfig { Url = "http://10.0.0.3/" } }));
        Assert.Equal(new[] { "ping", "tcp:502", "tcp:80" },
            Names(new DeviceConfig { Name = "G", Type = "generic", Host = "10.0.0.4", Port = 502, TcpPorts = [80, 502] }));
    }

    private sealed class NoQueues : IPrintQueueInspector
    {
        public PrintQueueProblem? FindProblem(string queueName) => null;
    }
}
