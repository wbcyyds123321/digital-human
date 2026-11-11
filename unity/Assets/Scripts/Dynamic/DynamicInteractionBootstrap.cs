// Scene bootstrap for node-2 dynamic entities. The map prefab now keeps vehicles, the tram
// body + text plates and the eternal-flame fire mesh under DYN_* nodes; this component wires
// the runtime systems when the scene starts (the scene builder adds it in batch mode).
using UnityEngine;

namespace Yixuan.Dynamic
{
    [DisallowMultipleComponent]
    public sealed class DynamicInteractionBootstrap : MonoBehaviour
    {
        public bool Verbose = true;
        public bool Ready { get; private set; }
        public string Error { get; private set; } = "";

        void Awake()
        {
            var world = DynamicWorld.EnsureInstance();
            var actor = DynamicActorSystem.EnsureInstance();
            Ready = world.Initialize();
            if (Ready) Ready = actor.Initialize();
            if (!Ready) Error = (world.Error ?? "") + " / " + (actor.LastError ?? "");
            if (Verbose)
            {
                Debug.Log("[DynamicBootstrap] ready=" + Ready + " error=" + Error + " :: " + actor.Describe());
            }
        }
    }
}
