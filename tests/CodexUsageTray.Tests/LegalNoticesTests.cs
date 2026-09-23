namespace CodexUsageTray.Tests;

public sealed class LegalNoticesTests
{
    [Fact]
    public void EmbeddedTextContainsProjectLicenseAndThirdPartyNotices()
    {
        var notices = LegalNotices.ReadAll();

        Assert.Contains("Copyright (c) 2026 Matthias Heinsius", notices);
        Assert.Contains("DISTRIBUTED WITH THE SELF-CONTAINED APPLICATION", notices);
        Assert.Contains("MICROSOFT .NET RUNTIME THIRD-PARTY NOTICES", notices);
        Assert.Contains("MICROSOFT CODE COVERAGE", notices);
        Assert.Contains("XUNIT.NET", notices);
    }
}
