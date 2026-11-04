using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Yixuan.AI;

namespace Yixuan.EditorTools
{
    public sealed class CharacterSkillsWindow : EditorWindow
    {
        GameObject _host;

        [MenuItem("UnityLITF/AI/人格与 Skills 设置")]
        public static void Open()
        {
            var window = GetWindow<CharacterSkillsWindow>();
            window.titleContent = new GUIContent("人格 / Skills");
            window.minSize = new Vector2(600, 600);
            if (window.position.height < 600) window.position = new Rect(100, 80, 740, 820);
            window.Show();
        }

        void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public void CreateGUI()
        {
            ReleaseHost();
            rootVisualElement.Clear();
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                rootVisualElement.Add(new Label("Play 期间请使用游戏右上角的人格 / Skills，退出 Play 后可在这里编辑。"));
                return;
            }
            var sceneSession = Object.FindObjectOfType<ChatSession>();
            var config = sceneSession != null && sceneSession.Config != null ? sceneSession.Config : AIConfig.FindDefault();
            _host = new GameObject("CharacterSkillsEditorHost") { hideFlags = HideFlags.HideAndDontSave };
            _host.SetActive(false);
            var session = _host.AddComponent<ChatSession>();
            session.Config = config;
            string issue;
            var profile = CharacterProfileStore.Load(config, out issue);
            if (!string.IsNullOrEmpty(issue)) Debug.LogWarning("[CharacterSkills] " + issue + " 使用出厂配置，原文件未覆盖。");
            session.TryApplyCharacterProfile(profile, false, out issue);
            var ui = _host.AddComponent<ChatUI>();
            ui.Session = session;
            session.UI = ui;
            ui.MountCharacterSkills(rootVisualElement);
        }

        void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.EnteredEditMode) CreateGUI();
        }

        void ReleaseHost()
        {
            if (_host != null) DestroyImmediate(_host);
            _host = null;
        }

        void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            ReleaseHost();
        }
    }
}
