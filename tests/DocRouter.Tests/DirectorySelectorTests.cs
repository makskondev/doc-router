using DocRouter.Core;
using DocRouter.Engine;

namespace DocRouter.Tests;

public class DirectorySelectorTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    private static FileEntry F(string name, int ageMin) => new(name, @"\\s\d\" + name, 1, Now.AddMinutes(-ageMin));

    [Fact]
    public void Without_signature_takes_old_files_and_skips_sig_and_young()
    {
        var o = new DirectorySourceOptions { MinAge = TimeSpan.FromMinutes(1) };
        var res = DirectoryDocumentSelector.Select([F("a.pdf", 5), F("b.pdf", 0), F("a.pdf.sig", 5)], o, Now);
        Assert.Equal(new[] { "a.pdf" }, res.Select(x => x.Name));
    }

    [Fact]
    public void With_signature_requires_pair_and_both_stable()
    {
        var o = new DirectorySourceOptions { RequireSignature = true, MinAge = TimeSpan.FromMinutes(1) };
        var res = DirectoryDocumentSelector.Select(
            [F("ok.pdf", 5), F("ok.pdf.sig", 5), F("nosig.pdf", 5), F("young.pdf", 5), F("young.pdf.sig", 0)], o, Now);
        Assert.Equal(new[] { "ok.pdf" }, res.Select(x => x.Name));
    }

    [Fact]
    public void Extensions_filter_is_case_insensitive()
    {
        var o = new DirectorySourceOptions { Extensions = [".pdf"], MinAge = TimeSpan.Zero };
        var res = DirectoryDocumentSelector.Select([F("a.PDF", 5), F("b.txt", 5)], o, Now);
        Assert.Equal(new[] { "a.PDF" }, res.Select(x => x.Name));
    }
}
