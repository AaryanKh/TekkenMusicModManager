using Tmm.Core.Analysis;
using Tmm.Core.Mods;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>The pure halves of the album-art lookup: tag parsing, query building, response parsing,
/// and matching cover pictures to Tekken tags. No network and no ffmpeg needed.</summary>
public class ArtLookupTests
{
    // ------------------------------------------------------------------ ffmetadata

    [Fact]
    public void FfmetadataKeysAreCaseInsensitiveAndTheFirstValueWins()
    {
        // FLAC shouts its keys, MP3 does not; the reader must not care.
        var text = ";FFMETADATA1\nTITLE=Seymour Battle\nalbum_artist=VA\nALBUM=Final Fantasy X Original Soundtrack\nalbum=ignored duplicate\n";
        var t = SongTagReader.Parse(text);
        Assert.Equal("Seymour Battle", t.Title);
        Assert.Equal("VA", t.Artist);
        Assert.Equal("Final Fantasy X Original Soundtrack", t.Album);
    }

    [Fact]
    public void FfmetadataEscapesAreUndone()
    {
        var t = SongTagReader.Parse("title=Fists \\= Fury\nartist=Namco \\; Bandai\n");
        Assert.Equal("Fists = Fury", t.Title);
        Assert.Equal("Namco ; Bandai", t.Artist);
    }

    [Fact]
    public void FfmetadataArtistFallsBackThroughTheUsualKeys()
    {
        Assert.Equal("Uematsu", SongTagReader.Parse("performer=Uematsu\n").Artist);
        Assert.Equal("Uematsu", SongTagReader.Parse("albumartist=Uematsu\n").Artist);
        Assert.True(SongTagReader.Parse(";FFMETADATA1\n").IsEmpty);
    }

    // ------------------------------------------------------------------ query

    [Fact]
    public void AlbumBeatsTitleAndAGenericArtistIsDropped()
    {
        var q = AlbumArtSearch.BuildQuery(new SongTags("Seymour Battle", "VA", "Final Fantasy X Original Soundtrack"), null);
        Assert.Equal("Final Fantasy X Original Soundtrack", q);
    }

    [Fact]
    public void ARealArtistIsKept()
    {
        var q = AlbumArtSearch.BuildQuery(new SongTags("Track", "Nobuo Uematsu", "FINAL FANTASY X"), null);
        Assert.Equal("FINAL FANTASY X Nobuo Uematsu", q);
    }

    [Fact]
    public void WithoutTagsTheFilenameIsUsedMinusItsTrackNumber()
    {
        var q = AlbumArtSearch.BuildQuery(SongTags.Empty, @"C:\music\4-15 - Seymour_Battle.flac");
        Assert.Equal("Seymour Battle", q);
    }

    // ------------------------------------------------------------------ iTunes response

    [Fact]
    public void ItunesResultsBecomeCandidatesWithALargerPicture()
    {
        const string json = """
            {"resultCount":2,"results":[
              {"collectionName":"FINAL FANTASY X (Original Soundtrack)","artistName":"Nobuo Uematsu","artworkUrl100":"https://x/y/100x100bb.jpg"},
              {"collectionName":"No art here","artistName":"Nobody"}
            ]}
            """;
        var list = AlbumArtSearch.ParseItunes(json);
        var c = Assert.Single(list);                       // the entry without a picture is dropped
        Assert.Equal("FINAL FANTASY X (Original Soundtrack)", c.Title);
        Assert.Equal("Nobuo Uematsu", c.Artist);
        Assert.Equal("https://x/y/100x100bb.jpg", c.ThumbUrl);
        Assert.Equal("https://x/y/600x600bb.jpg", c.FullUrl);
        Assert.Equal("iTunes", c.Source);
    }

    [Fact]
    public void AnEmptyOrOddResponseIsJustNoCandidates()
    {
        Assert.Empty(AlbumArtSearch.ParseItunes("""{"resultCount":0,"results":[]}"""));
        Assert.Empty(AlbumArtSearch.ParseItunes("""{"something":"else"}"""));
    }

    // ------------------------------------------------------------------ fallback ladder

    [Theory]
    [InlineData("YuGiOhDuelistsOfTheRoses", "Yu Gi Oh Duelists Of The Roses")]
    [InlineData("VsLancastrians", "Vs Lancastrians")]
    [InlineData("DBZBudokai3", "DBZ Budokai 3")]
    [InlineData("SeymourBattle", "Seymour Battle")]
    [InlineData("ArrangedByAJURIKA", "Arranged By AJURIKA")]
    [InlineData("Persona3", "Persona 3")]
    public void RunTogetherWordsAreSplitBackApart(string input, string expected)
        => Assert.Equal(expected, AlbumArtSearch.SplitWords(input));

    [Fact]
    public void TheModsOwnTagIsStrippedBeforeSearching()
    {
        // "[TTT]" is ours, not the album's; leaving it in would poison every query.
        var parts = AlbumArtSearch.ModNameParts("[TTT]_YuGiOhDuelistsOfTheRoses_VsLancastrians");
        Assert.Equal(new[] { "YuGiOhDuelistsOfTheRoses", "VsLancastrians" }, parts);
    }

    [Fact]
    public void TheLadderFallsBackToTheModNameWhenTagsAreMissing()
    {
        var ladder = AlbumArtSearch.QueryLadder(SongTags.Empty, "C:/dl/The_Duelists_of_the_Roses.wav",
                                                "[TTT]_YuGiOhDuelistsOfTheRoses_VsLancastrians");
        Assert.Contains("Yu Gi Oh Duelists Of The Roses Vs Lancastrians", ladder);
        Assert.Contains("Yu Gi Oh Duelists Of The Roses", ladder);   // narrower retry
        Assert.DoesNotContain(ladder, q => q.Contains("[TTT]"));
    }

    [Fact]
    public void TaggedFilesStillLeadWithTheirAlbum()
    {
        var ladder = AlbumArtSearch.QueryLadder(new SongTags("Seymour Battle", "VA", "Final Fantasy X Original Soundtrack"),
                                                "C:/x/4-15 - Seymour Battle.flac", "[T7]_SeymourBattle");
        Assert.Equal("Final Fantasy X Original Soundtrack", ladder[0]);
        Assert.Contains("Seymour Battle", ladder);                   // the fallback is still there
    }

    [Fact]
    public void TheLadderHasNoDuplicatesOrStubs()
    {
        var ladder = AlbumArtSearch.QueryLadder(new SongTags("A", "B", "Album"), "C:/x/Album.flac", "[T7]_Album");
        Assert.Equal(ladder.Count, ladder.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ladder, q => Assert.True(q.Length >= 3));
    }

    [Fact]
    public void ANamelessUntaggedSongProducesNoQueriesRatherThanJunk()
        => Assert.Empty(AlbumArtSearch.QueryLadder(SongTags.Empty, null, null));

    [Fact]
    public void ResultsClosestToTheQueryScoreHighest()
    {
        const string q = "Yu Gi Oh Duelists Of The Roses";
        var good = AlbumArtSearch.Similarity(q, "Yu-Gi-Oh! The Duelists of the Roses (Original Soundtrack)");
        var poor = AlbumArtSearch.Similarity(q, "Greatest Hits of the 90s");
        Assert.True(good > poor, $"{good} should beat {poor}");
        Assert.InRange(good, 0.8, 1.0);
    }

    [Fact]
    public void RankingPutsTheClosestRecordFirst()
    {
        // The real failure this guards: "Sonic Heroes Vs Team Battle" returns records, none of them
        // the right one, so "it returned something" cannot be the stopping rule.
        var results = new[]
        {
            new ArtCandidate("Pokemon X : Ten Years Of Pokemon", "Various", "t1", "f1", "iTunes"),
            new ArtCandidate("Sonic Heroes Original Soundtrack", "Jun Senoue", "t2", "f2", "iTunes"),
        };
        var ranked = AlbumArtSearch.Rank("Sonic Heroes", results);
        Assert.Equal("Sonic Heroes Original Soundtrack", ranked[0].Title);
        Assert.True(AlbumArtSearch.Similarity("Sonic Heroes Vs Team Battle", results[0].Title + " " + results[0].Artist)
                    < AlbumArtSearch.GoodEnough, "an unrelated record must not clear the bar");
        Assert.True(AlbumArtSearch.Similarity("Sonic Heroes", results[1].Title + " " + results[1].Artist)
                    >= AlbumArtSearch.GoodEnough, "the right record must clear it");
    }

    // ------------------------------------------------------------------ Tekken game covers

    [Theory]
    [InlineData("TEKKEN 7", "TEKKEN 7 Original Soundtrack")]
    [InlineData("TEKKEN TAG 2", "TEKKEN TAG 2 Original Soundtrack")]
    [InlineData("TEKKEN REVOLUTION", "TEKKEN REVOLUTION Original Soundtrack")]
    public void AGameLooksUpItsSoundtrackAlbum(string label, string expected)
        => Assert.Equal(expected, AlbumArtSearch.GameSoundtrackQuery(label));

    [Fact]
    public void AnEmptyGameLabelSearchesForNothing()
    {
        Assert.Equal("", AlbumArtSearch.GameSoundtrackQuery(""));
        Assert.Equal("", AlbumArtSearch.GameSoundtrackQuery("   "));
    }

    [Fact]
    public void EveryGameInTheListHasAUniqueTagAndASearchableName()
    {
        // The settings list is built from this, and each row writes to covers\<tag>: a duplicate tag
        // would make two games share one picture.
        var games = NameSuggester.Games;
        Assert.Equal(11, games.Count);
        Assert.Equal(games.Count, games.Select(g => g.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var (label, tag) in games)
        {
            Assert.Equal(tag, NameSuggester.TagFor(label));            // list and mapper agree
            Assert.NotEqual("", AlbumArtSearch.GameSoundtrackQuery(label));
        }
    }
}
