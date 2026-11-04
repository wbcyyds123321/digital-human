// VoiceServicesAutoStart.cs
// 打开本 Unity 项目时自动启动 Yixuan 语音服务（看门狗 + tts 8766 + voice 8767），
// 关闭编辑器（且是本次编辑器启动的服务）时自动停止。开机不再自启。
//
// 菜单：Tools / Yixuan / Voice Services / …
//   Start          手动启动
//   Stop           手动停止
//   Health Check   查看 8766 / 8767 健康状态
//   Auto-Start On Project Open  自动启动开关（默认开）
//
// 相关文件：voice/voice_watchdog.ps1（启动 + 看护）
//           voice/stop_voice.ps1（停止）
// 计划任务 "Yixuan Voice Server Watchdog" 已去掉登录触发器，仅用于手动/schtasks 调用。

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

[InitializeOnLoad]
public static class VoiceServicesAutoStart
{
    private const string TaskName = "Yixuan Voice Server Watchdog";
    private const string AutoStartPref = "Yixuan.VoiceServices.AutoStartOnProjectOpen";
    private const string StartedByEditorKey = "Yixuan.VoiceServices.StartedByThisEditorSession";
    private const string MenuRoot = "Tools/Yixuan/Voice Services/";

    private const string TtsPort = "8766";
    private const string VoicePort = "8767";

    private static string ProjectRoot
    {
        get { return Directory.GetParent(Application.dataPath).FullName; }
    }

    private static string VoiceDir
    {
        get { return Path.Combine(ProjectRoot, "voice"); } // public repo layout: voice/ at project root
    }

    private static string WatchdogScript
    {
        get { return Path.Combine(VoiceDir, "voice_watchdog.ps1"); }
    }

    private static string StopScript
    {
        get { return Path.Combine(VoiceDir, "stop_voice.ps1"); }
    }

    static VoiceServicesAutoStart()
    {
        EditorApplication.delayCall += OnEditorLoaded;
        EditorApplication.quitting += OnEditorQuitting;
    }

    // ---------------- 自动启停 ----------------

    private static void OnEditorLoaded()
    {
        if (!AutoStartEnabled) return;
        if (IsHealthy(TtsPort) && IsHealthy(VoicePort)) return; // 已经在跑
        if (!File.Exists(WatchdogScript))
        {
            Debug.LogWarning("[Voice] 找不到看门狗脚本: " + WatchdogScript);
            return;
        }
        RunHidden("schtasks.exe", "/Run /TN \"" + TaskName + "\"");
        SessionState.SetBool(StartedByEditorKey, true);
        Debug.Log("[Voice] 已随项目启动语音服务（TTS 8766 / Voice 8767）。首次启动会有一次约 1.5 秒的校准噪声，属正常。");
    }

    private static void OnEditorQuitting()
    {
        if (!SessionState.GetBool(StartedByEditorKey, false)) return;
        SessionState.SetBool(StartedByEditorKey, false);
        StopServices();
    }

    // ---------------- 菜单 ----------------

    [MenuItem(MenuRoot + "Start", false, 100)]
    public static void StartServices()
    {
        if (IsHealthy(TtsPort) && IsHealthy(VoicePort))
        {
            Debug.Log("[Voice] 服务已在运行，无需重复启动。");
            return;
        }
        if (!File.Exists(WatchdogScript))
        {
            Debug.LogWarning("[Voice] 找不到看门狗脚本: " + WatchdogScript);
            return;
        }
        RunHidden("schtasks.exe", "/Run /TN \"" + TaskName + "\"");
        SessionState.SetBool(StartedByEditorKey, true);
        Debug.Log("[Voice] 已请求启动语音服务。");
    }

    [MenuItem(MenuRoot + "Stop", false, 101)]
    public static void StopServices()
    {
        if (!File.Exists(StopScript))
        {
            Debug.LogWarning("[Voice] 找不到停止脚本: " + StopScript);
            return;
        }
        RunHidden("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + StopScript + "\"");
        Debug.Log("[Voice] 已请求停止语音服务。");
    }

    [MenuItem(MenuRoot + "Health Check", false, 102)]
    public static void CheckHealth()
    {
        Debug.Log("[Voice] TTS(8766)=" + IsHealthy(TtsPort) + "   Voice(8767)=" + IsHealthy(VoicePort));
    }

    [MenuItem(MenuRoot + "Auto-Start On Project Open", true, 110)]
    public static bool ToggleAutoStartValidate()
    {
        Menu.SetChecked(MenuRoot + "Auto-Start On Project Open", AutoStartEnabled);
        return true;
    }

    [MenuItem(MenuRoot + "Auto-Start On Project Open", false, 110)]
    public static void ToggleAutoStart()
    {
        AutoStartEnabled = !AutoStartEnabled;
        Debug.Log("[Voice] 项目打开时自动启动 = " + AutoStartEnabled);
    }

    // ---------------- 工具函数 ----------------

    private static bool AutoStartEnabled
    {
        get { return EditorPrefs.GetBool(AutoStartPref, true); }
        set { EditorPrefs.SetBool(AutoStartPref, value); }
    }

    private static bool IsHealthy(string port)
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/health");
            request.Timeout = 500;
            request.ReadWriteTimeout = 500;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                return response.StatusCode == HttpStatusCode.OK;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void RunHidden(string fileName, string arguments)
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo(fileName, arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.WorkingDirectory = ProjectRoot;
            Process.Start(info);
        }
        catch (Exception e)
        {
            Debug.LogError("[Voice] 启动失败: " + e.Message);
        }
    }
}
