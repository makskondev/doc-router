using DocRouter.Core;
using DocRouter.Engine;

namespace DocRouter.Tests;

public class ConfigCheckTests
{
    private static DirectoriesOptions Dirs() => new()
    {
        Docs = @"\\s\docs", Signed = @"\\s\signed", Reports = @"\\s\reports",
        DepoReportsIn = @"\\s\in\depo", BrokReportsIn = @"\\s\in\brok", StampTemplates = "/app/stamps"
    };

    [Fact]
    public void Complete_settings_have_no_errors() =>
        Assert.Empty(ConfigCheck.MissingSettings(Dirs(), new MailOptions { Default = "a@x", Brokers = "b@x", Depo = "c@x" }));

    [Fact]
    public void Missing_values_are_named_with_environment_variable_names()
    {
        var dirs = Dirs();
        dirs.Reports = "";
        var errors = ConfigCheck.MissingSettings(dirs, new MailOptions { Default = "a@x", Brokers = " ", Depo = "c@x" });

        Assert.Contains("Dirs__Reports", string.Join("|", errors));
        Assert.Contains("Mail__Brokers", string.Join("|", errors));
        Assert.Equal(2, errors.Count);
    }
}
