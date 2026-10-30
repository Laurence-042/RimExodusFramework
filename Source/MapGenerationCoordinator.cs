using System;
using Verse;

namespace RimExodus.Maps
{
    public sealed class MapGenerationCoordinator
    {
        private string owner;
        private Action drain;
        private bool draining;
        private int ownerThread;
        private readonly object gate = new object();
        private bool external;
        public Map GeneratingMap { get; private set; }
        public bool IsBusy { get { lock (gate) return owner != null || external; } }
        public bool IsOwnedBy(string requestedOwner) => owner != null && owner == requestedOwner;
        public void Acquire(string requestedOwner, Action drainSynchronously)
        {
            lock (gate)
            {
                if (owner != null || external || MapGenerator.mapBeingGenerated != null)
                    throw new InvalidOperationException("Map generation static state is already occupied.");
                if (drainSynchronously == null) throw new ArgumentNullException(nameof(drainSynchronously));
                owner = requestedOwner ?? throw new ArgumentNullException(nameof(requestedOwner));
                drain = drainSynchronously;
                ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            }
        }
        public void Bind(string requestedOwner, Map map)
        {
            if (owner != requestedOwner) throw new InvalidOperationException("Map generation owner mismatch.");
            GeneratingMap = map;
        }
        public void Release(string requestedOwner)
        {
            lock (gate)
            {
                if (owner == null) return;
                if (owner != requestedOwner) throw new InvalidOperationException("Map generation owner mismatch.");
                owner = null; drain = null; GeneratingMap = null;
                System.Threading.Monitor.PulseAll(gate);
            }
        }
        internal void EnterExternal()
        {
            lock (gate)
            {
                while (owner != null || external)
                {
                    if (ownerThread == System.Threading.Thread.CurrentThread.ManagedThreadId)
                        throw new InvalidOperationException("Nested synchronous map generation cannot overwrite active generation state.");
                    System.Threading.Monitor.Wait(gate, 100);
                    if (MapRuntime.Current?.Generation != this)
                        throw new OperationCanceledException("The game changed while map generation was waiting.");
                }
                external = true;
                ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            }
        }
        internal void ExitExternal()
        {
            lock (gate) { external = false; ownerThread = 0; System.Threading.Monitor.PulseAll(gate); }
        }
        public void Drain()
        {
            if (owner == null) return;
            if (draining || ownerThread != System.Threading.Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException("Cannot complete map generation recursively or from another thread.");
            draining = true;
            try
            {
                drain();
                if (owner != null) throw new InvalidOperationException("Map generation did not release its registered owner.");
            }
            finally { draining = false; }
        }
    }
}
