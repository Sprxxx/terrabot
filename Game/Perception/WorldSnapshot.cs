using System.Collections.Generic;

namespace TerraBot.Game.Perception;

/// <summary>Serializable perception snapshot written by /bot dump. Plain DTOs so System.Text.Json handles them.</summary>
public sealed class WorldSnapshot
{
	public string Mod { get; set; }
	public uint Tick { get; set; }
	public string World { get; set; }
	public PlayerInfo Player { get; set; }
	public Core.Physics.PhysicsParams Physics { get; set; }
	public BotInfo Bot { get; set; }
	public TileWindow Tiles { get; set; }
	public List<NpcInfo> Npcs { get; set; } = new();
	public List<ItemInfo> Items { get; set; } = new();
	public List<SlotInfo> Inventory { get; set; } = new();
}

public sealed class PlayerInfo
{
	public string Name { get; set; }
	public float PosX { get; set; }
	public float PosY { get; set; }
	public float VelX { get; set; }
	public float VelY { get; set; }
	public int TileX { get; set; }
	public int TileY { get; set; }
	public int NodeX { get; set; }
	public int NodeY { get; set; }
	public bool Grounded { get; set; }
	public int Life { get; set; }
	public int LifeMax { get; set; }
	public int Mana { get; set; }
	public int ManaMax { get; set; }
	public int Direction { get; set; }
	public bool Wet { get; set; }
	public bool LavaWet { get; set; }
	public bool Dead { get; set; }
	public int SelectedItem { get; set; }
	public int Jump { get; set; }
	public bool ReleaseJump { get; set; }
}

public sealed class BotInfo
{
	public bool Enabled { get; set; }
	public string Target { get; set; }
	public string State { get; set; }
	public string LastResult { get; set; }
	public string LastPlan { get; set; }
	public int StepIndex { get; set; }
	public string Phase { get; set; }
	public string LastInput { get; set; }
	public string Tools { get; set; }
	public bool AllowDig { get; set; }
	public bool AllowBuild { get; set; }
	public List<string> Path { get; set; } = new();
}

/// <summary>
/// ASCII tile window around the player, top row first. Legend: '#' solid, '%' falling block (sand...), '=' platform, '.' air, '~' water,
/// '!' lava, 'h' honey, 'P' player body (node cells), 'T' target tile, 'g' goal feet tile.
/// </summary>
public sealed class TileWindow
{
	public int Left { get; set; }
	public int Top { get; set; }
	public int Width { get; set; }
	public int Height { get; set; }
	public List<string> Rows { get; set; } = new();
}

public sealed class NpcInfo
{
	public int WhoAmI { get; set; }
	public int Type { get; set; }
	public string Name { get; set; }
	public int TileX { get; set; }
	public int TileY { get; set; }
	public int Life { get; set; }
	public int LifeMax { get; set; }
	public bool Friendly { get; set; }
	public bool TownNpc { get; set; }
	public bool Boss { get; set; }
}

public sealed class ItemInfo
{
	public int WhoAmI { get; set; }
	public int Type { get; set; }
	public string Name { get; set; }
	public int Stack { get; set; }
	public int TileX { get; set; }
	public int TileY { get; set; }
}

public sealed class SlotInfo
{
	public int Slot { get; set; }
	public int Type { get; set; }
	public string Name { get; set; }
	public int Stack { get; set; }
}
