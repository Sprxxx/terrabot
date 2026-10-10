using Terraria.ModLoader;

namespace TerraBot;

/// <summary>Mod entry point. Everything interesting lives in Game/ (tModLoader glue), Bot/ and Core/ (pure logic).</summary>
public class TerraBotMod : Mod
{
	public static TerraBotMod Instance { get; private set; }

	public override void Load()
	{
		Instance = this;
		Logger.Info($"TerraBot {Version} loaded.");
	}

	public override void Unload()
	{
		Instance = null;
	}
}
