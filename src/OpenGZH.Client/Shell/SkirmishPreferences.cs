using OpenGZH.Core.Rules;
using OpenGZH.Logic.Multiplayer;

namespace OpenGZH.Client.Shell;

/// <summary>
/// The skirmish screen's remembered choices (the original's SkirmishPreferences, Skirmish.ini in the
/// user's data folder): name, colour, faction, map, starting cash, superweapon limit, speed and the slot list.
/// </summary>
public sealed class SkirmishPreferences
{
    readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    readonly RuleSet rules;
    readonly MapCache maps;

    public static string FilePath => Path.Combine(UserDataFolder, "Skirmish.ini");

    /// <summary>Where OpenGZH keeps preferences and saves (like the original's "...Zero Hour Data" folder).</summary>
    public static string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenGZH");

    SkirmishPreferences(RuleSet rules, MapCache maps)
    {
        this.rules = rules;
        this.maps = maps;
    }

    public static SkirmishPreferences Load(RuleSet rules, MapCache maps)
    {
        var prefs = new SkirmishPreferences(rules, maps);
        try
        {
            if (File.Exists(FilePath))
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        prefs.values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }
        }
        catch (IOException) { }
        return prefs;
    }

    public string UserName => values.TryGetValue("UserName", out var name) && name.Length > 0 ? name : Environment.MachineName;

    public int PreferredColor =>
        values.TryGetValue("Color", out var v) && int.TryParse(v, out int c) && c >= -1 && c < GameInfo.Colors(rules).Count ? c : -1;

    public int PreferredFaction
    {
        get
        {
            if (!values.TryGetValue("PlayerTemplate", out var v) || !int.TryParse(v, out int t))
                return GameSlot.PlayerTemplateRandom;
            var templates = GameInfo.PlayerTemplates(rules);
            if (t < 0 || t >= templates.Count || !GameInfo.IsLobbyFaction(templates[t].Rules))
                return GameSlot.PlayerTemplateRandom;
            return t;
        }
    }

    /// <summary>The saved map, or the first skirmish map (the original's getDefaultMap).</summary>
    public string PreferredMap
    {
        get
        {
            if (values.TryGetValue("Map", out var map) && map.Length > 0 && maps.Find(map) is { IsMultiplayer: true })
                return map;
            return maps.Maps.Where(m => m.IsMultiplayer).OrderBy(m => m.FileName, StringComparer.Ordinal).FirstOrDefault()?.FileName ?? "";
        }
    }

    public bool SuperweaponRestricted => values.TryGetValue("SuperweaponRestrict", out var v) && v.Equals("yes", StringComparison.OrdinalIgnoreCase);

    public int StartingCash =>
        values.TryGetValue("StartingCash", out var v) && int.TryParse(v, out int cash) ? cash : DefaultStartingMoney(rules);

    public int Fps => values.TryGetValue("FPS", out var v) && int.TryParse(v, out int fps) ? fps : 30;

    /// <summary>The MultiplayerStartingMoneyChoice values, in order.</summary>
    public static List<int> StartingMoneyChoices(RuleSet rules)
    {
        var node = rules.Section("MultiplayerStartingMoneyChoice");
        var list = node.IsSequence ? node.Items : node.IsMapping && node.Entries.Any() ? [node] : [];
        var result = list.Select(n => RuleValue.Int(n.GetString("Value"))).Where(v => v > 0).ToList();
        return result.Count > 0 ? result : [10000];
    }

    public static int DefaultStartingMoney(RuleSet rules)
    {
        var node = rules.Section("MultiplayerStartingMoneyChoice");
        var list = node.IsSequence ? node.Items : [node];
        var def = list.FirstOrDefault(n => RuleValue.Bool(n.GetString("Default")));
        return def != null ? RuleValue.Int(def.GetString("Value"), 10000) : 10000;
    }

    /// <summary>
    /// The saved AI slots (the original's SlotList, one entry per slot after the human's):
    /// "state,color,template,startPos,team" separated by ';'.
    /// </summary>
    public void ApplySlotList(GameInfo info)
    {
        if (!values.TryGetValue("SlotList", out var list) || list.Length == 0)
            return;
        var entries = list.Split(';');
        for (int i = 1; i < GameInfo.MaxSlots && i < entries.Length; i++)
        {
            var parts = entries[i].Split(',');
            if (parts.Length < 5 || !Enum.TryParse<SlotState>(parts[0], out var state) || state == SlotState.Player)
                continue;
            var slot = info.Slots[i];
            slot.SetState(state);
            if (!slot.IsOccupied)
                continue;
            if (int.TryParse(parts[1], out int color)) slot.Color = color;
            if (int.TryParse(parts[2], out int template)) slot.PlayerTemplate = template;
            if (int.TryParse(parts[3], out int start)) slot.StartPos = start;
            if (int.TryParse(parts[4], out int team)) slot.TeamNumber = team;
        }
    }

    public static void Write(GameInfo info, int fps)
    {
        try
        {
            Directory.CreateDirectory(UserDataFolder);
            var slot0 = info.Slots[0];
            var slots = string.Join(';', info.Slots.Select(s => $"{s.State},{s.Color},{s.PlayerTemplate},{s.StartPos},{s.TeamNumber}"));
            File.WriteAllLines(FilePath,
            [
                $"Color = {slot0.Color}",
                $"PlayerTemplate = {slot0.PlayerTemplate}",
                $"Map = {info.Map}",
                $"UserName = {slot0.Name}",
                $"StartingCash = {info.StartingCash}",
                $"SuperweaponRestrict = {(info.SuperweaponRestriction ? "Yes" : "No")}",
                $"SlotList = {slots}",
                $"FPS = {fps}",
            ]);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
