using NetworkDeviceWatchdog.Configuration;

namespace NetworkDeviceWatchdog.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void Sample_configuration_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "watchdog.json");
        var config = ConfigLoader.Load(path);
        Assert.Equal(4, config.Devices.Count);
        Assert.Equal("escpos-printer", config.Devices[0].Type);
    }

    [Fact]
    public void Defaults_apply_when_omitted()
    {
        var config = ConfigLoader.Parse("""{ "devices": [ { "name": "A", "type": "generic", "host": "10.0.0.1" } ] }""");
        Assert.Equal("auto", config.Language);
        Assert.Equal(60, config.IntervalSeconds);
        Assert.Equal(3, config.FailThreshold);
        Assert.Equal(10, config.BusyThreshold);
        Assert.True(config.Notifications.AlertWindow);
        Assert.Equal(18, config.Notifications.AlertFontSize);
        Assert.True(config.Devices[0].Ping);
        Assert.True(config.Devices[0].Enabled);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        var config = ConfigLoader.Parse("""
            // leading comment
            {
              "devices": [
                { "name": "A", "host": "10.0.0.1", }, // trailing comma
              ],
            }
            """);
        Assert.Single(config.Devices);
    }

    [Fact]
    public void Unknown_properties_are_rejected_to_catch_typos()
    {
        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(
            """{ "intervalSecond": 30, "devices": [ { "name": "A", "host": "10.0.0.1" } ] }"""));
        Assert.Contains("intervalSecond", e.Errors[0]);
    }

    [Fact]
    public void Malformed_json_reports_the_line()
    {
        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("{\n  \"devices\": [\n    { \"name\": \"A\" \"host\": \"x\" }\n  ]\n}"));
        Assert.Contains("line 3", e.Errors[0]);
    }

    [Fact]
    public void Validation_collects_every_problem()
    {
        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("""
            {
              "intervalSeconds": 1,
              "failThreshold": 0,
              "notifications": { "ntfyUrl": "not a url", "alertFontSize": 200 },
              "devices": [
                { "name": "", "host": "" },
                { "name": "Dup", "host": "10.0.0.1", "type": "toaster" },
                { "name": "dup", "host": "10.0.0.2", "port": 70000, "tcpPorts": [0], "mac": "zz" },
                { "name": "Web", "host": "10.0.0.3", "http": { "url": "ftp://x" } },
                { "name": "Nothing", "host": "10.0.0.4", "ping": false }
              ]
            }
            """));
        var all = string.Join("\n", e.Errors);
        Assert.Contains("intervalSeconds", all);
        Assert.Contains("failThreshold", all);
        Assert.Contains("ntfyUrl", all);
        Assert.Contains("alertFontSize", all);
        Assert.Contains("name is required", all);
        Assert.Contains("host is required", all);
        Assert.Contains("\"toaster\" is unknown", all);
        Assert.Contains("used by another device", all);
        Assert.Contains("port 70000", all);
        Assert.Contains("tcpPorts contains 0", all);
        Assert.Contains("mac \"zz\"", all);
        Assert.Contains("http.url", all);
        Assert.Contains("needs at least one check", all);
    }

    [Fact]
    public void Empty_device_list_is_an_error()
    {
        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("""{ "devices": [] }"""));
        Assert.Contains("devices is empty", e.Errors[0]);
    }

    [Theory]
    [InlineData("00-11-22-33-44-55")]
    [InlineData("00:11:22:33:44:55")]
    [InlineData("0011.2233.4455")]
    [InlineData("001122334455")]
    [InlineData("aa-bb-cc-dd-ee-ff")]
    public void Mac_addresses_in_common_formats_are_accepted(string text)
    {
        Assert.True(ConfigLoader.TryParseMac(text, out var mac));
        Assert.Equal(6, mac.GetAddressBytes().Length);
    }

    [Theory]
    [InlineData("00-11-22-33-44")]
    [InlineData("00-11-22-33-44-55-66")]
    [InlineData("gg-11-22-33-44-55")]
    public void Invalid_mac_addresses_are_rejected(string text)
    {
        Assert.False(ConfigLoader.TryParseMac(text, out _));
    }

    [Theory]
    [InlineData("escpos-printer", DeviceKind.EscPosPrinter)]
    [InlineData("ESCPOS", DeviceKind.EscPosPrinter)]
    [InlineData("printer", DeviceKind.Printer)]
    [InlineData("ipp-printer", DeviceKind.Printer)]
    [InlineData("generic", DeviceKind.Generic)]
    [InlineData("embedded", DeviceKind.Generic)]
    public void Device_type_aliases(string text, DeviceKind expected)
    {
        Assert.True(DeviceKinds.TryParse(text, out var kind));
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Serialize_then_parse_round_trips()
    {
        var config = ConfigLoader.Parse("""{ "language": "pt", "devices": [ { "name": "A", "type": "printer", "host": "10.0.0.9", "ipp": { "path": "/ipp" } } ] }""");
        var again = ConfigLoader.Parse(ConfigLoader.Serialize(config));
        Assert.Equal("pt", again.Language);
        Assert.Equal("/ipp", again.Devices[0].Ipp!.Path);
    }
}
