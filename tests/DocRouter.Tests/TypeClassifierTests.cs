using DocRouter.Core;

namespace DocRouter.Tests;

public class TypeClassifierTests
{
    private static RouteOptions Route() => new()
    {
        TypeRules =
        [
            new TypeRule { Type = "Contract", Prefix = "CNT_" },
            new TypeRule { Type = "Addendum", Mask = "ADD_*.pdf" }
        ],
        DefaultType = "Other"
    };

    [Theory]
    [InlineData("CNT_123.pdf", "Contract")]
    [InlineData("cnt_123.pdf", "Contract")]
    [InlineData("ADD_7.PDF", "Addendum")]
    [InlineData("ADD_7.xml", "Other")]
    [InlineData("random.pdf", "Other")]
    public void Resolves_by_prefix_or_mask_then_default(string file, string expected) =>
        Assert.Equal(expected, TypeClassifier.Resolve(Route(), file, null));

    [Fact]
    public void Explicit_type_from_view_wins() =>
        Assert.Equal("Act", TypeClassifier.Resolve(Route(), "CNT_1.pdf", "Act"));

    [Fact]
    public void No_rule_and_no_default_gives_empty() =>
        Assert.Equal("", TypeClassifier.Resolve(new RouteOptions(), "x.pdf", null));

    [Fact]
    public void Mask_question_mark_matches_single_char()
    {
        var r = new RouteOptions { TypeRules = [new TypeRule { Type = "T", Mask = "A?.pdf" }] };
        Assert.Equal("T", TypeClassifier.Resolve(r, "A1.pdf", null));
        Assert.Equal("", TypeClassifier.Resolve(r, "A12.pdf", null));
    }
}
