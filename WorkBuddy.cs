using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CodexFloat {
    // Windows ships winsqlite3.dll. Open READONLY, select metadata only, close
    // promptly; the monitor never writes the source database or its session logs.
    public static class ReadOnlySqlite {
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_open_v2(byte[] filename,out IntPtr db,int flags,IntPtr vfs);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_busy_timeout(IntPtr db,int ms);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_prepare_v2(IntPtr db,byte[] sql,int bytes,out IntPtr stmt,IntPtr tail);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern IntPtr sqlite3_column_name16(IntPtr stmt,int column);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]static extern IntPtr sqlite3_column_text16(IntPtr stmt,int column);
        static byte[] Utf8(string s){return Encoding.UTF8.GetBytes(s+"\0");}
        public static List<Dictionary<string,string>> Query(string path,string sql){
            IntPtr db=IntPtr.Zero,stmt=IntPtr.Zero;
            try {
                int code=sqlite3_open_v2(Utf8(path),out db,1,IntPtr.Zero);
                if(code!=0)throw new IOException("SQLite read-only open failed ("+code+")");
                sqlite3_busy_timeout(db,150);code=sqlite3_prepare_v2(db,Utf8(sql),-1,out stmt,IntPtr.Zero);
                if(code!=0)throw new IOException("SQLite metadata query failed ("+code+")");
                var result=new List<Dictionary<string,string>>();int count=sqlite3_column_count(stmt);
                while((code=sqlite3_step(stmt))==100){
                    var row=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                    for(int i=0;i<count;i++)row[Marshal.PtrToStringUni(sqlite3_column_name16(stmt,i))]=Marshal.PtrToStringUni(sqlite3_column_text16(stmt,i))??"";
                    result.Add(row);
                }
                if(code!=101)throw new IOException("SQLite metadata read interrupted ("+code+")");return result;
            }finally{if(stmt!=IntPtr.Zero)sqlite3_finalize(stmt);if(db!=IntPtr.Zero)sqlite3_close(db);}
        }
    }
    public class WorkBuddyRecord {
        public string Id,Title,Cwd,Status;public long Created,Updated,Activity;
        public DateTime Timestamp { get { try{return new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddMilliseconds(Updated);}catch{return DateTime.UtcNow;} } }
    }
    public static class WorkBuddySource {
        public static string DefaultHome(){
            string custom=Environment.GetEnvironmentVariable("WORKBUDDY_HOME");if(!string.IsNullOrWhiteSpace(custom))return custom;
            string user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),newHome=Path.Combine(user,".workbuddy-ai");
            return File.Exists(Path.Combine(newHome,"workbuddy.db"))?newHome:Path.Combine(user,".workbuddy");
        }
        public static string State(string raw){
            switch((raw??"").Trim().ToLowerInvariant()){
                case "working":case "planning":case "running":case "in_progress":case "inprogress":return "running";
                case "pending":case "awaiting_input":case "waiting":return "waiting";
                case "completed":return "completed";
                case "terminated":case "interrupted":case "cancelled":case "canceled":return "interrupted";
                case "failed":case "error":case "errored":return "failed";
                case "archived":return "archived";
                default:return "unknown";
            }
        }
        public static string DeepLink(string id){Guid g;if(!Guid.TryParseExact(id,"D",out g))throw new ArgumentException("Invalid WorkBuddy session id");return "workbuddy://chat/"+g.ToString("D");}
        public static List<WorkBuddyRecord> Read(string path){
            var columns=new HashSet<string>(ReadOnlySqlite.Query(path,"PRAGMA table_info(sessions)").Select(r=>r["name"]),StringComparer.OrdinalIgnoreCase);
            foreach(string required in new[]{"id","cwd","status","updated_at"})if(!columns.Contains(required))throw new IOException("WorkBuddy session schema missing "+required);
            string title=columns.Contains("title")?"title":"NULL",custom=columns.Contains("custom_title")?"custom_title":"NULL";
            string sql="SELECT id,cwd,status,COALESCE(NULLIF("+custom+",''),"+title+",'') AS display_title,updated_at,"+(columns.Contains("created_at")?"created_at":"updated_at")+" AS created_at,"+(columns.Contains("last_activity_at")?"COALESCE(last_activity_at,0)":"0")+" AS activity FROM sessions"+(columns.Contains("deleted_at")?" WHERE deleted_at IS NULL":"")+" ORDER BY updated_at DESC";
            var result=new List<WorkBuddyRecord>();
            foreach(var row in ReadOnlySqlite.Query(path,sql)){
                Guid id;if(!Guid.TryParseExact(row["id"],"D",out id))continue;long created,updated,activity;
                long.TryParse(row["created_at"],out created);long.TryParse(row["updated_at"],out updated);long.TryParse(row["activity"],out activity);
                result.Add(new WorkBuddyRecord{Id=id.ToString("D"),Title=row["display_title"],Cwd=row["cwd"],Status=State(row["status"]),Created=created,Updated=updated,Activity=activity});
            }return result;
        }
    }
    public class WorkBuddyEngine:IDisposable {
        readonly string path,statePath;readonly Dictionary<string,Session> sessions=new Dictionary<string,Session>();
        readonly object gate=new object();bool initialized;public string Health="等待 WorkBuddy";
        public event Action<List<Session>> Completed;
        public WorkBuddyEngine(string home,string dataDir){path=Path.Combine(home,"workbuddy.db");statePath=Path.Combine(dataDir,"workbuddy-sessions.json");
            try{var state=Json.Serializer().Deserialize<SavedState>(Json.SharedText(statePath));foreach(var s in state.Sessions){s.Platform="workbuddy";sessions[s.Id]=s;}}catch{}
        }
        public List<Session> Snapshot(){lock(gate)return sessions.Values.Where(s=>s.Visible).Select(s=>s.Copy()).ToList();}
        public List<Session> CompletedSnapshot(){lock(gate)return sessions.Values.Where(s=>s.Status=="completed"&&(s.Visible||s.DismissedTurn!="")).Select(s=>s.Copy()).ToList();}
        public void ClearFinished(){lock(gate){foreach(var s in sessions.Values)if(s.Terminal){s.Visible=false;s.DismissedTurn=s.TurnId;}Save();}}
        public void Dismiss(string id){lock(gate){Session s;if(sessions.TryGetValue(id,out s)&&s.Status!="running"){s.Visible=false;s.DismissedTurn=s.TurnId;Save();}}}
        public void Poll(){
            List<Session> notices=new List<Session>();
            lock(gate){
                if(!File.Exists(path)){Health="等待 WorkBuddy 创建本地会话";return;}
                try{Apply(WorkBuddySource.Read(path),notices);Health="WorkBuddy 已连接";}
                catch(Exception ex){Health="WorkBuddy 读取暂不可用，正在重试";try{Directory.CreateDirectory(Path.GetDirectoryName(statePath));File.WriteAllText(Path.Combine(Path.GetDirectoryName(statePath),"workbuddy-read-error.log"),ex.GetType().Name+": "+ex.Message);}catch{}}
            }
            if(notices.Count>0&&Completed!=null)Completed(notices);
        }
        void Apply(List<WorkBuddyRecord> rows,List<Session> notices){
            bool changed=false;var present=new HashSet<string>();DateTime now=DateTime.UtcNow;
            foreach(var row in rows){
                present.Add(row.Id);Session s;bool known=sessions.TryGetValue(row.Id,out s);
                if(!known){s=new Session{Id=row.Id,Platform="workbuddy",FilePath=path,FirstSeen=now};sessions[row.Id]=s;}
                string before=s.Status;bool busyBefore=(before=="running"||before=="waiting")&&s.FinishedAt==DateTime.MinValue;
                bool start=row.Status=="running"&&(!busyBefore||!s.Visible||s.TurnId=="");
                string oldSignature=s.Title+"|"+s.Cwd+"|"+s.Status+"|"+s.Visible+"|"+s.LastActivity.Ticks;
                s.Title=row.Title;s.Cwd=row.Cwd;s.Platform="workbuddy";s.FilePath=path;
                if(start){s.TurnId="wb-"+row.Updated.ToString(CultureInfo.InvariantCulture);s.StartedAt=row.Timestamp;s.TimerEstimated=!initialized;s.FinishedAt=DateTime.MinValue;s.DismissedTurn="";s.Visible=true;}
                if(row.Status=="waiting"&&!busyBefore&&!s.Visible){
                    // A fresh pending draft is not a running task. Only restore an
                    // awaiting-input task if the source records recent activity.
                    if(row.Activity>row.Created&&(now-row.Timestamp).TotalDays<2){s.Visible=true;s.TurnId="wb-"+row.Updated;s.StartedAt=row.Timestamp;s.TimerEstimated=true;}
                }
                s.Status=row.Status;s.LastActivity=row.Timestamp;s.HasLifecycle=true;
                if(s.Terminal){
                    if(before!=s.Status||s.FinishedAt==DateTime.MinValue)s.FinishedAt=row.Timestamp;
                    if(s.Status=="completed"&&busyBefore&&s.Visible&&s.TurnId!=""){
                        string key=s.Key+":"+s.TurnId;if(s.NotifiedKey!=key){s.NotifiedKey=key;if(initialized)notices.Add(s.Copy());}
                    }
                }
                if(row.Status=="archived")s.Visible=false;
                string newSignature=s.Title+"|"+s.Cwd+"|"+s.Status+"|"+s.Visible+"|"+s.LastActivity.Ticks;
                if(oldSignature!=newSignature)changed=true;
            }
            foreach(var s in sessions.Values.Where(s=>!present.Contains(s.Id)&&s.Visible)){s.Visible=false;changed=true;}
            initialized=true;if(changed)Save();
        }
        void Save(){try{Json.Save(statePath,new SavedState{Sessions=sessions.Values.Where(s=>s.Visible||s.DismissedTurn!="").Select(s=>s.Copy()).ToList()});}catch{}}
        public void Dispose(){lock(gate)Save();}
    }
    public class MonitoringHub:IDisposable {
        readonly object gate=new object();readonly string codexHome,workbuddyHome,claudeHome,dataDir;
        Engine codex;WorkBuddyEngine workbuddy;ClaudeEngine claude;bool enableCodex,enableWorkBuddy,enableClaude;readonly CompletionHistory history;
        public event Action<List<Session>> Completed;
        public MonitoringHub(string ch,string wh,string data,bool c,bool w,string cl=null,bool cc=false){codexHome=ch;workbuddyHome=wh;claudeHome=cl??ClaudeDesktopSource.DefaultHome();dataDir=data;enableCodex=c;enableWorkBuddy=w;enableClaude=cc;history=new CompletionHistory(data);}
        public void Configure(bool c,bool w,bool cc=false){lock(gate){enableCodex=c;enableWorkBuddy=w;enableClaude=cc;if(!c&&codex!=null){codex.Dispose();codex=null;}if(!w&&workbuddy!=null){workbuddy.Dispose();workbuddy=null;}if(!cc&&claude!=null){claude.Dispose();claude=null;}}}
        public string Health { get {lock(gate){var names=new List<string>();if(enableCodex)names.Add("Codex");if(enableWorkBuddy)names.Add("WorkBuddy");if(enableClaude)names.Add("Claude 桌面端 Code");if(names.Count==0)return "请在设置里勾选监控平台";if(names.Count>1)return string.Join(" / ",names)+" 自动监控";if(enableCodex)return codex==null?"正在连接 Codex…":codex.Health;if(enableWorkBuddy)return workbuddy==null?"正在连接 WorkBuddy…":workbuddy.Health;return claude==null?"正在连接 Claude 桌面端 Code…":claude.Health;}} }
        public void Poll(){lock(gate){
            if(enableCodex){if(codex==null){codex=new Engine(codexHome,dataDir);codex.Poll();codex.Completed+=Forward;}else codex.Poll();}
            if(enableWorkBuddy){if(workbuddy==null){workbuddy=new WorkBuddyEngine(workbuddyHome,dataDir);workbuddy.Poll();workbuddy.Completed+=Forward;}else workbuddy.Poll();}
            if(enableClaude){if(claude==null){claude=new ClaudeEngine(claudeHome,dataDir);claude.Poll();claude.Completed+=Forward;}else claude.Poll();}
            RememberCompletions();history.Flush();
        }}
        void Forward(List<Session> items){history.Record(items);if(Completed!=null)Completed(items);}
        void RememberCompletions(){if(codex!=null)history.Record(codex.CompletedSnapshot());if(workbuddy!=null)history.Record(workbuddy.CompletedSnapshot());if(claude!=null)history.Record(claude.CompletedSnapshot());}
        public List<Session> RecentCompleted(){return history.Snapshot();}
        public string HistoryError {get{return history.Error;}}
        public List<Session> Snapshot(){lock(gate){var list=new List<Session>();if(enableCodex&&codex!=null)list.AddRange(codex.Snapshot());if(enableWorkBuddy&&workbuddy!=null)list.AddRange(workbuddy.Snapshot());if(enableClaude&&claude!=null)list.AddRange(claude.Snapshot());return list;}}
        public void ClearFinished(){lock(gate){RememberCompletions();if(enableCodex&&codex!=null)codex.ClearFinished();if(enableWorkBuddy&&workbuddy!=null)workbuddy.ClearFinished();if(enableClaude&&claude!=null)claude.ClearFinished();}}
        public void Dismiss(Session s){lock(gate){history.Record(new[]{s});if(s.Platform=="claude"){if(claude!=null)claude.Dismiss(s.Id);}else if(s.Platform=="workbuddy"){if(workbuddy!=null)workbuddy.Dismiss(s.Id);}else if(codex!=null)codex.Dismiss(s.Id);}}
        public void Dispose(){lock(gate){RememberCompletions();history.Flush();if(codex!=null)codex.Dispose();if(workbuddy!=null)workbuddy.Dispose();if(claude!=null)claude.Dispose();}}
    }
}
