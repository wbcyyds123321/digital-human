using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
namespace Yixuan.AI.Tasks
{
    // Metadata-based grounding, not a route table. Marker annotations take precedence.
    public static class TaskObjectSemantics
    {
        static readonly string[,] Types = {
            { @"^TrafficLight", "红绿灯;信号灯;交通灯;路口红绿灯" },
            { @"^(?:Parked)?(?:Van|Car|Sedan|Truck)(?:_|$|[0-9])", "汽车;小汽车;小轿车;轿车;车;车辆;面包车;厢式车;货车" },
            { @"^Bicycle", "自行车;单车;脚踏车" }, { @"^StreetLamp", "路灯;街灯" },
            { @"^RoadSign", "路牌;交通标志;道路标志" }, { @"^Cone(?:_|$|[0-9])", "路锥;交通锥;锥桶" },
            { @"^Vending_[0-9]+$", "自动售货机;售货机;贩卖机;自动贩卖机" },
            { @"^Trash(?:_|$|[0-9])", "垃圾桶;垃圾箱" }, { @"^NoticeBoard", "公告板;告示板;公告栏" },
            { @"^UmbrellaStand", "伞架;雨伞架" }, { @"^OutdoorRack", "室外货架;门口货架;户外货架" },
            { @"^AC_", "空调;空调外机" }, { @"^Store$", "便利店;商店;店铺" },
            { @"^Apartment$", "公寓;公寓楼" }, { @"^Alley$", "小巷;巷子" }
        };
        public static bool Enrich(Transform obj, List<string> aliases, out string label, out string description)
        {
            label = obj.name; description = ""; string[] nouns = null;
            for (int i=0;i<Types.GetLength(0);i++) if (Regex.IsMatch(obj.name, Types[i,0], RegexOptions.IgnoreCase))
            { nouns = Types[i,1].Split(';'); break; }
            if (nouns == null) return false;
            var materials = new List<string>(); string color = ""; float best = -1;
            foreach (var renderer in obj.GetComponentsInChildren<MeshRenderer>())
            {
                if (!renderer.enabled) continue;
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    if (!materials.Contains(mat.name)) materials.Add(mat.name);
                    // The body/cabin material is more representative than a lamp/window/tire.
                    float score = renderer.bounds.size.sqrMagnitude;
                    if (Regex.IsMatch(renderer.name, @"body|cabin|shell", RegexOptions.IgnoreCase)) score += 1000;
                    if (Regex.IsMatch(mat.name, @"glass|light|neon|metal|dark", RegexOptions.IgnoreCase)) score -= 100;
                    string c = ColorName(mat);
                    if (c.Length > 0 && score > best) { best=score; color=c; }
                }
            }
            // Signal color belongs to individual lenses, not to the pole/body material.
            if (obj.name.StartsWith("TrafficLight", StringComparison.OrdinalIgnoreCase)) color="";
            foreach (var noun in nouns) { aliases.Add(noun); if (color.Length>0) { aliases.Add(color+"色"+noun); aliases.Add(color+"色的"+noun); } }
            label = (color.Length>0?color+"色":"") + nouns[0] + "（"+obj.name+"）";
            description = "来源=Unity对象名/材质（不是视觉识别）；对象="+obj.name+"；材质="+string.Join(",",materials.ToArray());
            return true;
        }
        static string ColorName(Material mat)
        {
            string name=mat.name.ToLowerInvariant();
            if (name.Contains("blue")) return "蓝"; if (name.Contains("green")) return "绿";
            if (name.Contains("red")) return "红"; if (name.Contains("yellow")) return "黄";
            Color c;
            if (mat.HasProperty("_BaseColor")) c=mat.GetColor("_BaseColor");
            else if (mat.HasProperty("_Color")) c=mat.color; else return "";
            float h,s,v; Color.RGBToHSV(c,out h,out s,out v);
            if (v<0.12f) return "黑"; if(s<0.15f) return v>0.75f?"白":"灰";
            if(h<0.045f||h>0.95f) return "红"; if(h<0.15f) return "黄";
            if(h<0.46f) return "绿"; if(h<0.72f) return "蓝"; return "紫";
        }
    }
}
