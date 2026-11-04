using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;

namespace CodexFloat {
    public class CurrentTasksDocument {
        public int Version=1;
        public DateTime SavedAt;
        public List<Session> Sessions=new List<Session>();
        public List<Session> Dismissed=new List<Session>();
    }
    public static class CurrentTaskIdentity {
        public static bool SameRound(Session a,Session b){
            if(a==null||b==null||!string.Equals(a.Key,b.Key,StringComparison.OrdinalIgnoreCase))return false;
            if(!string.IsNullOrEmpty(a.TurnId)&&!string.IsNullOrEmpty(b.TurnId)){
                if(a.TurnId==b.TurnId)return true;
                if(!a.TurnId.StartsWith("desktop-",StringComparison.Ordinal)&&!b.TurnId.StartsWith("desktop-",StringComparison.Ordinal))return false;
            }
            if(a.StartedAt!=DateTime.MinValue&&b.StartedAt!=DateTime.MinValue&&Math.Abs((a.StartedAt.ToUniversalTime()-b.StartedAt.ToUniversalTime()).TotalMilliseconds)<1)return true;
            if(a.Terminal&&b.Terminal&&a.FinishedAt!=DateTime.MinValue&&b.FinishedAt!=DateTime.MinValue&&Math.Abs((a.FinishedAt.ToUniversalTime()-b.FinishedAt.ToUniversalTime()).TotalSeconds)<1)return true;
            return a.TurnId==b.TurnId&&a.StartedAt==b.StartedAt;
        }
        public static bool RuntimeRunning(string platform){
            if(platform=="claude")return ClaudeDesktopSource.RuntimeRunning();
            var processes=Process.GetProcessesByName(platform=="workbuddy"?"WorkBuddy":"codex");bool live=processes.Length>0;foreach(var p in processes)p.Dispose();return live;
        }
    }
    // A cross-platform list survives application/PC restarts even while provider
    // files are unavailable. Dismissals are scoped to a round, not forever to a chat.
    public class CurrentTasks {
        readonly object gate=new object();readonly string path;
        readonly Dictionary<string,Session> items=new Dictionary<string,Session>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,Session> dismissed=new Dictionary<string,Session>(StringComparer.OrdinalIgnoreCase);
        DateTime lastSaved,retryAt;bool dirty;
        public bool Checked {get;private set;}
        public string Error {get;private set;}
        public CurrentTasks(string dataDir){
            path=Path.Combine(dataDir,"current-tasks.json");Error="";
            if(!Load(path)&&!Load(path+".bak")){
                foreach(string name in new[]{"sessions.json","workbuddy-sessions.json"})try{
                    string file=Path.Combine(dataDir,name);var state=Json.Serializer().Deserialize<SavedState>(Json.SharedText(file));DateTime stamp=File.GetLastWriteTimeUtc(file);
                    foreach(var s in state.Sessions.Where(s=>s.Visible&&!s.IsChild)){var copy=s.Copy();copy.Platform=name.StartsWith("workbuddy")?"workbuddy":"codex";Restore(copy,stamp);}
                }catch{}
            }
        }
        void Restore(Session s,DateTime stamp){
            if(s==null||s.Id==""||s.IsChild)return;
            if(s.Status=="running"||s.Status=="waiting"){s.Status="unknown";if(stamp>s.LastActivity)s.LastActivity=stamp;}
            s.Visible=true;items[s.Key]=s;
        }
        bool Load(string file){try{if(!File.Exists(file))return false;var doc=Json.Serializer().Deserialize<CurrentTasksDocument>(Json.SharedText(file));if(doc==null||doc.Sessions==null)return false;
            foreach(var s in doc.Sessions)Restore(s,doc.SavedAt);foreach(var s in doc.Dismissed??new List<Session>())if(s!=null&&s.Id!="")dismissed[s.Key]=s;lastSaved=doc.SavedAt;return true;
        }catch{return false;}}
        public bool IsDismissed(Session s){lock(gate){Session old;return dismissed.TryGetValue(s.Key,out old)&&CurrentTaskIdentity.SameRound(old,s);}}
        public List<Session> Snapshot(Func<string,bool> enabled){lock(gate)return items.Values.Where(s=>enabled(s.Platform)).Select(s=>s.Copy()).ToList();}
        static string Signature(Session s){return Json.Serializer().Serialize(s);}
        public void Reconcile(IEnumerable<Session> source,Func<string,bool> enabled,Func<Session,bool> confirmActive){lock(gate){
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var incoming in source){if(incoming==null||!incoming.Visible||incoming.IsChild||!enabled(incoming.Platform))continue;seen.Add(incoming.Key);
                if(IsDismissed(incoming)){if(items.Remove(incoming.Key))dirty=true;continue;}
                if(dismissed.Remove(incoming.Key))dirty=true;
                var s=incoming.Copy();Session old;items.TryGetValue(s.Key,out old);
                if((s.Status=="running"||s.Status=="waiting")&&!confirmActive(s)){s.Status="unknown";if(old!=null&&old.LastActivity>s.LastActivity)s.LastActivity=old.LastActivity;}
                if(old==null||Signature(old)!=Signature(s)){items[s.Key]=s;dirty=true;}
            }
            foreach(var s in items.Values.Where(s=>enabled(s.Platform)&&!seen.Contains(s.Key)))if(s.Status=="running"||s.Status=="waiting"){s.Status="unknown";dirty=true;}
            Checked=true;Flush();
        }}
        public bool Remove(Session expected){lock(gate){Session live;if(!items.TryGetValue(expected.Key,out live)||!CurrentTaskIdentity.SameRound(live,expected))return false;
            dismissed[live.Key]=live.Copy();items.Remove(live.Key);dirty=true;Flush();return true;
        }}
        public void Flush(){lock(gate){
            DateTime now=DateTime.UtcNow;if(now<retryAt||(!dirty&&(now-lastSaved).TotalSeconds<10))return;
            try{Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+".tmp";
                var doc=new CurrentTasksDocument{SavedAt=now,Sessions=items.Values.Select(s=>s.Copy()).ToList(),Dismissed=dismissed.Values.OrderByDescending(s=>s.LastActivity).Take(500).Select(s=>s.Copy()).ToList()};
                File.WriteAllText(temp,Json.Serializer().Serialize(doc),new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);lastSaved=now;dirty=false;Error="";
            }catch{Error="当前列表暂未保存";retryAt=now.AddSeconds(5);}
        }}
    }
}
