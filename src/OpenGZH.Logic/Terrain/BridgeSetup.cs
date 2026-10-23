using System.Numerics;
using OpenGZH.Formats.Maps;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Terrain;

/// <summary>Creates a map's bridges (the original's W3DBridgeBuffer::loadBridges and TerrainLogic::addBridgeToLogic).</summary>
public static class BridgeSetup
{
    const int FlagBridgePoint1 = 0x10, FlagBridgePoint2 = 0x20;

    public static BridgeTemplate? FindTemplate(GameLogic game, string name) =>
        game.Rules.Section("Bridges")[name] is { } node ? new BridgeTemplate(name, node) : null;

    /// <summary>
    /// Pairs of bridge points on the map become bridges: the span comes from the bridge model's
    /// BRIDGE_LEFT mesh width times BridgeScale, the logic object is a GenericBridge in the middle.
    /// </summary>
    public static void LoadMapBridges(GameLogic game, MapFile map)
    {
        var objects = map.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            var first = objects[i];
            if ((first.Flags & FlagBridgePoint1) == 0)
                continue;
            if (i + 1 >= objects.Count)
                break;
            var second = objects[i + 1];
            if ((second.Flags & FlagBridgePoint2) == 0)
                continue;
            i++;

            var from = first.Position;
            from.Z = game.Terrain.GroundHeight(from) + BridgeList.BridgeFloatAmount;
            var to = second.Position;
            to.Z = game.Terrain.GroundHeight(to) + BridgeList.BridgeFloatAmount;
            AddBridge(game, from, to, first.TemplateName, first);
        }
    }

    static void AddBridge(GameLogic game, Vector3 from, Vector3 to, string templateName, MapObject mapObject)
    {
        var template = FindTemplate(game, templateName);
        if (template == null || game.Bones.Library is not { } library)
            return;
        var model = Art.BridgeModel.Load(library, template.Look(Modules.BodyDamageState.Pristine).Model);
        if (model == null)
            return;

        // The original's W3DBridge::getBridgeInfo.
        float scale = template.BridgeScale;
        var info = new BridgeInfo { From = from, To = to, BridgeWidth = (model.MaxY - model.MinY) * scale };
        var v = to - from;
        var normal = new Vector3(-v.Y, v.X, 0);
        if (normal.LengthSquared() > 0)
            normal = Vector3.Normalize(normal);
        info.FromLeft = from + normal * model.MaxY * scale;
        info.FromRight = from + normal * model.MinY * scale;
        info.ToLeft = to + normal * model.MaxY * scale;
        info.ToRight = to + normal * model.MinY * scale;
        info.BridgeIndex = game.Terrain.Bridges.All.Count;

        // The bridge's logic object (the original's Bridge constructor).
        if (game.Templates.Find("GenericBridge") is { } genericBridge)
        {
            var center = (info.FromLeft + info.ToRight) / 2;
            var bridgeObj = game.CreateObject(genericBridge, game.Players.Neutral, center, MathF.Atan2(info.ToLeft.Y - info.FromLeft.Y, info.ToLeft.X - info.FromLeft.X));
            info.BridgeObjectId = bridgeObj.Id;
            GameSetup.ApplyMapProperties(bridgeObj, mapObject);
            game.TreatAsBuilt(bridgeObj);
        }
        game.Terrain.Bridges.Add(info, templateName);
    }

    /// <summary>A landmark bridge object becomes a bridge layer, with its four towers (the original's addLandmarkBridgeToLogic).</summary>
    public static void AddLandmarkBridge(GameLogic game, GameObject bridgeObj)
    {
        var info = Bridge.InfoFromObject(bridgeObj);
        info.BridgeIndex = game.Terrain.Bridges.All.Count;
        var template = FindTemplate(game, bridgeObj.Template.Name);
        if (template != null)
        {
            var across = new Vector2(info.ToLeft.X - info.ToRight.X, info.ToLeft.Y - info.ToRight.Y);
            if (across.LengthSquared() > 0)
                across = Vector2.Normalize(across);
            var corners = new[] { info.FromLeft, info.FromRight, info.ToLeft, info.ToRight };
            for (int i = 0; i < 4; i++)
            {
                var type = (BridgeTowerType)i;
                var towerTemplate = game.Templates.Find(template.TowerObjectName(type));
                if (towerTemplate == null)
                    continue;
                float offset = towerTemplate.GeometryMajorRadius;
                var pos = corners[i];
                var shift = new Vector3(across.X * offset, across.Y * offset, 0);
                pos += type is BridgeTowerType.FromLeft or BridgeTowerType.ToLeft ? shift : -shift;
                float angle = type is BridgeTowerType.FromLeft or BridgeTowerType.FromRight ? bridgeObj.Orientation + MathF.PI : bridgeObj.Orientation;
                var tower = game.CreateObject(towerTemplate, bridgeObj.Owner, pos, angle, bridgeObj.Team);
                game.TreatAsBuilt(tower);
                bridgeObj.FindModule<Modules.Behaviors.BridgeBehavior>()?.SetTower(type, tower);
                if (tower.FindModule<Modules.Behaviors.BridgeTowerBehavior>() is { } towerBehavior)
                {
                    towerBehavior.SetBridge(bridgeObj);
                    towerBehavior.TowerType = type;
                }
                if (bridgeObj.Body is { Indestructible: true } && tower.Body != null)
                    tower.Body.Indestructible = true;
                info.TowerObjectIds[i] = tower.Id;
            }
        }
        game.Terrain.Bridges.Add(info, bridgeObj.Template.Name);
    }
}
