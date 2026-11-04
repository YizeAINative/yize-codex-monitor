using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Web.Script.Serialization;

namespace CodexFloat {
    public static class Json {
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 80 }; }
        public static Dictionary<string, object> Read(string text) { try { return Serializer().DeserializeObject(text) as Dictionary<string, object>; } catch { return null; } }
        public static string Text(Dictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is string ? (string)v : ""; }
        public static Dictionary<string, object> Obj(Dictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as Dictionary<string, object> : null; }
        public static DateTime Date(string s) { DateTime d; return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d) ? d.ToUniversalTime() : DateTime.MinValue; }
        public static string SharedText(string path) { using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var r = new StreamReader(f, Encoding.UTF8)) return r.ReadToEnd(); }
        public static void Save(string path, object value) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, Serializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }

    public class Session {
        public string Platform = "codex";
        public string Id = "", Title = "", Cwd = "", Project = "", FilePath = "", TurnId = "";
        public string Status = "unknown", NotifiedKey = "", DismissedTurn = "";
        public DateTime StartedAt, FinishedAt, LastActivity, FirstSeen;
        public bool Visible, HasLifecycle, IsChild, TimerEstimated;
        public Session Copy() { return (Session)MemberwiseClone(); }
        public string Key { get { return (Platform ?? "codex") + ":" + Id; } }
        public string PlatformName { get { return Platform == "workbuddy" ? "WorkBuddy" : "Codex"; } }
        public string RuntimeText(DateTime now){
            if(StartedAt==DateTime.MinValue)return "--:--";
            DateTime end=FinishedAt!=DateTime.MinValue?FinishedAt:(Status=="unknown"&&LastActivity!=DateTime.MinValue?LastActivity:now);
            var elapsed=end.ToUniversalTime()-StartedAt.ToUniversalTime();if(elapsed<TimeSpan.Zero)elapsed=TimeSpan.Zero;
            string text=elapsed.TotalHours>=1?((int)elapsed.TotalHours).ToString("00")+":"+elapsed.Minutes.ToString("00")+":"+elapsed.Seconds.ToString("00"):elapsed.Minutes.ToString("00")+":"+elapsed.Seconds.ToString("00");return (TimerEstimated?"≈":"")+text;
        }
        public string Label { get { return string.IsNullOrWhiteSpace(Title) ? "新对话 · " + (Id.Length >= 8 ? Id.Substring(0,8) : Id) : Title; } }
        public string Group { get { return !string.IsNullOrWhiteSpace(Project) ? Project : (!string.IsNullOrWhiteSpace(Cwd) ? Path.GetFileName(Cwd.TrimEnd('\\','/')) : "未分类对话"); } }
        public bool Terminal { get { return Status == "completed" || Status == "interrupted" || Status == "failed"; } }
    }
    public class Lifecycle {
        public string Kind, TurnId;
        public DateTime At, StartedAt;
        public static Lifecycle Parse(string line) {
            if (string.IsNullOrEmpty(line)) return null;
            string prefix = line.Substring(0, Math.Min(600, line.Length));
            if (!prefix.Contains("\"event_msg\"")) return null;
            if (!(prefix.Contains("\"task_started\"") || prefix.Contains("\"task_complete\"") || prefix.Contains("\"turn_aborted\"") || prefix.Contains("\"task_failed\""))) return null;
            var root = Json.Read(line);
            if (Json.Text(root,"type") != "event_msg") return null;
            var p = Json.Obj(root,"payload"); string kind = Json.Text(p,"type");
            if (kind != "task_started" && kind != "task_complete" && kind != "turn_aborted" && kind != "task_failed") return null;
            return new Lifecycle { Kind = kind, TurnId = Json.Text(p,"turn_id"), At = Json.Date(Json.Text(root,"timestamp")), StartedAt = Json.Date(Json.Text(p,"started_at")) };
        }
        public bool Apply(Session s) {
            if (Kind == "task_started") {
                // Duplicate starts must not resurrect a completed turn.
                if (s.HasLifecycle && s.TurnId == TurnId && TurnId != "" && s.Terminal) return false;
                s.TurnId = TurnId; s.Status = "running"; s.FinishedAt = DateTime.MinValue;
                s.StartedAt = StartedAt != DateTime.MinValue ? StartedAt : At;
                s.Visible = true; s.DismissedTurn = ""; s.HasLifecycle = true;
                return true;
            }
            // Late completion of an earlier turn must not finish the current turn.
            if (s.HasLifecycle && TurnId != "" && s.TurnId != "" && TurnId != s.TurnId) return false;
            if (TurnId != "") s.TurnId = TurnId;
            s.Status = Kind == "task_complete" ? "completed" : Kind == "turn_aborted" ? "interrupted" : "failed";
            s.FinishedAt = At; s.HasLifecycle = true;
            if (s.StartedAt == DateTime.MinValue && StartedAt != DateTime.MinValue) s.StartedAt = StartedAt;
            return true;
        }
    }

    // UTF-8 is decoded only after a full newline. Partial JSON and partial UTF-8
    // are retained across polls; oversized tool-output lines are skipped in bounded memory.
    public class IncrementalReader {
        public long Offset;
        readonly MemoryStream pending = new MemoryStream();
        bool skipping;
        const int MaxLine = 2 * 1024 * 1024;
        public void Reset(long offset) { Offset = offset; pending.SetLength(0); skipping = false; }
        public void Read(string path, Action<string> onLine, long limit) {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                if (f.Length < Offset) Reset(0);
                f.Position = Offset; var bytes = new byte[64 * 1024]; long remaining = Math.Min(limit, f.Length - Offset);
                while (remaining > 0) {
                    int n = f.Read(bytes,0,(int)Math.Min(bytes.Length,remaining)); if (n == 0) break;
                    int start = 0;
                    for (int i=0;i<n;i++) {
                        if (bytes[i] != 10) continue;
                        Append(bytes,start,i-start);
                        if (!skipping) onLine(Encoding.UTF8.GetString(pending.GetBuffer(),0,(int)pending.Length).TrimEnd('\r'));
                        pending.SetLength(0); skipping=false; start=i+1;
                    }
                    Append(bytes,start,n-start); Offset+=n; remaining-=n;
                }
            }
        }
        void Append(byte[] bytes,int start,int count) {
            if (skipping) return;
            if (pending.Length + count > MaxLine) { skipping=true;pending.SetLength(0);return; }
            pending.Write(bytes,start,count);
        }
    }

    public static class Rollout {
        public static Session Identity(string path) {
            try {
                using (var f = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                    var bytes = new byte[Math.Min(256*1024,(int)Math.Min(f.Length,256*1024))]; int n=f.Read(bytes,0,bytes.Length);
                    int end=Array.IndexOf(bytes,(byte)10,0,n); if (end<0) return null;
                    var root=Json.Read(Encoding.UTF8.GetString(bytes,0,end)); if (Json.Text(root,"type")!="session_meta") return null;
                    var p=Json.Obj(root,"payload"); string id=Json.Text(p,"id"); Guid guid;
                    if (!Guid.TryParse(id,out guid)) return null;
                    var source=Json.Obj(p,"source");
                    bool child=Json.Text(p,"parent_thread_id")!="" || (source!=null && source.ContainsKey("subagent"));
                    string ap=Json.Text(p,"agent_path"); if(ap!="" && ap!="/root") child=true;
                    return new Session { Id=id,FilePath=path,Cwd=Json.Text(p,"cwd"),IsChild=child };
                }
            } catch { return null; }
        }
        // Reverse scan avoids parsing or retaining the prompt/tool history.
        // Stop after enough lifecycle events to disambiguate late completions.
        public static long Hydrate(Session s) {
            var events=new List<Lifecycle>(); long endPosition=0;
            using(var f=new FileStream(s.FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                long len=f.Length,pos=len,scanned=0; var suffix=new List<byte>(); bool oversized=false,first=true;
                long completeEnd=0; var buffer=new byte[64*1024]; bool stop=false;
                while(pos>0 && scanned<128L*1024*1024 && !stop) {
                    int n=(int)Math.Min(buffer.Length,pos);pos-=n;f.Position=pos;int got=0;
                    while(got<n){int r=f.Read(buffer,got,n-got);if(r==0)break;got+=r;}
                    scanned+=got;
                    for(int i=got-1;i>=0;i--) {
                        if(buffer[i]==10) {
                            if(first) { completeEnd=pos+i+1;first=false;suffix.Clear();oversized=false;continue; }
                            if(!oversized && suffix.Count>0){suffix.Reverse();var ev=Lifecycle.Parse(Encoding.UTF8.GetString(suffix.ToArray()));if(ev!=null) events.Add(ev);}
                            suffix.Clear();oversized=false;
                            if(events.Count>=16){stop=true;break;}
                        } else if(!oversized) { if(suffix.Count<2*1024*1024) suffix.Add(buffer[i]); else {suffix.Clear();oversized=true;} }
                    }
                }
                if(pos==0 && !first && !oversized && suffix.Count>0){suffix.Reverse();var ev=Lifecycle.Parse(Encoding.UTF8.GetString(suffix.ToArray()));if(ev!=null) events.Add(ev);}
                endPosition=completeEnd;
            }
            events.Reverse(); foreach(var ev in events) ev.Apply(s);
            return endPosition;
        }
        public static bool HasWriter(string path) {
            try { using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){} return false; }
            catch(IOException ex) { return (ex.HResult & 0xFFFF)==32 || (ex.HResult & 0xFFFF)==33; }
            catch { return false; }
        }
        public static string DeepLink(string id) { Guid g; if(!Guid.TryParseExact(id,"D",out g)) throw new ArgumentException("Invalid thread id");return "codex://threads/"+g.ToString("D"); }
    }

    public class Settings {
        public bool MonitorCodex=true, MonitorWorkBuddy=true;
        public bool Sound=true, Notifications=true, AlwaysOnTop=true;
        public double Left=-99999,Top=-99999,Width=400;
    }
    public class SavedState { public List<Session> Sessions=new List<Session>(); }
    class Tracking { public Session S;public IncrementalReader Reader=new IncrementalReader();public DateTime WriteAt;public long Length; }
    public class Engine : IDisposable {
        readonly object gate=new object();
        readonly string home,statePath;
        readonly Dictionary<string,Tracking> files=new Dictionary<string,Tracking>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,Session> sessions=new Dictionary<string,Session>();
        readonly Dictionary<string,string> titles=new Dictionary<string,string>();
        readonly ConcurrentDictionary<string,byte> dirty=new ConcurrentDictionary<string,byte>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,Session> saved=new Dictionary<string,Session>();
        FileSystemWatcher watcher;DateTime lastScan,lastTitles,lastProjects;bool initialized,changed;
        Dictionary<string,string> projectNames=new Dictionary<string,string>(),projectAssignments=new Dictionary<string,string>(),rootNames=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        public string Health="正在连接本机会话…";
        public event Action<List<Session>> Completed;
        public Engine(string codexHome,string dataDir) {
            home=codexHome;statePath=Path.Combine(dataDir,"sessions.json");
            try {var data=Json.Serializer().Deserialize<SavedState>(Json.SharedText(statePath));foreach(var s in data.Sessions) if(s.Id!="") saved[s.Id]=s;}catch{}
            foreach(var s in saved.Values) if(s.Visible) sessions[s.Id]=s.Copy();
        }
        public List<Session> Snapshot(){lock(gate)return sessions.Values.Where(s=>s.Visible&&!s.IsChild).Select(s=>s.Copy()).ToList();}
        public void ClearFinished(){lock(gate){foreach(var s in sessions.Values)if(s.Terminal){s.Visible=false;s.DismissedTurn=s.TurnId;}Save();}}
        public void Dismiss(string id){lock(gate){Session s;if(sessions.TryGetValue(id,out s)&&s.Status!="running"){s.Visible=false;s.DismissedTurn=s.TurnId;Save();}}}
        public void Poll() {
            var notices=new List<Session>();
            lock(gate) {
                try {
                    string dir=Path.Combine(home,"sessions");if(!Directory.Exists(dir)){Health="等待 Codex 创建本机会话";return;}
                    if(watcher==null){watcher=new FileSystemWatcher(dir,"*.jsonl"){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.Size|NotifyFilters.FileName,InternalBufferSize=32768};
                        watcher.Changed+=(o,e)=>dirty[e.FullPath]=1;watcher.Created+=(o,e)=>dirty[e.FullPath]=1;watcher.Renamed+=(o,e)=>dirty[e.FullPath]=1;watcher.Error+=(o,e)=>lastScan=DateTime.MinValue;watcher.EnableRaisingEvents=true;}
                    ReadTitles();ReadProjects();DateTime now=DateTime.UtcNow;
                    if(!initialized || (now-lastScan).TotalSeconds>=10){
                        foreach(string path in Directory.EnumerateFiles(dir,"*.jsonl",SearchOption.AllDirectories)) {
                            var info=new FileInfo(path);Tracking t;
                            if(!files.TryGetValue(path,out t)){
                                // Read old paths as well: resuming a chat does not move it to today's folder.
                                if(info.LastWriteTimeUtc>=now.AddDays(-2) || Rollout.HasWriter(path) || saved.Values.Any(s=>s.FilePath==path)) Discover(path,notices);
                            } else if(info.LastWriteTimeUtc!=t.WriteAt || info.Length!=t.Length) dirty[path]=1;
                        }lastScan=now;
                    }
                    // Windows can defer last-write notifications while Codex keeps
                    // a file handle open. Poll length of known root chats as well,
                    // including completed chats, so every follow-up is detected.
                    foreach(var known in files.Values.Where(t=>!t.S.IsChild)){
                        var info=new FileInfo(known.S.FilePath);
                        if(info.Exists&&(info.Length!=known.Reader.Offset||info.LastWriteTimeUtc!=known.WriteAt))dirty[known.S.FilePath]=1;
                    }
                    foreach(string path in dirty.Keys.ToArray()){byte b;dirty.TryRemove(path,out b);Tracking t;
                        if(!files.TryGetValue(path,out t)){Discover(path,notices);continue;}
                        ReadChanges(t,notices);
                    }
                    foreach(var t in files.Values){var s=t.S;if(s.IsChild)continue;
                        string title; if(titles.TryGetValue(s.Id,out title)&&s.Title!=title){s.Title=title;changed=true;}
                        string project=ProjectFor(s);if(s.Project!=project){s.Project=project;changed=true;}
                        if(s.Status=="running"&&(now-s.LastActivity).TotalMinutes>3&&!Rollout.HasWriter(s.FilePath)){s.Status="unknown";changed=true;}
                    }
                    Health="自动监控已开启";initialized=true;
                    if(changed){Save();changed=false;}
                }catch(Exception ex){Health="读取暂不可用，正在重试";Log(ex.GetType().Name+": "+ex.Message);}
            }
            if(notices.Count>0 && Completed!=null)Completed(notices);
        }
        void Discover(string path,List<Session> notices){
            if(!File.Exists(path))return;var s=Rollout.Identity(path);if(s==null)return;
            if(s.IsChild){files[path]=new Tracking{S=s,WriteAt=File.GetLastWriteTimeUtc(path),Length=new FileInfo(path).Length};return;}
            Session previous; sessions.TryGetValue(s.Id,out previous);if(previous==null)saved.TryGetValue(s.Id,out previous);
            long pos=Rollout.Hydrate(s);DateTime now=DateTime.UtcNow;s.LastActivity=File.GetLastWriteTimeUtc(path);s.FirstSeen=previous!=null?previous.FirstSeen:now;
            if(previous!=null){s.NotifiedKey=previous.NotifiedKey;s.Title=previous.Title;s.DismissedTurn=previous.DismissedTurn;}
            string title;if(titles.TryGetValue(s.Id,out title))s.Title=title;s.Project=ProjectFor(s);
            bool live=s.Status=="running"&&(Rollout.HasWriter(path)||(now-s.LastActivity).TotalMinutes<3);
            if(s.Status=="running"&&!live)s.Status="unknown";
            s.Visible=live || (previous!=null&&previous.Visible);
            if(previous!=null&&previous.DismissedTurn==s.TurnId&&s.Terminal)s.Visible=false;
            // When discovered after startup, a short turn may already have finished.
            if(initialized&&s.Terminal&&s.FinishedAt>=now.AddSeconds(-30)){s.Visible=true;Notify(s,notices);}
            if(previous!=null&&previous.Visible&&previous.Status=="running"&&s.Status=="completed"&&previous.TurnId==s.TurnId)Notify(s,notices);
            var t=new Tracking{S=s,WriteAt=File.GetLastWriteTimeUtc(path),Length=new FileInfo(path).Length};t.Reader.Reset(pos);files[path]=t;sessions[s.Id]=s;changed=true;
            ReadChanges(t,notices);
        }
        void ReadChanges(Tracking t,List<Session> notices){
            if(t.S.IsChild)return;
            try {
                var fi=new FileInfo(t.S.FilePath);if(!fi.Exists){if(t.S.Status=="running"){t.S.Status="unknown";changed=true;}return;}
                bool grew=fi.Length!=t.Reader.Offset;
                if(fi.Length<t.Reader.Offset){t.Reader.Reset(0);t.S.HasLifecycle=false;t.S.Status="unknown";}
                t.Reader.Read(t.S.FilePath,line=>{
                    var ev=Lifecycle.Parse(line);if(ev==null)return;
                    if(ev.Apply(t.S)){changed=true;if(ev.Kind=="task_started")t.S.Visible=true;
                        else if(t.S.Visible)Notify(t.S,notices);}
                },16L*1024*1024);
                if(grew){t.S.LastActivity=fi.LastWriteTimeUtc;if(t.S.Status=="unknown"&&t.S.HasLifecycle&&t.S.FinishedAt==DateTime.MinValue){t.S.Status="running";t.S.Visible=true;}changed=true;}
                t.WriteAt=fi.LastWriteTimeUtc;t.Length=fi.Length;
                if(t.Reader.Offset<fi.Length)dirty[t.S.FilePath]=1;
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        void Notify(Session s,List<Session> notices){
            if(s.Status!="completed")return;
            string key=s.Id+":"+(s.TurnId!=""?s.TurnId:s.FinishedAt.ToString("O"));
            if(s.NotifiedKey==key)return;s.NotifiedKey=key;changed=true;notices.Add(s.Copy());
        }
        void ReadTitles(){string p=Path.Combine(home,"session_index.jsonl");if(!File.Exists(p))return;DateTime stamp=File.GetLastWriteTimeUtc(p);if(stamp==lastTitles)return;
            foreach(string line in Json.SharedText(p).Split('\n')){var o=Json.Read(line);string id=Json.Text(o,"id"),title=Json.Text(o,"thread_name");if(id!=""&&title!="")titles[id]=title;}lastTitles=stamp;
        }
        void ReadProjects(){string p=Path.Combine(home,".codex-global-state.json");if(!File.Exists(p))return;DateTime stamp=File.GetLastWriteTimeUtc(p);if(stamp==lastProjects)return;
            var root=Json.Read(Json.SharedText(p));if(root==null)return;var projects=Json.Obj(root,"local-projects");
            if(projects!=null)foreach(var kv in projects){var obj=kv.Value as Dictionary<string,object>;string name=Json.Text(obj,"name");projectNames[kv.Key]=name;
                object rr;if(obj!=null&&obj.TryGetValue("rootPaths",out rr)){var roots=rr as object[];if(roots!=null)foreach(var r in roots)if(r is string)rootNames[(string)r]=name;}}
            var assignments=Json.Obj(root,"thread-project-assignments");if(assignments!=null)foreach(var kv in assignments)if(kv.Value is string)projectAssignments[kv.Key]=(string)kv.Value;
            lastProjects=stamp;
        }
        string ProjectFor(Session s){string id,name;if(projectAssignments.TryGetValue(s.Id,out id)&&projectNames.TryGetValue(id,out name))return name;if(rootNames.TryGetValue(s.Cwd,out name))return name;return "";}
        void Save(){try{Json.Save(statePath,new SavedState{Sessions=sessions.Values.Where(s=>s.Visible||s.DismissedTurn!="").OrderByDescending(s=>s.LastActivity).Take(500).Select(s=>s.Copy()).ToList()});}catch(Exception ex){Log("save: "+ex.Message);}}
        void Log(string message){try{string p=Path.Combine(Path.GetDirectoryName(statePath),"diagnostics.log");Directory.CreateDirectory(Path.GetDirectoryName(p));if(File.Exists(p)&&new FileInfo(p).Length>512*1024)File.WriteAllText(p,"");File.AppendAllText(p,DateTime.Now.ToString("s")+" "+message+Environment.NewLine);}catch{}}
        public void Dispose(){lock(gate){if(watcher!=null)watcher.Dispose();Save();}}
    }
}
