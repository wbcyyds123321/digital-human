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

    // ================= generic visual-target semantics (B revision1) =================
    // This is a conservative, explainable consistency boundary for a label-driven
    // visual selection. It uses existing entity semantic metadata (lab entity
    // definitions / pick registry class + display name) and generic Chinese
    // category/colour/relative-position vocabularies. It does not contain any
    // test-case ids, colour-specific sentence rules, or model self-description as
    // proof. A generic "任意物体/东西/那个" target remains allowed.

    public sealed class VisualTargetSemantics
    {
        public string TargetPhrase = "";
        public string Category = "";
        public string Residual = "";
        public string ConstraintPhrase = "";
        public List<string> Colors = new List<string>();
        public List<string> Relative = new List<string>();
        public bool Generic;
        public bool HasAttributes { get { return !string.IsNullOrEmpty(Category) || Colors.Count > 0 || Relative.Count > 0; } }
    }

    static readonly string[,] VisualCategoryVocabulary =
    {
        { "sphere", @"球体|圆球|球形|小球|大球|球|sphere|ball" },
        { "cube", @"方块|立方体|正方体|方体|盒子|cube|block" },
        { "cylinder", @"圆柱体|圆柱|柱体|柱子|cylinder" },
        { "capsule", @"胶囊体|胶囊|capsule" },
        { "bench", @"长椅|椅子|凳子|板凳|bench|chair" },
        { "vehicle", @"汽车|轿车|卡车|公交|大巴|车辆|车|vehicle|car|truck" },
        { "building", @"建筑|大楼|楼房|房子|building" },
        { "tree", @"树木|树|tree" },
        { "poster", @"海报|poster" },
        { "traffic_light", @"红绿灯|信号灯|交通灯|trafficlight" },
        { "cone", @"路锥|交通锥|锥桶|cone" },
        { "trash", @"垃圾桶|垃圾箱|trash" },
        { "vending", @"自动售货机|售货机|贩卖机|自动贩卖机|vending" },
        { "notice_board", @"公告板|告示板|公告栏|noticeboard" },
        { "umbrella_stand", @"伞架|雨伞架|umbrellastand" },
        { "rack", @"货架|rack" },
        { "ac", @"空调外机|空调|airconditioner" },
        { "store", @"便利店|商店|店铺|store" },
        { "apartment", @"公寓楼|公寓|apartment" },
        { "alley", @"小巷|巷子|alley" },
        { "eternal_flame", @"长明火|永恒之火|eternalflame" },
        { "street_lamp", @"路灯|街灯|streetlamp" },
        { "road_sign", @"路牌|交通标志|道路标志|roadsign" },
        { "bicycle", @"自行车|单车|脚踏车|bicycle" }
    };

    static readonly string[,] VisualColorVocabulary =
    {
        { "红", @"红色|红" }, { "蓝", @"蓝色|蓝" }, { "绿", @"绿色|绿" },
        { "黄", @"黄色|黄" }, { "紫", @"紫色|紫" }, { "粉", @"粉色|粉红色|粉" },
        { "橙", @"橙色|橙" }, { "黑", @"黑色|黑" }, { "白", @"白色|白" },
        { "灰", @"灰色|灰" }, { "棕", @"棕色|棕" }, { "金", @"金色|金" },
        { "银", @"银色|银" }, { "青", @"青色|青" }
    };

    static readonly Regex VisualSpatialMarker = new Regex(
        @"(?:最近的?|最接近的?|最左边?|最右边?|右侧|左侧|右边|左边|右面|左面|旁边|附近|前面|后边|后面|前边|上方|上面|下方|下面|中间|对面|内侧|外侧)(?:的|那个|这个)?",
        RegexOptions.Compiled);

    static readonly Regex VisualMeasureQuantifier = new Regex(
        @"(?:[0-9０-９一二三四五六七八九十百千万两好几半]+\s*(?:个|只|辆|把|棵|张|块|根|件|条|台|座|栋|幢|艘|架|支|本|杯|瓶|盒|包|双|对|组|片|枚|颗|粒|位|名|种|类|层|间|所|家|头|匹|峰|朵|束|幅|扇|面|盏|顶|床|套|副|门))",
        RegexOptions.Compiled);

    static readonly Regex VisualGenericMarker = new Regex(
        @"(?:任意|随便|哪个都行|随便一个|任意一个|随便哪个|哪一个都)",
        RegexOptions.Compiled);

    static readonly Regex VisualGenericNoun = new Regex(
        @"(?:东西|物体|目标|对象|物品|家伙|那个|这个|它)",
        RegexOptions.Compiled);

    static readonly Regex VisualDeicticPrefix = new Regex(
        @"^(?:眼前的?|画面中?的?|画面里的?|看到的?)\s*",
        RegexOptions.Compiled);

    static readonly Regex VisualSelectionScaffolding = new Regex(
        @"^(?:(?:请|帮我|给我|麻烦你?|麻烦|你|您|先|再|然后|接着|现在|去|把|将|选中|选择|选一下|选出来|选上|挑中)\s*)+",
        RegexOptions.Compiled);

    public static VisualTargetSemantics ParseVisualTarget(string description)
    {
        var attr = new VisualTargetSemantics();
        string text = (description ?? "").Trim().Trim('。', '，', '！', '？', ';', '；', '"', '“', '”', '\'');
        text = VisualSelectionScaffolding.Replace(text, "");
        text = VisualDeicticPrefix.Replace(text, "").Trim();
        attr.ConstraintPhrase = NormalizeVisualTargetPhrase(description);
        if (text.Length == 0) { attr.Generic = true; return attr; }

        // Keep the target phrase for class/colour/locate semantics, but preserve the
        // complete source phrase separately for contract-time integrity checks.
        string phrase = text;
        foreach (Match m in VisualSpatialMarker.Matches(text))
        {
            string canonical = VisualRelativeToken(m.Value);
            if (!string.IsNullOrEmpty(canonical) && !attr.Relative.Contains(canonical)) attr.Relative.Add(canonical);
            string after = text.Substring(m.Index + m.Length).TrimStart('的', ' ');
            if (!string.IsNullOrEmpty(after)) phrase = after;
        }
        phrase = Regex.Replace(phrase, @"^(?:那个|这个|再|然后|的)\s*", "").Trim();
        attr.TargetPhrase = phrase;
        if (phrase.Length == 0)
        {
            attr.Generic = attr.Category.Length == 0 && attr.Colors.Count == 0 && attr.Relative.Count == 0;
            return attr;
        }

        string category = "";
        int bestAt = -1;
        string lowerPhrase = phrase.ToLowerInvariant();
        for (int i = 0; i < VisualCategoryVocabulary.GetLength(0); i++)
        {
            foreach (Match m in Regex.Matches(phrase, VisualCategoryVocabulary[i, 1], RegexOptions.IgnoreCase))
                if (m.Index >= bestAt) { bestAt = m.Index; category = VisualCategoryVocabulary[i, 0]; }
            string key = VisualCategoryVocabulary[i, 0];
            Match keyMatch = Regex.Match(lowerPhrase, "(?<![a-z0-9_])" + Regex.Escape(key) + "(?![a-z0-9_])", RegexOptions.IgnoreCase);
            if (keyMatch.Success && keyMatch.Index >= bestAt) { bestAt = keyMatch.Index; category = key; }
        }
        attr.Category = category;

        foreach (Match m in Regex.Matches(phrase, @"粉色|粉红色|红色|蓝色|绿色|黄色|紫色|橙色|黑色|白色|灰色|棕色|金色|银色|青色|粉|红|蓝|绿|黄|紫|橙|黑|白|灰|棕|金|银|青"))
        {
            string color = CanonicalVisualColor(m.Value);
            if (!string.IsNullOrEmpty(color) && !attr.Colors.Contains(color)) attr.Colors.Add(color);
        }

        string residual = phrase;
        for (int i = 0; i < VisualColorVocabulary.GetLength(0); i++)
            residual = Regex.Replace(residual, VisualColorVocabulary[i, 1], "", RegexOptions.IgnoreCase);
        for (int i = 0; i < VisualCategoryVocabulary.GetLength(0); i++)
        {
            residual = Regex.Replace(residual, VisualCategoryVocabulary[i, 1], "", RegexOptions.IgnoreCase);
            residual = Regex.Replace(residual, "(?<![a-z0-9_])" + Regex.Escape(VisualCategoryVocabulary[i, 0]) + "(?![a-z0-9_])", "", RegexOptions.IgnoreCase);
        }
        residual = VisualSpatialMarker.Replace(residual, "");
        residual = VisualMeasureQuantifier.Replace(residual, "");
        residual = VisualGenericMarker.Replace(residual, "");
        residual = VisualGenericNoun.Replace(residual, "");
        residual = Regex.Replace(residual, @"(?:的|一个|那个|这个|形状|颜色|色|左右|前后|上面|下面)", "");
        residual = residual.Trim(' ', '，', ',', '。', '.', '的', '、');
        attr.Residual = residual;

        bool genericMarker = VisualGenericMarker.IsMatch(text);
        bool genericNounOnly = Regex.IsMatch(phrase, @"^(?:东西|物体|目标|对象|物品|家伙|那个|这个|它|的)+$");
        attr.Generic = attr.Category.Length == 0 && attr.Colors.Count == 0 && attr.Relative.Count == 0 &&
                       attr.Residual.Length == 0 && (genericMarker || genericNounOnly);
        return attr;
    }

    static string VisualRelativeToken(string token)
    {
        if (token.IndexOf("最近", StringComparison.Ordinal) >= 0) return "nearest";
        if (token.IndexOf("最左", StringComparison.Ordinal) >= 0) return "leftmost";
        if (token.IndexOf("最右", StringComparison.Ordinal) >= 0) return "rightmost";
        if (token.IndexOf("右", StringComparison.Ordinal) >= 0) return "right";
        if (token.IndexOf("左", StringComparison.Ordinal) >= 0) return "left";
        if (token.IndexOf("旁", StringComparison.Ordinal) >= 0 || token.IndexOf("附近", StringComparison.Ordinal) >= 0) return "near";
        if (token.IndexOf("前", StringComparison.Ordinal) >= 0) return "front";
        if (token.IndexOf("后", StringComparison.Ordinal) >= 0) return "back";
        if (token.IndexOf("上", StringComparison.Ordinal) >= 0) return "above";
        if (token.IndexOf("下", StringComparison.Ordinal) >= 0) return "below";
        if (token.IndexOf("中间", StringComparison.Ordinal) >= 0) return "middle";
        if (token.IndexOf("对面", StringComparison.Ordinal) >= 0) return "opposite";
        if (token.IndexOf("内侧", StringComparison.Ordinal) >= 0) return "inside";
        if (token.IndexOf("外侧", StringComparison.Ordinal) >= 0) return "outside";
        return "";
    }

    public static string CanonicalVisualColor(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        string t = token.Trim();
        for (int i = 0; i < VisualColorVocabulary.GetLength(0); i++)
            if (Regex.IsMatch(t, "^(?:" + VisualColorVocabulary[i, 1] + ")$", RegexOptions.IgnoreCase))
                return VisualColorVocabulary[i, 0];
        return "";
    }

    /// <summary>Extract the first generic colour token from an entity display name or
    /// material/colour metadata string; empty when there is no trustworthy colour.</summary>
    public static string ColorNameFromText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var match = Regex.Match(text, @"粉色|粉红色|红色|蓝色|绿色|黄色|紫色|橙色|黑色|白色|灰色|棕色|金色|银色|青色|粉|红|蓝|绿|黄|紫|橙|黑|白|灰|棕|金|银|青");
        return match.Success ? CanonicalVisualColor(match.Value) : "";
    }

    public static string CanonicalVisualCategory(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        string t = token.Trim();
        // B rev2: registry/catalog schema uses the canonical key itself
        // (vehicle/street_lamp/road_sign/notice_board/eternal_flame...); recognize it
        // before aliases so real metadata is not rejected as an unknown class.
        for (int i = 0; i < VisualCategoryVocabulary.GetLength(0); i++)
            if (string.Equals(t, VisualCategoryVocabulary[i, 0], StringComparison.OrdinalIgnoreCase))
                return VisualCategoryVocabulary[i, 0];
        for (int i = 0; i < VisualCategoryVocabulary.GetLength(0); i++)
            if (Regex.IsMatch(t, "^(?:" + VisualCategoryVocabulary[i, 1] + ")$", RegexOptions.IgnoreCase))
                return VisualCategoryVocabulary[i, 0];
        return "";
    }

    public static string TargetCategoryFor(string description)
    {
        return ParseVisualTarget(description).Category;
    }

    /// <summary>Normalized FULL target phrase used only for source->description integrity.
    /// It preserves the reference object, the relation and the nearest/leftmost modifier;
    /// it removes only selection scaffolding, leading deictics (眼前/画面/看到), generic
    /// any-match markers, and normal numeral+measure quantifiers. It does NOT remove an
    /// unknown target noun or arbitrary digits.</summary>
    public static string NormalizeVisualTargetPhrase(string description)
    {
        string s = (description ?? "").Trim().Trim('。', '，', '！', '？', ';', '；', '"', '“', '”', '\'');
        s = VisualSelectionScaffolding.Replace(s, "");
        s = VisualDeicticPrefix.Replace(s, "");
        s = VisualGenericMarker.Replace(s, "");
        s = VisualMeasureQuantifier.Replace(s, "");
        for (int i = 0; i < VisualColorVocabulary.GetLength(0); i++)
            s = Regex.Replace(s, VisualColorVocabulary[i, 1], VisualColorVocabulary[i, 0], RegexOptions.IgnoreCase);
        for (int i = 0; i < VisualCategoryVocabulary.GetLength(0); i++)
        {
            s = Regex.Replace(s, VisualCategoryVocabulary[i, 1], VisualCategoryVocabulary[i, 0], RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "(?<![a-z0-9_])" + Regex.Escape(VisualCategoryVocabulary[i, 0]) + "(?![a-z0-9_])", VisualCategoryVocabulary[i, 0], RegexOptions.IgnoreCase);
        }
        s = Regex.Replace(s, @"[\s，,。.；;、！!？?：:]+", "");
        return s.Trim('的');
    }

    /// <summary>Deterministic contract-time check: the slot description may not silently
    /// drop or invent the target category/colour/relative-position qualifiers or the
    /// reference object/nearest modifier from the full source phrase. Returns null when
    /// consistent.</summary>
    public static string ValidateTargetSlotSemantics(TaskTargetSlot slot)
    {
        if (slot == null) return "invalid_target_slot";
        string source = (slot.source ?? "").Trim();
        string description = (slot.description ?? "").Trim();
        var s = ParseVisualTarget(source);
        var d = ParseVisualTarget(description);
        if (s.Generic) return null;
        if (d.Generic) return "target_slot_description_lost_constraints";
        foreach (string relative in s.Relative)
            if (!d.Relative.Contains(relative)) return "unverified_target_slot_relative_constraint: missing=" + relative;
        foreach (string relative in d.Relative)
            if (!s.Relative.Contains(relative)) return "target_slot_description_adds_relative_constraint: " + relative;
        if (s.Category != d.Category)
            return "unverified_target_slot_category: source=" + (s.Category.Length == 0 ? "(none)" : s.Category) +
                   ";description=" + (d.Category.Length == 0 ? "(none)" : d.Category);
        foreach (string color in s.Colors)
            if (!d.Colors.Contains(color)) return "unverified_target_slot_color: missing=" + color;
        foreach (string color in d.Colors)
            if (!s.Colors.Contains(color)) return "target_slot_description_adds_color: " + color;
        if (s.Residual.Length > 0 && d.Residual.Length == 0)
            return "unverified_target_slot_object_noun: missing=" + s.Residual;
        if (d.Residual.Length > 0 && s.Residual.Length == 0)
            return "target_slot_description_adds_object_noun: " + d.Residual;
        if (s.Residual != d.Residual)
            return "unverified_target_slot_object_noun: source=" + s.Residual + ";description=" + d.Residual;
        // Independent full-phrase integrity: reference object, relation and nearest/
        // leftmost modifiers must survive. This intentionally rejects unsupported
        // rewrites instead of silently relaxing them.
        if (!string.Equals(s.ConstraintPhrase, d.ConstraintPhrase, StringComparison.Ordinal))
            return "unverified_target_slot_full_phrase: source='" + s.ConstraintPhrase + "';description='" + d.ConstraintPhrase + "'";
        return null;
    }

    /// <summary>Runtime publish gate for a description-driven visual target. Uses only
    /// existing entity semantic metadata. Unknown/unsupported specific targets are
    /// conservatively rejected; a fully unconstrained generic any-object target remains
    /// allowed. Qualifiers are parsed first; "任意" never discards colour/category/space.
    /// </summary>
    public static string CheckBoundVisualTarget(string requestedDescription, string entityId, string entityClass,
        string entityColor, string entityDisplayName, out string detail)
    {
        var requested = ParseVisualTarget(requestedDescription);
        string display = (entityDisplayName ?? "").Trim();
        string displayCompact = Regex.Replace(display, @"\s+", "");
        string requestedPhrase = Regex.Replace(requested.TargetPhrase ?? "", @"\s+", "");
        string entityClassNorm = CanonicalVisualCategory(entityClass);
        if (entityClassNorm.Length == 0) entityClassNorm = CanonicalVisualCategory(displayCompact);
        var entityColors = new List<string>();
        string colorFromMetadata = CanonicalVisualColor(entityColor);
        if (colorFromMetadata.Length > 0) entityColors.Add(colorFromMetadata);
        foreach (Match m in Regex.Matches(displayCompact, @"粉色|粉红色|红色|蓝色|绿色|黄色|紫色|橙色|黑色|白色|灰色|棕色|金色|银色|青色|粉|红|蓝|绿|黄|紫|橙|黑|白|灰|棕|金|银|青"))
        {
            string c = CanonicalVisualColor(m.Value);
            if (c.Length > 0 && !entityColors.Contains(c)) entityColors.Add(c);
        }
        bool displayHasPhrase = requestedPhrase.Length >= 2 && displayCompact.IndexOf(requestedPhrase, StringComparison.Ordinal) >= 0;
        bool displayHasResidual = requested.Residual.Length >= 2 && displayCompact.IndexOf(requested.Residual, StringComparison.Ordinal) >= 0;
        detail = "requested='" + (requestedDescription ?? "") + "';target_phrase='" + requested.TargetPhrase + "';category=" +
                 (requested.Category.Length == 0 ? "(none)" : requested.Category) + ";colors=" + string.Join(",", requested.Colors.ToArray()) +
                 ";relative=" + string.Join(",", requested.Relative.ToArray()) + ";residual='" + requested.Residual +
                 "';entity=" + entityId + ";entity_class=" + (entityClassNorm.Length == 0 ? (string.IsNullOrEmpty(entityClass) ? "(empty)" : entityClass) : entityClassNorm) +
                 ";entity_color=" + (entityColors.Count == 0 ? "(unknown)" : string.Join(",", entityColors.ToArray())) + ";entity_display='" + display + "'";
        if (requested.Generic)
        {
            detail += ";verdict=generic_any_target_allowed";
            return null;
        }
        if (!string.IsNullOrEmpty(requested.Category))
        {
            if (entityClassNorm.Length == 0)
            { detail += ";verdict=semantic_unverified:entity_class_unknown"; return "semantic_unverified: entity class is not known; cannot support requested " + requested.Category; }
            if (entityClassNorm != requested.Category)
            { detail += ";verdict=semantic_unverified:class_mismatch"; return "semantic_unverified: requested " + requested.Category + " but bound entity class is " + entityClassNorm; }
        }
        if (requested.Colors.Count > 0)
        {
            bool any = false;
            foreach (string c in requested.Colors) if (entityColors.Contains(c)) { any = true; break; }
            if (!any)
            { detail += ";verdict=semantic_unverified:color_mismatch"; return "semantic_unverified: requested colour " + string.Join("/", requested.Colors.ToArray()) + " does not match bound entity colour metadata"; }
        }
        if (requested.Residual.Length > 0 && !displayHasResidual && !displayHasPhrase)
        {
            detail += ";verdict=semantic_unverified:unsupported_object_noun";
            return "semantic_unverified: unsupported target noun '" + requested.Residual + "' has no trusted entity-semantic match";
        }
        if (requested.Category.Length == 0 && requested.Colors.Count == 0 && requested.Residual.Length == 0)
        {
            if (requested.Relative.Count > 0)
            {
                detail += ";verdict=position_constraint_only_allowed";
                return null;
            }
            if (!displayHasPhrase)
            {
                detail += ";verdict=semantic_unverified:no_semantic_support";
                return "semantic_unverified: target description has no trusted category/colour/semantic support";
            }
        }
        detail += ";verdict=semantic_supported";
        return null;
    }

    }
}
