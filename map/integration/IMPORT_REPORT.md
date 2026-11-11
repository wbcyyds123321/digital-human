# SovietBlueHour GLB → Unity（URP）导入报告

- **工程**：`<project>`（Unity 2022.3.62f3c1，URP 14.0.12，Linear 色彩空间）
- **执行**：unity-import-engineer（task-2），2026-11-02
- **源资产**：`Maps/SovietBlueHour/map/SovietBlueHour_game.glb`（117,624,704 B / 117.62 MB，Khronos glTF Blender I/O v5.2.41 导出）
- **导入方式**：工程自带 **UniGLTF `GlbScriptedImporter`**（`Packages/com.vrmc.gltf`），`m_reverseAxis = Z`、`m_renderPipeline = UniversalRenderPipeline`
- **结论**：**导入成功，全部可复用资产已生成**。导入耗时是**秒级（~7–15 s）**，不是预判的 10–30 分钟；51 节点 / 2047 submesh / **1,417,342 三角面** 与 GLB 导出报告完全一致；571 个材质 **100% 为 `Universal Render Pipeline/Lit`**；自发光（`KHR_materials_emissive_strength`，强度 1.086–40）已正确乘进 HDR `_EmissionColor`；5 张 QA 截图全部正常（0% 洋红像素），其中 CAM_HERO/CAM_AVE 两个机位与 Blender 成品图构图一致。

---

## 1. 资产与导入方式

| 项 | 值 |
|---|---|
| 复制后路径 | `Assets/Maps/SovietBlueHour/SovietBlueHour_game.glb`（原文件 `Maps/.../map/` 保留未动） |
| 大小 | 117,624,704 B = 117.62 MB（112.2 MiB） |
| Importer | `UniGLTF.GlbScriptedImporter`（ScriptedImporter，版本 1） |
| 轴转换 | `m_reverseAxis = Z (1)` → Unity = (gltf.x, gltf.y, **−gltf.z**) |
| 渲染管线 | `m_renderPipeline = UniversalRenderPipeline (2)` → 生成 URP/Lit 材质 |
| GLB meta guid | `f20a93d37c1842fdaa0869acfad2c3e1` |
| 使用的扩展 | `KHR_materials_emissive_strength` ✅ 支持、`KHR_texture_transform` ✅ 支持、`KHR_materials_specular` ⚠ 导入器忽略（见 §9） |

**⚠ 注意（复用时的坑）**：手工预写的 `.glb.meta` 中 `m_reverseAxis` / `m_renderPipeline` key 名不被 Unity 采纳，首次自动导入时读到的是默认值 `0/0`。脚本因此用 `SerializedObject`（属性名 `m_reverseAxis` / `m_renderPipeline`）+ `SaveAndReimport()` 强制修正，之后 meta 落盘为 `reverseAxis: 1 / renderPipeline: 2`。**以后重导请直接用菜单，不要手写 meta**。

---

## 2. 耗时（重要：远快于预期）

| 阶段 | 实测 |
|---|---|
| Unity 进程总时长（stage1：导入 + prefab + 场景 + 统计） | **54.3 s** |
| 启动期 Asset Pipeline Refresh（含 117 MB GLB 首次导入） | 30.65 s |
| 其中脚本编译 | 9.49 s |
| 无新资产时的启动基线（stage2 / stage3 对照） | 24.8 s / 26.1 s |
| → **首次自动导入净耗时（推算）** | **约 10–15 s** |
| **显式 `SaveAndReimport()`（URP + Z 设置）** | **6.67 s** |
| stage1 脚本本身（读回资产 + 统计 + 存 prefab + 建场景 + 写 JSON） | 8.32 s |
| stage2 脚本（材质审计 + 5 张渲染 + JSON） | 2.8 s |
| stage3 脚本（shader 诊断 + 5 张重渲 ×2 + 控制渲染 + 几何探针） | 4.2 s（进程 33.2 s） |

> 117 MB / 1.4M tri / 134 张 JPEG 贴图完全在内存里处理，机器（32 GB RAM，RTX 4060 Laptop）无内存压力，峰值没有触发 OOM 或交换。**10–30 分钟的说法不成立**，以后可以放心直接重导。

---

## 3. 导入结果统计

### 3.1 层级 / 几何

| 指标 | 值 | 对应源数据 |
|---|---|---|
| 根对象 | `SovietBlueHour_game`（Unity 子资产根），51 个子节点 | 51 个 glTF root node ✅ |
| 子节点 | 48 × `BLD_*.001` + `PROPS` + `SITE` + `VEHICLES` | ✅ 名称原样保留 |
| MeshRenderer / MeshFilter | **51 / 51** | 1 mesh per node ✅ |
| 唯一 Mesh | 51 | ✅ |
| **SubMesh 总数（= draw call 基数）** | **2047** | 2047 primitives ✅ |
| **三角面** | **1,417,342** | 导出报告 1,417,342 ✅ |
| 顶点（Unity Mesh.vertexCount 合计） | 2,634,879 | 导出报告 position_verts 5,269,758（按 primitive 重复计；几何面数一致） |
| 子资产数 | 982（含 Mesh/Material/Texture 与导入器辅助对象） | — |
| 材质 | **571**（另有 4 组重名，Unity 侧可正常引用） | 571 ✅ |
| 贴图对象 | **154 个 Texture2D**（源自 134 张 JPEG，20 个重复实例） | images 134 / textures 154 ✅ |
| 贴图尺寸 | 512×512 ×86，1024×1024 ×58，1536×1024 ×6，1024×1536 ×4 | 全部 JPEG、0 张损坏 |

最大节点（三角面）：`PROPS` 262,938 / `BLD_MSU_MAIN.001` 88,492 / `BLD_UKRAINA.001` 64,776 / `BLD_MFA.001` 59,104 / `BLD_HIT.001` 53,072。

### 3.2 材质 / Shader

| 指标 | 值 |
|---|---|
| Shader 种类 | **1**：`Universal Render Pipeline/Lit`（571/571） |
| null shader / 非 URP / `isSupported=false` / ShaderUtil 报错 | **0 / 0 / 0 / 0** |
| `shader.isSupported` | true（D3D11，SM 5.0，RTX 4060 Laptop） |
| `ShaderUtil.GetShaderMessages` | 0 条 |
| `_BaseMap` 绑定 | **152 / 152**（= GLB baseColorTexture 数量） |
| 自发光材质（`_EMISSION` 关键字） | **88 / 88**，`_EmissionColor` > 0 的也是 88 |
| `_EmissionMap` 绑定 | **6 / 6** |
| `_EmissionColor` HDR 最大分量 | **40.00**（= `KHR_materials_emissive_strength` 最大值 40，说明强度已乘入） |
| 透明材质（renderQueue ≥ 3000） | **3**（= GLB alphaMode BLEND 3 个） |
| 双面材质（`_Cull = Off`） | **571 / 571**（GLB 里 doubleSided 全为 true） |
| 材质实例化 / Shader 替换 / 后处理修复数 | **0**（无需任何材质后处理） |

自发光示例（已乘强度）：`IND_lamp_red (40.0, 0.93, 0.37)`、`CIV_MARQ_BULB (26.0, 15.0, 5.1)`、`CIV_WALL_LAMP (20.0, 11.2, 3.9)`、`LAMP_GLOW (22.0, 10.0, 3.1)`。

> **结论：材质层无需后处理。** 没有过暗/过亮/粉红/丢贴图/丢自发光。

---

## 4. 坐标与包围盒（Lead 构建主场景需要的数据）

### 4.1 prefab 实例化后的世界包围盒（identity transform 下）

```
world min = (-520.000,  -0.292,  -410.000)
world max = ( 520.000,  68.625,   410.000)
world size = (1040.000, 68.917,   820.000)   // 米
```

- **地面 y ≈ 0**：建筑全部以 y=0 为基座（如 `BLD_MSU_MAIN` minY=0.00）；`SITE`（地面/路面/路缘）y 范围 **−0.04 → 20.21**（20 m 部分是场地内的墙体/台阶等构件）。
- **最低点 y = −0.292**，来自 `PROPS`（路面标线/井盖等贴地小物件），不是地形。
- **最高点 y = 68.625**，来自 `BLD_MSU_MAIN.001`（莫斯科大学主楼）。
- 实际建筑群集中在 **X ∈ [−199, 200]、Z ∈ [−151, 151]**（≈400×300 m，与 `make_contact_sheet.py` 描述一致）；`SITE` 把马路/地面外扩到 ±520 / ±410。
- **所有 prefab 子节点的 Transform 都是 identity**（glTF 导出时已把世界坐标烘进顶点），所以节点的 `Transform.position` 全是 `(0,0,0)`，有意义的"位置"要看 mesh bounds。

### 4.2 坐标映射确认（ReverseZ）

映射实测为：

```
Unity = (gltf.x, gltf.y, −gltf.z) = (blender.x, blender.z, blender.y)
```

证据（均为 prefab 实例的真实 world bounds）：

| 对象 | Unity world bounds | 源数据核对 | 结论 |
|---|---|---|---|
| `BLD_MSU_MAIN.001` | min (172.56, 0.00, 35.28) max (198.80, 68.63, 74.72) | glTF X[173,199] Y[0,69] Z[−75,−35] → Unity Z[35,75] | ✅ 一致 |
| `BLD_BAKERY.001` | min (−8.04, −0.06, −102.97) max (6.86, 8.52, −91.64) | glTF X[−8,7] Y[0,9] Z[92,103] → Unity Z[−103,−92] | ✅ 一致 |
| **`BLD_MOTHERLAND.001`（祖国母亲雕像）** | **min (−8.40, 2.70, 137.39) max (8.40, 41.93, 149.81)** | glTF 本地 bounds X[−8.40,8.40] Y[0,39.23] Z[−149.81,−137.39]，且该节点是全 GLB **唯一带 TRS 的节点**：translation = (0, **2.70**, 0) → Y[2.70,41.93]；Unity Z = −glTF.z = [137.39,149.81] | ✅ 一致（中心 ≈ **(0, 22.3, 143.6)**，底座 16.8 × 12.4 m，顶高 41.93 m） |

> 其他 50 个节点都是 identity，只有 `BLD_MOTHERLAND.001` 带 `(0, 2.7, 0)` 的位移（雕像抬高了 2.7 m，底座在 y=2.7）。
> **视觉复核**：把 Blender `CAM_HERO`（loc (205,−195,85) / target (0,10,12) / lens 38）按此映射换算成 Unity `(205,85,−195) → (0,12,10)`、vFOV 29.85°，渲染出的 `qa_04_hero_camhero.png` 与 `renders/final/CAM_HERO.png` 构图一致（右侧祖国母亲雕像、左下方八角亭、林荫大道、远处塔楼），**坐标映射由此得到画面级确认**。

---

## 5. 产出的可复用资产

### 5.1 Prefab

```
Assets/Maps/SovietBlueHour/SovietBlueHour_Map.prefab
```

| 项 | 值 |
|---|---|
| 根节点名 | **`SovietBlueHour_Map`** |
| Transform | position (0,0,0)、rotation identity、scale (1,1,1) ✅ |
| 子节点 | **51**（48 BLD_* + PROPS + SITE + VEHICLES），Transform 全 identity |
| 校验（从 prefab 资产读回） | MeshRenderer **51**、SubMesh **2047**、三角面 **1,417,342** |
| 文件大小 | 286 KB（仅引用 GLB 内部子资产，不复制几何/贴图） |
| 依赖 | `SovietBlueHour_game.glb`（mesh/material/texture 均在其子资产里） |

### 5.2 临时 QA 测试场景（仅用于导入 QA，不属于主场景）

```
Assets/Scenes/_SovietBlueHourImportTest.unity   （14.8 KB）
```

内容：`SovietBlueHour_Map` prefab 实例（identity） + `QA_Moon` 方向光 + `QA_SkyFill` 补光 + `QA_Camera` + `QA_GlobalVolume`（Bloom / Neutral tonemap / ColorAdjustments / Vignette，profile 资产 `Assets/Maps/SovietBlueHour/SovietBlueHour_QAPost.asset`）。
环境：flat 深蓝 ambient(#22344E×1.35)、Exp² 雾(#2C4058×1.6, density 0.0011)、相机 SolidColor 深蓝。
月光方向由 Blender `SUN_MOON` 欧拉角 (64°,0,−28°) 换算，颜色 #9DB8DD、intensity 1.15、Hard shadows。**这些只是 QA 近似光照，主场景请用你自己的统一灯光配置。**

### 5.3 编辑器脚本（菜单可复用）

| 文件 | 菜单 | 作用 |
|---|---|---|
| `Assets/Editor/MapMigration/ImportSovietBlueHour.cs` | `UnityLITF/地图/① 导入 SovietBlueHour（GLB → URP）+ 建 prefab + 测试场景` | 设 importer（URP/Z）→ 重导 → 统计 → 存 prefab → 建测试场景 |
| 同文件 | — | 批量入口 `ImportSovietBlueHour.RunStage1` |
| `Assets/Editor/MapMigration/MapImportQA.cs` | `UnityLITF/地图/② 渲染导入 QA 截图`、`③ 审计材质`、`⑤ 洋红诊断 + shader 支持 + 重渲` | 材质审计、QA 渲染、洋红诊断、shader 支持报告 |
| 同文件 | — | 批量入口 `MapImportQA.RunStage2` / `MapImportQA.RunDiagnosis` |
| `Assets/Editor/MapMigration/MapGeometryProbe.cs` | `UnityLITF/地图/④ 几何/坐标复核（只读）` | prefab 世界包围盒 + 逐节点 bounds + 轴映射核对 |

> 未改动 `BatchProbe.cs` 与 `SovietBlueHourSceneBuilder.cs`（Lead 的文件）。

### 5.4 机器可读产物（`Logs/map_import/`）

| 文件 | 内容 |
|---|---|
| `import_stats.json` | 导入统计（节点、tris、材质、shader、emissive、bounds、prefab 校验、耗时） |
| `material_audit.json` | 571 材质逐项审计（shader/baseMap/emission/透明/双面 + 自发光前 40 名） |
| `shader_support.json` | 2047 个 renderer 材质槽位的 `isSupported` / `ShaderHasError` / ShaderUtil 消息 |
| `geometry_probe.json` | 51 节点逐节点 world bounds + 轴映射 checks |
| `render_qa.json` | 5 张截图的机位 / 像素统计（meanLuma、black、pink、bright）/ status |
| `magenta_diagnosis.json` | 洋红问题结论与证据 |
| `stage1_timing.json` / `stage2_timing.json` / `stage3_timing.json` | 三次 Unity 批处理墙钟时间 |
| `stage1.log` / `stage2.log` / `stage3_diag.log` | 完整 Unity 日志 |
| `run_stage1.ps1` | 批处理 runner（含日志标记轮询计时） |
| `qa_01..05_*.png` | 5 张 1280×720 QA 渲染 |

---

## 6. QA 截图（1280×720，`Logs/map_import/`）

渲染方式：Editor 批处理里 `Camera.Render()` → HDR RT（RGBA16F, linear）→ `Graphics.Blit` 到 sRGB LDR RT → `ReadPixels` → `EncodeToPNG`；URP 后处理（Bloom 0.72/0.55、Neutral tonemap、曝光 +0.15、饱和 −16、Vignette 0.28）已开启。**每张先渲染一次丢弃预热 shader 变体，再渲染正式图**（见 §7）。

| # | 文件 | 机位（Unity 世界坐标 → 目标，vFOV） | 像素统计 | 画面内容（read_image 实看） |
|---|---|---|---|---|
| 1 | `qa_01_hero_aerial.png` | (150,120,180) → (0,10,0)，35° | mean 0.0925 / 黑 13.0% / **洋红 0.0000** / 高光 0.09% | 东北向 3/4 鸟瞰整片小区：成排 5 层赫鲁晓夫楼、9 层塔楼、中央广场与方尖碑、树列街道、路边汽车、成片暖黄窗光；地面读出浅色路面/积雪感；天空深藏青。**无粉红、无空洞。** |
| 2 | `qa_02_street_level.png` | (-100,2.5,-60) → (0,3,0)，60° | mean 0.0393 / 黑 54.7% / **洋红 0.0000** | 平视贴近一栋楼的山墙：可见石材/砖砌贴图细节、窗框与 1 扇亮着的窗；画面偏暗（机位紧贴建筑，视野被墙占据）。**可作为"贴图正常"的证据，但构图一般。** |
| 3 | `qa_03_night_detail.png` | (0,2.2,40) → (0,3,-40)，55° | mean 0.0526 / 黑 44.0% / **洋红 0.0000** | 楼间院落：近景矮墙/女儿墙、窄巷、地面碎石贴图、远处对面楼成排窗（暖黄 + 少量冷白）、夜空。**夜景氛围成立，但整体偏暗。** |
| 4 | `qa_04_hero_camhero.png` | (205,85,−195) → (0,12,10)，29.85°（= Blender CAM_HERO 换算） | mean 0.0737 / 黑 10.3% / **洋红 0.0000** | **与 Blender `renders/final/CAM_HERO.png` 构图一致**：右侧祖国母亲雕像立于基地、前方长条纪念广场、左下方八角亭、林荫大道与两侧住宅楼、远处高层塔楼；成片暖窗光。 |
| 5 | `qa_05_street_camave.png` | (−52,1.75,−71.5) → (30,3.5,−80)，39.75°（= Blender CAM_AVE 换算） | mean 0.0665 / 黑 23.7% / **洋红 0.0000** / 高光 0.66% | **与 Blender `renders/final/CAM_AVE.png` 构图一致**：公交候车棚、右侧公交车（车头 "5 ГАСТРОНОМ" 发光字清晰可读）、左侧带暖光壁灯的店面、电车线/电线杆、路面贴图。**最能证明贴图/材质/自发光/发光字都正确。** |

> 3 张验收所需机位中，qa_02 / qa_03 用的是任务书给的近似坐标，画面偏暗/贴墙——**若要更好的视觉 QA，建议用 qa_04 / qa_05 这种"Blender 原机位换算"**（换算公式已固化在 `MapImportQA.Shots` 里）。

---

## 7. 洋红（magenta）问题：已定位并修复（结论：batchmode 异步 shader 编译，不是材质缺陷）

### 7.1 现象
第一轮 stage2 渲染中，**只有第一张 `qa_01`** 出现约 **2.01%** 的 `(254,3,254)` 纯洋红像素（= `Hidden/InternalErrorShader`），表现为：右侧地面成片条纹（斑马线/铺装条）+ 多栋楼的窗光面 + 零星发光面。第 2–5 张同一场景、同一批材质 **0.0000%**；机位变换并不能解释 → 是**渲染顺序依赖**。

### 7.2 排除项（数据见 `shader_support.json`）
- 2047 个 renderer 材质槽位：`null` 槽 **0**、`materials.Length != subMeshCount` **0**、null shader **0**；
- 材质资产 571/571 `Universal Render Pipeline/Lit`，`isSupported = true`，`ShaderUtil.ShaderHasError = false`，`GetShaderMessages` **0 条**；
- GPU：NVIDIA RTX 4060 Laptop / Direct3D11 / shaderLevel 50；**没有** `-nographics`；
- 控制实验：把 88 个 `_EMISSION` 材质的关键字在内存里临时关掉再渲染 → 洋红仍为 0（且窗光消失），说明洋红面确实来自这批"发光变体"。

### 7.3 根因
**批处理模式下 URP/Lit 的 `_EMISSION` shader 变体是异步编译的**：第一次绘制尚未编译完成的变体会退化为 `Hidden/InternalErrorShader`（洋红）。第一张图命中、后续图正常，因为变体已经编完了。

### 7.4 修复（已固化进脚本，重渲后全部通过）
1. 渲染前 `ShaderUtil.allowAsyncCompilation = false;`
2. 先从 700 m 高空做一次"全图预热"渲染（覆盖全部可见材质）；
3. 每张图先渲染一次丢弃，再渲染正式图（两次取第二张）；
4. 渲染结束恢复 `allowAsyncCompilation`。

**修复后实测：qa_01 洋红 2.0092% → 0.0000%，5 张全部 0.0000%，status 全 ok。**

> ⚠ **给 Lead 的提醒**：你在主场景构建器里如果也用批处理 `Camera.Render()`/截图，**同样会命中这个坑**。建议照抄这三步（`MapImportQA.WarmupShaders()` + `allowAsyncCompilation=false` + 每张双渲染）。这跟材质/资产无关，不需要任何材质后处理。

---

## 8. 与 Blender 成品渲染的差异（预期内）
1. **GLB 不含灯光/相机**（导出时 LIGHTS、RENDER_ONLY 已剥离），Unity 场景必须自建灯光 → QA 图的光比/色调是我按 `tools/look.py`（SUN_MOON #9DB8DD、AMBIENT #22344E、FOG #2C4058）近似的，不是 Blender 的 1:1；
2. GLB 不包含 Blender 的体积雾 / 地面雾 / 天空穹顶 / 后期合成（Bloom、Khronos PBR Neutral、vignette）→ Unity 侧用 flat ambient + Exp² 雾 + URP Bloom/Neutral tonemap 近似；
3. `KHR_materials_specular`（90 个材质）UniGLTF 导入器不支持，忽略后 specular 走默认（0.5），金属/高光细节与 Blender 略不同；
4. 所有材质 `doubleSided`（GLB 原样）→ Unity 里全部 `_Cull = Off`，背面受光会略有差异。

---

## 9. 已知问题 / 风险清单

| # | 问题 | 影响 | 处理 / 建议 |
|---|---|---|---|
| 1 | 手写 `.glb.meta` 的 importer key 不被采纳（读到 0/0） | 首次导入用了默认轴/管线（实际默认也是 Z+Auto，结果相同） | 已用 SerializedObject 强制设为 Z + URP 并落盘；菜单入口可复用 |
| 2 | 批处理首帧洋红（异步 shader 编译） | 只影响第一张截图 | 已修复（§7），渲染脚本内置预热 |
| 3 | **2047 submesh → 2047 draw call** | 移动端/低配 GPU 有压力；街景视角下大量细碎 submesh | 见 §10 |
| 4 | 所有材质双面（`_Cull = Off`） | 光栅化开销约 ×1.3–2；背面光照不自然 | 建筑不透明材质可改 `Cull Back`（薄片标线/树叶保留双面） |
| 5 | 154 Texture2D ↔ 134 JPEG（20 个重复实例） | 显存浪费 | 可合并重复贴图（同图多 sampler） |
| 6 | `KHR_materials_specular` 被忽略（90 材质） | 高光/金属观感与 Blender 略差 | 如需 1:1，可写自定义 URP specular-setup 后处理（当前不必要） |
| 7 | 571 材质但只有 2047 submesh（平均每材质 3.6 个 submesh） | SRP Batcher 会因 keyword/属性不同拆批 | 合并材质或按材质重建顶点色可大幅降 draw call |
| 8 | 命名保留 glTF 后缀 `BLD_*.001` | 与人可读性 | 若主场景需要稳定 id，建议加一层 name→id 映射（`scene_manifest.json` 已有 id） |
| 9 | 资产不在 git 版本控制友好范围（117 MB 单文件） | clone/提交慢 | 建议 Git LFS 或保留在 `Maps/` 源目录、按需复制 |
| 10 | 自发光 HDR 最高 40（灯面/红色指示） | 无 Bloom/HDR 时会直接过曝成白块 | 主场景务必开 HDR + Bloom（URP asset 已 `m_SupportsHDR=1`） |

---

## 10. 性能建议（主场景构建参考）

1. **Draw call 优先**：2047 个 submesh 是主要瓶颈。建议：
   - 对静态建筑 mark static + **Static Batching**（Renderer 层面合批，draw call 可降到每材质/每批次级别，内存换 CPU）；
   - 或者离线把同材质的 submesh 合并（`Mesh.CombineMeshes`），再按"材质 → 1 个 submesh"重建，目标 submesh 数 < 500；
   - URP 2022.3 支持 SRP Batcher，但不同 keyword 组合（尤其 88 个 `_EMISSION`、3 个透明、双面）会拆批，合材质时把 keyword 归一。
2. **LOD**：400×300 m 小区，街景视角视距 150–300 m。给 BLD_* 建 2 级 LOD（原 mesh / 简化 50%），远景切换可减 40–60% 三角形。
3. **模块化拆块**：`PROPS`（262,938 tri / 99 submesh）与 `SITE`（14430 tri / 27 submesh）建议按空间象限拆成 4–9 个 chunk，配合 **Occlusion Culling**（街景相机遮挡率很高）。
4. **碰撞**：GLB 只有渲染 mesh；`Maps/SovietBlueHour/map/collision.glb` 与 `web/collision.json`、`nav_graph.json` 已在源目录，可作为碰撞/寻路来源（不在本次导入范围内）。
5. **贴图**：134 张 JPEG 在编辑器里是未压缩解码，打包时建议 BC7/ASTC + mipmap + 按距离流式；512×512 ×86 张可考虑合图集。
6. **阴影**：地图尺寸 1040×820 m，方向光阴影距离需要权衡（当前 URP asset shadowDistance=50，Ultra 档 150）；建议主光只对近景投影，远景用假阴影/贴地 AO。
7. **双面材质**：把建筑不透明材质的 `_Cull` 改回 Back（保留标线/树叶/旗帜双面），可省不少填充率。

---

## 11. 验收自检

| 验收项 | 结果 |
|---|---|
| `Assets/Maps/SovietBlueHour/SovietBlueHour_Map.prefab` 存在，实例化后 51 节点 | ✅ 51 children / 51 MeshRenderer / 2047 submesh / **1,417,342** tris |
| 3 张以上渲染 PNG 非空、非粉红、可见建筑/街道/窗光 | ✅ 5 张 1280×720，洋红 0.0000%，qa_01/04/05 建筑街道窗光齐全 |
| 工程正常编译 | ✅ 三次批处理均通过（最后一次 script compilation 8.60 s，日志 0 条 `error CS`），Lead 的最新 `MapWorldProfile/WorldData/CameraDirector/SkyGradient/SovietBlueHourSceneBuilder` 一并编译通过 |
| 主场景 `RainyConvenience_TaskPlan.unity` 未被修改 | ✅ SHA256 与开工前基线一致（另有 `RainyConvenience.unity`、`SampleScene.unity`、`GraphicsSettings.asset`、`QualitySettings.asset`、`UniversalRP.asset` 同样未变） |
| Unity 已退出、无残留进程 | ✅ 无 `Unity.exe` 进程 |
| 写入范围 | ✅ 仅 `Assets/Maps/SovietBlueHour/`、`Assets/Editor/MapMigration/`（新增 3 个脚本）、`Assets/Scenes/_SovietBlueHourImportTest.unity*`、`Logs/map_import/` |

---

## 12. 复现 / 重导步骤

```powershell
# 1) 重导 + 重建 prefab + 重建 QA 场景（改 GLB 或改 importer 设置后跑）
powershell -NoProfile -ExecutionPolicy Bypass -File "Logs/map_import/run_stage1.ps1" `
  -Method ImportSovietBlueHour.RunStage1 -LogName stage1.log -TimingName stage1_timing.json

# 2) 材质审计 + 5 张 QA 渲染
... -Method MapImportQA.RunStage2 -LogName stage2.log -TimingName stage2_timing.json

# 3) 洋红诊断 / shader 支持 / 预热重渲 / 几何坐标复核
... -Method MapImportQA.RunDiagnosis -LogName stage3_diag.log -TimingName stage3_timing.json
```

也可以在编辑器里直接用菜单 `UnityLITF/地图/①…⑤`。
Unity 路径：`D:\Unity\2022.3.62f3c1\Editor\Unity.exe`；批处理必须整段字符串传 `-ArgumentList`（`run_stage1.ps1` 已处理）。

---

## 13. 一句话给主场景构建器

> prefab `Assets/Maps/SovietBlueHour/SovietBlueHour_Map.prefab` 直接实例化即可（identity，51 子节点，世界包围盒 min(−520,−0.292,−410) / max(520,68.625,410)，地面 y≈0，最高 68.6 m，`BLD_MOTHERLAND` 中心 ≈ (0,22.3,143.6)）；材质全 URP/Lit、自发光 HDR 已带强度、无需后处理；**批处理里 Camera.Render 前记得关异步 shader 编译 + 预热一帧**，否则第一张截图会有洋红。
