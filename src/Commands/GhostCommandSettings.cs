using Spectre.Console.Cli;
using System.ComponentModel;
namespace Ghost.Commands;

public abstract class GhostCommandSettings : CommandSettings 
{
  [CommandOption("--debug")]
  [Description("Enable debug logging")]
  public bool Debug { get; set; }

  [CommandOption("--config <PATH>")]
  [Description("Path to config file")]
  public string ConfigPath { get; set; }
}
