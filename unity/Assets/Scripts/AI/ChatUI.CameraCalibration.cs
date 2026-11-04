using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yixuan.AI
{
    public partial class ChatUI
    {
        TextField _bindingPhraseField;
        FloatField _bindingAngleField;
        DropdownField _bindingMoveField;
        VisualElement _bindingRows;
        TextField _cameraTestField;
        Label _cameraTestResult;
        readonly List<string> _cameraMoveIds = new List<string>();

        void BuildCameraCalibration(VisualElement parent)
        {
            var foldout = new Foldout { text = "云台校准 / 整句映射", value = false, name = "camera-calibration" };
            foldout.style.marginTop = 10;
            foldout.Add(SkillNote("把常用说法直接映射到已安装动作，不依赖模型猜词。只匹配完整句子（忽略句末标点），不从视觉记录执行；不支持否定/过去句。填完先加入草稿，再保存。"));
            _bindingPhraseField = SkillText("触发句，例如：往左偏一点", false, 100);
            _bindingPhraseField.name = "camera-binding-phrase";
            foldout.Add(_bindingPhraseField);
            var choices = new List<string>();
            foreach (var move in CameraMoveCatalog.Moves) { _cameraMoveIds.Add(move.Id); choices.Add(move.Id + " · " + move.Label); }
            _bindingMoveField = new DropdownField("动作", choices, 0) { name = "camera-binding-move" };
            foldout.Add(_bindingMoveField);
            _bindingAngleField = new FloatField("请求角度（0 使用默认）") { name = "camera-binding-angle" };
            _bindingAngleField.style.color = new Color(0.10f, 0.12f, 0.15f);
            _bindingAngleField.labelElement.style.color = TextDim;
            foldout.Add(_bindingAngleField);
            foldout.Add(SkillButton("加入 / 更新草稿映射", AddCameraBinding));
            _bindingRows = new ScrollView(ScrollViewMode.Vertical) { name = "camera-binding-rows" };
            _bindingRows.style.height = 105;
            _bindingRows.style.flexShrink = 0;
            foldout.Add(_bindingRows);
            _cameraTestField = SkillText("测试一句话（使用当前草稿）", false, 150);
            _cameraTestField.name = "camera-skill-test-input";
            foldout.Add(_cameraTestField);
            var test = SkillButton("只解析，不转镜头", delegate { _cameraTestResult.text = CameraCommandBinding.Diagnose(_profileDraft, _cameraTestField.value); });
            test.name = "camera-skill-dry-run";
            foldout.Add(test);
            _cameraTestResult = SkillNote("测试不调用模型、不播放动作、不转动相机。角度显示会包含实际增益/限幅；跟踪预设的方向由目标位置决定。" );
            _cameraTestResult.name = "camera-skill-test-result";
            foldout.Add(_cameraTestResult);
            parent.Add(foldout);
        }

        void AddCameraBinding()
        {
            if (_bindingMoveField.index < 0 || _bindingMoveField.index >= _cameraMoveIds.Count) return;
            var binding = new CameraCommandBinding
            {
                Phrase = CameraCommandBinding.NormalizePhrase(_bindingPhraseField.value),
                MoveId = _cameraMoveIds[_bindingMoveField.index],
                Angle = _bindingAngleField.value
            };
            string issue;
            if (!binding.TryValidate(out issue)) { _cameraTestResult.text = issue; return; }
            int existing = _profileDraft.CameraBindings.FindIndex(item => CameraCommandBinding.NormalizePhrase(item.Phrase) == binding.Phrase);
            if (existing >= 0) _profileDraft.CameraBindings[existing] = binding;
            else
            {
                if (_profileDraft.CameraBindings.Count >= CharacterProfile.MaxBindings) { _cameraTestResult.text = "最多保留 24 条映射。"; return; }
                _profileDraft.CameraBindings.Add(binding);
            }
            RefreshCameraBindingRows();
            _cameraTestField.SetValueWithoutNotify(binding.Phrase);
            _cameraTestResult.text = CameraCommandBinding.Diagnose(_profileDraft, binding.Phrase);
            MarkProfileDirty();
        }

        void RefreshCameraBindingRows()
        {
            if (_bindingRows == null || _profileDraft == null) return;
            _bindingRows.Clear();
            if (_profileDraft.CameraBindings.Count == 0) _bindingRows.Add(SkillNote("尚无自定义映射；内置规则与 DeepSeek 路径仍保留。"));
            foreach (var binding in _profileDraft.CameraBindings)
            {
                var row = SkillRow();
                var edit = SkillButton(binding.Phrase + " → " + binding.MoveId + (binding.Angle > 0 ? " " + binding.Angle.ToString("F0") + "°" : " 默认"), delegate
                {
                    _bindingPhraseField.SetValueWithoutNotify(binding.Phrase);
                    int index = _cameraMoveIds.IndexOf(binding.MoveId);
                    _bindingMoveField.index = index;
                    _bindingAngleField.SetValueWithoutNotify(binding.Angle);
                });
                edit.style.flexShrink = 1;
                edit.style.flexGrow = 1;
                row.Add(edit);
                row.Add(SkillButton("删除", delegate { _profileDraft.CameraBindings.Remove(binding); RefreshCameraBindingRows(); MarkProfileDirty(); }));
                _bindingRows.Add(row);
            }
        }
    }
}
