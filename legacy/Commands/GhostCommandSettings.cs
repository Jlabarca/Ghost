using Spectre.Console.Cli;
using System.ComponentModel;

namespace Ghost.Legacy.Commands;

public class GhostCommandSettings : CommandSettings
{
  [CommandOption("--debug")]
  [Description("Print debug information including all commands being run.")]
  public bool Debug { get; set; }
}
