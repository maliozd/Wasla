using System.Globalization;
using System.Xml.Linq;
using Wasla.Web.Ui;

namespace Wasla.UnitTests.Web;

public sealed class UserInitialsTests
{
    [Fact]
    public void BlankNameAndEmail_ReturnsQuestionMark()
    {
        Assert.Equal("?", UserInitials.FromDisplayName(null, null));
        Assert.Equal("?", UserInitials.FromDisplayName("   ", "   "));
    }

    [Fact]
    public void OneWordName_UsesFirstGrapheme()
    {
        Assert.Equal("A", UserInitials.FromDisplayName("Ada", culture: CultureInfo.InvariantCulture));
    }

    [Fact]
    public void MultiWordName_UsesFirstAndLast()
    {
        Assert.Equal("MA", UserInitials.FromDisplayName("Mehmet Ali", culture: new CultureInfo("tr-TR")));
        Assert.Equal("MÖ", UserInitials.FromDisplayName("Mehmet Ali Özdemir", culture: new CultureInfo("tr-TR")));
    }

    [Fact]
    public void TurkishDottedI_UppercasesWithTurkishCulture()
    {
        Assert.Equal("İ", UserInitials.FromDisplayName("ipek", culture: new CultureInfo("tr-TR")));
    }

    [Fact]
    public void ArabicName_UsesFirstAndLastWords()
    {
        Assert.Equal("عم", UserInitials.FromDisplayName("علي محمد", culture: new CultureInfo("ar-SA")));
    }

    [Fact]
    public void RussianName_UsesFirstAndLastWords()
    {
        Assert.Equal("ИП", UserInitials.FromDisplayName("Иван Петров", culture: new CultureInfo("ru-RU")));
    }

    [Fact]
    public void MissingName_FallsBackToEmailLocalPart()
    {
        Assert.Equal("JD", UserInitials.FromDisplayName("  ", "jane.doe@example.test", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ToneIndex_IsDeterministicForTheSameId()
    {
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Assert.Equal(UserInitials.ToneIndex(id), UserInitials.ToneIndex(id));
        Assert.InRange(UserInitials.ToneIndex(id), 0, UserInitials.ToneCount - 1);
    }
}
