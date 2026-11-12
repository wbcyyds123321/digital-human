using UnityEngine;
using Yixuan.Pick;

namespace Yixuan.SelectionLab
{
    /// <summary>
    /// Small lab-only status area, crosshair and top-view minimap. It is drawn
    /// as IMGUI after the EyeCamera render, so it is never part of the RGB frame
    /// sent to the vision model.
    /// </summary>
    public sealed class SelectionLabHud : MonoBehaviour
    {
        GUIStyle _label;
        GUIStyle _title;
        Texture2D _white;
        Vector2 _statusScroll;

        void OnGUI()
        {
            var lab = SelectionLabContext.Scene;
            if (lab == null) return;
            EnsureStyles();
            DrawCrosshair();
            if (Yixuan.UI.UnifiedHud.Instance != null) return;
            DrawStatus(lab, new Rect(16f, 16f, 620f, 260f));
            DrawMinimap(lab, new Rect(Screen.width - 240f, Screen.height - 240f, 220f, 220f));
        }

        public void DrawConsole(Rect content)
        {
            var lab = SelectionLabContext.Scene;
            if (lab == null) return;
            EnsureStyles();
            float mapHeight = Mathf.Min(210f, content.height * 0.4f);
            DrawMinimap(lab, new Rect(content.x, content.y, content.width, mapHeight));
            DrawStatus(lab, new Rect(content.x, content.y + mapHeight + 8f,
                content.width, Mathf.Max(60f, content.height - mapHeight - 8f)));
        }

        void EnsureStyles()
        {
            if (_white == null) _white = Texture2D.whiteTexture;
            if (_label == null)
                _label = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = false, wordWrap = true, alignment = TextAnchor.UpperLeft };
            if (_title == null)
                _title = new GUIStyle(_label) { fontSize = 15, fontStyle = FontStyle.Bold };
        }

        void DrawCrosshair()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(new Rect(cx - 14f, cy - 1f, 28f, 2f), _white);
            GUI.DrawTexture(new Rect(cx - 1f, cy - 14f, 2f, 28f), _white);
            GUI.color = new Color(1f, 1f, 1f, 0.92f);
            GUI.DrawTexture(new Rect(cx - 13f, cy - 1f, 26f, 1f), _white);
            GUI.DrawTexture(new Rect(cx - 1f, cy - 13f, 1f, 26f), _white);
            GUI.color = Color.white;
        }

        void DrawStatus(SelectionLabScene lab, Rect rect)
        {
            var rec = SelectionBroker.Current;
            var charT = PickService.FindCharacterTransform();
            var eye = PickService.ResolveEye();
            string text = "";
            text += "观察点 A: " + (lab.observationA != null ? lab.observationA.position.ToString("F1") : "-") +
                    "   B: " + (lab.observationB != null ? lab.observationB.position.ToString("F1") : "-") + "\n";
            text += "在聊天框要求选中物体，镜头会自动对准并确认。\n";
            text += "T 人工确认；1/2 切换观察点；F4 收起控制台。\n";
            if (rec == null)
            {
                text += "当前选中: （无）";
            }
            else
            {
                var live = PickService.SampleLive(rec);
                var current = live != null && live.available ? live.current_position : rec.entity_position;
                float distance = live != null && live.available ? live.distance_m : rec.distance_m;
                float bearing = live != null && live.available ? live.bearing_deg : rec.bearing_deg;
                text += "当前选中: " + rec.display_name + " [" + rec.entity_id + "]\n";
                text += "类别/能力: " + rec.cls + " / " + rec.capabilities + "\n";
                text += "observed_entity_world（确认时）: " + rec.entity_position.ToString("F2") + "\n";
                text += "observed_surface_world（确认时）: " + rec.surface_point.ToString("F2") + "\n";
                if (charT != null) text += "character_world（实时）: " + charT.position.ToString("F2") + "\n";
                if (eye != null) text += "eye_world（实时）: " + eye.transform.position.ToString("F2") + "\n";
                if (charT != null) text += "target_in_character_local（实时）: " + charT.InverseTransformPoint(current).ToString("F2") + "\n";
                if (eye != null) text += "target_in_eye_local（实时）: " + eye.transform.InverseTransformPoint(current).ToString("F2") + "\n";
                Vector2 uv = lab.MapUv(current);
                text += "map_uv（实时）: (" + uv.x.ToString("F3") + ", " + uv.y.ToString("F3") + ")\n";
                text += "distance/bearing（实时）: " + distance.ToString("F2") + "m  " + bearing.ToString("F1") + "°";
            }
            GUI.color = new Color(0f, 0f, 0f, 0.62f);
            GUI.DrawTexture(rect, _white);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 12f, rect.y + 10f, rect.width - 24f, 24f), "白色选中实验室 · 当前选择", _title);
            var viewport = new Rect(rect.x + 12f, rect.y + 36f, rect.width - 24f, Mathf.Max(16f, rect.height - 44f));
            float textWidth = Mathf.Max(40f, viewport.width - 20f);
            float textHeight = _label.CalcHeight(new GUIContent(text), textWidth);
            _statusScroll = GUI.BeginScrollView(viewport, _statusScroll, new Rect(0f, 0f, textWidth, textHeight));
            GUI.Label(new Rect(0f, 0f, textWidth, textHeight), text, _label);
            GUI.EndScrollView();
        }

        void DrawMinimap(SelectionLabScene lab, Rect rect)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.62f);
            GUI.DrawTexture(rect, _white);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, 20f), "实验室顶视小地图", _label);
            var inner = new Rect(rect.x + 12f, rect.y + 28f, rect.width - 24f, rect.height - 40f);
            GUI.color = new Color(0.18f, 0.22f, 0.26f, 0.95f);
            GUI.DrawTexture(inner, _white);
            GUI.color = Color.white;
            var charT = PickService.FindCharacterTransform();
            if (charT != null)
            {
                Vector2 uv = lab.MapUv(charT.position);
                float px = inner.x + uv.x * inner.width;
                float py = inner.y + (1f - uv.y) * inner.height;
                Vector2 dir = SelectionLabScene.MapForward(charT.forward);
                GUI.color = new Color(0.25f, 0.95f, 1f);
                for (int step = 2; step <= 12; step += 2)
                    GUI.DrawTexture(new Rect(px + dir.x * step - 1f, py + dir.y * step - 1f, 2f, 2f), _white);
                float tx = px + dir.x * 14f;
                float ty = py + dir.y * 14f;
                GUI.DrawTexture(new Rect(tx - 2.5f, ty - 2.5f, 5f, 5f), _white);
                GUI.DrawTexture(new Rect(px - 4f, py - 4f, 8f, 8f), _white);
                GUI.color = Color.white;
            }
            foreach (var e in lab.entities)
            {
                if (e == null || e.renderer == null) continue;
                Vector2 uv = lab.MapUv(e.renderer.bounds.center);
                float px = inner.x + uv.x * inner.width;
                float py = inner.y + (1f - uv.y) * inner.height;
                GUI.color = ColorFor(e.color);
                GUI.DrawTexture(new Rect(px - 3f, py - 3f, 6f, 6f), _white);
            }
            var rec = SelectionBroker.Current;
            if (rec != null)
            {
                Vector2 uv = lab.MapUv(rec.entity_position);
                float px = inner.x + uv.x * inner.width;
                float py = inner.y + (1f - uv.y) * inner.height;
                GUI.color = new Color(1f, 0.75f, 0.2f);
                GUI.DrawTexture(new Rect(px - 5f, py - 5f, 10f, 10f), _white);
            }
            GUI.color = Color.white;
        }

        static Color ColorFor(string color)
        {
            if (string.IsNullOrEmpty(color)) return Color.white;
            if (color.Contains("红")) return new Color(0.95f, 0.2f, 0.2f);
            if (color.Contains("蓝")) return new Color(0.2f, 0.55f, 1f);
            if (color.Contains("黄")) return new Color(1f, 0.9f, 0.2f);
            if (color.Contains("绿")) return new Color(0.25f, 0.9f, 0.35f);
            if (color.Contains("紫")) return new Color(0.65f, 0.35f, 0.95f);
            if (color.Contains("橙")) return new Color(1f, 0.5f, 0.15f);
            return Color.white;
        }
    }
}
