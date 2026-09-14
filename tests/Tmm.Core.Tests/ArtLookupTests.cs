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

    // ------------------------------------------------------------------ Tekken cover import

    [Theory]
    [InlineData("T_UI_Jukebox_Tekken7.png", "T7")]
    [InlineData("tk7.png", "T7")]
    [InlineData("TEKKEN 8.jpg", "T8")]
    [InlineData("tekken-3.png", "T3")]
    [InlineData("Tekken Tag Tournament.png", "TTT")]
    [InlineData("TTT.png", "TTT")]
    [InlineData("Tekken Tag Tournament 2.png", "TTT2")]
    [InlineData("ttt2.png", "TTT2")]
    [InlineData("tekken_tag_2.png", "TTT2")]
    [InlineData("Tekken Revolution.png", "TREV")]
    [InlineData("TREV.png", "TREV")]
    [InlineData("tekken.png", "T1")]
    public void CoverFilenamesMapToTags(string file, string tag) => Assert.Equal(tag, CoverImport.TagFor(file));

    [Theory]
    [InlineData("Street Fighter 6.png")]
    [InlineData("cover.jpg")]
    [InlineData("t9.png")]
    public void UnrelatedFilenamesMapToNothing(string file) => Assert.Null(CoverImport.TagFor(file));

    [Fact]
    public void TagTwoIsNotMistakenForTag()
    {
        // The ordering of the rules is what makes this work; pin it.
        Assert.Equal("TTT2", CoverImport.TagFor("TekkenTag2.png"));
        Assert.Equal("TTT", CoverImport.TagFor("TekkenTag.png"));
    }
}
