using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace TerraBot.Game;

/// <summary>
/// What an accessory is worth to the bot, in defence points (1 = one point of defence), for ranking what to wear in the five
/// slots (<see cref="TerrariaGameInfo.AccessoryScore"/>). Most accessory effects are code, not data (vanilla
/// Player.ApplyEquipFunctional per item type), so the effect's worth is this table; the item's own defence and its prefix
/// (vanilla Player.GrantPrefixBenefits, Player.cs:12278+: Hard..Warding +1..+4 defence, Precise/Lucky +2/+4 crit, Jagged..Menacing
/// +1..+4 % damage, Brisk..Quick +1..+4 % move speed, Wild..Violent melee speed, Arcane +20 mana) are added from the item itself.
/// Facts and sources: Desktop\Projekt\terrabot\docs\knowledge (topics/equipment-rules.md, game/items.tsv).
/// <para>
/// Worth nothing: the information accessories, which work from the bag (vanilla Player.RefreshInfoAccsFromItemType, Player.cs:11194+;
/// seen in game, world_wof E26: a Depth Meter in a slot through the Wall fight), and those that change how the body moves in ways
/// the bot's physics and jump library do not model (higher jumps, wall climbing, flight, reach), which would throw its planned jumps
/// off.
/// </para>
/// </summary>
public static class AccessoryValue
{
	/// <summary>An accessory not in the table: worth a little (most help), plus its defence and prefix.</summary>
	public const float Unknown = 1f;

	private static readonly Dictionary<int, float> ByType = new() {
		// Knockback immunity: knockbacks threw the bot off the hellbridge into lava (E11, E12, E24, 2026-10-17).
		[ItemID.CobaltShield] = 8f,
		[ItemID.ObsidianShield] = 10f,
		[ItemID.AnkhShield] = 12f,
		[ItemID.PaladinsShield] = 10f,
		[ItemID.FrozenTurtleShell] = 6f,
		// No fall damage.
		[ItemID.LuckyHorseshoe] = 5f,
		[ItemID.ObsidianHorseshoe] = 6f,
		// Life regeneration; shorter potion sickness; longer invincibility after a hit.
		[ItemID.BandofRegeneration] = 5f,
		[ItemID.ManaRegenerationBand] = 3f,
		[ItemID.PhilosophersStone] = 6f,
		[ItemID.CharmofMyths] = 9f,
		[ItemID.CrossNecklace] = 4f,
		[ItemID.StarVeil] = 5f,
		// Fire blocks, lava.
		[ItemID.ObsidianSkull] = 3f,
		[ItemID.ObsidianRose] = 3f,
		[ItemID.LavaCharm] = 4f,
		[ItemID.MoltenSkullRose] = 5f,
		// Run speed (the physics reads the run speeds every tick).
		[ItemID.HermesBoots] = 4f,
		[ItemID.FlurryBoots] = 4f,
		[ItemID.SailfishBoots] = 4f,
		[ItemID.SandBoots] = 4f,
		[ItemID.Aglet] = 2f,
		[ItemID.AnkletoftheWind] = 3f,
		// Damage and its helpers.
		[ItemID.SharkToothNecklace] = 2f,
		[ItemID.FeralClaws] = 1f,
		[ItemID.MagmaStone] = 1f,
		[ItemID.HoneyComb] = 2f,
		[ItemID.PanicNecklace] = 2f,
		[ItemID.StarCloak] = 2f,
		[ItemID.RangerEmblem] = 6f,
		[ItemID.AvengerEmblem] = 6f,
		[ItemID.WarriorEmblem] = 2f,
		[ItemID.SorcererEmblem] = 1f,
		[ItemID.SummonerEmblem] = 2f,
		// Breath under water (drowning in shafts through pockets: E24).
		[ItemID.DivingHelmet] = 2f,
		[ItemID.Flipper] = 1f,
		[ItemID.DivingGear] = 3f,
		// Double jumps: the bot does not plan them; harmless, worth their prefix.
		[ItemID.CloudinaBottle] = 0.5f,
		[ItemID.BlizzardinaBottle] = 0.5f,
		[ItemID.SandstorminaBottle] = 0.5f,
		[ItemID.FartinaJar] = 0.5f,
		[ItemID.TsunamiInABottle] = 0.5f,
		// Body physics the bot does not model: kept off.
		[ItemID.ShinyRedBalloon] = 0f,
		[ItemID.FrogLeg] = 0f,
		[ItemID.BalloonPufferfish] = 0f,
		[ItemID.BundleofBalloons] = 0f,
		[ItemID.CloudinaBalloon] = 0f,
		[ItemID.BlizzardinaBalloon] = 0f,
		[ItemID.SandstorminaBalloon] = 0f,
		[ItemID.FartInABalloon] = 0f,
		[ItemID.SharkronBalloon] = 0f,
		[ItemID.BlueHorseshoeBalloon] = 0f,
		[ItemID.WhiteHorseshoeBalloon] = 0f,
		[ItemID.YellowHorseshoeBalloon] = 0f,
		[ItemID.BalloonHorseshoeFart] = 0f,
		[ItemID.BalloonHorseshoeHoney] = 0f,
		[ItemID.BalloonHorseshoeSharkron] = 0f,
		[ItemID.ClimbingClaws] = 0f,
		[ItemID.ShoeSpikes] = 0f,
		[ItemID.TigerClimbingGear] = 0f,
		[ItemID.MasterNinjaGear] = 0f,
		[ItemID.Tabi] = 0f,
		[ItemID.BlackBelt] = 0f,
		[ItemID.RocketBoots] = 0f,
		[ItemID.SpectreBoots] = 0f,
		[ItemID.LightningBoots] = 0f,
		[ItemID.FrostsparkBoots] = 0f,
		[ItemID.TerrasparkBoots] = 0f,
		[ItemID.LavaWaders] = 0f,
		// Flight and gravity (review, 2026-10-17: a held jump in a kite becomes a carpet hover or a wing flight; Up flips gravity).
		[ItemID.FlyingCarpet] = 0f,
		[ItemID.GravityGlobe] = 0f,
		[ItemID.FairyBoots] = 0f,
		[ItemID.HellfireTreads] = 0f,
		[ItemID.EmpressFlightBooster] = 0f,
		[ItemID.ClothierVoodooDoll] = 0f,
		[ItemID.WaterWalkingBoots] = 0f,
		[ItemID.ObsidianWaterWalkingBoots] = 0f,
		[ItemID.IceSkates] = 0f,
		[ItemID.Toolbelt] = 0f,
		[ItemID.Toolbox] = 0f,
		[ItemID.ExtendoGrip] = 0f,
		[ItemID.PortableStool] = 0f,
		// Arrow damage +10 %, a fifth of the arrows kept; the aim reads the quiver's x1.1 speed (TerrariaBlockActions.ArrowSpeed).
		[ItemID.MagicQuiver] = 4f,
		[ItemID.MoltenQuiver] = 5f,
		[ItemID.StalkersQuiver] = 6f,
		// Mana: the bot casts nothing yet.
		[ItemID.NaturesGift] = 0.2f,
		[ItemID.BandofStarpower] = 0.2f,
		[ItemID.ManaFlower] = 0.2f,
		[ItemID.CelestialMagnet] = 0f,
		[ItemID.TreasureMagnet] = 0.5f,
	};

	/// <summary>Vanilla Player.RefreshInfoAccsFromItemType's item types (Player.cs:11194+): they work from the bag.</summary>
	private static readonly HashSet<int> WorksFromTheBag = new() {
		15, 707, 16, 708, 17, 709, 393, 18, 395, 3123, 3124, 5358, 5359, 5360, 5361, 3120, 3036, 3037, 3096, 3084, 3122, 3095, 3118,
		3099, 3121, 3102, 3119, 3619, 3611, 486, 2799, 2216, 3061, 5126, 3624, 4346, 4767, 5323, 5309, 5095,
	};

	/// <summary>
	/// Effects that do not add up (vanilla sets a flag or overwrites a value: two run-speed boots both set accRunSpeed, two shields
	/// both set noKnockback): accessories of one group are worth their table value only once; a second one is worth its prefix and
	/// defence alone (<see cref="GroupOf"/>).
	/// </summary>
	private static readonly Dictionary<int, int> Group = new() {
		[ItemID.HermesBoots] = 1, [ItemID.FlurryBoots] = 1, [ItemID.SailfishBoots] = 1, [ItemID.SandBoots] = 1,
		[ItemID.CobaltShield] = 2, [ItemID.ObsidianShield] = 2, [ItemID.AnkhShield] = 2, [ItemID.PaladinsShield] = 2,
		[ItemID.LuckyHorseshoe] = 3, [ItemID.ObsidianHorseshoe] = 3,
		[ItemID.ObsidianSkull] = 4, [ItemID.ObsidianRose] = 4, [ItemID.MoltenSkullRose] = 4, [ItemID.ObsidianShield] = 2,
		[ItemID.MagicQuiver] = 5, [ItemID.MoltenQuiver] = 5, [ItemID.StalkersQuiver] = 5,
		[ItemID.BandofRegeneration] = 6, [ItemID.ManaRegenerationBand] = 6, [ItemID.CharmofMyths] = 6,
		[ItemID.PhilosophersStone] = 7, [ItemID.CharmofMyths] = 7,
		[ItemID.DivingHelmet] = 8, [ItemID.DivingGear] = 8,
		[ItemID.CloudinaBottle] = 9, [ItemID.BlizzardinaBottle] = 9, [ItemID.SandstorminaBottle] = 9, [ItemID.FartinaJar] = 9, [ItemID.TsunamiInABottle] = 9,
	};

	/// <summary>The effect group of an accessory, 0 for none (its effect adds up with anything).</summary>
	public static int GroupOf(int type) => Group.TryGetValue(type, out int g) ? g : 0;

	/// <summary>The accessory's worth as worn: the table (or <see cref="Unknown"/>), plus its defence, plus its prefix.</summary>
	public static float Of(Item item) => Of(item, effectCounts: true);

	/// <summary>As <see cref="Of(Item)"/>; with <paramref name="effectCounts"/> false, only its defence and prefix (its group's effect is had already).</summary>
	public static float Of(Item item, bool effectCounts)
	{
		if (item == null || item.IsAir || !item.accessory || item.vanity || item.wingSlot > 0)
			return 0f; // wings: flight the bot does not model
		if (WorksFromTheBag.Contains(item.type))
			return 0f;
		float value = ByType.TryGetValue(item.type, out float v) ? v : Unknown;
		if (value <= 0f)
			return 0f; // kept off: its prefix does not buy the physics it changes
		return (effectCounts ? value : 0.1f) + item.defense + Prefix(item.prefix);
	}

	/// <summary>An accessory prefix's worth (vanilla Player.GrantPrefixBenefits, Player.cs:12278+).</summary>
	public static float Prefix(int prefix) => prefix switch {
		62 => 1f, 63 => 2f, 64 => 3f, 65 => 4f, // Hard, Guarding, Armored, Warding: defence
		67 => 1f, 68 => 2f,                     // Precise, Lucky: +2, +4 % crit
		69 => 0.8f, 70 => 1.6f, 71 => 2.4f, 72 => 3.2f, // Jagged..Menacing: +1..+4 % damage
		73 => 0.3f, 74 => 0.6f, 75 => 0.9f, 76 => 1.2f, // Brisk..Quick: +1..+4 % move speed
		77 => 0.1f, 78 => 0.2f, 79 => 0.3f, 80 => 0.4f, // Wild..Violent: melee speed
		_ => 0f,
	};
}
