using DocRouter.Core;
using DocRouter.Engine;

namespace DocRouter.Tests;

public class DirectorySelectorTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    private static FileEntry F(string name, int ageMin) => new(name, @"\\s\d\" + name, 1, Now.AddMinutes(-ageMin));
    private static DirectorySourceDef Def(string? mask = null, bool sig = false, int minAgeMin = 1) =>
        new(@"\\s\d", mask, sig, ".sig", TimeSpan.FromMinutes(minAgeMin), null);

    [Fact]
    public void Without_signature_takes_old_files_and_skips_sig_and_young()
    {
        var res = DirectoryDocumentSelector.Select([F("a.pdf", 5), F("b.pdf", 0), F("a.pdf.sig", 5)], Def(), Now);
        Assert.Equal(new[] { "a.pdf" }, res.Select(x => x.Name));
    }

    [Fact]
    public void With_signature_requires_pair_and_both_stable()
    {
        var res = DirectoryDocumentSelector.Select(
            [F("ok.pdf", 5), F("ok.pdf.sig", 5), F("nosig.pdf", 5), F("young.pdf", 5), F("young.pdf.sig", 0)], Def(sig: true), Now);
        Assert.Equal(new[] { "ok.pdf" }, res.Select(x => x.Name));
    }

    [Fact]
    public void Mask_picks_only_files_of_this_type_in_shared_folder_case_insensitive()
    {
        var res = DirectoryDocumentSelector.Select(
            [F("DEPO_AGR_1.pdf", 5), F("depo_agr_2.PDF", 5), F("BROK_AGR_3.pdf", 5)], Def(mask: "DEPO_AGR_*"), Now);
        Assert.Equal(new[] { "DEPO_AGR_1.pdf", "depo_agr_2.PDF" }, res.Select(x => x.Name));
    }

    [Fact]
    public void Mask_applies_to_document_name_not_to_signature()
    {
        var res = DirectoryDocumentSelector.Select(
            [F("CNT_1.pdf", 5), F("CNT_1.pdf.sig", 5), F("INV_2.pdf", 5), F("INV_2.pdf.sig", 5)], Def(mask: "CNT_*", sig: true), Now);
        Assert.Equal(new[] { "CNT_1.pdf" }, res.Select(x => x.Name));
    }
}
