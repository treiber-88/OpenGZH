namespace OpenGZH.Logic.Core;

/// <summary>
/// Every named flag list the rules use. The names and their order match the original game,
/// so the converted data reads exactly as it did there.
/// </summary>
public static class Flags
{
    public static readonly FlagNames KindOf = new("KindOf",
    [
        "OBSTACLE", "SELECTABLE", "IMMOBILE", "CAN_ATTACK", "STICK_TO_TERRAIN_SLOPE", "CAN_CAST_REFLECTIONS",
        "SHRUBBERY", "STRUCTURE", "INFANTRY", "VEHICLE", "AIRCRAFT", "HUGE_VEHICLE", "DOZER", "HARVESTER",
        "COMMANDCENTER", "PRISON", "COLLECTS_PRISON_BOUNTY", "POW_TRUCK", "LINEBUILD", "SALVAGER",
        "WEAPON_SALVAGER", "TRANSPORT", "BRIDGE", "LANDMARK_BRIDGE", "BRIDGE_TOWER", "PROJECTILE", "PRELOAD",
        "NO_GARRISON", "WAVEGUIDE", "WAVE_EFFECT", "NO_COLLIDE", "REPAIR_PAD", "HEAL_PAD", "STEALTH_GARRISON",
        "CASH_GENERATOR", "DRAWABLE_ONLY", "MP_COUNT_FOR_VICTORY", "REBUILD_HOLE", "SCORE", "SCORE_CREATE",
        "SCORE_DESTROY", "NO_HEAL_ICON", "CAN_RAPPEL", "PARACHUTABLE", "CAN_SURRENDER", "CAN_BE_REPULSED",
        "MOB_NEXUS", "IGNORED_IN_GUI", "CRATE", "CAPTURABLE", "CLEARED_BY_BUILD", "SMALL_MISSILE",
        "ALWAYS_VISIBLE", "UNATTACKABLE", "MINE", "CLEANUP_HAZARD", "PORTABLE_STRUCTURE", "ALWAYS_SELECTABLE",
        "ATTACK_NEEDS_LINE_OF_SIGHT", "WALK_ON_TOP_OF_WALL", "DEFENSIVE_WALL", "FS_POWER", "FS_FACTORY",
        "FS_BASE_DEFENSE", "FS_TECHNOLOGY", "AIRCRAFT_PATH_AROUND", "LOW_OVERLAPPABLE", "FORCEATTACKABLE",
        "AUTO_RALLYPOINT", "TECH_BUILDING", "POWERED", "PRODUCED_AT_HELIPAD", "DRONE",
        "CAN_SEE_THROUGH_STRUCTURE", "BALLISTIC_MISSILE", "CLICK_THROUGH", "SUPPLY_SOURCE_ON_PREVIEW",
        "PARACHUTE", "GARRISONABLE_UNTIL_DESTROYED", "BOAT", "IMMUNE_TO_CAPTURE", "HULK",
        "SHOW_PORTRAIT_WHEN_CONTROLLED", "SPAWNS_ARE_THE_WEAPONS", "CANNOT_BUILD_NEAR_SUPPLIES",
        "SUPPLY_SOURCE", "REVEAL_TO_ALL", "DISGUISER", "INERT", "HERO", "IGNORES_SELECT_ALL",
        "DONT_AUTO_CRUSH_INFANTRY", "CLIFF_JUMPER", "FS_SUPPLY_DROPZONE", "FS_SUPERWEAPON", "FS_BLACK_MARKET",
        "FS_SUPPLY_CENTER", "FS_STRATEGY_CENTER", "MONEY_HACKER", "ARMOR_SALVAGER", "REVEALS_ENEMY_PATHS",
        "BOOBY_TRAP", "FS_FAKE", "FS_INTERNET_CENTER", "BLAST_CRATER", "PROP", "OPTIMIZED_TREE",
        "FS_ADVANCED_TECH", "FS_BARRACKS", "FS_WARFACTORY", "FS_AIRFIELD", "AIRCRAFT_CARRIER", "NO_SELECT",
        "REJECT_UNMANNED", "CANNOT_RETALIATE", "TECH_BASE_DEFENSE", "EMP_HARDENED", "DEMOTRAP",
        "CONSERVATIVE_BUILDING", "IGNORE_DOCKING_BONES",
        // OpenGZH additions (naval): structures that must be placed at the shoreline.
        "SHIPYARD",
    ]);

    public static readonly FlagNames ModelCondition = new("ModelCondition",
    [
        "TOPPLED", "FRONTCRUSHED", "BACKCRUSHED", "DAMAGED", "REALLYDAMAGED", "RUBBLE", "SPECIAL_DAMAGED",
        "NIGHT", "SNOW", "PARACHUTING", "GARRISONED", "ENEMYNEAR", "WEAPONSET_VETERAN", "WEAPONSET_ELITE",
        "WEAPONSET_HERO", "WEAPONSET_CRATEUPGRADE_ONE", "WEAPONSET_CRATEUPGRADE_TWO", "WEAPONSET_PLAYER_UPGRADE",
        "DOOR_1_OPENING", "DOOR_1_CLOSING", "DOOR_1_WAITING_OPEN", "DOOR_1_WAITING_TO_CLOSE",
        "DOOR_2_OPENING", "DOOR_2_CLOSING", "DOOR_2_WAITING_OPEN", "DOOR_2_WAITING_TO_CLOSE",
        "DOOR_3_OPENING", "DOOR_3_CLOSING", "DOOR_3_WAITING_OPEN", "DOOR_3_WAITING_TO_CLOSE",
        "DOOR_4_OPENING", "DOOR_4_CLOSING", "DOOR_4_WAITING_OPEN", "DOOR_4_WAITING_TO_CLOSE",
        "ATTACKING", "PREATTACK_A", "FIRING_A", "BETWEEN_FIRING_SHOTS_A", "RELOADING_A",
        "PREATTACK_B", "FIRING_B", "BETWEEN_FIRING_SHOTS_B", "RELOADING_B",
        "PREATTACK_C", "FIRING_C", "BETWEEN_FIRING_SHOTS_C", "RELOADING_C",
        "TURRET_ROTATE", "POST_COLLAPSE", "MOVING", "DYING", "AWAITING_CONSTRUCTION", "PARTIALLY_CONSTRUCTED",
        "ACTIVELY_BEING_CONSTRUCTED", "PRONE", "FREEFALL", "ACTIVELY_CONSTRUCTING", "CONSTRUCTION_COMPLETE",
        "RADAR_EXTENDING", "RADAR_UPGRADED", "PANICKING", "AFLAME", "SMOLDERING", "BURNED", "DOCKING",
        "DOCKING_BEGINNING", "DOCKING_ACTIVE", "DOCKING_ENDING", "CARRYING", "FLOODED", "LOADED",
        "JETAFTERBURNER", "JETEXHAUST", "PACKING", "UNPACKING", "DEPLOYED", "OVER_WATER",
        "POWER_PLANT_UPGRADED", "CLIMBING", "SOLD", "SURRENDER", "RAPPELLING", "ARMED", "POWER_PLANT_UPGRADING",
        "SPECIAL_CHEERING", "CONTINUOUS_FIRE_SLOW", "CONTINUOUS_FIRE_MEAN", "CONTINUOUS_FIRE_FAST",
        "RAISING_FLAG", "CAPTURED", "EXPLODED_FLAILING", "EXPLODED_BOUNCING", "SPLATTED", "USING_WEAPON_A",
        "USING_WEAPON_B", "USING_WEAPON_C", "PREORDER", "CENTER_TO_LEFT", "LEFT_TO_CENTER", "CENTER_TO_RIGHT",
        "RIGHT_TO_CENTER", "RIDER1", "RIDER2", "RIDER3", "RIDER4", "RIDER5", "RIDER6", "RIDER7", "RIDER8",
        "STUNNED_FLAILING", "STUNNED", "SECOND_LIFE", "JAMMED", "ARMORSET_CRATEUPGRADE_ONE",
        "ARMORSET_CRATEUPGRADE_TWO", "USER_1", "USER_2", "DISGUISED",
    ]);

    public static readonly FlagNames ObjectStatus = new("ObjectStatus",
    [
        "NONE", "DESTROYED", "CAN_ATTACK", "UNDER_CONSTRUCTION", "UNSELECTABLE", "NO_COLLISIONS", "NO_ATTACK",
        "AIRBORNE_TARGET", "PARACHUTING", "REPULSOR", "HIJACKED", "AFLAME", "BURNED", "WET", "IS_FIRING_WEAPON",
        "IS_BRAKING", "STEALTHED", "DETECTED", "CAN_STEALTH", "SOLD", "UNDERGOING_REPAIR", "RECONSTRUCTING",
        "MASKED", "IS_ATTACKING", "USING_ABILITY", "IS_AIMING_WEAPON", "NO_ATTACK_FROM_AI", "IGNORING_STEALTH",
        "IS_CARBOMB", "DECK_HEIGHT_OFFSET", "STATUS_RIDER1", "STATUS_RIDER2", "STATUS_RIDER3", "STATUS_RIDER4",
        "STATUS_RIDER5", "STATUS_RIDER6", "STATUS_RIDER7", "STATUS_RIDER8", "FAERIE_FIRE", "KILLING_SELF",
        "REASSIGN_PARKING", "BOOBY_TRAPPED", "IMMOBILE", "DISGUISED", "DEPLOYED",
        // The original keeps these "script status" bits separately; here they are ordinary status bits.
        "SCRIPT_DISABLED", "SCRIPT_UNPOWERED", "SCRIPT_UNSELLABLE", "SCRIPT_UNSTEALTHED", "SCRIPT_TARGETABLE",
    ]);

    public static readonly FlagNames Disabled = new("Disabled",
    [
        "DEFAULT", "DISABLED_HACKED", "DISABLED_EMP", "DISABLED_HELD", "DISABLED_PARALYZED", "DISABLED_UNMANNED",
        "DISABLED_UNDERPOWERED", "DISABLED_FREEFALL", "DISABLED_AWESTRUCK", "DISABLED_BRAINWASHED",
        "DISABLED_SUBDUED", "DISABLED_SCRIPT_DISABLED", "DISABLED_SCRIPT_UNDERPOWERED",
    ]);

    public static readonly FlagNames WeaponSet = new("WeaponSet",
    [
        "VETERAN", "ELITE", "HERO", "PLAYER_UPGRADE", "CRATEUPGRADE_ONE", "CRATEUPGRADE_TWO", "VEHICLE_HIJACK",
        "CARBOMB", "MINE_CLEARING_DETAIL", "WEAPON_RIDER1", "WEAPON_RIDER2", "WEAPON_RIDER3", "WEAPON_RIDER4",
        "WEAPON_RIDER5", "WEAPON_RIDER6", "WEAPON_RIDER7", "WEAPON_RIDER8",
    ]);

    public static readonly FlagNames ArmorSet = new("ArmorSet",
    [
        "VETERAN", "ELITE", "HERO", "PLAYER_UPGRADE", "WEAK_VERSUS_BASEDEFENSES", "SECOND_LIFE",
        "CRATE_UPGRADE_ONE", "CRATE_UPGRADE_TWO",
    ]);

    public static readonly FlagNames DamageType = new("DamageType",
    [
        "EXPLOSION", "CRUSH", "ARMOR_PIERCING", "SMALL_ARMS", "GATTLING", "RADIATION", "FLAME", "LASER",
        "SNIPER", "POISON", "HEALING", "UNRESISTABLE", "WATER", "DEPLOY", "SURRENDER", "HACK", "KILL_PILOT",
        "PENALTY", "FALLING", "MELEE", "DISARM", "HAZARD_CLEANUP", "PARTICLE_BEAM", "TOPPLING",
        "INFANTRY_MISSILE", "AURORA_BOMB", "LAND_MINE", "JET_MISSILES", "STEALTHJET_MISSILES",
        "MOLOTOV_COCKTAIL", "COMANCHE_VULCAN", "SUBDUAL_MISSILE", "SUBDUAL_VEHICLE", "SUBDUAL_BUILDING",
        "SUBDUAL_UNRESISTABLE", "MICROWAVE", "KILL_GARRISONED", "STATUS",
    ]);

    public static readonly FlagNames DeathType = new("DeathType",
    [
        "NORMAL", "NONE", "CRUSHED", "BURNED", "EXPLODED", "POISONED", "TOPPLED", "FLOODED", "SUICIDED",
        "LASERED", "DETONATED", "SPLATTED", "POISONED_BETA", "EXTRA_2", "EXTRA_3", "EXTRA_4", "EXTRA_5",
        "EXTRA_6", "EXTRA_7", "EXTRA_8", "POISONED_GAMMA",
    ]);

    public static readonly FlagNames Veterancy = new("Veterancy", ["REGULAR", "VETERAN", "ELITE", "HEROIC"]);

    public static readonly FlagNames LocomotorSurface = new("LocomotorSurface", ["GROUND", "WATER", "CLIFF", "AIR", "RUBBLE"]);

    public static readonly FlagNames WeaponAffects = new("WeaponAffects",
        ["SELF", "ALLIES", "ENEMIES", "NEUTRALS", "SUICIDE", "NOT_SIMILAR", "NOT_AIRBORNE"]);

    public static readonly FlagNames WeaponCollides = new("WeaponCollides",
        ["ALLIES", "ENEMIES", "STRUCTURES", "SHRUBBERY", "PROJECTILES", "WALLS", "SMALL_MISSILES", "BALLISTIC_MISSILES", "CONTROLLED_STRUCTURES"]);

    /// <summary>Conditions that grant weapon bonuses (rules "WeaponBonus: VETERAN DAMAGE 110%").</summary>
    public static readonly FlagNames WeaponBonusCondition = new("WeaponBonusCondition",
    [
        "GARRISONED", "HORDE", "CONTINUOUS_FIRE_MEAN", "CONTINUOUS_FIRE_FAST", "NATIONALISM", "PLAYER_UPGRADE",
        "DRONE_SPOTTING", "DEMORALIZED", "DEMORALIZED_OBSOLETE", "ENTHUSIASTIC", "VETERAN", "ELITE", "HERO",
        "BATTLEPLAN_BOMBARDMENT", "BATTLEPLAN_HOLDTHELINE", "BATTLEPLAN_SEARCHANDDESTROY", "SUBLIMINAL",
        "SOLO_HUMAN_EASY", "SOLO_HUMAN_NORMAL", "SOLO_HUMAN_HARD", "SOLO_AI_EASY", "SOLO_AI_NORMAL", "SOLO_AI_HARD",
        "TARGET_FAERIE_FIRE", "FANATICISM", "FRENZY_ONE", "FRENZY_TWO", "FRENZY_THREE",
    ]);

    /// <summary>The body damage states, in increasing order of damage.</summary>
    public static readonly FlagNames BodyDamage = new("BodyDamage", ["PRISTINE", "DAMAGED", "REALLYDAMAGED", "RUBBLE"]);
}

/// <summary>Shortcuts for frequently tested flags, so code reads "Kind.Structure" instead of a lookup.</summary>
public static class Kind
{
    public static readonly int Structure = Flags.KindOf.IndexOf("STRUCTURE");
    public static readonly int Infantry = Flags.KindOf.IndexOf("INFANTRY");
    public static readonly int Vehicle = Flags.KindOf.IndexOf("VEHICLE");
    public static readonly int Aircraft = Flags.KindOf.IndexOf("AIRCRAFT");
    public static readonly int Boat = Flags.KindOf.IndexOf("BOAT");
    public static readonly int Dozer = Flags.KindOf.IndexOf("DOZER");
    public static readonly int Harvester = Flags.KindOf.IndexOf("HARVESTER");
    public static readonly int Projectile = Flags.KindOf.IndexOf("PROJECTILE");
    public static readonly int Selectable = Flags.KindOf.IndexOf("SELECTABLE");
    public static readonly int Immobile = Flags.KindOf.IndexOf("IMMOBILE");
    public static readonly int CanAttack = Flags.KindOf.IndexOf("CAN_ATTACK");
    public static readonly int CommandCenter = Flags.KindOf.IndexOf("COMMANDCENTER");
    public static readonly int Shrubbery = Flags.KindOf.IndexOf("SHRUBBERY");
    public static readonly int Obstacle = Flags.KindOf.IndexOf("OBSTACLE");
    public static readonly int Shipyard = Flags.KindOf.IndexOf("SHIPYARD");
    public static readonly int AircraftCarrier = Flags.KindOf.IndexOf("AIRCRAFT_CARRIER");
    public static readonly int Unattackable = Flags.KindOf.IndexOf("UNATTACKABLE");
    public static readonly int SupplySource = Flags.KindOf.IndexOf("SUPPLY_SOURCE");
    public static readonly int Bridge = Flags.KindOf.IndexOf("BRIDGE");
    public static readonly int Prop = Flags.KindOf.IndexOf("PROP");
    public static readonly int Crate = Flags.KindOf.IndexOf("CRATE");
    public static readonly int Mine = Flags.KindOf.IndexOf("MINE");
    public static readonly int Hulk = Flags.KindOf.IndexOf("HULK");
    public static readonly int Drone = Flags.KindOf.IndexOf("DRONE");
}

/// <summary>Shortcuts for object status flags.</summary>
public static class Status
{
    public static readonly int Destroyed = Flags.ObjectStatus.IndexOf("DESTROYED");
    public static readonly int UnderConstruction = Flags.ObjectStatus.IndexOf("UNDER_CONSTRUCTION");
    public static readonly int Unselectable = Flags.ObjectStatus.IndexOf("UNSELECTABLE");
    public static readonly int NoCollisions = Flags.ObjectStatus.IndexOf("NO_COLLISIONS");
    public static readonly int AirborneTarget = Flags.ObjectStatus.IndexOf("AIRBORNE_TARGET");
    public static readonly int Stealthed = Flags.ObjectStatus.IndexOf("STEALTHED");
    public static readonly int Detected = Flags.ObjectStatus.IndexOf("DETECTED");
    public static readonly int Sold = Flags.ObjectStatus.IndexOf("SOLD");
    public static readonly int IsFiringWeapon = Flags.ObjectStatus.IndexOf("IS_FIRING_WEAPON");
    public static readonly int IsAttacking = Flags.ObjectStatus.IndexOf("IS_ATTACKING");
    public static readonly int Reconstructing = Flags.ObjectStatus.IndexOf("RECONSTRUCTING");
    public static readonly int Aflame = Flags.ObjectStatus.IndexOf("AFLAME");
    public static readonly int Burned = Flags.ObjectStatus.IndexOf("BURNED");
    public static readonly int CanStealth = Flags.ObjectStatus.IndexOf("CAN_STEALTH");
    public static readonly int Disguised = Flags.ObjectStatus.IndexOf("DISGUISED");
    public static readonly int UsingAbility = Flags.ObjectStatus.IndexOf("USING_ABILITY");
    public static readonly int BoobyTrapped = Flags.ObjectStatus.IndexOf("BOOBY_TRAPPED");
    public static readonly int ScriptUnstealthed = Flags.ObjectStatus.IndexOf("SCRIPT_UNSTEALTHED");
}

/// <summary>Shortcuts for model condition flags the logic sets for the renderer.</summary>
public static class Condition
{
    public static readonly int Damaged = Flags.ModelCondition.IndexOf("DAMAGED");
    public static readonly int ReallyDamaged = Flags.ModelCondition.IndexOf("REALLYDAMAGED");
    public static readonly int Rubble = Flags.ModelCondition.IndexOf("RUBBLE");
    public static readonly int Moving = Flags.ModelCondition.IndexOf("MOVING");
    public static readonly int Dying = Flags.ModelCondition.IndexOf("DYING");
    public static readonly int Attacking = Flags.ModelCondition.IndexOf("ATTACKING");
    public static readonly int AwaitingConstruction = Flags.ModelCondition.IndexOf("AWAITING_CONSTRUCTION");
    public static readonly int PartiallyConstructed = Flags.ModelCondition.IndexOf("PARTIALLY_CONSTRUCTED");
    public static readonly int ActivelyBeingConstructed = Flags.ModelCondition.IndexOf("ACTIVELY_BEING_CONSTRUCTED");
    public static readonly int ActivelyConstructing = Flags.ModelCondition.IndexOf("ACTIVELY_CONSTRUCTING");
    public static readonly int ConstructionComplete = Flags.ModelCondition.IndexOf("CONSTRUCTION_COMPLETE");
    public static readonly int Night = Flags.ModelCondition.IndexOf("NIGHT");
    public static readonly int Snow = Flags.ModelCondition.IndexOf("SNOW");
    public static readonly int Garrisoned = Flags.ModelCondition.IndexOf("GARRISONED");
    public static readonly int Sold = Flags.ModelCondition.IndexOf("SOLD");
    public static readonly int OverWater = Flags.ModelCondition.IndexOf("OVER_WATER");
    public static readonly int Docking = Flags.ModelCondition.IndexOf("DOCKING");
    public static readonly int Carrying = Flags.ModelCondition.IndexOf("CARRYING");
    public static readonly int Toppled = Flags.ModelCondition.IndexOf("TOPPLED");
    public static readonly int Prone = Flags.ModelCondition.IndexOf("PRONE");

    public static int FiringA(char slot) => Flags.ModelCondition.IndexOf("FIRING_" + slot);
    public static int ReloadingA(char slot) => Flags.ModelCondition.IndexOf("RELOADING_" + slot);
    public static int BetweenShots(char slot) => Flags.ModelCondition.IndexOf("BETWEEN_FIRING_SHOTS_" + slot);
    public static int PreAttack(char slot) => Flags.ModelCondition.IndexOf("PREATTACK_" + slot);
    public static int UsingWeapon(char slot) => Flags.ModelCondition.IndexOf("USING_WEAPON_" + slot);
}
