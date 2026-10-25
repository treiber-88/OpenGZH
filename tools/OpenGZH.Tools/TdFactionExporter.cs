using System.Globalization;
using System.Text;
using OpenGZH.Core.Mods;
using OpenGZH.Core.Rules;
using OpenGZH.TiberianDawn;
using OpenGZH.TiberianDawn.Logic;
using OpenGZH.TiberianDawn.Logic.Types;

namespace OpenGZH.Tools;

/// <summary>
/// export-td-factions: turns Tiberian Dawn's GDI and Nod (from mods/tiberiandawn/rules) into Zero Hour
/// engine factions, so they can play against the Generals factions: player templates, every buildable
/// unit and structure, weapons, armour, locomotors, projectiles, command sets and the TD scenery
/// (trees, Tiberium) used on converted TD maps. Written to mods/zh/rules/td/.
///
/// Conversions (one TD cell = 36 world units, so TD art at 1.5 units a pixel matches ZH sizes):
///   speed      MPH (leptons a TD frame, 15 frames a second) x 15 x 36/256
///   range      leptons x 36/256
///   fire rate  TD frames x 1000/15 ms
///   damage, health, cost: as TD; build time: TD's (cost x 15/1000 frames... see BuildSeconds)
///   warheads   to the ZH damage types they resemble; TD armour classes become ZH armour sets that
///              use TD's own warhead-versus-armour table for those types.
/// </summary>
public static class TdFactionExporter
{
    const float CellUnits = 36f;                 // one TD cell in ZH world units
    const float InfantrySpeedFactor = 1.5f;      // TD soldiers walk this much faster (see the infantry locomotors)
    const float LeptonUnits = CellUnits / 256f;  // one TD lepton
    const float FramesPerSecond = 15f;
    const int BuildLevel = 10;                   // TD skirmish's tech level

    sealed record Side(string Faction, string Prefix, string SideName, string BaseSide, HousesType House, string DisplayName, string Color);

    static readonly Side[] Sides =
    [
        new("FactionTDGDI", "TDGDI_", "TDGDI", "USA", HousesType.HOUSE_GOOD, "GDI (Tiberian Dawn)", "R:255 G:200 B:0"),
        new("FactionTDNod", "TDNOD_", "TDNod", "GLA", HousesType.HOUSE_BAD, "Nod (Tiberian Dawn)", "R:200 G:0 B:0"),
    ];

    // TD warhead -> the ZH damage type it acts as.
    static readonly Dictionary<string, string> DamageTypeOf = new()
    {
        ["SA"] = "SMALL_ARMS", ["HE"] = "EXPLOSION", ["AP"] = "ARMOR_PIERCING", ["FIRE"] = "FLAME",
        ["LASER"] = "LASER", ["PB"] = "PARTICLE_BEAM", ["FIST"] = "MELEE", ["FOOT"] = "CRUSH",
        ["HOLLOW_POINT"] = "SNIPER", ["SPORE"] = "POISON", ["HEADBUTT"] = "MELEE", ["FEEDME"] = "MELEE",
    };

    // TD armour -> (ZH armour it starts from, column in TD's Modifier table).
    static readonly (ArmorType Armor, string Name, string Base)[] Armors =
    [
        (ArmorType.ARMOR_NONE, "TD_ArmorNone", "HumanArmor"),
        (ArmorType.ARMOR_WOOD, "TD_ArmorWood", "StructureArmor"),
        (ArmorType.ARMOR_ALUMINUM, "TD_ArmorAluminum", "TruckArmor"),
        (ArmorType.ARMOR_STEEL, "TD_ArmorSteel", "TankArmor"),
        (ArmorType.ARMOR_CONCRETE, "TD_ArmorConcrete", "StructureArmor"),
    ];

    // Structures whose presence counts as another for prerequisites (HouseClass::Can_Build).
    static readonly (StructType Has, StructType CountsAs)[] Equivalents =
    [
        (StructType.STRUCT_ADVANCED_POWER, StructType.STRUCT_POWER),
        (StructType.STRUCT_HAND, StructType.STRUCT_BARRACKS),
        (StructType.STRUCT_OBELISK, StructType.STRUCT_ATOWER),
        (StructType.STRUCT_TEMPLE, StructType.STRUCT_EYE),
        (StructType.STRUCT_AIRSTRIP, StructType.STRUCT_WEAP),
        (StructType.STRUCT_SAM, StructType.STRUCT_HELIPAD),
    ];

    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static int Run(string root)
    {
        using var td = TdContent.Load(root);
        var types = new TdTypes(td.Rules);
        string output = Path.Combine(root, "mods", "zh", "rules", "td");
        Directory.CreateDirectory(output);

        // The ZH armour sets the TD ones start from.
        var zhArmor = RuleYaml.Parse(File.ReadAllText(Path.Combine(root, "mods", "zh", "rules", "armor.yaml")), "armor.yaml")["Armor"]!;

        var text = new TextNames();
        var objects = new StringBuilder("Objects:\n");
        var weapons = new StringBuilder("Weapons:\n");
        var locomotors = new StringBuilder("Locomotors:\n");
        var commandSets = new StringBuilder("CommandSets:\n");
        var buttons = new StringBuilder("CommandButtons:\n");
        var armor = new StringBuilder("Armor:\n");
        var factions = new StringBuilder("PlayerTemplates:\n");

        // ---- Armour ----
        foreach (var (armorType, name, baseName) in Armors)
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (zhArmor.TryGet(baseName, out var b) && b.TryGet("Armor", out var list))
                foreach (var e in list.AsList())
                    if (RuleValue.Words(e.Value) is [var dt, var pct, ..])
                        entries[dt] = pct;
            foreach (var w in types.AllWarheads)
            {
                string key = WarheadName(w);
                if (!DamageTypeOf.TryGetValue(key, out var dt) || dt == "CRUSH")
                    continue;
                int modifier = w.Modifier.Length > (int)armorType ? w.Modifier[(int)armorType] : 256;
                entries[dt] = $"{modifier * 100 / 256}%";
            }
            armor.Append($"  {name}:\n    Armor:\n");
            foreach (var (dt, pct) in entries)
                armor.Append($"    - {dt} {pct}\n");
        }

        // ---- Weapons and projectiles (shared by both sides) ----
        var weaponNames = new Dictionary<WeaponType, string>();
        foreach (var w in types.AllWeapons)
        {
            var weaponType = (WeaponType)w.Id;
            if (w.Fires == BulletType.BULLET_NONE)
                continue;
            var bullet = types.Bullet(w.Fires);
            var warhead = types.Warhead(bullet.Warhead);
            string name = $"TD_Weapon_{weaponType.ToString()["WEAPON_".Length..]}";
            weaponNames[weaponType] = name;
            string dt = DamageTypeOf.GetValueOrDefault(WarheadName(warhead), "EXPLOSION");
            float radius = Math.Max(3, (1 << warhead.SpreadFactor) * LeptonUnits);
            weapons.Append($"  {name}:\n");
            weapons.Append($"    PrimaryDamage: {w.Attack}.0\n");
            weapons.Append($"    PrimaryDamageRadius: {F(Math.Min(radius, 27))}\n");
            if (warhead.SpreadFactor >= 4)
            {
                weapons.Append($"    SecondaryDamage: {F(w.Attack / 2f)}\n");
                weapons.Append($"    SecondaryDamageRadius: {F(Math.Min(radius * 2, CellUnits * 1.5f))}\n");
            }
            weapons.Append($"    AttackRange: {F(Math.Max(w.Range * LeptonUnits, 15))}\n");
            weapons.Append($"    DamageType: {dt}\n");
            weapons.Append($"    DeathType: {(dt == "FLAME" ? "BURNED" : dt == "EXPLOSION" ? "EXPLODED" : "NORMAL")}\n");
            weapons.Append($"    DelayBetweenShots: {(int)(Math.Max(w.ROF, 1) * 1000 / FramesPerSecond)}\n");
            weapons.Append("    ClipSize: 0\n    ClipReloadTime: 0\n");
            weapons.Append("    RadiusDamageAffects: ALLIES ENEMIES NEUTRALS\n");
            bool antiAir = bullet.IsAntiAircraft;
            // TD's anti-aircraft-only projectile (the SAM's Nike) can't hit the ground.
            bool antiGround = !(antiAir && bullet.Type == BulletType.BULLET_SAM && weaponType == WeaponType.WEAPON_NIKE);
            weapons.Append($"    AntiGround: {(antiGround ? "Yes" : "No")}\n");
            weapons.Append($"    AntiAirborneVehicle: {(antiAir ? "Yes" : "No")}\n");
            weapons.Append($"    AntiAirborneInfantry: {(antiAir ? "Yes" : "No")}\n");
            weapons.Append($"    TdFireAnim: {w.Anim}\n");
            weapons.Append($"    TdExplosion: {bullet.Explosion}\n");
            // TD's own muzzle flash and explosion, drawn by the client as TD animations ("TDFX:" names).
            // A shot with no visible bullet hits at once: its explosion rides on the fire FX ("flash|explosion",
            // the second played where the shot lands), as Zero Hour plays no detonation FX for instant weapons.
            if (bullet.IsInvisible)
            {
                if (w.Anim != AnimType.ANIM_NONE || bullet.Explosion != AnimType.ANIM_NONE)
                    weapons.Append($"    FireFX: TDFX:{w.Anim}|{bullet.Explosion}\n");
            }
            else
            {
                if (w.Anim != AnimType.ANIM_NONE)
                    weapons.Append($"    FireFX: TDFX:{w.Anim}\n");
                if (bullet.Explosion != AnimType.ANIM_NONE)
                    weapons.Append($"    ProjectileDetonationFX: TDFX:{bullet.Explosion}\n");
            }
            if (!bullet.IsInvisible)
            {
                string proj = $"TD_Projectile_{bullet.IniName}_{bullet.Type.ToString()["BULLET_".Length..]}";
                weapons.Append($"    ProjectileObject: {proj}\n");
                weapons.Append($"    WeaponSpeed: {F(Math.Max(Speed(bullet.MaxSpeed), 60))}\n");
            }
        }

        // Projectile objects, one per visible bullet type, drawn with the TD shape.
        var projectiles = new StringBuilder("Objects:\n");
        foreach (var bullet in types.AllBullets)
        {
            if (bullet.IsInvisible)
                continue;
            string proj = $"TD_Projectile_{bullet.IniName}_{bullet.Type.ToString()["BULLET_".Length..]}";
            projectiles.Append($"  {proj}:\n");
            projectiles.Append("    DisplayName: TD projectile\n    EditorSorting: SYSTEM\n    VisionRange: 0.0\n");
            projectiles.Append(bullet.IsHoming ? "    KindOf: PROJECTILE SMALL_MISSILE\n" : "    KindOf: PROJECTILE\n");
            if (bullet.IsHoming)
                projectiles.Append($"    Locomotor: SET_NORMAL {proj}Locomotor\n");
            projectiles.Append("    Geometry: Sphere\n    GeometryIsSmall: Yes\n    GeometryMajorRadius: 1.0\n");
            projectiles.Append("    ArmorSet:\n      Armor: ProjectileArmor\n");
            projectiles.Append("    Modules:\n");
            projectiles.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: TDSpriteDraw\n        Shape: {bullet.IniName}\n        TdKind: Bullet\n        TdType: {bullet.Type}\n");
            projectiles.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 100.0\n        InitialHealth: 100.0\n");
            projectiles.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
            if (bullet.IsHoming)
            {
                projectiles.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: MissileAIUpdate\n        TryToFollowTarget: Yes\n        FuelLifetime: 10000\n");
                projectiles.Append($"        InitialVelocity: {F(Speed(bullet.MaxSpeed))}\n        IgnitionDelay: 0\n        DistanceToTravelBeforeTurning: 5\n        DistanceToTargetForLock: 60\n");
                locomotors.Append($"  {proj}Locomotor:\n    Surfaces: AIR\n    Speed: {F(Math.Max(Speed(bullet.MaxSpeed), 60))}\n    MinSpeed: 30\n    Acceleration: 400\n    Braking: 0\n");
                locomotors.Append($"    TurnRate: {F(Math.Max(bullet.ROT * 15 * 360 / 256f, 90))}\n    MaxThrustAngle: 30\n    AllowAirborneMotiveForce: Yes\n    Appearance: THRUST\n    CloseEnoughDist: 1\n");
            }
            else
            {
                float arc = bullet.IsArcing ? 60 : bullet.IsDropping ? 0 : 5;
                projectiles.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: DumbProjectileBehavior\n        FirstHeight: {F(arc)}\n        SecondHeight: {F(arc)}\n");
                projectiles.Append("        FirstPercentIndent: 30%\n        SecondPercentIndent: 70%\n        FlightPathAdjustDistPerSecond: 100\n");
            }
            projectiles.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 0.01\n");
        }

        // ---- The two sides ----
        foreach (var side in Sides)
        {
            bool Own(TechnoTypeClass t) => t.IsBuildable && (t.Ownable & (1L << (int)side.House)) != 0 && t.Level <= BuildLevel;

            var structs = types.AllBuildings.Where(Own).ToList();
            var units = types.AllUnits.Where(Own).ToList();
            var infantry = types.AllInfantry.Where(Own).ToList();
            var aircraft = types.AllAircraft.Where(Own).ToList();
            // The Construction Yard isn't buildable, but every side has one (from its MCV).
            var yard = types.Building(StructType.STRUCT_CONST);
            structs.Insert(0, yard);
            var structTypes = structs.Select(b => b.Type).ToHashSet();
            string N(ObjectTypeClass t) => side.Prefix + t.IniName;

            // Prerequisites: each STRUCTF bit becomes one "Object" line of alternatives.
            string Prerequisites(TechnoTypeClass t)
            {
                var lines = new List<string>();
                for (int bit = 0; bit < 32; bit++)
                {
                    if ((t.Pre & (1L << bit)) == 0)
                        continue;
                    var needed = (StructType)bit;
                    var alternatives = new List<StructType> { needed };
                    alternatives.AddRange(Equivalents.Where(e => e.CountsAs == needed).Select(e => e.Has));
                    var names = alternatives.Where(structTypes.Contains).Select(s => N(types.Building(s))).ToList();
                    if (names.Count > 0)
                        lines.Add(string.Join(' ', names));
                }
                if (lines.Count == 0)
                    return "    Prerequisites: {}\n";
                var sb = new StringBuilder("    Prerequisites:\n      Object:\n");
                foreach (var l in lines)
                    sb.Append($"      - {l}\n");
                return sb.ToString();
            }

            // Command buttons for everything this side builds.
            string Button(TechnoTypeClass t)
            {
                string name = $"Command_TD_{side.Prefix}{t.IniName}";
                buttons.Append($"  {name}:\n    Command: UNIT_BUILD\n    Object: {N(t)}\n    TextLabel: {text.Name(t)}\n    ButtonImage: TDCAMEO:{CameoName(t)}\n    ButtonBorderType: BUILD\n    DescriptLabel: {text.Name(t)}\n");
                return name;
            }

            void CommandSet(string name, IEnumerable<string> entries)
            {
                commandSets.Append($"  {name}:\n");
                int slot = 1;
                foreach (var e in entries)
                    commandSets.Append($"    {slot++}: {e}\n");
            }

            // Factories' command sets.
            string FactoryCommandSet(BuildingTypeClass b)
            {
                string setName = $"{N(b)}CommandSet";
                var built = b.ToBuild switch
                {
                    RTTIType.RTTI_BUILDINGTYPE => structs.Where(s => s != yard).Cast<TechnoTypeClass>(),
                    RTTIType.RTTI_UNITTYPE => units.Cast<TechnoTypeClass>(),
                    RTTIType.RTTI_INFANTRYTYPE => infantry.Cast<TechnoTypeClass>(),
                    RTTIType.RTTI_AIRCRAFTTYPE => aircraft.Cast<TechnoTypeClass>(),
                    _ => [],
                };
                // The airstrip delivers vehicles too (it counts as a war factory), as in TD.
                var list = built.Select(Button).ToList();
                if (b.ToBuild != RTTIType.RTTI_BUILDINGTYPE)
                    list.Add("Command_SetRallyPoint");
                list.Add("Command_Sell");
                CommandSet(setName, list);
                return setName;
            }

            // ---- Structures ----
            foreach (var b in structs)
            {
                float w = b.Width() * CellUnits, h = b.Height() * CellUnits;
                // TD blocks only the Occupy cells (a refinery's bottom row is a drivable apron where the
                // harvester docks); placing also needs the bib. The object stands at the middle of its
                // occupied cells, so points on the art are given relative to that (see CellBox).
                var occupy = CellBox(b, b.Occupy_List(types, types.Special));
                var footprint = CellBox(b, b.Occupy_List(types, types.Special, placement: true));
                (float X, float Y) Art(float col, float row) => ((col - b.Width() / 2f) * CellUnits - occupy.X, -(row - b.Height() / 2f) * CellUnits - occupy.Y);
                objects.Append($"  {N(b)}:\n");
                objects.Append($"    DisplayName: {text.Name(b)}\n    Side: {side.SideName}\n    EditorSorting: STRUCTURE\n    ButtonImage: TDCAMEO:{CameoName(b)}\n    SelectPortrait: TDCAMEO:{CameoName(b)}\n");
                objects.Append($"    BuildCost: {b.Cost}\n    BuildTime: {F(BuildSeconds(b.Cost))}\n");
                int energy = b.Power - b.Drain;
                if (energy != 0)
                    objects.Append($"    EnergyProduction: {energy}\n");
                objects.Append($"    VisionRange: {F(Math.Max(b.SightRange, 2) * CellUnits)}\n    ShroudClearingRange: {F(Math.Max(b.SightRange, 2) * CellUnits)}\n");
                objects.Append("    RadarPriority: STRUCTURE\n");
                var kinds = new List<string> { "PRELOAD", "STRUCTURE", "SELECTABLE", "IMMOBILE", "SCORE", "TD_OBJECT" };
                if (!b.IsWall)
                    kinds.Add("MP_COUNT_FOR_VICTORY");
                if (b.IsCaptureable)
                    kinds.Add("CAPTURABLE");
                if (b.Type == StructType.STRUCT_CONST)
                    kinds.AddRange(["COMMANDCENTER", "FS_FACTORY", "AUTO_RALLYPOINT"]);
                if (b.Power > 0)
                    kinds.Add("FS_POWER");
                if (b.IsWall)
                    kinds.Add("WALK_ON_TOP_OF_WALL");
                switch (b.ToBuild)
                {
                    case RTTIType.RTTI_INFANTRYTYPE: kinds.AddRange(["FS_FACTORY", "FS_BARRACKS", "AUTO_RALLYPOINT"]); break;
                    case RTTIType.RTTI_UNITTYPE: kinds.AddRange(["FS_FACTORY", "FS_WARFACTORY", "AUTO_RALLYPOINT"]); break;
                    case RTTIType.RTTI_AIRCRAFTTYPE: kinds.AddRange(["FS_FACTORY", "FS_AIRFIELD"]); break;
                }
                if (b.Type == StructType.STRUCT_REFINERY)
                    kinds.AddRange(["FS_SUPPLY_CENTER", "CASH_GENERATOR", "CANNOT_BUILD_NEAR_SUPPLIES"]);
                if (b.Primary != WeaponType.WEAPON_NONE)
                    kinds.AddRange(["CAN_ATTACK", "FS_BASE_DEFENSE"]);
                if (b.Type is StructType.STRUCT_RADAR or StructType.STRUCT_EYE)
                    kinds.Add("FS_TECHNOLOGY");
                objects.Append($"    KindOf: {string.Join(' ', kinds)}\n");
                objects.Append($"    Geometry: BOX\n    GeometryMajorRadius: {F(occupy.HalfX)}\n    GeometryMinorRadius: {F(occupy.HalfY)}\n    GeometryHeight: {F(Math.Min(w, h) / 2)}\n    GeometryIsSmall: No\n");
                // Where the art's centre is, and the placement footprint (centre X Y, half sizes X Y), from the object's position.
                objects.Append($"    TdArtOffset: 'X: {F(-occupy.X)} Y: {F(-occupy.Y)} Z:0.0'\n");
                objects.Append($"    TdFootprint: {F(footprint.X - occupy.X)} {F(footprint.Y - occupy.Y)} {F(footprint.HalfX)} {F(footprint.HalfY)}\n");
                objects.Append("    BuildCompletion: PLACED_BY_PLAYER\n    PlacementViewAngle: 0\n");
                // TD builds next to the base, from the Construction Yard (not with a dozer).
                objects.Append("    TdBuiltByYard: Yes\n");
                objects.Append(Prerequisites(b));
                objects.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {ArmorName(b.Armor)}\n      DamageFX: StructureDamageFXNoShake\n");
                if (b.Primary != WeaponType.WEAPON_NONE && weaponNames.TryGetValue(b.Primary, out var bw))
                    objects.Append($"    WeaponSet:\n      Conditions: None\n      Weapon: PRIMARY {bw}\n");
                if (b.ToBuild != RTTIType.RTTI_NONE)
                    objects.Append($"    CommandSet: {FactoryCommandSet(b)}\n");
                else
                {
                    CommandSet($"{N(b)}CommandSet", ["Command_Sell"]);
                    objects.Append($"    CommandSet: {N(b)}CommandSet\n");
                }

                objects.Append("    Modules:\n");
                objects.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: TDSpriteDraw\n        Shape: {b.IniName}\n        TdKind: Building\n        TdType: {b.Type}\n");
                objects.Append($"      ModuleTag_02:\n        Module: Body\n        Class: StructureBody\n        MaxHealth: {b.MaxStrength}.0\n        InitialHealth: {b.MaxStrength}.0\n");
                objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
                objects.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: CreateObjectDie\n        CreationList: OCL_SmallStructureDebris\n        ExemptStatus: UNDER_CONSTRUCTION\n");
                if (b.Primary != WeaponType.WEAPON_NONE)
                {
                    objects.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: AIUpdateInterface\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (b.IsTurretEquipped)
                        objects.Append("        Turret:\n          TurretTurnRate: 180\n          ControlledWeaponSlots: PRIMARY\n");
                }
                if (b.ToBuild != RTTIType.RTTI_NONE)
                {
                    objects.Append("      ModuleTag_06:\n        Module: Behavior\n        Class: ProductionUpdate\n        MaxQueueEntries: 5\n");
                    if (b.ToBuild == RTTIType.RTTI_BUILDINGTYPE)
                        objects.Append("        PlacesStructures: Yes\n");
                    else
                    {
                        // Units come out of the factory's bottom edge (TD's ExitList).
                        var create = Art(b.Width() / 2f, b.Height() - 0.25f);
                        var rally = Art(b.Width() / 2f, b.Height() + 1);
                        objects.Append($"      ModuleTag_07:\n        Module: Behavior\n        Class: DefaultProductionExitUpdate\n        UnitCreatePoint: 'X: {F(create.X)} Y: {F(create.Y)} Z:0.0'\n        NaturalRallyPoint: 'X: {F(rally.X)} Y: {F(rally.Y)} Z:0.0'\n");
                    }
                }
                if (b.Type == StructType.STRUCT_REFINERY)
                {
                    objects.Append("      ModuleTag_08:\n        Module: Behavior\n        Class: SupplyCenterCreate\n");
                    objects.Append("      ModuleTag_09:\n        Module: Behavior\n        Class: SupplyCenterDockUpdate\n        AllowsPassthrough: No\n        NumberApproachPositions: -1\n");
                    // The harvester backs onto the middle of the bottom row (the apron), coming up from below the bib.
                    var dock = Art(b.Width() / 2f, b.Height() - 0.5f);
                    var start = Art(b.Width() / 2f, b.Height() + 0.5f);
                    objects.Append($"        DockActionOffset: 'X: {F(dock.X)} Y: {F(dock.Y)} Z:0.0'\n");
                    objects.Append($"        DockStartOffset: 'X: {F(start.X)} Y: {F(start.Y)} Z:0.0'\n        DockEndOffset: 'X: {F(start.X)} Y: {F(start.Y)} Z:0.0'\n");
                    // The free harvester that comes with a refinery.
                    objects.Append($"      ModuleTag_10:\n        Module: Behavior\n        Class: TdFreeUnitOnBuilt\n        Unit: {side.Prefix}HARV\n");
                }
                if (b.Type == StructType.STRUCT_REPAIR)
                    objects.Append("      ModuleTag_11:\n        Module: Behavior\n        Class: RepairDockUpdate\n        TimeForFullHeal: 5000\n        NumberApproachPositions: 5\n");
            }

            // ---- Mobile units ----
            void Mobile(TechnoTypeClass t, string kind, string locomotor, float radius, float height)
            {
                objects.Append($"  {N(t)}:\n");
                objects.Append($"    DisplayName: {text.Name(t)}\n    Side: {side.SideName}\n    EditorSorting: {(kind == "Infantry" ? "INFANTRY" : "VEHICLE")}\n    ButtonImage: TDCAMEO:{CameoName(t)}\n    SelectPortrait: TDCAMEO:{CameoName(t)}\n");
                objects.Append($"    BuildCost: {t.Cost}\n    BuildTime: {F(BuildSeconds(t.Cost))}\n");
                float sight = Math.Max(t.SightRange, 1) * CellUnits;
                objects.Append($"    VisionRange: {F(sight)}\n    ShroudClearingRange: {F(sight)}\n    IsTrainable: Yes\n    RadarPriority: UNIT\n");
                var kinds = new List<string> { "PRELOAD", "SELECTABLE", "SCORE", "TD_OBJECT" };
                if (t.Primary != WeaponType.WEAPON_NONE)
                    kinds.Add("CAN_ATTACK");
                kinds.Add(kind switch { "Infantry" => "INFANTRY", "Aircraft" => "AIRCRAFT", _ => "VEHICLE" });
                if (t is UnitTypeClass ut)
                {
                    if (ut.IsToHarvest)
                        kinds.Add("HARVESTER");
                    if (ut.Type == UnitType.UNIT_MCV)
                        kinds.Add("TD_MCV");
                }
                if (kind == "Aircraft")
                    kinds.Add("VTOL");
                objects.Append($"    KindOf: {string.Join(' ', kinds)}\n");
                objects.Append($"    Locomotor: SET_NORMAL {locomotor}\n");
                objects.Append($"    Geometry: {(kind == "Infantry" ? "CYLINDER" : "BOX")}\n    GeometryMajorRadius: {F(radius)}\n    GeometryMinorRadius: {F(radius)}\n    GeometryHeight: {F(height)}\n    GeometryIsSmall: Yes\n");
                if (kind == "Infantry")
                    objects.Append("    CrushableLevel: 0\n");
                else if (t is UnitTypeClass u)
                    objects.Append($"    CrusherLevel: {(u.IsCrusher ? 2 : 1)}\n    CrushableLevel: 2\n");
                objects.Append(Prerequisites(t));
                objects.Append($"    ArmorSet:\n      Conditions: None\n      Armor: {ArmorName(t.Armor)}\n");
                var slots = new List<string>();
                if (t.Primary != WeaponType.WEAPON_NONE && weaponNames.TryGetValue(t.Primary, out var pw))
                    slots.Add($"PRIMARY {pw}");
                if (t.Secondary != WeaponType.WEAPON_NONE && weaponNames.TryGetValue(t.Secondary, out var sw))
                    slots.Add($"SECONDARY {sw}");
                if (slots.Count > 0)
                    objects.Append($"    WeaponSet:\n      Conditions: None\n      Weapon: [{string.Join(", ", slots)}]\n");
                objects.Append("    CommandSet: GenericCommandSet\n");
                objects.Append("    Modules:\n");
                string tdType = t switch { UnitTypeClass x => x.Type.ToString(), InfantryTypeClass x => x.Type.ToString(), AircraftTypeClass x => x.Type.ToString(), _ => "" };
                objects.Append($"      ModuleTag_01:\n        Module: Draw\n        Class: TDSpriteDraw\n        Shape: {t.IniName}\n        TdKind: {kind}\n        TdType: {tdType}\n");
                objects.Append($"      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: {t.MaxStrength}.0\n        InitialHealth: {t.MaxStrength}.0\n");
                if (t is UnitTypeClass { IsToHarvest: true })
                {
                    // TD's harvester holds 700 credits of Tiberium (28 bails of 25).
                    objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: SupplyTruckAIUpdate\n        MaxBoxes: 9\n        SupplyCenterActionDelay: 1500\n        SupplyWarehouseActionDelay: 600\n        SupplyWarehouseScanDistance: 900\n");
                }
                else
                {
                    objects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: AIUpdateInterface\n        AutoAcquireEnemiesWhenIdle: Yes\n");
                    if (t.IsTurretEquipped)
                        objects.Append($"        Turret:\n          TurretTurnRate: {F(Math.Max(Rot(t) * 2, 180))}\n          ControlledWeaponSlots: PRIMARY\n");
                }
                objects.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 5.0\n");
                if (kind == "Infantry")
                    objects.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 1000\n");
                else
                    objects.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 100\n        FX: FINAL FX_GenericTankDeathExplosion\n");
                if (t is UnitTypeClass { Type: UnitType.UNIT_MCV })
                    objects.Append($"      ModuleTag_06:\n        Module: Behavior\n        Class: TdDeployToStructure\n        Structure: {side.Prefix}FACT\n");
                if (kind == "Aircraft")
                    objects.Append("      ModuleTag_07:\n        Module: Behavior\n        Class: ProductionUpdate\n        MaxQueueEntries: 1\n");
            }

            foreach (var u in units)
            {
                string loco = $"{N(u)}Locomotor";
                bool tracked = u.IsTracked;
                locomotors.Append($"  {loco}:\n    Surfaces: GROUND\n    Speed: {F(Speed(u.MaxSpeed))}\n    SpeedDamaged: {F(Speed(u.MaxSpeed) * 0.75f)}\n");
                locomotors.Append($"    TurnRate: {F(Rot(u))}\n    TurnRateDamaged: {F(Rot(u))}\n    Acceleration: 1000\n    AccelerationDamaged: 1000\n    Braking: 1000\n    MinTurnSpeed: 0\n");
                locomotors.Append($"    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: {(tracked ? "TREADS" : "FOUR_WHEELS")}\n");
                Mobile(u, "Unit", loco, u.Type is UnitType.UNIT_HTANK or UnitType.UNIT_MCV or UnitType.UNIT_HARVESTER ? 17 : 13, 12);
            }
            foreach (var i in infantry)
            {
                string loco = $"{N(i)}Locomotor";
                // Soldiers walk 1.5 times their TD speed: at 15 TD frames a second they would crawl next to
                // Zero Hour's infantry (a Minigunner 17 units a second, a Ranger 20); TD's skirmish ran faster.
                float infantrySpeed = Speed(i.MaxSpeed) * InfantrySpeedFactor;
                locomotors.Append($"  {loco}:\n    Surfaces: GROUND RUBBLE\n    Speed: {F(infantrySpeed)}\n    SpeedDamaged: {F(infantrySpeed)}\n");
                locomotors.Append("    TurnRate: 500\n    TurnRateDamaged: 500\n    Acceleration: 100\n    AccelerationDamaged: 100\n    Braking: 100\n    MinTurnSpeed: 0\n");
                locomotors.Append("    ZAxisBehavior: NO_Z_MOTIVE_FORCE\n    Appearance: TWO_LEGS\n    StickToGround: Yes\n    GroupMovementPriority: MOVES_FRONT\n");
                Mobile(i, "Infantry", loco, 5, 12);
            }
            foreach (var a in aircraft)
            {
                string loco = $"{N(a)}Locomotor";
                locomotors.Append($"  {loco}:\n    Surfaces: AIR\n    Speed: {F(Math.Max(Speed(a.MaxSpeed), 40))}\n    SpeedDamaged: {F(Math.Max(Speed(a.MaxSpeed), 40))}\n");
                locomotors.Append($"    TurnRate: {F(Math.Max(Rot(a), 120))}\n    TurnRateDamaged: {F(Math.Max(Rot(a), 120))}\n    Acceleration: 60\n    AccelerationDamaged: 60\n    Lift: 120\n    LiftDamaged: 80\n    Braking: 240\n    MinTurnSpeed: 0\n");
                // TD's FLIGHT_LEVEL is 24 pixels.
                locomotors.Append($"    PreferredHeight: {F(24 * 1.5f)}\n    AllowAirborneMotiveForce: Yes\n    ZAxisBehavior: SURFACE_RELATIVE_HEIGHT\n    Appearance: HOVER\n    Apply2DFrictionWhenAirborne: Yes\n    AirborneTargetingHeight: 30\n    LocomotorWorksWhenDead: Yes\n");
                Mobile(a, "Aircraft", loco, 14, 10);
            }

            // ---- The player template ----
            factions.Append($"  {side.Faction}:\n");
            factions.Append($"    Side: {side.SideName}\n    BaseSide: {side.BaseSide}\n    PlayableSide: Yes\n    StartMoney: 0\n    PreferredColor: {side.Color}\n");
            factions.Append($"    DisplayName: {side.DisplayName}\n");
            factions.Append($"    StartingUnit0: {side.Prefix}MCV\n");
            factions.Append("    TdFaction: Yes\n");
            factions.Append($"    ScoreScreenImage: {(side.House == HousesType.HOUSE_GOOD ? "America_ScoreScreen" : "GLA_ScoreScreen")}\n");
            factions.Append($"    LoadScreenImage: {(side.House == HousesType.HOUSE_GOOD ? "SAFactionLogoPage_US" : "SUFactionLogoPage_GLA")}\n");
            factions.Append("    BeaconName: MultiplayerBeacon\n    OldFaction: Yes\n");
        }

        // ---- TD scenery for converted TD maps: trees and Tiberium ----
        var scenery = new StringBuilder("Objects:\n");
        foreach (var t in types.AllTerrains)
        {
            float w = Math.Max(1, t.Occupy.Count(o => o != TdMath.RefreshEol)) > 1 ? CellUnits : CellUnits * 0.6f;
            scenery.Append($"  TD_Terrain_{t.IniName}:\n    DisplayName: {t.IniName}\n    EditorSorting: MISC_NATURAL\n    KindOf: IMMOBILE TD_OBJECT {(t.IsImmune ? "" : "")}\n");
            scenery.Append($"    Geometry: CYLINDER\n    GeometryMajorRadius: {F(w / 2)}\n    GeometryMinorRadius: {F(w / 2)}\n    GeometryHeight: 30\n    GeometryIsSmall: No\n");
            scenery.Append("    ArmorSet:\n      Conditions: None\n      Armor: TD_ArmorWood\n");
            scenery.Append($"    Modules:\n      ModuleTag_01:\n        Module: Draw\n        Class: TDSpriteDraw\n        Shape: {t.IniName}\n        TdKind: Terrain\n        TdType: {t.Type}\n");
            scenery.Append($"      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: {Math.Max(t.MaxStrength, 1)}.0\n        InitialHealth: {Math.Max(t.MaxStrength, 1)}.0\n");
            scenery.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
        }
        // One Tiberium cell: a small supply pile (boxes of 75 credits) that disappears when harvested out.
        for (int stage = 0; stage < 12; stage++)
        {
            int credits = 25 * (stage + 1) * 3;   // TD: up to 12 bails of 25 credits (tripled for ZH's harvest pace)
            int boxes = Math.Max(1, (int)Math.Ceiling(credits / 75f));
            scenery.Append($"  TD_Tiberium{stage + 1:00}:\n    DisplayName: Tiberium\n    EditorSorting: MISC_NATURAL\n    KindOf: IMMOBILE SUPPLY_SOURCE STRUCTURE TD_OBJECT\n");
            scenery.Append($"    Geometry: CYLINDER\n    GeometryMajorRadius: 10\n    GeometryMinorRadius: 10\n    GeometryHeight: 3\n    GeometryIsSmall: Yes\n");
            scenery.Append("    ArmorSet:\n      Conditions: None\n      Armor: InvulnerableAllArmor\n");
            scenery.Append($"    Modules:\n      ModuleTag_01:\n        Module: Draw\n        Class: TDSpriteDraw\n        Shape: TI1\n        TdKind: Tiberium\n        TdType: {stage}\n");
            scenery.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 1.0\n        InitialHealth: 1.0\n");
            scenery.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: SupplyWarehouseCreate\n");
            scenery.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: SupplyWarehouseDockUpdate\n        StartingBoxes: {boxes}\n        DeleteWhenEmpty: Yes\n        AllowsPassthrough: Yes\n        NumberApproachPositions: -1\n");
        }

        const string header = "# GENERATED by 'OpenGZH.Tools export-td-factions' from Tiberian Dawn's rules (mods/tiberiandawn/rules).\n" +
                              "# Tiberian Dawn's GDI and Nod as Zero Hour engine factions. Edit freely, or re-export.\n";
        File.WriteAllText(Path.Combine(output, "factions.yaml"), header + factions);
        File.WriteAllText(Path.Combine(output, "objects.yaml"), header + objects);
        File.WriteAllText(Path.Combine(output, "projectiles.yaml"), header + projectiles);
        File.WriteAllText(Path.Combine(output, "scenery.yaml"), header + scenery);
        File.WriteAllText(Path.Combine(output, "weapons.yaml"), header + weapons);
        File.WriteAllText(Path.Combine(output, "locomotors.yaml"), header + locomotors);
        File.WriteAllText(Path.Combine(output, "armor.yaml"), header + armor);
        File.WriteAllText(Path.Combine(output, "command-sets.yaml"), header + commandSets);
        File.WriteAllText(Path.Combine(output, "command-buttons.yaml"), header + buttons);
        Console.WriteLine($"Wrote TD factions to {output}");
        return 0;
    }

    static string WarheadName(WarheadTypeClass w) => ((WarheadType)w.Id).ToString()["WARHEAD_".Length..];

    static string ArmorName(ArmorType a) => Armors.FirstOrDefault(x => x.Armor == a).Name ?? "TD_ArmorNone";

    /// <summary>MPH (leptons a TD frame) to world units a second.</summary>
    static float Speed(MPHType mph) => (int)mph * FramesPerSecond * LeptonUnits;

    /// <summary>TD's turn rate (256 = a full turn, per frame) in degrees a second.</summary>
    static float Rot(TechnoTypeClass t) => t switch
    {
        UnitTypeClass u => u.ROT * FramesPerSecond * 360 / 256f,
        AircraftTypeClass a => a.ROT * FramesPerSecond * 360 / 256f,
        _ => 360,
    };

    /// <summary>TD's build time: a factory adds one step of 108 every few frames; the result is about
    /// cost/100 x 1.6 seconds at full power (FactoryClass rates), which is what this returns.</summary>
    static float BuildSeconds(int cost) => Math.Max(2, cost / 100f * 1.6f);

    /// <summary>
    /// The box around a building's cells (TD cell offsets from its top-left, MAP_CELL_W per row), in ZH
    /// units: its centre from the art's centre (+X east, +Y north, as TD rows run south) and its half sizes.
    /// </summary>
    static (float X, float Y, float HalfX, float HalfY) CellBox(BuildingTypeClass b, short[] cells)
    {
        int minCol = int.MaxValue, maxCol = int.MinValue, minRow = int.MaxValue, maxRow = int.MinValue;
        foreach (short offset in cells)
        {
            if (offset == TdMath.RefreshEol)
                break;
            int row = offset / TdMath.MapCellW, col = offset % TdMath.MapCellW;
            minCol = Math.Min(minCol, col); maxCol = Math.Max(maxCol, col);
            minRow = Math.Min(minRow, row); maxRow = Math.Max(maxRow, row);
        }
        if (minCol == int.MaxValue)
            (minCol, maxCol, minRow, maxRow) = (0, b.Width() - 1, 0, b.Height() - 1);
        float cx = (minCol + maxCol + 1) / 2f, cy = (minRow + maxRow + 1) / 2f;
        return ((cx - b.Width() / 2f) * CellUnits, -(cy - b.Height() / 2f) * CellUnits,
            (maxCol - minCol + 1) * CellUnits / 2, (maxRow - minRow + 1) * CellUnits / 2);
    }

    /// <summary>Cameo names: infantry use the first four letters of their name (%.4sICNH).</summary>
    static string CameoName(TechnoTypeClass t) => t is InfantryTypeClass && t.IniName.Length > 4 ? t.IniName[..4] : t.IniName;

    /// <summary>English names from TD's text numbers (the TXT_ constant names).</summary>
    sealed class TextNames
    {
        readonly Dictionary<int, string> names = new();

        public TextNames()
        {
            foreach (var f in typeof(TdDefines).GetFields())
                if (f.IsLiteral && f.Name.StartsWith("TXT_") && f.GetRawConstantValue() is int v && !names.ContainsKey(v))
                    names[v] = string.Join(' ', f.Name[4..].Split('_').Select(w => w.Length > 0 ? w[0] + w[1..].ToLowerInvariant() : w));
        }

        // The freeware files lack CONQUER.ENG (the game's text), so the TXT_ constant names are the fallback;
        // the playable objects get the names the game shows.
        static readonly Dictionary<string, string> Shown = new(StringComparer.OrdinalIgnoreCase)
        {
            ["E1"] = "Minigunner", ["E2"] = "Grenadier", ["E3"] = "Bazooka", ["E4"] = "Flamethrower", ["E5"] = "Chem Warrior",
            ["E6"] = "Engineer", ["RMBO"] = "Commando", ["APC"] = "APC", ["ARTY"] = "Artillery", ["BGGY"] = "Nod Buggy",
            ["BIKE"] = "Recon Bike", ["FTNK"] = "Flame Tank", ["HARV"] = "Harvester", ["HTNK"] = "Mammoth Tank", ["JEEP"] = "Hum-Vee",
            ["LTNK"] = "Light Tank", ["MTNK"] = "Medium Tank", ["MCV"] = "Mobile Construction Vehicle", ["MLRS"] = "SSM Launcher",
            ["MSAM"] = "Rocket Launcher", ["STNK"] = "Stealth Tank", ["HELI"] = "Apache", ["ORCA"] = "Orca", ["TRAN"] = "Chinook Transport",
            ["AFLD"] = "Airstrip", ["ATWR"] = "Advanced Guard Tower", ["GTWR"] = "Guard Tower", ["GUN"] = "Gun Turret",
            ["BRIK"] = "Concrete Wall", ["CYCL"] = "Chain Link Fence", ["SBAG"] = "Sandbag Wall", ["EYE"] = "Advanced Communications Center",
            ["HQ"] = "Communications Center", ["FACT"] = "Construction Yard", ["FIX"] = "Repair Facility", ["HAND"] = "Hand of Nod",
            ["HPAD"] = "Helipad", ["NUKE"] = "Power Plant", ["NUK2"] = "Advanced Power Plant", ["OBLI"] = "Obelisk of Light",
            ["PROC"] = "Tiberium Refinery", ["PYLE"] = "Barracks", ["SAM"] = "SAM Site", ["SILO"] = "Tiberium Silo",
            ["TMPL"] = "Temple of Nod", ["WEAP"] = "Weapons Factory",
        };

        public string Name(ObjectTypeClass t) =>
            Shown.TryGetValue(t.IniName, out var shown) ? shown : names.TryGetValue(t.Name, out var n) ? n : t.IniName;
    }
}
