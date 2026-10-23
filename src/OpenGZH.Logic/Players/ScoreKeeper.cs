using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Players;

/// <summary>
/// One player's game statistics for the score screen (the original's ScoreKeeper): money earned and
/// spent, units and buildings built, lost and destroyed (per victim player), and what was captured.
/// Only objects marked SCORE / SCORE_CREATE / SCORE_DESTROY count, and nothing while scoring is off.
/// </summary>
public sealed class ScoreKeeper
{
    readonly GameLogic game;
    readonly int myPlayerIndex;

    public int TotalMoneyEarned { get; private set; }
    public int TotalMoneySpent { get; private set; }
    public int TotalUnitsBuilt { get; private set; }
    public int TotalUnitsLost { get; private set; }
    public int TotalBuildingsBuilt { get; private set; }
    public int TotalBuildingsLost { get; private set; }
    public int TotalFactionBuildingsCaptured { get; private set; }
    public int TotalTechBuildingsCaptured { get; private set; }

    readonly Dictionary<int, int> unitsDestroyed = new(), buildingsDestroyed = new();
    readonly Dictionary<ObjectTemplate, int> objectsBuilt = new(), objectsLost = new(), objectsCaptured = new();
    readonly Dictionary<int, Dictionary<ObjectTemplate, int>> objectsDestroyed = new();

    public ScoreKeeper(GameLogic game, int playerIndex)
    {
        this.game = game;
        myPlayerIndex = playerIndex;
    }

    public int TotalUnitsDestroyed => unitsDestroyed.Values.Sum();
    public int TotalBuildingsDestroyed => buildingsDestroyed.Values.Sum();

    public IReadOnlyDictionary<ObjectTemplate, int> ObjectsBuilt => objectsBuilt;
    public IReadOnlyDictionary<ObjectTemplate, int> ObjectsLost => objectsLost;
    public IReadOnlyDictionary<ObjectTemplate, int> ObjectsCaptured => objectsCaptured;

    public int UnitsDestroyedOf(int playerIndex) => unitsDestroyed.GetValueOrDefault(playerIndex);
    public int BuildingsDestroyedOf(int playerIndex) => buildingsDestroyed.GetValueOrDefault(playerIndex);

    public IReadOnlyDictionary<ObjectTemplate, int> ObjectsDestroyedOf(int playerIndex) =>
        objectsDestroyed.TryGetValue(playerIndex, out var map) ? map : new Dictionary<ObjectTemplate, int>();

    public void AddMoneyEarned(int amount)
    {
        if (game.IsScoringEnabled)
            TotalMoneyEarned += amount;
    }

    public void AddMoneySpent(int amount)
    {
        if (game.IsScoringEnabled)
            TotalMoneySpent += amount;
    }

    static bool IsScoringBuilding(GameObject o) => o.IsKindOf(Kind.Structure) && o.IsKindOf("SCORE");
    static bool IsScoringBuildingCreate(GameObject o) => o.IsKindOf(Kind.Structure) && o.IsKindOf("SCORE_CREATE");
    static bool IsScoringBuildingDestroy(GameObject o) => o.IsKindOf(Kind.Structure) && o.IsKindOf("SCORE_DESTROY");
    static bool IsUnit(GameObject o) => o.IsKindOf(Kind.Infantry) || o.IsKindOf(Kind.Vehicle);

    static void Bump(Dictionary<ObjectTemplate, int> map, ObjectTemplate template, int by) =>
        map[template] = map.GetValueOrDefault(template) + by;

    public void AddObjectBuilt(GameObject o) => ChangeObjectBuilt(o, +1);

    /// <summary>A building that was started but never finished (an AI cancelling it) no longer counts.</summary>
    public void RemoveObjectBuilt(GameObject o) => ChangeObjectBuilt(o, -1);

    void ChangeObjectBuilt(GameObject o, int by)
    {
        if (!game.IsScoringEnabled)
            return;
        bool count = false;
        if (IsScoringBuilding(o) || IsScoringBuildingCreate(o))
        {
            TotalBuildingsBuilt += by;
            count = true;
        }
        else if (IsUnit(o) && (o.IsKindOf("SCORE") || o.IsKindOf("SCORE_CREATE")))
        {
            TotalUnitsBuilt += by;
            count = true;
        }
        if (count)
            Bump(objectsBuilt, o.Template, by);
    }

    public void AddObjectCaptured(GameObject o)
    {
        if (!game.IsScoringEnabled || !o.IsKindOf(Kind.Structure))
            return;
        if (o.IsKindOf("SCORE"))
            TotalFactionBuildingsCaptured++;
        else
            TotalTechBuildingsCaptured++;
        Bump(objectsCaptured, o.Template, 1);
    }

    public void AddObjectDestroyed(GameObject o)
    {
        if (!game.IsScoringEnabled || o.IsUnderConstruction)
            return;
        int victim = o.Owner.Index;
        if (IsScoringBuilding(o) || IsScoringBuildingDestroy(o))
            buildingsDestroyed[victim] = buildingsDestroyed.GetValueOrDefault(victim) + 1;
        else if (IsUnit(o) && (o.IsKindOf("SCORE") || o.IsKindOf("SCORE_DESTROY")))
            unitsDestroyed[victim] = unitsDestroyed.GetValueOrDefault(victim) + 1;
        else
            return;
        if (!objectsDestroyed.TryGetValue(victim, out var map))
            objectsDestroyed[victim] = map = new();
        Bump(map, o.Template, 1);
    }

    public void AddObjectLost(GameObject o)
    {
        if (!game.IsScoringEnabled || o.IsUnderConstruction)
            return;
        if (IsScoringBuilding(o) || IsScoringBuildingDestroy(o))
            TotalBuildingsLost++;
        else if (IsUnit(o) && (o.IsKindOf("SCORE") || o.IsKindOf("SCORE_DESTROY")))
            TotalUnitsLost++;
        else
            return;
        Bump(objectsLost, o.Template, 1);
    }

    /// <summary>The score used to rank players: 100 per unit and building built or destroyed, plus money earned.</summary>
    public int CalculateScore()
    {
        int score = TotalUnitsBuilt * 100 + TotalMoneyEarned + TotalBuildingsBuilt * 100;
        foreach (var (player, count) in unitsDestroyed)
            if (player != myPlayerIndex)
                score += count * 100;
        foreach (var (player, count) in buildingsDestroyed)
            if (player != myPlayerIndex)
                score += count * 100;
        return score;
    }
}
