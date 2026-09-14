using Tmm.Core;
using Tmm.Core.Mods;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>The suggested name follows the scheme "[T7]_FinalFantasyX_SeymourBattle".</summary>
public class NameSuggesterTests
{
    private const int Rate = 48000;

    private static Slot SlotIn(string game, string title = "Some Stage") =>
        new(new SlotIdentity(1, title, game, null, 4242, null, 30),
            null, new WemInfo(4242, 30 * Rate, Rate, 2, 0xFFFF, 0));

    [Theory]
    [InlineData("TEKKEN", "T1")]
    [InlineData("TEKKEN 2", "T2")]
    [InlineData("TEKKEN 3", "T3")]
    [InlineData("TEKKEN 4", "T4")]
    [InlineData("TEKKEN 5", "T5")]
    [InlineData("TEKKEN 6", "T6")]
    [InlineData("TEKKEN 7", "T7")]
    [InlineData("TEKKEN 8", "T8")]
    [InlineData("TEKKEN TAG", "TTT")]
    [InlineData("TEKKEN TAG 2", "TTT2")]
    [InlineData("TEKKEN REVOLUTION", "TREV")]
    public void EverySheetGameHasATag(string game, string tag) => Assert.Equal(tag, NameSuggester.TagFor(game));

    [Fact]
    public void EveryGameInTheShippedSheetIsCovered()
    {
        // A tag falling through to the generic path would quietly produce odd names, so the sheet's
        // own labels are checked rather than assumed.
        var sheetGames = new[]
        {
            "TEKKEN", "TEKKEN 2", "TEKKEN 3", "TEKKEN 4", "TEKKEN 5",
            "TEKKEN 6", "TEKKEN 7", "TEKKEN 8", "TEKKEN TAG", "TEKKEN TAG 2", "TEKKEN REVOLUTION",
        };
        foreach (var g in sheetGames)
            Assert.Matches("^T", NameSuggester.TagFor(g));
    }

    [Fact]
    public void ALeadingTrackNumberIsDropped()
    {
        var name = NameSuggester.Suggest(SlotIn("TEKKEN 7"), @"C:\Users\x\Desktop\4-15 - Seymour Battle.flac");
        Assert.Equal("[T7]_SeymourBattle", name);
    }

    [Theory]
    [InlineData("19. Battle Hymn of the Soul.mp3", "[TTT]_BattleHymnOfTheSoul")]
    [InlineData("03) Inside Buu.wav", "[TTT]_InsideBuu")]
    [InlineData("07 Vs Team Battle.ogg", "[TTT]_VsTeamBattle")]
    [InlineData("Deep Space Climax.flac", "[TTT]_DeepSpaceClimax")]
    public void TrackNamesBecomePascalCase(string file, string expected)
    {
        var name = NameSuggester.Suggest(SlotIn("TEKKEN TAG"), @"C:\Users\x\Downloads\" + file);
        Assert.Equal(expected, name);
    }

    [Fact]
    public void AMeaningfulFolderBecomesTheMiddlePart()
    {
        var name = NameSuggester.Suggest(SlotIn("TEKKEN 7"), @"C:\Music\Final Fantasy X\4-15 - Seymour Battle.flac");
        Assert.Equal("[T7]_FinalFantasyX_SeymourBattle", name);
    }

    [Theory]
    [InlineData(@"C:\Users\x\Desktop\Seymour Battle.flac")]
    [InlineData(@"C:\Users\x\Downloads\Seymour Battle.flac")]
    [InlineData(@"C:\Users\x\Music\Seymour Battle.flac")]
    [InlineData(@"C:\Users\x\OneDrive\Seymour Battle.flac")]
    [InlineData(@"C:\Soundtracks\Seymour Battle.flac")]
    public void AGenericFolderIsLeftOutRatherThanGuessedAt(string path)
    {
        // Better a shorter name than "[T7]_Downloads_SeymourBattle".
        Assert.Equal("[T7]_SeymourBattle", NameSuggester.Suggest(SlotIn("TEKKEN 7"), path));
    }

    [Fact]
    public void ShoutingWordsKeepTheirCase()
    {
        var name = NameSuggester.Suggest(SlotIn("TEKKEN 3"), @"C:\Users\x\Desktop\City Ruins Arranged by AJURIKA.mp3");
        Assert.Equal("[T3]_CityRuinsArrangedByAJURIKA", name);
    }

    [Fact]
    public void NonAsciiIsDroppedBecauseThePackerDropsItAnyway()
    {
        var name = NameSuggester.Suggest(SlotIn("TEKKEN 3"), "C:\\Users\\x\\Downloads\\遺サレタ場所 Arranged by AJURIKA.mp3");
        Assert.Equal("[T3]_ArrangedByAJURIKA", name);
    }

    [Fact]
    public void AnUnreadableFilenameFallsBackToTheSlotTitle()
    {
        var name = NameSuggester.Suggest(SlotIn("TEKKEN 5", "Massive Stunner"), "C:\\Users\\x\\Downloads\\遺サレタ場所.mp3");
        Assert.Equal("[T5]_MassiveStunner", name);
    }

    [Fact]
    public void NoSongStillProducesAUsableName()
    {
        var name = NameSuggester.Suggest(SlotIn("TEKKEN 8", "Deep Space Climax"), null);
        Assert.Equal("[T8]_DeepSpaceClimax", name);
    }

    [Fact]
    public void AnExistingNameIsNotReused()
    {
        var slot = SlotIn("TEKKEN 7");
        var taken = new[] { "[T7]_SeymourBattle", "[T7]_SeymourBattle_2" };
        var name = NameSuggester.Suggest(slot, @"C:\Users\x\Desktop\Seymour Battle.flac", taken);
        Assert.Equal("[T7]_SeymourBattle_3", name);
    }

    [Fact]
    public void TheSuggestionSurvivesSanitisingUnchangedApartFromBrackets()
    {
        // Whatever is suggested has to still be a legal pak name once sanitised, or the mod would be
        // built under something the user never saw.
        var name = NameSuggester.Suggest(SlotIn("TEKKEN 7"), @"C:\Music\Final Fantasy X\4-15 - Seymour Battle.flac");
        var safe = Core.Pak.PakLayout.SanitizeModName(name);
        Assert.Equal("T7_FinalFantasyX_SeymourBattle", safe);
    }

    [Fact]
    public void LongNamesAreTrimmed()
    {
        var silly = new string('a', 200) + " battle";
        var name = NameSuggester.Suggest(SlotIn("TEKKEN 7"), $@"C:\Users\x\Desktop\{silly}.flac");
        Assert.True(name.Length <= 70, $"name was {name.Length} chars");
    }
}
