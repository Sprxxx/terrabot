using System;
using log4net;
using TerraBot.Bot;

namespace TerraBot.Game;

/// <summary>Routes bot logging to Mod.Logger, i.e. tModLoader-Logs/client.log lines tagged [TerraBot].</summary>
public sealed class ModLog : IBotLog
{
	private readonly ILog log;

	public ModLog(ILog log) => this.log = log;

	public void Info(string message) => log.Info(message);
	public void Warn(string message) => log.Warn(message);
	public void Error(string message, Exception ex = null) => log.Error(message, ex);
	public void Debug(string message) => log.Debug(message);
}
