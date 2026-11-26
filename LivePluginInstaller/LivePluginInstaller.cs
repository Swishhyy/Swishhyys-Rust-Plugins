using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("LivePluginInstaller", "Swishhyy", "0.1.0")]
    [Description("Installs and reloads plugins from oxide/plugin-staging on a live server, without a restart")]
    public class LivePluginInstaller : RustPlugin
    {
        #region Constants

        private const string PermAdmin = "liveplugininstaller.admin";
        private const string SelfName = "LivePluginInstaller";
        private const float ReportDelay = 3f;   // seconds to let Oxide finish compiling before checking results
        private static readonly Regex ValidName = new Regex("^[A-Za-z0-9_]{1,64}$");

        #endregion

        #region Paths

        // Drop .cs files in here (FTP, SFTP, or a git pull on the server).
        private string StagingDir => Path.Combine(Interface.Oxide.RootDirectory, "plugin-staging");
        private string PluginsDir => Interface.Oxide.PluginDirectory;

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            AddCovalenceCommand("lpi", nameof(LpiCommand));

            try
            {
                Directory.CreateDirectory(StagingDir);
            }
            catch (Exception ex)
            {
                PrintError($"Could not create staging folder {StagingDir}: {ex.Message}");
            }
        }

        #endregion

        #region Commands

        private void LpiCommand(IPlayer caller, string command, string[] args)
        {
            if (!caller.IsServer && !caller.HasPermission(PermAdmin))
            {
                caller.Reply("You don't have permission to use this command.");
                return;
            }

            string action = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            switch (action)
            {
                case "list":
                    caller.Reply(BuildList());
                    return;

                case "install":
                    Install(caller);
                    return;

                default:
                    caller.Reply("Usage: lpi list | lpi install");
                    return;
            }
        }

        // Shows what is staged and whether each file is new, changed, or unchanged.
        private string BuildList()
        {
            var files = StagedFiles();
            if (files.Count == 0)
                return $"Nothing staged in {StagingDir}";

            var lines = new List<string> { $"Staged in {StagingDir}:" };
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string state;
                if (name == SelfName)
                    state = "skipped (this installer)";
                else if (!ValidName.IsMatch(name))
                    state = "skipped (bad file name)";
                else if (!File.Exists(Path.Combine(PluginsDir, name + ".cs")))
                    state = "new";
                else if (!SameContent(file, Path.Combine(PluginsDir, name + ".cs")))
                    state = "changed";
                else
                    state = "unchanged";

                string loaded = plugins.Find(name) != null ? "loaded" : "not loaded";
                lines.Add($"  {name}.cs: {state}, {loaded}");
            }
            return string.Join("\n", lines);
        }

        // Copies changed staged files into oxide/plugins, then loads new plugins and reloads changed ones.
        private void Install(IPlayer caller)
        {
            var files = StagedFiles();
            if (files.Count == 0)
            {
                caller.Reply($"Nothing staged in {StagingDir}");
                return;
            }

            var touched = new List<string>();
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name == SelfName)
                {
                    // Reloading this plugin from inside itself would cut the install short.
                    caller.Reply($"Skipped {name}: the installer can't update itself. Replace it manually.");
                    continue;
                }
                if (!ValidName.IsMatch(name))
                {
                    caller.Reply($"Skipped {Path.GetFileName(file)}: file names may only use letters, digits and underscores.");
                    continue;
                }

                string dest = Path.Combine(PluginsDir, name + ".cs");
                bool existed = File.Exists(dest);
                if (existed && SameContent(file, dest) && plugins.Find(name) != null)
                    continue;   // nothing changed and already running

                try
                {
                    File.Copy(file, dest, true);
                }
                catch (Exception ex)
                {
                    caller.Reply($"Could not copy {name}.cs: {ex.Message}");
                    continue;
                }

                // oxide.reload on a plugin that isn't loaded yet fails, so pick the command by current state.
                bool running = plugins.Find(name) != null;
                covalence.Server.Command(running ? "oxide.reload" : "oxide.load", name);
                touched.Add(name);
            }

            if (touched.Count == 0)
            {
                caller.Reply("Everything staged is already installed and running.");
                return;
            }

            caller.Reply($"Installing {touched.Count} plugin(s): {string.Join(", ", touched)}. Checking results in {ReportDelay}s...");
            timer.Once(ReportDelay, () => ReportResults(caller, touched));
        }

        private void ReportResults(IPlayer caller, List<string> names)
        {
            var lines = new List<string>();
            foreach (string name in names)
            {
                bool ok = plugins.Find(name) != null;
                lines.Add(ok
                    ? $"  {name}: running"
                    : $"  {name}: NOT running. Check the server console for compile errors.");
            }

            string report = "Install results:\n" + string.Join("\n", lines);
            caller.Reply(report);
            Puts(report);
        }

        #endregion

        #region Helpers

        private List<string> StagedFiles()
        {
            var files = new List<string>();
            try
            {
                if (Directory.Exists(StagingDir))
                    files.AddRange(Directory.GetFiles(StagingDir, "*.cs"));
            }
            catch (Exception ex)
            {
                PrintError($"Could not read staging folder {StagingDir}: {ex.Message}");
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        private static bool SameContent(string a, string b)
        {
            try
            {
                byte[] left = File.ReadAllBytes(a);
                byte[] right = File.ReadAllBytes(b);
                if (left.Length != right.Length)
                    return false;
                for (int i = 0; i < left.Length; i++)
                {
                    if (left[i] != right[i])
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}
