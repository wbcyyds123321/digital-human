using System;

namespace Yixuan.Stage5
{
    /// <summary>
    /// Test-only hook for malformed/failed model responses. Real success runs must
    /// leave this null; every injected response is marked separately in evidence.
    /// </summary>
    public static class Stage5TestHooks
    {
        public static Func<string, int, Stage5VisionResult> VisionOverride;
        public static Action BeforeQueuedStart;
        public static int Calls;

        public static bool Active { get { return VisionOverride != null; } }

        public static void Clear()
        {
            VisionOverride = null;
            BeforeQueuedStart = null;
            Calls = 0;
        }

        public static Stage5VisionResult Invoke(string instruction, int attempt)
        {
            Calls++;
            return VisionOverride != null ? VisionOverride(instruction, attempt) : null;
        }
    }
}
