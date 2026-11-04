using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>Batchmode 探针：验证 -executeMethod 通路、工程可打开、日志可写。</summary>
public static class BatchProbe
{
    public static void Run()
    {
        Debug.Log("[BatchProbe] OK dataPath=" + Application.dataPath + " unity=" + Application.unityVersion);
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/batch_probe_marker.txt", System.DateTime.Now.ToString("o") + " " + Application.unityVersion);
        Debug.Log("[BatchProbe] 写入 Logs/batch_probe_marker.txt 完成");
        EditorApplication.Exit(0);
    }
}
