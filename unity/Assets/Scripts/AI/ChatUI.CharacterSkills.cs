using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yixuan.AI
{
    public partial class ChatUI
    {
        VisualElement _skillEditor;
        CharacterProfile _profileDraft;
        TextField _profileNameField;
        TextField _personaField;
        DropdownField _skillSelector;
        TextField _skillTitleField;
        TextField _skillInstructionsField;
        Toggle _skillEnabledField;
        Label _skillIdLabel;
        Label _profileSaveStatus;
        Button _removeSkillButton;
        Button _characterSkillsShortcut;
        TextField _promptPreview;
        Foldout _promptPreviewFoldout;
        int _selectedSkill;
        bool _profileUIRefreshing;

        void BuildCharacterSkillEntry(VisualElement parent)
        {
            var entry = SkillButton("人格 / Skills 设置", OpenCharacterSkills);
            entry.name = "open-character-skills";
            entry.style.marginTop = 10;
            parent.Add(entry);
            var shortcut = SkillButton("人格 / Skills", OpenCharacterSkills);
            _characterSkillsShortcut = shortcut;
            shortcut.name = "character-skills-shortcut";
            shortcut.style.position = Position.Absolute;
            shortcut.style.right = 126;
            shortcut.style.top = 24;
            shortcut.style.width = 142;
            _root.Add(shortcut);
        }

        public void OpenCharacterSkills()
        {
            if (!_built || _root == null || Session == null) return;
            SetControlPanel(false);
            if (_skillEditor == null) BuildCharacterSkillEditor();
            if (_profileDraft == null) SetProfileDraft(Session.CurrentCharacterProfile());
            _skillEditor.style.display = DisplayStyle.Flex;
            _skillEditor.BringToFront();
        }

        public void RefreshCharacterProfileView()
        {
            if (_nameLabel != null && Session != null) _nameLabel.text = Session.CurrentCharacterProfile().CharacterName;
        }

        public void MountCharacterSkills(VisualElement host)
        {
            if (host == null || Session == null || _built) throw new InvalidOperationException("独立设置界面需要空闲的 ChatUI 和有效会话。");
            _root = host;
            _built = true;
            BuildCharacterSkillEditor();
            SetProfileDraft(Session.CurrentCharacterProfile());
            _skillEditor.style.left = 0;
            _skillEditor.style.top = 0;
            _skillEditor.style.right = 0;
            _skillEditor.style.bottom = 0;
            _skillEditor.style.width = StyleKeyword.Auto;
            _skillEditor.style.maxWidth = StyleKeyword.None;
            _skillEditor.Q<Button>("close-character-skills").style.display = DisplayStyle.None;
        }

        void BuildCharacterSkillEditor()
        {
            _skillEditor = new VisualElement { name = "character-skills-editor" };
            _skillEditor.style.position = Position.Absolute;
            _skillEditor.style.left = 16;
            _skillEditor.style.top = 70;
            _skillEditor.style.bottom = 100;
            _skillEditor.style.width = new Length(96, LengthUnit.Percent);
            _skillEditor.style.maxWidth = 720;
            _skillEditor.style.backgroundColor = new Color(0.07f, 0.09f, 0.13f, 0.98f);
            _skillEditor.style.paddingLeft = 16;
            _skillEditor.style.paddingRight = 16;
            _skillEditor.style.paddingTop = 12;
            _skillEditor.style.paddingBottom = 12;
            _skillEditor.style.borderTopLeftRadius = 10;
            _skillEditor.style.borderTopRightRadius = 10;
            _skillEditor.style.borderBottomLeftRadius = 10;
            _skillEditor.style.borderBottomRightRadius = 10;
            var header = SkillRow();
            var title = new Label("人格 / Skills");
            title.style.fontSize = 18;
            title.style.color = TextMain;
            title.style.flexGrow = 1;
            header.Add(title);
            var close = SkillButton("关闭", delegate { _skillEditor.style.display = DisplayStyle.None; });
            close.name = "close-character-skills";
            header.Add(close);
            _skillEditor.Add(header);
            _skillEditor.Add(SkillNote("人格定义她是谁；Skill 定义交流/能力使用规则。保存后下一轮生效；修改后请点击保存，聊天不会自动清空。"));
            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "character-skills-scroll" };
            scroll.style.flexGrow = 1;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _skillEditor.Add(scroll);
            _profileNameField = SkillText("角色名", false, 40);
            _profileNameField.name = "profile-character-name";
            _profileNameField.RegisterValueChangedCallback(change => { if (_profileUIRefreshing) return; _profileDraft.CharacterName = change.newValue; MarkProfileDirty(); });
            scroll.Add(_profileNameField);
            _personaField = SkillText("人格 / 背景 / 说话方式", true, CharacterProfile.MaxPersonaChars);
            _personaField.name = "profile-persona";
            _personaField.RegisterValueChangedCallback(change => { if (_profileUIRefreshing) return; _profileDraft.Persona = change.newValue; MarkProfileDirty(); });
            scroll.Add(_personaField);
            scroll.Add(SkillNote("例如：你是一位沉稳的店员，称呼我为店长，回答先给结论。这里不修改声音，也不会训练模型或新增动画。"));
            BuildSkillFields(scroll);
            BuildCameraCalibration(scroll);
            var capabilities = new Foldout { text = "已安装能力清单（只读）", value = false };
            capabilities.Add(SkillNote("执行仍经过目录校验；添加文字 Skill 不会凭空增加动作或访问电脑文件。"));
            capabilities.Add(SkillNote("云台\n" + CameraMoveCatalog.Describe() + "\n表情\n" + ActionCatalog.DescribeEmotions() + "\n动作\n" + ActionCatalog.DescribeGestures()));
            scroll.Add(capabilities);
            _promptPreviewFoldout = new Foldout { text = "预览当前草稿的系统提示词", value = false, name = "profile-prompt-preview" };
            _promptPreview = SkillText("实际协议预览（草稿）", true, -1);
            _promptPreview.isReadOnly = true;
            _promptPreview.style.height = 200;
            _promptPreviewFoldout.Add(_promptPreview);
            _promptPreviewFoldout.RegisterValueChangedCallback(change => { if (change.target == _promptPreviewFoldout && change.newValue) RefreshSkillPromptPreview(); });
            scroll.Add(_promptPreviewFoldout);
            _profileSaveStatus = SkillNote("草稿尚未修改。");
            _profileSaveStatus.name = "profile-save-status";
            _skillEditor.Add(_profileSaveStatus);
            var buttons = SkillRow();
            var save = SkillButton("保存并应用", SaveCharacterSkills);
            save.name = "save-character-skills";
            buttons.Add(save);
            buttons.Add(SkillButton("撤销未保存", delegate { SetProfileDraft(Session.CurrentCharacterProfile()); }));
            buttons.Add(SkillButton("出厂配置草稿", delegate { SetProfileDraft(CharacterProfile.FromConfig(Session.Config)); MarkProfileDirty(); }));
            _skillEditor.Add(buttons);
            _skillEditor.Add(SkillNote("独立保存到：" + CharacterProfileStore.FilePath + "\n不清聊天记忆，不改变模型/API/TTS 配置。"));
            _root.Add(_skillEditor);
        }

        void BuildSkillFields(VisualElement parent)
        {
            var heading = SkillNote("Skills：开启后作为规则注入 DeepSeek；关闭云台 Skill 会同时禁止本地、预规划和回复阶段的对话转向。");
            heading.style.marginTop = 10;
            parent.Add(heading);
            var row = SkillRow();
            _skillSelector = new DropdownField { name = "profile-skill-selector" };
            _skillSelector.style.flexGrow = 1;
            _skillSelector.RegisterValueChangedCallback(change => { if (_profileUIRefreshing) return; _selectedSkill = _skillSelector.index; RefreshSkillFields(); });
            row.Add(_skillSelector);
            row.Add(SkillButton("新增 Skill", AddCharacterSkill));
            _removeSkillButton = SkillButton("删除", RemoveCharacterSkill);
            row.Add(_removeSkillButton);
            parent.Add(row);
            _skillIdLabel = SkillNote("");
            parent.Add(_skillIdLabel);
            _skillTitleField = SkillText("Skill 名称", false, 40);
            _skillTitleField.RegisterValueChangedCallback(change => { if (_profileUIRefreshing) return; _profileDraft.Skills[_selectedSkill].Title = change.newValue; MarkProfileDirty(); });
            parent.Add(_skillTitleField);
            _skillEnabledField = new Toggle("启用此 Skill") { name = "profile-skill-enabled" };
            _skillEnabledField.style.color = TextMain;
            _skillEnabledField.RegisterValueChangedCallback(change => { if (_profileUIRefreshing) return; _profileDraft.Skills[_selectedSkill].Enabled = change.newValue; MarkProfileDirty(); });
            parent.Add(_skillEnabledField);
            _skillInstructionsField = SkillText("适用情境 / 处理步骤 / 回复习惯", true, CharacterProfile.MaxSkillChars);
            _skillInstructionsField.name = "profile-skill-instructions";
            _skillInstructionsField.RegisterValueChangedCallback(change => { if (_profileUIRefreshing) return; _profileDraft.Skills[_selectedSkill].Instructions = change.newValue; MarkProfileDirty(); });
            parent.Add(_skillInstructionsField);
        }

        void SetProfileDraft(CharacterProfile profile)
        {
            _profileDraft = profile.Clone();
            _profileUIRefreshing = true;
            _profileNameField.SetValueWithoutNotify(_profileDraft.CharacterName);
            _personaField.SetValueWithoutNotify(_profileDraft.Persona);
            _profileUIRefreshing = false;
            RefreshSkillFields();
            RefreshCameraBindingRows();
            if (_promptPreviewFoldout.value) RefreshSkillPromptPreview();
            _profileSaveStatus.text = "已读取当前配置；修改后请保存。";
        }

        void RefreshSkillFields()
        {
            _selectedSkill = Mathf.Clamp(_selectedSkill, 0, _profileDraft.Skills.Count - 1);
            _profileUIRefreshing = true;
            var choices = new List<string>();
            for (int index = 0; index < _profileDraft.Skills.Count; index++) choices.Add((index + 1) + " · " + _profileDraft.Skills[index].Title);
            _skillSelector.choices = choices;
            _skillSelector.SetValueWithoutNotify(choices[_selectedSkill]);
            var skill = _profileDraft.Skills[_selectedSkill];
            _skillIdLabel.text = "ID：" + skill.Id + (skill.Id == CharacterProfile.CameraSkillId ? "（内置执行能力，可关闭，不可删除）" : "（对话规则，不是新执行器）");
            _skillTitleField.SetValueWithoutNotify(skill.Title);
            _skillEnabledField.SetValueWithoutNotify(skill.Enabled);
            _skillInstructionsField.SetValueWithoutNotify(skill.Instructions);
            _removeSkillButton.SetEnabled(skill.Id != CharacterProfile.CameraSkillId);
            _profileUIRefreshing = false;
        }

        void AddCharacterSkill()
        {
            if (_profileDraft.Skills.Count >= CharacterProfile.MaxSkills) { _profileSaveStatus.text = "最多保留 12 个 Skills。"; return; }
            _profileDraft.Skills.Add(new CharacterSkill { Id = "skill_" + Guid.NewGuid().ToString("N").Substring(0, 10), Title = "新 Skill" });
            _selectedSkill = _profileDraft.Skills.Count - 1;
            RefreshSkillFields();
            MarkProfileDirty();
        }

        void RemoveCharacterSkill()
        {
            if (_profileDraft.Skills[_selectedSkill].Id == CharacterProfile.CameraSkillId) return;
            _profileDraft.Skills.RemoveAt(_selectedSkill);
            RefreshSkillFields();
            MarkProfileDirty();
        }

        void SaveCharacterSkills()
        {
            string issue;
            if (!Session.TryApplyCharacterProfile(_profileDraft, true, out issue)) { _profileSaveStatus.text = issue; return; }
            SetProfileDraft(Session.CurrentCharacterProfile());
            _profileSaveStatus.text = "已保存并应用；下一轮对话生效，当前轮不变。旧聊天不会自动清空。";
        }

        void MarkProfileDirty()
        {
            if (_profileSaveStatus != null) _profileSaveStatus.text = "草稿未保存；点击“保存并应用”后下一轮生效。";
            if (_promptPreviewFoldout != null && _promptPreviewFoldout.value) RefreshSkillPromptPreview();
        }

        void RefreshSkillPromptPreview()
        {
            string issue;
            _promptPreview.SetValueWithoutNotify(_profileDraft.TryValidate(out issue) ? PromptBuilder.BuildSystemPrompt(Session.Config, _profileDraft) : issue);
        }

        Button SkillButton(string text, Action clicked)
        {
            var button = new Button(clicked) { text = text };
            button.style.height = 30;
            button.style.fontSize = 12;
            button.style.flexShrink = 0;
            return button;
        }

        VisualElement SkillRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 6;
            row.style.marginBottom = 4;
            row.style.flexShrink = 0;
            return row;
        }

        Label SkillNote(string text)
        {
            var label = new Label(text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.color = TextDim;
            label.style.fontSize = 12;
            label.style.marginTop = 5;
            label.style.marginBottom = 5;
            label.style.flexShrink = 0;
            return label;
        }

        TextField SkillText(string label, bool multiline, int maxLength)
        {
            var field = new TextField(label) { multiline = multiline, maxLength = maxLength };
            field.style.flexShrink = 0;
            field.style.marginTop = 6;
            field.style.flexDirection = FlexDirection.Column;
            if (multiline) field.style.height = 145;
            var input = field.Q("unity-text-input");
            if (input != null) { input.style.backgroundColor = new Color(0.94f, 0.95f, 0.97f); input.style.whiteSpace = WhiteSpace.Normal; }
            foreach (var element in field.Query<TextElement>().Build()) { element.style.color = new Color(0.08f, 0.10f, 0.14f); element.style.fontSize = 13; }
            field.labelElement.style.color = TextDim;
            field.labelElement.style.minWidth = 0;
            field.RegisterCallback<FocusInEvent>(delegate { _inputFocused = false; });
            field.RegisterCallback<KeyDownEvent>(change => { if (change.keyCode == KeyCode.Return || change.keyCode == KeyCode.KeypadEnter) change.StopPropagation(); });
            return field;
        }
    }
}
