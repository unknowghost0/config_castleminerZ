using Microsoft.Xna.Framework;
using System.Reflection;
using DNA.CastleMinerZ;
using DNA.Input;
using ModLoader;
using System;

using static ModLoader.LogSystem;

namespace Config
{
    [Priority(Priority.Low)]
    public class Config : ModBase
    {
        public Config() : base("Config", new Version("0.0.1"))
        {
            var game = CastleMinerZGame.Instance;
            if (game != null)
                game.Exiting += (s, e) => Shutdown();
        }

        public override void Start()
        {
            var game = CastleMinerZGame.Instance;
            if (game == null)
            {
                Log("Game instance is null.");
                return;
            }

            GamePatches.ApplyAllPatches();
            Log($"{MethodBase.GetCurrentMethod().DeclaringType.Namespace} loaded.");
        }

        public static void Shutdown()
        {
            try
            {
                try { GamePatches.DisableAll(); } catch (Exception ex) { Log($"Disable hooks failed: {ex.Message}."); }
                Log($"{MethodBase.GetCurrentMethod().DeclaringType.Namespace} shutdown complete.");
            }
            catch (Exception ex)
            {
                Log($"Error shutting down mod: {ex}.");
            }
        }

        public override void Tick(InputManager inputManager, GameTime gameTime)
        {
        }
    }
}

