using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Yixuan.AI
{
    public static class CharacterProfileStore
    {
        public static string FilePath
        {
            get { return Path.Combine(Application.persistentDataPath, "character_skills.json"); }
        }

        public static CharacterProfile Load(AIConfig config, out string issue, string path = null)
        {
            issue = null;
            try
            {
                string target = path ?? FilePath;
                if (!File.Exists(target)) return CharacterProfile.FromConfig(config);
                var loaded = JsonUtility.FromJson<CharacterProfile>(File.ReadAllText(target, Encoding.UTF8));
                if (loaded == null) issue = "人格配置为空。";
                else if (loaded.TryValidate(out issue)) return loaded;
            }
            catch (Exception exception) { issue = "读取人格配置失败：" + exception.Message; }
            return CharacterProfile.FromConfig(config);
        }

        public static bool TrySave(CharacterProfile profile, out string issue, string path = null)
        {
            issue = null;
            if (profile == null) { issue = "人格配置为空。"; return false; }
            if (!profile.TryValidate(out issue)) return false;
            string temporary = null;
            try
            {
                string target = Path.GetFullPath(path ?? FilePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                temporary = target + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(profile, true), Encoding.UTF8);
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
                return true;
            }
            catch (Exception exception)
            {
                issue = "保存失败，未应用修改：" + exception.Message;
                return false;
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
