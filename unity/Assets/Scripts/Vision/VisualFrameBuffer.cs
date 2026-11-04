using System;
using System.Collections.Generic;

namespace Yixuan.Vision
{
    public sealed class VisualFrame
    {
        public readonly float CapturedAt;
        public readonly int Source;
        public readonly byte[] Jpeg;
        public readonly bool IsDark;

        public VisualFrame(float capturedAt, int source, byte[] jpeg, bool dark = false)
        {
            CapturedAt = capturedAt;
            Source = source;
            Jpeg = jpeg;
            IsDark = dark;
        }
    }

    public sealed class VisualClip
    {
        public readonly List<VisualFrame> Frames;
        public int Source { get { return Frames.Count == 0 ? 0 : Frames[0].Source; } }
        public float Start { get { return Frames.Count == 0 ? 0f : Frames[0].CapturedAt; } }
        public float End { get { return Frames.Count == 0 ? 0f : Frames[Frames.Count - 1].CapturedAt; } }
        public float Duration { get { return End - Start; } }
        public float Fps { get { return Duration > 0f ? (Frames.Count - 1) / Duration : 2f; } }
        public bool HasVisibleFrames { get { return Frames.Exists(frame => !frame.IsDark); } }
        public bool Usable { get { return Frames.Count >= 4 && Duration >= 1f; } }

        public VisualClip(List<VisualFrame> frames) { Frames = frames; }
    }

    public sealed class VisualFrameBuffer
    {
        readonly List<VisualFrame> _frames = new List<VisualFrame>();
        public int Count { get { return _frames.Count; } }
        public long Bytes { get; private set; }

        public void Add(float time, int source, byte[] jpeg, float keepSeconds, int maxFrames, long maxBytes, bool dark = false)
        {
            if ((source != 1 && source != 2) || jpeg == null || jpeg.Length == 0 || float.IsNaN(time) || float.IsInfinity(time)) return;
            if (_frames.Count > 0 && time < _frames[_frames.Count - 1].CapturedAt) return;
            if (jpeg.Length > maxBytes) return;
            _frames.Add(new VisualFrame(time, source, jpeg, dark));
            Bytes += jpeg.Length;
            Prune(time, keepSeconds, maxFrames, maxBytes);
        }

        public void Prune(float now, float keepSeconds, int maxFrames, long maxBytes)
        {
            while (_frames.Count > 0 && (now - _frames[0].CapturedAt > keepSeconds || _frames.Count > maxFrames || Bytes > maxBytes))
            {
                Bytes -= _frames[0].Jpeg.Length;
                _frames.RemoveAt(0);
            }
        }

        public VisualClip Snapshot(int source, float end, float seconds, int maxFrames, float maxGap)
        {
            var selected = new List<VisualFrame>();
            for (int index = _frames.Count - 1; index >= 0; index--)
            {
                var frame = _frames[index];
                if (frame.Source != source || frame.CapturedAt > end) continue;
                if (frame.CapturedAt < end - seconds) break;
                if (selected.Count > 0 && selected[selected.Count - 1].CapturedAt - frame.CapturedAt > maxGap) break;
                selected.Add(frame);
            }
            selected.Reverse();
            if (selected.Count > maxFrames)
            {
                var sampled = new List<VisualFrame>();
                for (int index = 0; index < maxFrames; index++)
                    sampled.Add(selected[(int)Math.Round(index * (selected.Count - 1.0) / (maxFrames - 1))]);
                selected = sampled;
            }
            return new VisualClip(selected);
        }

        public void Clear() { _frames.Clear(); Bytes = 0; }
    }
}
