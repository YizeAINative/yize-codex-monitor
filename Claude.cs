using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Management;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace CodexFloat {
    public static class ClaudeSource {
        public static string DefaultHome(){return Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude");}
        public static string FileId(string path){Guid g;string stem=Path.GetFileNameWithoutExtension(path);return Guid.TryParseExact(stem,"D",out g)?g.ToString("D"):"";}
        public static string ResumeArguments(string id){Guid g;if(!Guid.TryParseExact(id,"D",out g))throw new ArgumentException("Invalid Claude Code session id");return "--resume "+g.ToString("D");}
        public static ProcessStartInfo ResumeStartInfo(Session session){
            string args=ResumeArguments(session.Id);
            if(!Directory.Exists(session.Cwd))throw new IOException("此对话的工作目录已不存在");
            string user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates=new List<string>{Path.Combine(user,@".local\bin\claude.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"npm\claude.cmd")};
            foreach(string dir in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)){
                try{candidates.Add(Path.Combine(dir.Trim('"'),"claude.exe"));candidates.Add(Path.Combine(dir.Trim('"'),"claude.cmd"));}catch{}
            }
            string cli=candidates.FirstOrDefault(File.Exists);if(cli==null)throw new IOException("未找到 Claude Code CLI，请先安装 claude 命令");
            if(cli.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))return new ProcessStartInfo(cli,args){UseShellExecute=true,WorkingDirectory=session.Cwd,WindowStyle=ProcessWindowStyle.Normal};
            string command="& '"+cli.Replace("'","''")+"' "+args;
            string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
            return new ProcessStartInfo(shell,"-NoProfile -NoExit -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(command))){UseShellExecute=true,WorkingDirectory=session.Cwd,WindowStyle=ProcessWindowStyle.Normal};
        }
        public static bool RuntimeRunning(){
            var native=Process.GetProcessesByName("claude");if(native.Length>0){foreach(var p in native)p.Dispose();return true;}
            try{using(var query=new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='node.exe'"))using(var found=query.Get())foreach(ManagementObject process in found){using(process){string cmd=Convert.ToString(process["CommandLine"]);if(cmd.IndexOf("claude-code",StringComparison.OrdinalIgnoreCase)>=0)return true;}}return false;}
            catch{return true;}
        }
        static bool Flag(Dictionary<string,object> o,string key){object v;return o!=null&&o.TryGetValue(key,out v)&&v is bool&&(bool)v;}
        static List<Dictionary<string,object>> Blocks(Dictionary<string,object> message){object v;if(message==null||!message.TryGetValue("content",out v))return new List<Dictionary<string,object>>();var items=v as object[];return items==null?new List<Dictionary<string,object>>():items.OfType<Dictionary<string,object>>().ToList();}
        // Read structural lifecycle fields; never store prompts, responses, or tool output.
        public static bool Apply(string line,Session s){
            var o=Json.Read(line);if(o==null||Flag(o,"isSidechain"))return false;
            string sid=Json.Text(o,"sessionId");if(sid!=""&&!string.Equals(sid,s.Id,StringComparison.OrdinalIgnoreCase))return false;
            string type=Json.Text(o,"type"),cwd=Json.Text(o,"cwd");if(cwd!="")s.Cwd=cwd;
            string title=type=="custom-title"?Json.Text(o,"customTitle"):type=="ai-title"?Json.Text(o,"aiTitle"):type=="summary"?Json.Text(o,"summary"):"";
            if(title!=""){if(s.TitleKind=="custom-title"&&type!="custom-title")return false;s.TitleKind=type;s.Title=title.Replace('\r',' ').Replace('\n',' ').Trim();if(s.Title.Length>80)s.Title=s.Title.Substring(0,80);return true;}
            var m=Json.Obj(o,"message");var blocks=Blocks(m);DateTime at=Json.Date(Json.Text(o,"timestamp"));if(at==DateTime.MinValue)return false;
            if(s.LastActivity>at)return false;
            if(type=="user"){
                string text=Json.Text(m,"content");
                bool interrupted=text=="[Request interrupted by user]"||text=="[Request interrupted by user for tool use]"||blocks.Any(b=>Json.Text(b,"type")=="text"&&(Json.Text(b,"text")=="[Request interrupted by user]"||Json.Text(b,"text")=="[Request interrupted by user for tool use]"));
                if(interrupted){s.Status="interrupted";s.FinishedAt=at;s.LastActivity=at;return true;}
                if(Flag(o,"isMeta"))return false;
                bool result=blocks.Any(b=>Json.Text(b,"type")=="tool_result");
                if(result){if(!s.Terminal&&s.HasLifecycle)s.Status="running";s.LastActivity=at;return true;}
                if(m==null)return false;
                string turn=Json.Text(o,"uuid");if(turn=="")turn="cc-"+at.Ticks;
                if(turn==s.TurnId)return false;
                s.TurnId=turn;s.StartedAt=at;s.LastActivity=at;s.FinishedAt=DateTime.MinValue;s.TimerEstimated=false;s.Status="running";s.Visible=true;s.DismissedTurn="";s.HasLifecycle=true;return true;
            }
            if(type=="assistant"){
                s.LastActivity=at;
                if(Flag(o,"isApiErrorMessage")||Json.Text(m,"stop_reason")=="max_tokens"){s.Status="failed";s.FinishedAt=at;return true;}
                if(Json.Text(m,"stop_reason")=="end_turn"){
                    if(s.Status=="interrupted"||s.Status=="failed")return false;
                    s.Status="completed";s.FinishedAt=at;s.HasLifecycle=true;return true;
                }
                if(s.HasLifecycle){if(s.Terminal){s.Status="running";s.FinishedAt=DateTime.MinValue;}else s.Status="running";
                    if(blocks.Any(b=>Json.Text(b,"type")=="tool_use"&&(Json.Text(b,"name")=="AskUserQuestion"||Json.Text(b,"name")=="ExitPlanMode")))s.Status="waiting";}
                return true;
            }
            if(type=="system"&&Json.Text(o,"subtype")=="turn_duration"){
                // Duration is also emitted after interruption; it is not proof of success.
                if(s.Status=="completed")s.FinishedAt=at;s.LastActivity=at;return true;
            }
            if(type=="progress"){s.LastActivity=at;return true;}
            return false;
        }
    }
    public class ClaudeEngine:IDisposable {
        class Track{public Session S;public IncrementalReader Reader=new IncrementalReader();public long Length;public DateTime WriteAt;}
        readonly object gate=new object();readonly string home,statePath;readonly Func<bool> runtime;
        readonly Dictionary<string,Track> tracks=new Dictionary<string,Track>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,Session> sessions=new Dictionary<string,Session>();
        readonly ConcurrentDictionary<string,byte> dirty=new ConcurrentDictionary<string,byte>(StringComparer.OrdinalIgnoreCase);
        FileSystemWatcher watcher;DateTime lastScan,lastRuntime;bool live=true,initialized,changed;
        public string Health="等待 Claude Code";public event Action<List<Session>> Completed;
        public ClaudeEngine(string root,string dataDir,Func<bool> runtimeCheck=null){home=root;statePath=Path.Combine(dataDir,"claude-sessions.json");runtime=runtimeCheck??ClaudeSource.RuntimeRunning;
            try{var state=Json.Serializer().Deserialize<SavedState>(Json.SharedText(statePath));foreach(var s in state.Sessions){s.Platform="claude";sessions[s.Id]=s;}}catch{}
        }
        public List<Session> Snapshot(){lock(gate)return sessions.Values.Where(s=>s.Visible).Select(s=>s.Copy()).ToList();}
        public void ClearFinished(){lock(gate){foreach(var s in sessions.Values)if(s.Terminal){s.Visible=false;s.DismissedTurn=s.TurnId;}Save();}}
        public void Dismiss(string id){lock(gate){Session s;if(sessions.TryGetValue(id,out s)&&s.Status!="running"){s.Visible=false;s.DismissedTurn=s.TurnId;Save();}}}
        bool Eligible(string path){string rel=path.Substring(Math.Min(path.Length,Path.Combine(home,"projects").Length)).Replace('\\','/');return rel.IndexOf("/subagents/",StringComparison.OrdinalIgnoreCase)<0&&ClaudeSource.FileId(path)!="";}
        public void Poll(){var notices=new List<Session>();lock(gate){try{
            string dir=Path.Combine(home,"projects");if(!Directory.Exists(dir)){Health="等待 Claude Code 创建本地会话";return;}
            DateTime now=DateTime.UtcNow;
            if(watcher==null){watcher=new FileSystemWatcher(dir,"*.jsonl"){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.FileName|NotifyFilters.Size,InternalBufferSize=32768};watcher.Changed+=(o,e)=>dirty[e.FullPath]=1;watcher.Created+=(o,e)=>dirty[e.FullPath]=1;watcher.Renamed+=(o,e)=>dirty[e.FullPath]=1;watcher.Error+=(o,e)=>lastScan=DateTime.MinValue;watcher.EnableRaisingEvents=true;}
            if((now-lastRuntime).TotalSeconds>=10){live=runtime();lastRuntime=now;}
            if(!initialized||(now-lastScan).TotalSeconds>=10){foreach(string path in Directory.EnumerateFiles(dir,"*.jsonl",SearchOption.AllDirectories))if(Eligible(path)){
                var info=new FileInfo(path);Track t;
                if(!tracks.TryGetValue(path,out t)){if(info.LastWriteTimeUtc>=now.AddDays(-2)||sessions.ContainsKey(ClaudeSource.FileId(path)))dirty[path]=1;}
                else if(info.Length!=t.Reader.Offset||info.LastWriteTimeUtc!=t.WriteAt)dirty[path]=1;
            }lastScan=now;}
            foreach(var t in tracks.Values){var info=new FileInfo(t.S.FilePath);if(info.Exists&&(info.Length!=t.Reader.Offset||info.LastWriteTimeUtc!=t.WriteAt))dirty[t.S.FilePath]=1;}
            foreach(string path in dirty.Keys.ToArray()){byte b;dirty.TryRemove(path,out b);if(!File.Exists(path)||!Eligible(path))continue;Track t;
                if(!tracks.TryGetValue(path,out t))Discover(path,notices);else Read(t,notices,false);
            }
            foreach(var s in sessions.Values)if((s.Status=="running"||s.Status=="waiting")&&!live&&(now-s.LastActivity).TotalMinutes>3){s.Status="unknown";changed=true;}
            initialized=true;Health="Claude Code 已连接";if(changed){Save();changed=false;}
        }catch(Exception ex){Health="Claude Code 读取暂不可用，正在重试";try{Directory.CreateDirectory(Path.GetDirectoryName(statePath));File.WriteAllText(Path.Combine(Path.GetDirectoryName(statePath),"claude-read-error.log"),ex.GetType().Name+": "+ex.Message);}catch{}}}
            if(notices.Count>0&&Completed!=null)Completed(notices);
        }
        void Discover(string path,List<Session> notices){
            string id=ClaudeSource.FileId(path);Session previous;sessions.TryGetValue(id,out previous);
            var s=new Session{Id=id,Platform="claude",FilePath=path,FirstSeen=DateTime.UtcNow};
            if(previous!=null){s.Title=previous.Title;s.TitleKind=previous.TitleKind;s.NotifiedKey=previous.NotifiedKey;s.DismissedTurn=previous.DismissedTurn;}
            var t=new Track{S=s};tracks[path]=t;sessions[id]=s;
            // Source transcripts are bounded during hydration. A larger file is
            // incrementally read on subsequent polls without blocking the UI.
            Read(t,notices,true);
            bool running=(s.Status=="running"||s.Status=="waiting")&&(live||(DateTime.UtcNow-s.LastActivity).TotalMinutes<3);
            s.Visible=running||(previous!=null&&previous.Visible);
            if(previous!=null&&s.Terminal&&previous.DismissedTurn==s.TurnId){s.Visible=false;s.DismissedTurn=previous.DismissedTurn;}
            if(!initialized&&previous==null&&s.Terminal)s.Visible=false;
            if(!initialized&&s.Status=="completed"&&s.TurnId!="")s.NotifiedKey=s.Key+":"+s.TurnId;
            if(initialized&&previous==null&&s.Terminal&&s.FinishedAt>=DateTime.UtcNow.AddSeconds(-20)){s.Visible=true;Notify(s,notices);}
            if(s.Title=="")s.Title="Claude Code 对话 · "+id.Substring(0,8);changed=true;
        }
        void Read(Track t,List<Session> notices,bool hydrate){
            try{var info=new FileInfo(t.S.FilePath);if(!info.Exists)return;
                if(info.Length<t.Reader.Offset){t.Reader.Reset(0);t.S.HasLifecycle=false;t.S.Status="unknown";}
                t.Reader.Read(t.S.FilePath,line=>{if(ClaudeSource.Apply(line,t.S))changed=true;},16L*1024*1024);
                t.WriteAt=info.LastWriteTimeUtc;t.Length=info.Length;
                if(t.Reader.Offset<info.Length)dirty[t.S.FilePath]=1;
                if(!hydrate&&t.S.Visible&&t.S.Status=="completed"&&t.Reader.Offset>=info.Length)Notify(t.S,notices);
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        void Notify(Session s,List<Session> notices){if(s.Status!="completed"||s.TurnId=="")return;string key=s.Key+":"+s.TurnId;if(s.NotifiedKey==key)return;s.NotifiedKey=key;changed=true;notices.Add(s.Copy());}
        void Save(){try{Json.Save(statePath,new SavedState{Sessions=sessions.Values.Where(s=>s.Visible||s.DismissedTurn!="").Select(s=>s.Copy()).ToList()});}catch{}}
        public void Dispose(){lock(gate){if(watcher!=null)watcher.Dispose();Save();}}
    }
}
