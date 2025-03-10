// using Microsoft.Build.Framework;
// using Microsoft.Build.Utilities;
// using System.Reflection;
//
// namespace Ghost.Build;
//
// public class ValidateBuildTask : Task
// {
//     [Required]
//     public string TargetPath { get; set; }
//
//     [Required]
//     public string ProjectDir { get; set; }
//
//     public override bool Execute()
//     {
//         try
//         {
//             Log.LogMessage(MessageImportance.High, "Validating Ghost build...");
//
//             // Load the built assembly
//             var assembly = Assembly.LoadFrom(TargetPath);
//
//             // Find all command types
//             var commandTypes = assembly.GetTypes()
//                 .Where(t => !t.IsAbstract &&
//                            t.IsClass &&
//                            t.Namespace?.StartsWith("Ghost.Father.CLI.Commands") == true &&
//                            t.Name.EndsWith("Command"))
//                 .ToList();
//
//             Log.LogMessage(MessageImportance.Normal, $"Found {commandTypes.Count} commands to validate");
//
//             var success = true;
//
//             // Basic validation
//             foreach (var commandType in commandTypes)
//             {
//                 try
//                 {
//                     // Validate constructor
//                     var constructor = commandType.GetConstructors()
//                         .OrderByDescending(c => c.GetParameters().Length)
//                         .FirstOrDefault();
//
//                     if (constructor == null)
//                     {
//                         Log.LogError($"Command {commandType.Name} has no public constructor");
//                         success = false;
//                         continue;
//                     }
//
//                     // Validate Settings property
//                     var settingsProperty = commandType.GetProperty("Settings");
//                     if (settingsProperty != null)
//                     {
//                         var settingsType = settingsProperty.PropertyType;
//                         var properties = settingsType.GetProperties();
//
//                         foreach (var prop in properties)
//                         {
//                             var hasCommandArg = prop.GetCustomAttributes()
//                                 .Any(attr => attr.GetType().Name.Contains("CommandArgument"));
//                             var hasCommandOpt = prop.GetCustomAttributes()
//                                 .Any(attr => attr.GetType().Name.Contains("CommandOption"));
//
//                             if (!hasCommandArg && !hasCommandOpt)
//                             {
//                                 Log.LogWarning($"Property {prop.Name} in {settingsType.Name} lacks command attributes");
//                             }
//                         }
//                     }
//
//                     // Validate command is registered
//                     var isRegistered = assembly.GetTypes()
//                         .Any(t => t.Name == "CommandRegistry" &&
//                                  t.GetMethods(BindingFlags.Public | BindingFlags.Static)
//                                  .Any(m => m.Name == "GetCommands" &&
//                                          m.Invoke(null, null) is IEnumerable<object> commands &&
//                                          commands.Any(c => c.GetType()
//                                              .GetProperty("CommandType")
//                                              ?.GetValue(c) == commandType)));
//
//                     if (!isRegistered)
//                     {
//                         Log.LogError($"Command {commandType.Name} is not registered in CommandRegistry");
//                         success = false;
//                     }
//
//                     Log.LogMessage(MessageImportance.Low, $"Validated {commandType.Name}");
//                 }
//                 catch (Exception ex)
//                 {
//                     Log.LogError($"Error validating {commandType.Name}: {ex.Message}");
//                     success = false;
//                 }
//             }
//
//             if (success)
//             {
//                 Log.LogMessage(MessageImportance.High, "Ghost build validation succeeded");
//             }
//             else
//             {
//                 Log.LogError("Ghost build validation failed");
//             }
//
//             return success;
//         }
//         catch (Exception ex)
//         {
//             Log.LogError($"Build validation failed: {ex.Message}");
//             return false;
//         }
//     }
// }