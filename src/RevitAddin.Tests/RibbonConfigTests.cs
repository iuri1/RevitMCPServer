using RevitMCPAddin.Panel;
using Xunit;

namespace RevitMCPAddin.Tests;

public class RibbonConfigTests
{
    private const string ExtraNoFlag =
        "{\"url\":\"http://127.0.0.1:8610/\",\"tab\":\"Orquestrador\",\"label\":\"Orquestrador\"}";

    [Fact]
    public void Default_NothingConfigured_NoTabsNoAutoAudit()
    {
        var s = RibbonConfig.Resolve(null, null, null);
        Assert.False(s.Tabs);
        Assert.False(s.AutoAudit);
    }

    [Fact]
    public void ExtraPanelWithoutFlag_NoTabs()
    {
        var s = RibbonConfig.Resolve(null, ExtraNoFlag, null);
        Assert.False(s.Tabs);
        Assert.False(s.AutoAudit);
    }

    [Fact]
    public void AbasTrueInExtraPanel_RestoresUpstreamBehaviour()
    {
        var json = "{\"url\":\"http://127.0.0.1:8610/\",\"abas\":true}";
        var s = RibbonConfig.Resolve(null, json, null);
        Assert.True(s.Tabs);
        Assert.True(s.AutoAudit);
    }

    [Fact]
    public void AbasInPanelJson_UsedWhenExtraHasNone()
    {
        var s = RibbonConfig.Resolve(null, ExtraNoFlag, "{\"abas\":true}");
        Assert.True(s.Tabs);
    }

    [Fact]
    public void ExtraPanelFlag_WinsOverPanelJson()
    {
        var s = RibbonConfig.Resolve(null, "{\"abas\":false}", "{\"abas\":true}");
        Assert.False(s.Tabs);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("SIM", true)]
    [InlineData("0", false)]
    [InlineData("nao", false)]
    [InlineData("off", false)]
    public void EnvVar_WinsOverFiles(string env, bool expected)
    {
        var s = RibbonConfig.Resolve(env, "{\"abas\":" + (!expected).ToString().ToLowerInvariant() + "}", null);
        Assert.Equal(expected, s.Tabs);
    }

    [Fact]
    public void UnknownEnvValue_FallsThroughToFiles()
    {
        var s = RibbonConfig.Resolve("talvez", "{\"abas\":true}", null);
        Assert.True(s.Tabs);
    }

    [Fact]
    public void AutoAuditFlag_IndependentOfTabs()
    {
        var on = RibbonConfig.Resolve(null, null, "{\"autoaudit\":true}");
        Assert.False(on.Tabs);
        Assert.True(on.AutoAudit);

        var off = RibbonConfig.Resolve("1", null, "{\"autoaudit\":false}");
        Assert.True(off.Tabs);
        Assert.False(off.AutoAudit);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"abas\":{}}")]
    [InlineData("")]
    public void CorruptConfig_FallsBackToDefault(string json)
    {
        var s = RibbonConfig.Resolve(null, json, json);
        Assert.False(s.Tabs);
        Assert.False(s.AutoAudit);
    }

    [Fact]
    public void StringAndNumberFlags_Accepted()
    {
        Assert.True(RibbonConfig.Resolve(null, "{\"abas\":\"sim\"}", null).Tabs);
        Assert.True(RibbonConfig.Resolve(null, "{\"abas\":1}", null).Tabs);
        Assert.False(RibbonConfig.Resolve(null, "{\"abas\":0}", null).Tabs);
    }
}
