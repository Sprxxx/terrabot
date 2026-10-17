using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace TerraBot.Game;

/// <summary>TerraBot's own settings (Settings > Mod Configuration > TerraBot), per player's client.</summary>
public sealed class TerraBotConfig : ModConfig
{
	public override ConfigScope Mode => ConfigScope.ClientSide;

	/// <summary>
	/// Journey: the bot trashes researched items it has no use for (ResearchHabit.TrashResearched), keeping the bag clean as a
	/// player with researched items does; whatever it needs later it duplicates. Works with or without a research mod.
	/// </summary>
	[DefaultValue(true)]
	public bool TrashResearchedItems { get; set; } = true;
}
