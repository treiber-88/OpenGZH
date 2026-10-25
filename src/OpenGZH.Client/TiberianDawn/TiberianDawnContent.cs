using OpenGZH.TiberianDawn;

namespace OpenGZH.Client;

/// <summary>Tiberian Dawn's files, loaded once when first needed (they stay open while the game runs).</summary>
public static class TiberianDawnContent
{
    static TdContent? content;
    static bool tried;

    /// <summary>Whether the original TD files are installed (content/tiberiandawn/conquer.mix).</summary>
    public static bool Available =>
        File.Exists(Path.Combine(GameContent.FindRoot(), "content", "tiberiandawn", "conquer.mix"));

    static OpenGZH.TiberianDawn.Art.TdArt? art;

    /// <summary>TD's art with the temperate palette (for TD factions in Zero Hour engine matches).</summary>
    public static OpenGZH.TiberianDawn.Art.TdArt? Art =>
        art ??= Get() is { } c ? new OpenGZH.TiberianDawn.Art.TdArt(c, OpenGZH.TiberianDawn.Logic.TheaterType.THEATER_TEMPERATE) : null;

    public static TdContent? Get()
    {
        if (!tried)
        {
            tried = true;
            if (Available)
                content = TdContent.Load(GameContent.FindRoot());
        }
        return content;
    }
}
