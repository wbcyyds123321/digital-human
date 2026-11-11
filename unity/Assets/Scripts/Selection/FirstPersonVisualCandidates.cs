using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace Yixuan.Pick
{
    public sealed class FpVisibleCandidate
    {
        public int index;
        public string entity_id;
        public int pixels;
        public Rect box;
        public Vector2 centre;
        public PickHit hit;
    }

    public sealed class FpVisibleCandidates
    {
        public readonly List<FpVisibleCandidate> Items = new List<FpVisibleCandidate>();
        public byte[] AnnotatedJpeg;
        public string Error = "";
        public int Omitted;

        static readonly string[] Digits = {
            "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
            "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111"
        };

        public static FpVisibleCandidates Build(PickFrame frame)
        {
            var result = new FpVisibleCandidates();
            if (frame == null || !frame.Ok) { result.Error = "missing_frame"; return result; }
            var resolved = PickService.ResolveBox(frame, new Rect(0f, 0f, 1f, 1f));
            foreach (var candidate in resolved.candidates)
            {
                if (!candidate.has_reliable_point || candidate.pixel_count < 12) { result.Omitted++; continue; }
                var entry = EntityPickRegistry.Get(candidate.entity_id);
                if (entry == null || !entry.Selectable) continue;
                result.Items.Add(new FpVisibleCandidate {
                    entity_id = candidate.entity_id, pixels = candidate.pixel_count, centre = candidate.centroid_uv,
                    hit = frame.ResolveUv((candidate.sample_pixel_x + 0.5f) / frame.width,
                        (candidate.sample_pixel_y + 0.5f) / frame.height)
                });
            }
            result.Items.Sort((left, right) => right.pixels.CompareTo(left.pixels));
            if (result.Items.Count > 96)
            { result.Omitted += result.Items.Count - 96; result.Items.RemoveRange(96, result.Items.Count - 96); }
            var byCode = new Dictionary<int, FpVisibleCandidate>();
            var bounds = new Dictionary<int, int[]>();
            for (int candidateIndex = 0; candidateIndex < result.Items.Count; candidateIndex++)
            {
                var item = result.Items[candidateIndex];
                item.index = candidateIndex + 1;
                byCode[item.hit.code] = item;
                bounds[item.hit.code] = new[] { frame.width, frame.height, -1, -1 };
            }
            for (int pixelY = 0; pixelY < frame.height; pixelY++)
                for (int pixelX = 0; pixelX < frame.width; pixelX++)
                {
                    int[] extent;
                    if (!bounds.TryGetValue(frame.CodeAt(pixelX, pixelY), out extent)) continue;
                    extent[0] = Math.Min(extent[0], pixelX); extent[1] = Math.Min(extent[1], pixelY);
                    extent[2] = Math.Max(extent[2], pixelX); extent[3] = Math.Max(extent[3], pixelY);
                }
            foreach (var pair in byCode)
            {
                var extent = bounds[pair.Key];
                pair.Value.box = Rect.MinMaxRect(extent[0] / (float)frame.width, extent[1] / (float)frame.height,
                    (extent[2] + 1f) / frame.width, (extent[3] + 1f) / frame.height);
            }
            result.Draw(frame);
            return result;
        }

        void Draw(PickFrame frame)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                if (!texture.LoadImage(frame.rgb_png) || texture.width != frame.width || texture.height != frame.height)
                { Error = "rgb_id_dimensions_mismatch"; return; }
                var pixels = texture.GetPixels32();
                foreach (var item in Items.OrderByDescending(candidate => candidate.pixels))
                {
                    int left = Mathf.FloorToInt(item.box.xMin * frame.width);
                    int top = Mathf.FloorToInt(item.box.yMin * frame.height);
                    int right = Mathf.CeilToInt(item.box.xMax * frame.width) - 1;
                    int bottom = Mathf.CeilToInt(item.box.yMax * frame.height) - 1;
                    var color = new Color32(0, 255, 225, 255);
                    for (int pixelX = left; pixelX <= right; pixelX++)
                    { Put(pixels, frame.width, frame.height, pixelX, top, color); Put(pixels, frame.width, frame.height, pixelX, bottom, color); }
                    for (int pixelY = top; pixelY <= bottom; pixelY++)
                    { Put(pixels, frame.width, frame.height, left, pixelY, color); Put(pixels, frame.width, frame.height, right, pixelY, color); }
                    int labelX = Mathf.Clamp(item.hit.pixel_x, 0, Math.Max(0, frame.width - 32));
                    int labelY = Mathf.Clamp(item.hit.pixel_y - 13, 0, Math.Max(0, frame.height - 12));
                    string label = item.index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    for (int offsetY = -1; offsetY < 11; offsetY++)
                        for (int offsetX = -1; offsetX < label.Length * 8; offsetX++)
                            Put(pixels, frame.width, frame.height, labelX + offsetX, labelY + offsetY, new Color32(0, 0, 0, 255));
                    for (int digitIndex = 0; digitIndex < label.Length; digitIndex++)
                        for (int bit = 0; bit < 15; bit++)
                        {
                            if (Digits[label[digitIndex] - '0'][bit] != '1') continue;
                            for (int scaleY = 0; scaleY < 2; scaleY++)
                                for (int scaleX = 0; scaleX < 2; scaleX++)
                                    Put(pixels, frame.width, frame.height, labelX + digitIndex * 8 + bit % 3 * 2 + scaleX,
                                        labelY + bit / 3 * 2 + scaleY, color);
                        }
                }
                texture.SetPixels32(pixels); texture.Apply(false, false);
                AnnotatedJpeg = texture.EncodeToJPG(92);
            }
            finally { UnityEngine.Object.Destroy(texture); }
        }

        static void Put(Color32[] pixels, int width, int height, int pixelX, int pixelY, Color32 value)
        {
            if (pixelX < 0 || pixelY < 0 || pixelX >= width || pixelY >= height) return;
            pixels[(height - 1 - pixelY) * width + pixelX] = value;
        }

        public string Prompt(FpVisualRequest request)
        {
            return "你是第一人称物体识别器。第一张图是原始当前画面，第二张图是同一帧的编号辅助图。按原图识别颜色和外观，用辅助图对应编号。" +
                "辅助图中的青色框和数字是引擎从同帧可见物体轮廓生成的临时编号，不是类别或答案。" +
                "只按图像外观和用户描述判断。先列出同时符合用户指定类别、颜色、车型等外观条件的所有编号 matching_indices；" +
                "最左/靠近准星等位置条件由程序在这些匹配物体之间计算。不要把不符合颜色或类别的物体放入匹配集合。" +
                "candidate_index 是你推荐的目标编号。没有目标返回 not_found；无法分辨或多个目标无位置条件可区分返回 ambiguous。" +
                "不要根据旧场景记忆、物体ID或世界坐标猜测，不要编造编号，不返回像素坐标。严格只返回JSON：" +
                "{\"status\":\"located|not_found|ambiguous\",\"candidate_index\":1,\"matching_indices\":[1],\"description\":\"可见外观描述\"}。" +
                "用户描述：" + request.target_description + "。位置要求：" +
                (request.leftmost ? "最左；" : "") + (request.near_crosshair ? "离画面中心十字最近；" : "") +
                "可用编号和框（归一化左上角坐标）：" + JsonConvert.SerializeObject(Items.Select(item => new {
                    index = item.index, bbox_uv = new[] { item.box.xMin, item.box.yMin, item.box.xMax, item.box.yMax }
                }));
        }

        public bool Bind(FpVisualRawReply reply, FpVisualRequest request, out string error)
        {
            error = "";
            if (reply.matching_indices == null || reply.matching_indices.Length == 0)
            { error = "missing_visual_matches"; return false; }
            var eligible = new List<FpVisibleCandidate>();
            foreach (int index in reply.matching_indices)
            {
                var item = Items.Find(candidate => candidate.index == index);
                if (item == null || eligible.Contains(item)) { error = "invalid_or_duplicate_candidate_index"; return false; }
                eligible.Add(item);
            }
            var chosen = eligible.Find(item => item.index == reply.candidate_index);
            if (chosen == null) { error = "selected_index_not_in_visual_matches"; return false; }
            if (request.leftmost) chosen = eligible.OrderBy(item => item.centre.x).First();
            if (request.near_crosshair)
            {
                var nearest = eligible.OrderBy(item => (item.centre - new Vector2(0.5f, 0.5f)).sqrMagnitude).First();
                if (request.leftmost && nearest != chosen) { error = "conflicting_position_constraints"; return false; }
                chosen = nearest;
            }
            if (eligible.Count > 1 && !request.leftmost && !request.near_crosshair)
            { error = "multiple_visual_matches_need_clarification"; return false; }
            if (chosen.hit == null || !chosen.hit.Valid || chosen.hit.entity.Id != chosen.entity_id)
            { error = "candidate_pixel_not_reliable"; return false; }
            reply.candidate_index = chosen.index;
            reply.point_uv = new[] { chosen.hit.u, chosen.hit.v };
            reply.bbox_uv = new[] { chosen.box.xMin, chosen.box.yMin, chosen.box.xMax, chosen.box.yMax };
            return true;
        }
    }
}
