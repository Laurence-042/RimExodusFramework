using System;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>A synchronous Scribe lease; never spans a game frame or steals an active save.</summary>
    internal sealed class ScribeOperation : IDisposable
    {
        [ThreadStatic] private static bool entered;
        internal static bool IsAvailable => !entered && Scribe.mode == LoadSaveMode.Inactive;
        private readonly ProgramState previous;
        public ScribeOperation(bool loading = false)
        {
            if (!IsAvailable)
                throw new InvalidOperationException("A Scribe operation is already active.");
            entered = true;
            previous = Current.ProgramState;
            if (loading) Current.ProgramState = ProgramState.MapInitializing;
        }
        public void Dispose()
        {
            try { if (Scribe.mode != LoadSaveMode.Inactive) Scribe.ForceStop(); }
            finally { Current.ProgramState = previous; entered = false; }
        }
    }
}
