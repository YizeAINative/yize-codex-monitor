using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;

namespace CodexFloat {
    public class CompletionHistoryDocument {
        public int Version=1;
        public List<Session> Entries=new List<Session>();
    }
    // Independent immutable round snapshots: clearing the live list or starting
    // a new turn must never erase an already completed round.
    public class CompletionHistory {
        public const int Capacity=200;
        readonly object gate=new object();readonly string path;
        readonly List<Session> entries=new List<Session>();
        bool dirty;DateTime retryAt;
        public string Error {get;private set;}
        public CompletionHistory(string dataDir){
            path=Path.Combine(dataDir,"completion-history.json");Error="";
            if(!Load(path))Load(path+".bak");
            // Only our own small caches; never replay provider transcripts.
            foreach(string name in new[]{"sessions.json","workbuddy-sessions.json"}){
                try{var state=Json.Serializer().Deserialize<SavedState>(Json.SharedText(Path.Combine(dataDir,name)));if(state!=null&&state.Sessions!=null)Record(state.Sessions.Where(s=>s.Visible||s.DismissedTurn!=""));}catch{}
            }
        }
        bool Load(string file){try{if(!File.Exists(file))return false;var doc=Json.Serializer().Deserialize<CompletionHistoryDocument>(Json.SharedText(file));if(doc==null||doc.Entries==null)return false;foreach(var item in doc.Entries)Add(item);dirty=false;return true;}catch{return false;}}
        static bool Valid(Session s){
            if(s==null||s.Status!="completed"||s.IsChild||s.FinishedAt==DateTime.MinValue)return false;
            if(s.Platform=="claude")return ClaudeDesktopSource.ValidId(s.Id);
            Guid id;return (s.Platform=="codex"||s.Platform=="workbuddy")&&Guid.TryParseExact(s.Id,"D",out id);
        }
        static bool SameRound(Session a,Session b){
            if(a.Key!=b.Key)return false;
            if(!string.IsNullOrEmpty(a.TurnId)&&!string.IsNullOrEmpty(b.TurnId)&&a.TurnId==b.TurnId)return true;
            if(!string.IsNullOrEmpty(a.TurnId)&&!string.IsNullOrEmpty(b.TurnId)&&!a.TurnId.StartsWith("desktop-",StringComparison.Ordinal)&&!b.TurnId.StartsWith("desktop-",StringComparison.Ordinal))return false;
            // Desktop tail hydration can replace a transcript UUID with the
            // desktop prompt timestamp. Match the same terminal timestamp too.
            return a.FinishedAt.ToUniversalTime().Ticks/TimeSpan.TicksPerMillisecond==b.FinishedAt.ToUniversalTime().Ticks/TimeSpan.TicksPerMillisecond;
        }
        bool Add(Session item){
            if(!Valid(item)||entries.Any(e=>SameRound(e,item)))return false;
            if(entries.Count>=Capacity&&item.FinishedAt.ToUniversalTime()<=entries[entries.Count-1].FinishedAt)return false;
            var copy=item.Copy();copy.Visible=true;copy.FilePath="";copy.NotifiedKey="";copy.DismissedTurn="";
            copy.FinishedAt=copy.FinishedAt.ToUniversalTime();if(copy.StartedAt!=DateTime.MinValue)copy.StartedAt=copy.StartedAt.ToUniversalTime();
            entries.Add(copy);entries.Sort((a,b)=>b.FinishedAt.CompareTo(a.FinishedAt));if(entries.Count>Capacity)entries.RemoveRange(Capacity,entries.Count-Capacity);return true;
        }
        public void Record(IEnumerable<Session> items){lock(gate){foreach(var item in items)if(Add(item))dirty=true;Flush();}}
        public List<Session> Snapshot(){lock(gate)return entries.Select(e=>e.Copy()).ToList();}
        public void Flush(){lock(gate){
            if(!dirty||DateTime.UtcNow<retryAt)return;
            try{
                Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+".tmp";
                File.WriteAllText(temp,Json.Serializer().Serialize(new CompletionHistoryDocument{Entries=entries}),new UTF8Encoding(false));
                if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);
                dirty=false;Error="";
            }catch{Error="完成记录暂未保存";retryAt=DateTime.UtcNow.AddSeconds(5);}
        }}
    }
}
