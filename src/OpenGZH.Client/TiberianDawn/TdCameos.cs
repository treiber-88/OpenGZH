using OpenGZH.Formats.Images;

namespace OpenGZH.Client.TiberianDawn;

/// <summary>Tiberian Dawn's sidebar pictures (NAMEICNH), as images for buttons named "TDCAMEO:NAME".</summary>
public static class TdCameos
{
    public const string Prefix = "TDCAMEO:";

    /// <summary>The cameo of a TD object (its INI name; infantry use their first four letters), or null.</summary>
    public static Image? Image(string iniName) =>
        TiberianDawnContent.Art is { } art && art.Cameo(iniName) is { } shape ? art.Frame(shape, 0, null) : null;
}
