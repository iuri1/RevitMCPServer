using System.IO;
using RevitMCPAddin.Panel;
using Xunit;

namespace RevitMCPAddin.Tests;

public class ExtraPanelConfigTests
{
    [Fact]
    public void UrlOnly_PlainBrowserPane()
    {
        var s = ExtraPanelConfig.Parse("{\"url\":\"http://127.0.0.1:8610/\",\"label\":\"Bis\"}");
        Assert.NotNull(s);
        Assert.Equal("Bis", s!.Label);
        Assert.Equal("Bis", s.Tab);
        Assert.Null(s.Start);
        Assert.Null(s.Waiting);
    }

    [Fact]
    public void StartBlock_ParsedWithCwd()
    {
        var cwd = Path.GetTempPath().TrimEnd('\\', '/');
        var json = "{\"url\":\"http://127.0.0.1:8610/\",\"label\":\"Bis\",\"waiting\":\"Iniciando o Bis…\","
                   + "\"start\":{\"file\":\"python\",\"args\":\"web_dashboard.py --port 8610\",\"cwd\":"
                   + System.Text.Json.JsonSerializer.Serialize(cwd) + "}}";
        var s = ExtraPanelConfig.Parse(json);
        Assert.NotNull(s?.Start);
        Assert.Equal("python", s!.Start!.File);
        Assert.Equal("web_dashboard.py --port 8610", s.Start.Args);
        Assert.Equal(cwd, s.Start.Cwd);
        Assert.Equal("Iniciando o Bis…", s.Waiting);
    }

    [Fact]
    public void StartBlock_MissingCwdFolder_IgnoredButPaneKept()
    {
        var json = "{\"url\":\"http://127.0.0.1:8610/\",\"start\":{\"file\":\"python\",\"cwd\":\"Z:\\\\nao\\\\existe\"}}";
        var s = ExtraPanelConfig.Parse(json);
        Assert.NotNull(s);
        Assert.Null(s!.Start);
    }

    [Fact]
    public void StartBlock_WithoutFile_Ignored()
    {
        var s = ExtraPanelConfig.Parse("{\"url\":\"http://127.0.0.1:8610/\",\"start\":{\"args\":\"x\"}}");
        Assert.NotNull(s);
        Assert.Null(s!.Start);
    }

    [Fact]
    public void Disabled_OrBadUrl_Null()
    {
        Assert.Null(ExtraPanelConfig.Parse("{\"url\":\"http://127.0.0.1:8610/\",\"enabled\":false}"));
        Assert.Null(ExtraPanelConfig.Parse("{\"url\":\"ftp://x/\"}"));
        Assert.Null(ExtraPanelConfig.Parse("not json"));
    }
}
