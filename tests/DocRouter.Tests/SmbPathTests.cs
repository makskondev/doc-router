using DocRouter.Infrastructure;

namespace DocRouter.Tests;

public class SmbPathTests
{
    [Fact]
    public void Parse_splits_host_share_relative()
    {
        var p = SmbPath.Parse(@"\\srv\share\a\b\f.pdf");
        Assert.Equal("srv", p.Host);
        Assert.Equal("share", p.Share);
        Assert.Equal(@"a\b\f.pdf", p.Relative);
    }

    [Fact]
    public void Share_root_has_empty_relative_and_combine_works()
    {
        var root = SmbPath.Parse(@"\\srv\share");
        Assert.Equal("", root.Relative);
        Assert.Equal(@"\\srv\share\x.pdf", root.Combine("x.pdf").Full);
    }

    [Fact]
    public void Non_unc_path_throws() => Assert.Throws<ArgumentException>(() => SmbPath.Parse("/tmp/x"));
}
