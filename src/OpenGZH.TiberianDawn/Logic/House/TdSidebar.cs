using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.TiberianDawn.Logic;

/// <summary>One sidebar entry: what can be built, and the factory building it (-1 if none).</summary>
public sealed class TdBuildable
{
    public RTTIType BuildableType = RTTIType.RTTI_NONE;
    public int BuildableID;
    public int Factory = -1;
    public bool BuildableViaCapture;
}

/// <summary>
/// SidebarGlyphxClass (SIDEBARGlyphx.CPP): one player's sidebar — the buildings column and the
/// units column, each a list of what the house can build. The remaster keeps one per player.
/// </summary>
public sealed class TdSidebar
{
    const int MAX_BUILDABLES = 75;

    public sealed class StripClass(TdSidebar parent)
    {
        public bool IsBuilding;
        public readonly List<TdBuildable> Buildables = new();

        public void Init_Clear()
        {
            IsBuilding = false;
            Buildables.Clear();
        }

        public bool Add(RTTIType type, int id, bool viaCapture)
        {
            if (Buildables.Count <= MAX_BUILDABLES)
            {
                foreach (var b in Buildables)
                    if (b.BuildableType == type && b.BuildableID == id)
                        return false;
                if (parent.game.ScenarioInit == 0 && type != RTTIType.RTTI_SPECIAL)
                    parent.game.Speak(VoxType.VOX_NEW_CONSTRUCT);
                Buildables.Add(new TdBuildable { BuildableType = type, BuildableID = id, Factory = -1, BuildableViaCapture = viaCapture });
                return true;
            }
            return false;
        }

        /// <summary>AI: finished units and infantry leave their factory (a PLACE event); a finished
        /// building waits to be placed.</summary>
        public void AI()
        {
            if (!IsBuilding)
                return;
            var game = parent.game;
            foreach (var b in Buildables)
            {
                if (b.Factory == -1)
                    continue;
                var factory = game.Factories.RawPtr(b.Factory);
                if (factory != null && (factory.Has_Changed() || factory.Is_Blocked()) && factory.Has_Completed())
                {
                    var pending = factory.Get_Object();
                    if (pending == null)
                        continue;
                    switch (pending.What_Am_I())
                    {
                        case RTTIType.RTTI_UNIT:
                        case RTTIType.RTTI_AIRCRAFT:
                        case RTTIType.RTTI_INFANTRY:
                            game.OutList.Add(EventClass.Place(game, pending.What_Am_I(), -1));
                            if (!factory.Is_Blocked())
                                game.Speak(VoxType.VOX_UNIT_READY);
                            break;
                        case RTTIType.RTTI_BUILDING:
                            if (!factory.Is_Blocked())
                                game.Speak(VoxType.VOX_CONSTRUCTION);
                            break;
                    }
                }
            }
        }

        /// <summary>Recalc: what can no longer be built (no factory left, special weapon gone) is removed.</summary>
        public void Recalc()
        {
            var game = parent.game;
            var player = parent.Player;
            for (int index = 0; index < Buildables.Count; index++)
            {
                var b = Buildables[index];
                bool ok;
                var tech = game.Fetch_Techno_Type(b.BuildableType, b.BuildableID);
                if (tech != null)
                {
                    ok = game.Who_Can_Build_Me(tech, true, false, player.Class.House) != null;
                }
                else
                {
                    ok = (SpecialWeaponType)b.BuildableID switch
                    {
                        SpecialWeaponType.SPC_ION_CANNON => player.IonCannon.Is_Present(),
                        SpecialWeaponType.SPC_NUCLEAR_BOMB => player.NukeStrike.Is_Present(),
                        SpecialWeaponType.SPC_AIR_STRIKE => player.AirStrike.Is_Present(),
                        _ => false,
                    };
                }
                if (!ok)
                {
                    Buildables.RemoveAt(index);
                    index--;
                }
            }
        }

        public bool Factory_Link(int factory, RTTIType type, int id)
        {
            foreach (var b in Buildables)
            {
                if (b.BuildableType == type && b.BuildableID == id)
                {
                    b.Factory = factory;
                    IsBuilding = true;
                    return true;
                }
            }
            return false;
        }

        public bool Abandon_Production(int factory)
        {
            bool noprod = true, abandon = false;
            foreach (var b in Buildables)
            {
                if (b.Factory == factory)
                {
                    parent.game.Factories.RawPtr(factory)?.Abandon();
                    b.Factory = -1;
                    abandon = true;
                }
                else if (b.Factory != -1)
                {
                    noprod = false;
                }
            }
            if (noprod)
                IsBuilding = false;
            return abandon;
        }
    }

    readonly TdGame game;
    public readonly HouseClass Player;
    public readonly StripClass[] Column;

    public TdSidebar(TdGame game, HouseClass player)
    {
        this.game = game;
        Player = player;
        Column = [new StripClass(this), new StripClass(this)];
    }

    public static int Which_Column(RTTIType type) => type is RTTIType.RTTI_BUILDINGTYPE or RTTIType.RTTI_BUILDING ? 0 : 1;

    public bool Add(RTTIType type, int id, bool viaCapture) => !game.DebugMap && Column[Which_Column(type)].Add(type, id, viaCapture);
    public bool Factory_Link(int factory, RTTIType type, int id) => Column[Which_Column(type)].Factory_Link(factory, type, id);
    public bool Abandon_Production(RTTIType type, int factory) => Column[Which_Column(type)].Abandon_Production(factory);

    public void AI()
    {
        if (game.DebugMap)
            return;
        Column[0].AI();
        Column[1].AI();
    }

    public void Recalc()
    {
        Column[0].Recalc();
        Column[1].Recalc();
    }

    public TdBuildable? Find(RTTIType type, int id) =>
        Column[Which_Column(type)].Buildables.FirstOrDefault(b => b.BuildableType == type && b.BuildableID == id);
}
