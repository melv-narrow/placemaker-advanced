using System;
using Colossal.Logging;
using Game;
using Game.Modding;

namespace Placemaker
{
    public sealed class Mod : IMod
    {
        public static readonly ILog Log = KeepOpen(LogManager.GetLogger(nameof(Placemaker)).SetShowsErrorsInUI(false));

        /// <summary>
        /// (fork) Placemaker.log stays open for the whole session. By default the game's logger reopens the file for every
        /// line (UnityLogger.keepStreamOpen false); when one reopen fails, for a moment, because something else is reading the
        /// file (an antivirus scan, a log viewer), Open() swallows the error and leaves its writer null, and the next line
        /// throws a NullReferenceException inside the game's logger (Internal_WriteStream), shown as an error dialog in game
        /// although this log never shows errors in the UI. Seen in the game runner on 1.6.3f1.
        /// </summary>
        private static ILog KeepOpen(ILog log)
        {
            try { if (log is UnityLogger u) u.keepStreamOpen = true; } catch (Exception) { }
            return log;
        }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info($"Placemaker {typeof(Mod).Assembly.GetName().Version} loading");
            // Each workstream registers its own systems; a failure in one area is logged and the
            // rest still loads, so a broken feature never takes the whole mod (or the game) down.
            Try("build", () => Game.Build.BuildRegistration.Register(updateSystem));
            Try("tool", () => Game.Tool.ToolRegistration.Register(updateSystem, this));
            // In-game test suite: inert unless the command line AND an armed ModsData/Placemaker/suite/run.json ask for it.
            Try("suite", () => Game.Suite.SuiteGate.Register(updateSystem));
        }

        public void OnDispose()
        {
            try { Game.Tool.ToolRegistration.Unregister(); } catch (Exception e) { Log.Error(e, "Placemaker: tool unregister failed"); }
            Game.Services.Reset();
        }

        private static void Try(string area, Action register)
        {
            try { register(); }
            catch (Exception e) { Log.Error(e, $"Placemaker: registering '{area}' failed"); }
        }
    }
}
