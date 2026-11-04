#if UNITY_EDITOR
// ============================================================================
//  CharShot —— 用独立 RenderTexture 给角色拍"定妆照"
//
//  为什么不用 Game View 截图：Game View 的宽高比取决于编辑器窗口，实测只有
//  800×188（极端信箱），人物会被压扁、根本看不清细节。
//  这里自己建相机 + RenderTexture，固定 640×960 竖幅、正交投影、三点布光，
//  输出到 Tools/_verify/shots/ 供人工（和 AI）肉眼检查。
//
//  菜单：UnityLITF/换模型/③ 拍角色定妆照
// ============================================================================

using System.IO;
using UnityEngine;
using UnityEditor;

namespace Yixuan.ModelSwap
{
    public static class CharShot
    {
        const int W = 640, H = 960;
        const string OutDir = "Tools/_verify/shots";

        [MenuItem("UnityLITF/换模型/③ 拍角色定妆照", false, 10)]
        public static void Shoot() { ShootTarget(null, "char"); }

        public static string ShootTarget(GameObject target, string tag)
        {
            if (target == null)
            {
                target = GameObject.Find("Zhuangfy_BUILD");
                if (target == null) target = GameObject.Find("Yixuan_Test");
            }
            if (target == null) { Debug.LogError("[CharShot] 找不到角色"); return null; }

            // 关掉其他相机的干扰：自己建一个
            var camGo = new GameObject("__CharShotCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.13f, 0.14f, 0.17f, 1f);
            cam.orthographic = true;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;
            cam.enabled = false;   // 不参与 Game View 渲染，只手动 Render

            // 关键光 / 补光 / 轮廓光
            var lights = new GameObject[3];
            var lp = new[] { new Vector3(2.5f, 3.0f, 3.0f), new Vector3(-3.0f, 1.5f, 2.0f), new Vector3(0f, 2.5f, -4f) };
            var le = new[] { 1.15f, 0.45f, 0.7f };
            var lc = new[] { new Color(1f, 0.97f, 0.92f), new Color(0.75f, 0.82f, 1f), new Color(1f, 0.9f, 0.95f) };
            for (int i = 0; i < 3; i++)
            {
                var go = new GameObject("__CharShotLight" + i);
                var l = go.AddComponent<Light>();
                l.type = LightType.Directional;
                l.intensity = le[i];
                l.color = lc[i];
                l.cullingMask = ~0;
                go.transform.position = lp[i];
                go.transform.LookAt(new Vector3(0f, 1.0f, 0f));
                lights[i] = go;
            }

            // 角色包围盒（含蒙皮）
            Bounds b = new Bounds(new Vector3(0, 0.9f, 0), new Vector3(1f, 1.8f, 1f));
            var smr = target.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null) b = smr.bounds;
            // 稍微放宽，避免发梢被切掉
            b.Expand(0.08f);

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 4;
            cam.targetTexture = rt;

            string dir = Path.Combine(Directory.GetCurrentDirectory(), OutDir);
            Directory.CreateDirectory(dir);

            // 多角度：正面 / 3/4 / 侧面 / 背面
            var views = new (string name, Vector3 dirv)[]
            {
                ("front", new Vector3(0f, 0f, 1f)),
                ("q34",   new Vector3(0.75f, 0f, 0.85f).normalized),
                ("side",  new Vector3(1f, 0f, 0f)),
                ("back",  new Vector3(0f, 0f, -1f)),
            };

            string last = null;
            foreach (var v in views)
            {
                // 正交尺寸按包围盒高度定，留 12% 边距
                float aspect = (float)W / H;
                float halfH = Mathf.Max(b.extents.y, b.extents.x / aspect, b.extents.z / aspect) * 1.12f;
                cam.orthographicSize = halfH;
                cam.transform.position = b.center + v.dirv * 10f + Vector3.up * 0f;
                cam.transform.LookAt(b.center);

                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = null;

                string path = Path.Combine(dir, tag + "_" + v.name + ".png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                last = path;
                Debug.Log("[CharShot] " + path);
            }

            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(camGo);
            foreach (var l in lights) Object.DestroyImmediate(l);

            AssetDatabase.Refresh();
            Debug.Log("[CharShot] 出图目录: " + dir + "  包围盒 center=" + b.center + " size=" + b.size);
            return last;
        }
    }
}
#endif
