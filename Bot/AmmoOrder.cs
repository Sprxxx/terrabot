namespace TerraBot.Bot;

/// <summary>
/// Which arrows to shoot first, as a player sorts the ammo slots: vanilla fires the first matching ammo in slots 55-58,
/// then the rest of the inventory (Player.ChooseAmmo), so the order of the slots is the choice.
/// </summary>
public static class AmmoOrder
{
	/// <summary>
	/// Segments one pierce counts for: a worm's body lies in a line, so an arrow that pierces goes on into the next ones.
	/// Unholy arrows pierce 5 (Projectile.cs:954); 3 is what a shot along a curving worm was taken to hit, not more than a
	/// worm's line holds in a shot's flight.
	/// </summary>
	public const int PierceCounted = 3;

	/// <summary>
	/// An arrow's worth in a boss fight: its damage for each target it can hit (<paramref name="penetrate"/>: -1 for no
	/// limit). Wooden 5 × 1 = 5, flaming 7 × 1 = 7, unholy 12 × 3 = 36 (Item.cs case 47: damage 12).
	/// </summary>
	public static float BossValue(int damage, int penetrate) =>
		damage * (penetrate < 0 ? PierceCounted : System.Math.Clamp(penetrate, 1, PierceCounted));

	/// <summary>
	/// Away from a boss the cheapest arrows go first (by the item's value), keeping the good ones for the boss. Seen in game
	/// (measured run M10o): 60 unholy arrows in the last ammo slot, never fired; two Eater of Worlds fights lost on wooden
	/// arrows, the worm at 6636 and 4264 of 10050 life.
	/// </summary>
	public static float EverydayValue(int value) => -value;
}
