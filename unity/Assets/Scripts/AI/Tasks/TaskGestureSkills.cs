using System;
using System.Collections.Generic;
using UnityEngine;
using Yixuan.Motion;
namespace Yixuan.AI.Tasks
{
    public static class TaskGestureSkills
    {
        public static MixamoActionManifest.Entry[] Available()
        {
            var actor=UnityEngine.Object.FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            if(actor==null||actor.Manifest==null||actor.Manifest.Actions==null||actor.AnimPlayer==null) return new MixamoActionManifest.Entry[0];
            var entries=new List<MixamoActionManifest.Entry>();
            foreach(var e in actor.Manifest.Actions) if(e!=null&&!string.IsNullOrEmpty(e.Id)&&actor.AnimPlayer.HasClip(e.ClipName)) entries.Add(e);
            return entries.ToArray();
        }
        public static MixamoActionManifest.Entry Get(string id)
        { foreach(var e in Available()) if(e.Id==id) return e; return null; }
        public static string Match(string phrase)
        {
            string normalized=phrase.Replace("挥舞双手","双手挥手").Replace("挥动双手","双手挥手").Replace("双手挥舞","双手挥手").Replace("两只手挥舞","双手挥手");
            string best=null; int length=0;
            foreach(var e in Available())
            {
                if(!string.IsNullOrEmpty(e.Label)&&normalized.Contains(e.Label)&&e.Label.Length>length) {best=e.Id;length=e.Label.Length;}
                if(string.Equals(phrase.Trim(),e.Id,StringComparison.OrdinalIgnoreCase)) return e.Id;
            }
            return best;
        }
    }
}
