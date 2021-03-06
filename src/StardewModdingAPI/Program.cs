﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
#if SMAPI_FOR_WINDOWS
using System.Windows.Forms;
#endif
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json;
using StardewModdingAPI.AssemblyRewriters;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.AssemblyRewriting;
using StardewModdingAPI.Inheritance;
using StardewValley;
using Monitor = StardewModdingAPI.Framework.Monitor;

namespace StardewModdingAPI
{
    /// <summary>The main entry point for SMAPI, responsible for hooking into and launching the game.</summary>
    public class Program
    {
        /*********
        ** Properties
        *********/
        /// <summary>The target game platform.</summary>
        private static readonly Platform TargetPlatform =
#if SMAPI_FOR_WINDOWS
        Platform.Windows;
#else
        Platform.Mono;
#endif

        /// <summary>The full path to the Stardew Valley executable.</summary>
        private static readonly string GameExecutablePath = Path.Combine(Constants.ExecutionPath, Program.TargetPlatform == Platform.Windows ? "Stardew Valley.exe" : "StardewValley.exe");

        /// <summary>The full path to the folder containing mods.</summary>
        private static readonly string ModPath = Path.Combine(Constants.ExecutionPath, "Mods");

        /// <summary>The name of the folder containing a mod's cached assembly data.</summary>
        private static readonly string CacheDirName = ".cache";

        /// <summary>The log file to which to write messages.</summary>
        private static readonly LogFileManager LogFile = new LogFileManager(Constants.LogPath);

        /// <summary>The core logger for SMAPI.</summary>
        private static readonly Monitor Monitor = new Monitor("SMAPI", Program.LogFile);

        /// <summary>Whether SMAPI is running in developer mode.</summary>
        private static bool DeveloperMode;

        /// <summary>Tracks whether the game should exit immediately and any pending initialisation should be cancelled.</summary>
        private static readonly CancellationTokenSource CancellationTokenSource = new CancellationTokenSource();


        /*********
        ** Accessors
        *********/
        /// <summary>The number of mods currently loaded by SMAPI.</summary>
        public static int ModsLoaded;

        /// <summary>The underlying game instance.</summary>
        public static SGame gamePtr;

        /// <summary>Whether the game is currently running.</summary>
        public static bool ready;

        /// <summary>The underlying game assembly.</summary>
        public static Assembly StardewAssembly;

        /// <summary>The underlying <see cref="StardewValley.Program"/> type.</summary>
        public static Type StardewProgramType;

        /// <summary>The field containing game's main instance.</summary>
        public static FieldInfo StardewGameInfo;

        // ReSharper disable once PossibleNullReferenceException
        /// <summary>The game's build type (i.e. GOG vs Steam).</summary>
        public static int BuildType => (int)Program.StardewProgramType.GetField("buildType", BindingFlags.Public | BindingFlags.Static).GetValue(null);

        /// <summary>Tracks the installed mods.</summary>
        internal static readonly ModRegistry ModRegistry = new ModRegistry();

        /// <summary>Manages deprecation warnings.</summary>
        internal static readonly DeprecationManager DeprecationManager = new DeprecationManager(Program.Monitor, Program.ModRegistry);


        /*********
        ** Public methods
        *********/
        /// <summary>The main entry point which hooks into and launches the game.</summary>
        private static void Main()
        {
            // set thread culture for consistent log formatting
            Thread.CurrentThread.CurrentCulture = CultureInfo.CreateSpecificCulture("en-GB");

            // add info header
            Program.Monitor.Log($"SMAPI {Constants.Version} with Stardew Valley {Game1.version} on {Environment.OSVersion}", LogLevel.Info);

            // load user settings
            {
                string settingsFileName = $"{typeof(Program).Assembly.GetName().Name}-settings.json";
                string settingsPath = Path.Combine(Constants.ExecutionPath, settingsFileName);
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    UserSettings settings = JsonConvert.DeserializeObject<UserSettings>(json);
                    Program.DeveloperMode = settings?.DeveloperMode == true;

                    if (Program.DeveloperMode)
                    {
                        Program.Monitor.ShowTraceInConsole = true;
                        Program.Monitor.Log($"SMAPI is running in developer mode. The console may be much more verbose. You can disable developer mode by deleting the {settingsFileName} file in the game directory.", LogLevel.Alert);
                    }
                }
            }

            // initialise legacy log
            Log.Monitor = new Monitor("legacy mod", Program.LogFile) { ShowTraceInConsole = Program.DeveloperMode };
            Log.ModRegistry = Program.ModRegistry;

            // hook into & launch the game
            try
            {
                // verify version
                if (String.Compare(Game1.version, Constants.MinimumGameVersion, StringComparison.InvariantCultureIgnoreCase) < 0)
                {
                    Program.Monitor.Log($"Oops! You're running Stardew Valley {Game1.version}, but the oldest supported version is {Constants.MinimumGameVersion}. Please update your game before using SMAPI. If you're on the Steam beta channel, note that the beta channel may not receive the latest updates.", LogLevel.Error);
                    return;
                }

                // initialise
                Program.Monitor.Log("Loading SMAPI...");
                Console.Title = Constants.ConsoleTitle;
                Program.VerifyPath(Program.ModPath);
                Program.VerifyPath(Constants.LogDir);
                if (!File.Exists(Program.GameExecutablePath))
                {
                    Program.Monitor.Log($"Couldn't find executable: {Program.GameExecutablePath}", LogLevel.Error);
                    Program.PressAnyKeyToExit();
                    return;
                }

                // check for update when game loads
                GameEvents.GameLoaded += (sender, e) => Program.CheckForUpdateAsync();

                // launch game
                Program.StartGame();
            }
            catch (Exception ex)
            {
                Program.Monitor.Log($"Critical error: {ex.GetLogSummary()}", LogLevel.Error);
            }
            Program.PressAnyKeyToExit();
        }

        /// <summary>Immediately exit the game without saving. This should only be invoked when an irrecoverable fatal error happens that risks save corruption or game-breaking bugs.</summary>
        /// <param name="module">The module which requested an immediate exit.</param>
        /// <param name="reason">The reason provided for the shutdown.</param>
        internal static void ExitGameImmediately(string module, string reason)
        {
            Program.Monitor.LogFatal($"{module} requested an immediate game shutdown: {reason}");
            Program.CancellationTokenSource.Cancel();
            if (Program.ready)
            {
                Program.gamePtr.Exiting += (sender, e) => Program.PressAnyKeyToExit();
                Program.gamePtr.Exit();
            }
        }

        /// <summary>Get a monitor for legacy code which doesn't have one passed in.</summary>
        [Obsolete("This method should only be used when needed for backwards compatibility.")]
        internal static IMonitor GetLegacyMonitorForMod()
        {
            string modName = Program.ModRegistry.GetModFromStack() ?? "unknown";
            return new Monitor(modName, Program.LogFile);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Asynchronously check for a new version of SMAPI, and print a message to the console if an update is available.</summary>
        private static void CheckForUpdateAsync()
        {
            new Thread(() =>
            {
                try
                {
                    GitRelease release = UpdateHelper.GetLatestVersionAsync(Constants.GitHubRepository).Result;
                    Version latestVersion = new Version(release.Tag);
                    if (latestVersion.IsNewerThan(Constants.Version))
                        Program.Monitor.Log($"You can update SMAPI from version {Constants.Version} to {latestVersion}", LogLevel.Alert);
                }
                catch (Exception ex)
                {
                    Program.Monitor.Log($"Couldn't check for a new version of SMAPI. This won't affect your game, but you may not be notified of new versions if this keeps happening.\n{ex.GetLogSummary()}");
                }
            }).Start();
        }

        /// <summary>Hook into Stardew Valley and launch the game.</summary>
        private static void StartGame()
        {
            try
            {
                // load the game assembly
                Program.Monitor.Log("Loading game...");
                Program.StardewAssembly = Assembly.UnsafeLoadFrom(Program.GameExecutablePath);
                Program.StardewProgramType = Program.StardewAssembly.GetType("StardewValley.Program", true);
                Program.StardewGameInfo = Program.StardewProgramType.GetField("gamePtr");
                Game1.version += $"-Z_MODDED | SMAPI {Constants.Version}";

                // add error interceptors
#if SMAPI_FOR_WINDOWS
                Application.ThreadException += (sender, e) => Program.Monitor.Log($"Critical thread exception: {e.Exception.GetLogSummary()}", LogLevel.Error);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
#endif
                AppDomain.CurrentDomain.UnhandledException += (sender, e) => Program.Monitor.Log($"Critical app domain exception: {e.ExceptionObject}", LogLevel.Error);

                // initialise game instance
                Program.gamePtr = new SGame(Program.Monitor) { IsMouseVisible = false };
                Program.gamePtr.Exiting += (sender, e) => Program.ready = false;
                Program.gamePtr.Window.ClientSizeChanged += (sender, e) => GraphicsEvents.InvokeResize(Program.Monitor, sender, e);
                Program.gamePtr.Window.Title = $"Stardew Valley - Version {Game1.version}";
                Program.StardewGameInfo.SetValue(Program.StardewProgramType, Program.gamePtr);

                // patch graphics
                Game1.graphics.GraphicsProfile = GraphicsProfile.HiDef;

                // load mods
                Program.LoadMods();
                if (Program.CancellationTokenSource.IsCancellationRequested)
                {
                    Program.Monitor.Log("Shutdown requested; interrupting initialisation.", LogLevel.Error);
                    return;
                }

                // initialise console after game launches
                new Thread(() =>
                {
                    // wait for the game to load up
                    while (!Program.ready) Thread.Sleep(1000);

                    // register help command
                    Command.RegisterCommand("help", "Lists all commands | 'help <cmd>' returns command description").CommandFired += Program.help_CommandFired;

                    // listen for command line input
                    Program.Monitor.Log("Starting console...");
                    Program.Monitor.Log("Type 'help' for help, or 'help <cmd>' for a command's usage", LogLevel.Info);
                    Thread consoleInputThread = new Thread(Program.ConsoleInputLoop);
                    consoleInputThread.Start();
                    while (Program.ready)
                        Thread.Sleep(1000 / 10); // Check if the game is still running 10 times a second

                    // abort the console thread, we're closing
                    if (consoleInputThread.ThreadState == ThreadState.Running)
                        consoleInputThread.Abort();
                }).Start();

                // start game loop
                Program.Monitor.Log("Starting game...");
                if (Program.CancellationTokenSource.IsCancellationRequested)
                {
                    Program.Monitor.Log("Shutdown requested; interrupting initialisation.", LogLevel.Error);
                    return;
                }
                try
                {
                    Program.ready = true;
                    Program.gamePtr.Run();
                }
                finally
                {
                    Program.ready = false;
                }
            }
            catch (Exception ex)
            {
                Program.Monitor.Log($"SMAPI encountered a fatal error:\n{ex.GetLogSummary()}", LogLevel.Error);
            }
        }

        /// <summary>Create a directory path if it doesn't exist.</summary>
        /// <param name="path">The directory path.</param>
        private static void VerifyPath(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                Program.Monitor.Log($"Couldn't create a path: {path}\n\n{ex.GetLogSummary()}", LogLevel.Error);
            }
        }

        /// <summary>Load and hook up all mods in the mod directory.</summary>
        private static void LoadMods()
        {
            Program.Monitor.Log("Loading mods...");

            // get assembly loader
            ModAssemblyLoader modAssemblyLoader = new ModAssemblyLoader(Program.CacheDirName, Program.TargetPlatform, Program.Monitor);
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => modAssemblyLoader.ResolveAssembly(e.Name);

            // load mods
            foreach (string directory in Directory.GetDirectories(Program.ModPath))
            {
                // ignore internal directory
                if (new DirectoryInfo(directory).Name == ".cache")
                    continue;

                // check for cancellation
                if (Program.CancellationTokenSource.IsCancellationRequested)
                {
                    Program.Monitor.Log("Shutdown requested; interrupting mod loading.", LogLevel.Error);
                    return;
                }

                // get helper
                IModHelper helper = new ModHelper(directory);

                // get manifest path
                string manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    Program.Monitor.Log($"Ignored folder \"{new DirectoryInfo(directory).Name}\" which doesn't have a manifest.json.", LogLevel.Warn);
                    continue;
                }
                string errorPrefix = $"Couldn't load mod for manifest '{manifestPath}'";

                // read manifest
                Manifest manifest;
                try
                {
                    // read manifest text
                    string json = File.ReadAllText(manifestPath);
                    if (string.IsNullOrEmpty(json))
                    {
                        Program.Monitor.Log($"{errorPrefix}: manifest is empty.", LogLevel.Error);
                        continue;
                    }

                    // deserialise manifest
                    manifest = helper.ReadJsonFile<Manifest>("manifest.json");
                    if (manifest == null)
                    {
                        Program.Monitor.Log($"{errorPrefix}: the manifest file does not exist.", LogLevel.Error);
                        continue;
                    }
                    if (string.IsNullOrEmpty(manifest.EntryDll))
                    {
                        Program.Monitor.Log($"{errorPrefix}: manifest doesn't specify an entry DLL.", LogLevel.Error);
                        continue;
                    }

                    // log deprecated fields
                    if (manifest.UsedAuthourField)
                        Program.DeprecationManager.Warn(manifest.Name, $"{nameof(Manifest)}.{nameof(Manifest.Authour)}", "1.0", DeprecationLevel.Notice);
                }
                catch (Exception ex)
                {
                    Program.Monitor.Log($"{errorPrefix}: manifest parsing failed.\n{ex.GetLogSummary()}", LogLevel.Error);
                    continue;
                }

                // validate version
                if (!string.IsNullOrWhiteSpace(manifest.MinimumApiVersion))
                {
                    try
                    {
                        Version minVersion = new Version(manifest.MinimumApiVersion);
                        if (minVersion.IsNewerThan(Constants.Version))
                        {
                            Program.Monitor.Log($"{errorPrefix}: this mod requires SMAPI {minVersion} or later. Please update SMAPI to the latest version to use this mod.", LogLevel.Error);
                            continue;
                        }
                    }
                    catch (FormatException ex) when (ex.Message.Contains("not a semantic version"))
                    {
                        Program.Monitor.Log($"{errorPrefix}: the mod specified an invalid minimum SMAPI version '{manifest.MinimumApiVersion}'. This should be a semantic version number like {Constants.Version}.", LogLevel.Error);
                        continue;
                    }
                }

                // create per-save directory
                if (manifest.PerSaveConfigs)
                {
                    Program.DeprecationManager.Warn(manifest.Name, $"{nameof(Manifest)}.{nameof(Manifest.PerSaveConfigs)}", "1.0", DeprecationLevel.Notice);
                    try
                    {
                        string psDir = Path.Combine(directory, "psconfigs");
                        Directory.CreateDirectory(psDir);
                        if (!Directory.Exists(psDir))
                        {
                            Program.Monitor.Log($"{errorPrefix}: couldn't create the per-save configuration directory ('psconfigs') requested by this mod. The failure reason is unknown.", LogLevel.Error);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Monitor.Log($"{errorPrefix}: couldn't create the per-save configuration directory ('psconfigs') requested by this mod.\n{ex.GetLogSummary()}", LogLevel.Error);
                        continue;
                    }
                }

                // preprocess mod assemblies for compatibility
                var processedAssemblies = new List<RewriteResult>();
                {
                    bool succeeded = true;
                    foreach (string assemblyPath in Directory.GetFiles(directory, "*.dll"))
                    {
                        try
                        {
                            processedAssemblies.Add(modAssemblyLoader.ProcessAssemblyUnlessCached(assemblyPath));
                        }
                        catch (Exception ex)
                        {
                            Program.Monitor.Log($"{errorPrefix}: an error occurred while preprocessing '{Path.GetFileName(assemblyPath)}'.\n{ex.GetLogSummary()}", LogLevel.Error);
                            succeeded = false;
                            break;
                        }
                    }
                    if (!succeeded)
                        continue;
                }
                bool forceUseCachedAssembly = processedAssemblies.Any(p => p.UseCachedAssembly); // make sure DLLs are kept together for dependency resolution
                if (processedAssemblies.Any(p => p.IsNewerThanCache))
                    modAssemblyLoader.WriteCache(processedAssemblies, forceUseCachedAssembly);

                // get entry assembly path
                string mainAssemblyPath;
                {
                    RewriteResult mainProcessedAssembly = processedAssemblies.FirstOrDefault(p => p.OriginalAssemblyPath == Path.Combine(directory, manifest.EntryDll));
                    if (mainProcessedAssembly == null)
                    {
                        Program.Monitor.Log($"{errorPrefix}: the specified mod DLL does not exist.", LogLevel.Error);
                        continue;
                    }
                    mainAssemblyPath = forceUseCachedAssembly ? mainProcessedAssembly.CachePaths.Assembly : mainProcessedAssembly.OriginalAssemblyPath;
                }

                // load entry assembly
                Assembly modAssembly;
                try
                {
                    modAssembly = Assembly.UnsafeLoadFrom(mainAssemblyPath); // unsafe load allows downloaded DLLs
                    if (modAssembly.DefinedTypes.Count(x => x.BaseType == typeof(Mod)) == 0)
                    {
                        Program.Monitor.Log($"{errorPrefix}: the mod DLL does not contain an implementation of the 'Mod' class.", LogLevel.Error);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Program.Monitor.Log($"{errorPrefix}: an error occurred while optimising the target DLL.\n{ex.GetLogSummary()}", LogLevel.Error);
                    continue;
                }

                // hook up mod
                try
                {
                    TypeInfo modEntryType = modAssembly.DefinedTypes.First(x => x.BaseType == typeof(Mod));
                    Mod modEntry = (Mod)modAssembly.CreateInstance(modEntryType.ToString());
                    if (modEntry != null)
                    {
                        // track mod
                        Program.ModRegistry.Add(manifest, modAssembly);

                        // hook up mod
                        modEntry.Manifest = manifest;
                        modEntry.Helper = helper;
                        modEntry.Monitor = new Monitor(manifest.Name, Program.LogFile) { ShowTraceInConsole = Program.DeveloperMode };
                        modEntry.PathOnDisk = directory;
                        Program.Monitor.Log($"Loaded mod: {modEntry.Manifest.Name} by {modEntry.Manifest.Author}, v{modEntry.Manifest.Version} | {modEntry.Manifest.Description}", LogLevel.Info);
                        Program.ModsLoaded += 1;
                        modEntry.Entry(); // deprecated since 1.0
                        modEntry.Entry((ModHelper)modEntry.Helper); // deprecated since 1.1
                        modEntry.Entry(modEntry.Helper); // deprecated since 1.1

                        // raise deprecation warning for old Entry() method
                        if (Program.DeprecationManager.IsVirtualMethodImplemented(modEntryType, typeof(Mod), nameof(Mod.Entry), new[] { typeof(object[]) }))
                            Program.DeprecationManager.Warn(manifest.Name, $"an old version of {nameof(Mod)}.{nameof(Mod.Entry)}", "1.0", DeprecationLevel.Notice);
                        if (Program.DeprecationManager.IsVirtualMethodImplemented(modEntryType, typeof(Mod), nameof(Mod.Entry), new[] { typeof(ModHelper) }))
                            Program.DeprecationManager.Warn(manifest.Name, $"an old version of {nameof(Mod)}.{nameof(Mod.Entry)}", "1.1", DeprecationLevel.Notice);
                    }
                }
                catch (Exception ex)
                {
                    Program.Monitor.Log($"{errorPrefix}: an error occurred while loading the target DLL.\n{ex.GetLogSummary()}", LogLevel.Error);
                }
            }

            // print result
            Program.Monitor.Log($"Loaded {Program.ModsLoaded} mods.");
            Console.Title = Constants.ConsoleTitle;
        }

        // ReSharper disable once FunctionNeverReturns
        /// <summary>Run a loop handling console input.</summary>
        private static void ConsoleInputLoop()
        {
            while (true)
                Command.CallCommand(Console.ReadLine(), Program.Monitor);
        }

        /// <summary>The method called when the user submits the help command in the console.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private static void help_CommandFired(object sender, EventArgsCommand e)
        {
            if (e.Command.CalledArgs.Length > 0)
            {
                var command = Command.FindCommand(e.Command.CalledArgs[0]);
                if (command == null)
                    Program.Monitor.Log("The specified command could't be found", LogLevel.Error);
                else
                    Program.Monitor.Log(command.CommandArgs.Length > 0 ? $"{command.CommandName}: {command.CommandDesc} - {string.Join(", ", command.CommandArgs)}" : $"{command.CommandName}: {command.CommandDesc}", LogLevel.Info);
            }
            else
                Program.Monitor.Log("Commands: " + string.Join(", ", Command.RegisteredCommands.Select(x => x.CommandName)), LogLevel.Info);
        }

        /// <summary>Show a 'press any key to exit' message, and exit when they press a key.</summary>
        private static void PressAnyKeyToExit()
        {
            Program.Monitor.Log("Game has ended. Press any key to exit.", LogLevel.Info);
            Thread.Sleep(100);
            Console.ReadKey();
            Environment.Exit(0);
        }
    }
}
