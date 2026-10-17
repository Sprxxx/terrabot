using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace TerraBot.Game;

/// <summary>
/// TerraBot's own settings (Settings > Mod Configuration > TerraBot), per player's client. The Journey ones are what the user
/// had from Upgraded Research (<see cref="ResearchFeatures"/>), on by default as the user had them; Classic is untouched.
/// </summary>
public sealed class TerraBotConfig : ModConfig
{
	public override ConfigScope Mode => ConfigScope.ClientSide;

	/// <summary>
	/// Journey: a picked-up item already researched is deleted, and the bot trashes researched stacks in the bag
	/// (ResearchHabit.TrashResearched); with the bot in auto mode only what it has no use for. Coins and prefixed items stay.
	/// </summary>
	[DefaultValue(true)]
	public bool TrashResearchedItems { get; set; } = true;

	/// <summary>Journey: a researched consumable (blocks, potions, life crystals, bombs, summons) is not used up.</summary>
	[DefaultValue(true)]
	public bool InfiniteResearchedConsumables { get; set; } = true;

	/// <summary>Journey: researched ammo (arrows, bullets) is not used up.</summary>
	[DefaultValue(true)]
	public bool InfiniteResearchedAmmo { get; set; } = true;

	/// <summary>Journey: a researched crafting station counts as in range anywhere.</summary>
	[DefaultValue(true)]
	public bool CraftFromResearchedStations { get; set; } = true;

	/// <summary>Journey: a shift-click on a researched item with the research menu open trashes it.</summary>
	[DefaultValue(true)]
	public bool ShiftClickTrashResearched { get; set; } = true;

	/// <summary>Journey: the tooltip shows how many more research takes.</summary>
	[DefaultValue(true)]
	public bool ShowResearchLeft { get; set; } = true;
}
