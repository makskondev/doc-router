using DocRouter.Core;
using DocRouter.Engine;

namespace DocRouter.Tests;

public class ConfigCheckTests
{
    private static DirectoriesOptions Dirs() => new()
    {
        BrokRep = @"\\fd\brok_rep", BrokRepEmail = @"\\fd\brok_rep\email", SignedDocs = @"\\fd\signed_docs",
        DepoReports = @"\\fd\depo_reports", WasSignedDepo = @"\\fd\w\depo", WasSignedBrok = @"\\fd\w\brok"
    };

    private static MailOptions Mail() => new() { Default = "a@x", Brokers = "b@x", Depo = "c@x" };
    private static PostgresOptions Pg() => new() { ConnectionString = "Host=pg;Database=d" };

    [Fact]
    public void Complete_settings_have_no_errors() =>
        Assert.Empty(ConfigCheck.MissingSettings(Dirs(), Mail(), Pg()));

    [Fact]
    public void Missing_values_are_named_with_environment_variable_names()
    {
        var dirs = Dirs();
        dirs.BrokRepEmail = "";
        var mail = Mail();
        mail.Brokers = " ";
        var errors = ConfigCheck.MissingSettings(dirs, mail, new PostgresOptions());

        var all = string.Join("|", errors);
        Assert.Contains("Dirs__BrokRepEmail", all);
        Assert.Contains("Mail__Brokers", all);
        Assert.Contains("Postgres__ConnectionString", all);
        Assert.Equal(3, errors.Count);
    }
}
