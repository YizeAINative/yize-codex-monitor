using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CodexFloat {
    public static class AntigravitySource {
        public const string Platform = "antigravity";

        public static string DefaultHome() {
            string custom = Environment.GetEnvironmentVariable("ANTIGRAVITY_HOME");
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity")
                : custom;
        }

        public static string OpenDesktop() {
            foreach (var process in Process.GetProcessesByName("Antigravity")) {
                using (process) {
                    try {
                        if (process.MainWindowHandle != IntPtr.Zero) {
                            // Relaunching the executable asks the existing single instance to show.
                            string executable = process.MainModule.FileName;
                            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                            return executable;
                        }
                    } catch (System.ComponentModel.Win32Exception) {}
                    catch (InvalidOperationException) {}
                }
            }
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "antigravity", "Antigravity.exe");
            Process.Start(new ProcessStartInfo(installed) { UseShellExecute = true });
            return installed;
        }

        internal static string WorkspacePath(string value) {
            string[] uris;
            try { uris = value.StartsWith("[") ? Json.Serializer().Deserialize<string[]>(value) : new[] { value }; }
            catch (ArgumentException) { return ""; }
            catch (InvalidOperationException) { return ""; }
            foreach (string item in uris ?? new string[0]) {
                Uri uri;
                if (Uri.TryCreate(item, UriKind.Absolute, out uri) && uri.IsFile && !uri.IsUnc)
                    return uri.LocalPath;
            }
            return "";
        }

        internal static string Status(Dictionary<string,string> summary, List<Dictionary<string,string>> steps = null) {
            // Numeric values match the installed app's CortexStepStatus and CortexStepType enums.
            if (summary["killed"] == "1") return "interrupted";
            string runStatus = summary["status"];
            bool running = runStatus == "CASCADE_RUN_STATUS_RUNNING" || runStatus == "CASCADE_RUN_STATUS_BUSY"
                || runStatus == "CASCADE_RUN_STATUS_CANCELING";
            if (steps != null && steps.Any(step => step["status"] == "9")) return "waiting";
            if (running || summary["not_fully_idle"] == "1") return "running";
            if (steps == null || runStatus != "CASCADE_RUN_STATUS_IDLE") return "unknown";

            foreach (var step in steps) {
                string type = step["step_type"];
                string status = step["status"];
                if (status == "6" || status == "12") return "interrupted";
                if (type == "17") return "failed"; // ERROR_MESSAGE
                if (type == "2" || type == "15" || type == "82") { // FINISH, PLANNER_RESPONSE, NOTIFY_USER
                    if (status == "7") return "failed";
                    if (status == "3") return "completed";
                }
            }
            return "unknown";
        }
    }

    internal sealed class AntigravityName {
        readonly string text;
        readonly bool fromPreview;

        public AntigravityName(string title,string preview) {
            fromPreview = Missing(title);
            if (!fromPreview) { text = title.Trim(); return; }
            string line = string.Join(" ", preview.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            var elements = new StringInfo(line);
            text = elements.LengthInTextElements > 48 ? elements.SubstringByTextElements(0,48) + "…" : line;
        }

        static bool Missing(string value) {
            string name = (value ?? "").Trim();
            return name.Length == 0 || name == "新对话" ||
                name.Equals("New conversation",StringComparison.OrdinalIgnoreCase) ||
                name.Equals("New chat",StringComparison.OrdinalIgnoreCase);
        }

        public bool Apply(Session session) {
            if (Missing(text) || (fromPreview && !Missing(session.Title) && session.TitleKind != "preview")) return false;
            string kind = fromPreview ? "preview" : "title";
            if (session.Title == text && session.TitleKind == kind) return false;
            session.Title = text;
            session.TitleKind = kind;
            return true;
        }
    }

    // CascadeTrajectorySummary.trajectory_metadata (17) contains the lineage even
    // when the denormalized SQLite parent and depth columns have not been filled.
    internal static class AntigravityLineage {
        static int Varint(byte[] data,ref int position,int end) {
            int result=0;
            for(int shift=0;shift<32 && position<end;shift+=7){
                int value=data[position++];
                if(shift==28 && value>7)return -1;
                result|=(value&127)<<shift;
                if(value<128)return result;
            }
            return -1;
        }

        static bool Scan(byte[] data,int position,int end,Guid self,bool metadata,out bool child) {
            child=false;
            while(position<end){
                int tag=Varint(data,ref position,end);
                if(tag<8)return false;
                int field=tag>>3;
                switch(tag&7){
                    case 0:
                        // Unknown numeric fields may be full 64-bit values.
                        int start=position;
                        while(position<end && (data[position]&128)!=0 && position-start<9)position++;
                        if(position==end)return false;
                        int last=data[position];
                        if((last&128)!=0 || (position-start==9 && last>1))return false;
                        position++;
                        break;
                    case 1: if(end-position<8)return false;position+=8;break;
                    case 5: if(end-position<4)return false;position+=4;break;
                    case 2:
                        int size=Varint(data,ref position,end);
                        if(size<0 || size>end-position)return false;
                        if(!metadata && field==17){
                            bool nested;
                            if(!Scan(data,position,position+size,self,true,out nested))return false;
                            child|=nested;
                        }else if(metadata && (field==5 || field==6) && size==36){
                            Guid related;
                            if(Guid.TryParseExact(System.Text.Encoding.ASCII.GetString(data,position,size),"D",out related)
                                && related!=Guid.Empty && related!=self)child=true;
                        }
                        position+=size;break;
                    default:return false;
                }
            }
            return true;
        }

        public static bool IsChild(string id,string hex) {
            Guid self;
            if(!Guid.TryParseExact(id,"D",out self) || hex.Length==0 || hex.Length%2!=0)return false;
            var data=new byte[hex.Length/2];
            for(int i=0;i<data.Length;i++){
                // SQLite hex() emits uppercase ASCII.
                int hi="0123456789ABCDEF".IndexOf(hex[2*i]),lo="0123456789ABCDEF".IndexOf(hex[2*i+1]);
                if(hi<0 || lo<0)return false;
                data[i]=(byte)(hi*16+lo);
            }
            bool child;
            return Scan(data,0,data.Length,self,false,out child) && child;
        }
    }

    // The hub owns this adapter and calls it on its serial polling worker.
    internal sealed class AntigravityEngine {
        readonly string home;
        readonly string database;
        readonly Dictionary<string, Session> sessions;
        DateTime attachedAt;
        string summaryQuery;
        public readonly HashSet<string> ChildIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string,AntigravityName> Names = new Dictionary<string,AntigravityName>(StringComparer.OrdinalIgnoreCase);
        public bool Available { get; private set; }
        public string Health { get; private set; }
        public List<Session> Snapshot { get; private set; }

        public AntigravityEngine(string home, IEnumerable<Session> restored) {
            this.home = home;
            database = Path.Combine(home, "conversation_summaries.db");
            sessions = restored.ToDictionary(session => session.Id, session => session, StringComparer.OrdinalIgnoreCase);
            Snapshot = new List<Session>();
            Health = "等待 Antigravity";
        }

        public List<Session> Poll(bool live, DateTime now) {
            var notices = new List<Session>();
            Available = false;
            Snapshot = new List<Session>();
            if (!File.Exists(database)) {
                Health = "等待 Antigravity 本地会话";
                return notices;
            }
            try {
                if(summaryQuery==null){
                    var columns=new HashSet<string>(ReadOnlySqlite.Query(database,"PRAGMA table_info(conversation_summaries)").Select(c=>c["name"]));
                    string preview=columns.Contains("preview")?"substr(preview,1,160)":"''";
                    string lineage=columns.Contains("raw_summary")?"CASE WHEN length(raw_summary)<=262144 THEN hex(raw_summary) ELSE '' END":"''";
                    var parentColumns=ReadOnlySqlite.Query(database,"PRAGMA table_info(conversation_parents)").Select(c=>c["name"]).ToList();
                    string linked=parentColumns.Contains("parent_id")&&parentColumns.Contains("conversation_id")
                        ?" OR EXISTS(SELECT 1 FROM conversation_parents p WHERE p.conversation_id=s.conversation_id AND p.parent_id<>'' AND p.parent_id<>s.conversation_id)":"";
                    summaryQuery="SELECT conversation_id,title,"+preview+" AS preview,"+lineage+" AS lineage,"+
                        "(coalesce(parent_conversation_id,'')<>'' OR coalesce(nesting_depth,0)>0"+linked+") AS is_child,"+
                        "workspace_uris,status,not_fully_idle,killed,last_modified_time,last_user_input_time,last_user_input_step_index FROM conversation_summaries s";
                }
                var rows=ReadOnlySqlite.Query(database,summaryQuery);
                Names.Clear();
                ChildIds.Clear();
                foreach (var row in rows) {
                    string id=row["conversation_id"];
                    if(row["is_child"]=="1" || AntigravityLineage.IsChild(id,row["lineage"])){
                        ChildIds.Add(id);
                        sessions.Remove(id);
                        continue;
                    }
                    Names[row["conversation_id"]] = new AntigravityName(row["title"], row["preview"]);
                    Session session = ReadSession(row, live, now);
                    if (session == null) continue;
                    Session previous;
                    bool sameRound = sessions.TryGetValue(session.Id, out previous) && CurrentTaskIdentity.SameRound(previous, session);
                    session.NotifiedKey = sameRound ? previous.NotifiedKey : "";
                    if (session.Status == "completed") {
                        if (attachedAt != DateTime.MinValue && live && session.NotifiedKey != session.TurnId)
                            notices.Add(session.Copy());
                        session.NotifiedKey = session.TurnId;
                    }
                    sessions[session.Id] = session;
                    Snapshot.Add(session.Copy());
                }
                if (attachedAt == DateTime.MinValue) attachedAt = now;
                Available = live;
                Health = live ? "Antigravity 已连接" : "等待 Antigravity 启动";
            } catch (IOException) { Health = "Antigravity 本地会话暂不可读"; }
            return notices;
        }

        Session ReadSession(Dictionary<string, string> row, bool live, DateTime now) {
            Guid id;
            int inputIndex;
            DateTime started = Json.Date(row["last_user_input_time"]);
            if (!Guid.TryParseExact(row["conversation_id"], "D", out id) ||
                !int.TryParse(row["last_user_input_step_index"], NumberStyles.Integer, CultureInfo.InvariantCulture, out inputIndex) ||
                started == DateTime.MinValue)
                return null;

            string key = id.ToString("D");
            Session previous;
            bool known = sessions.TryGetValue(key, out previous);
            string summaryStatus = AntigravitySource.Status(row);
            bool active = live && (summaryStatus == "running" || summaryStatus == "waiting");
            DateTime modified = Json.Date(row["last_modified_time"]);
            // Keep startup quiet, but catch fast turns that start and finish between two polls.
            bool newTurn = live && attachedAt != DateTime.MinValue && started >= attachedAt && started <= now;
            if (!known && !active && !newTurn) return null;
            if (inputIndex < 0) {
                string path = Path.Combine(home,"conversations",key+".db");
                if (!File.Exists(path)) return null;
                try {
                    var inputs = ReadOnlySqlite.Query(path,"SELECT idx FROM steps WHERE step_type=14 ORDER BY idx DESC LIMIT 1");
                    if (inputs.Count == 0 || !int.TryParse(inputs[0]["idx"],NumberStyles.None,CultureInfo.InvariantCulture,out inputIndex)) return null;
                } catch (IOException) { return null; }
            }
            string turn = "agy-" + started.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + inputIndex.ToString(CultureInfo.InvariantCulture);
            bool sameRound = known && previous.TurnId == turn;
            if (known && !sameRound && !active && !newTurn) return null;
            if (sameRound && previous.Terminal && !active && modified == previous.LastActivity) {
                Session cached = previous.Copy();
                Names[key].Apply(cached);
                return cached;
            }

            var steps = ReadTail(key, inputIndex);
            string status = AntigravitySource.Status(row, steps);
            if (!live && (status == "running" || status == "waiting")) status = "unknown";
            if (status == "unknown" && sameRound && previous.Terminal) status = previous.Status;
            if (!sameRound && status == "unknown") return null;

            var session = new Session {
                Platform = AntigravitySource.Platform,
                Id = key,
                Title = known ? previous.Title : "",
                TitleKind = known ? previous.TitleKind : "",
                Cwd = AntigravitySource.WorkspacePath(row["workspace_uris"]),
                FilePath = database,
                TurnId = turn,
                StartedAt = started,
                LastActivity = modified < started ? started : modified,
                FirstSeen = known ? previous.FirstSeen : now,
                Status = status,
                Visible = true,
                HasLifecycle = true
            };
            Names[key].Apply(session);
            if (session.Terminal) {
                session.FinishedAt = sameRound && previous.FinishedAt != DateTime.MinValue
                    ? previous.FinishedAt : session.LastActivity;
                session.TimerEstimated = true; // The summary time is the last edit, not an explicit finish event time.
            }
            return session;
        }

        List<Dictionary<string, string>> ReadTail(string id, int inputIndex) {
            string path = Path.Combine(home, "conversations", id + ".db");
            if (File.Exists(path)) {
                try {
                    return ReadOnlySqlite.Query(path, "SELECT step_type,status FROM steps WHERE idx>" +
                        inputIndex.ToString(CultureInfo.InvariantCulture) + " ORDER BY idx DESC LIMIT 24");
                } catch (IOException) {}
            }
            return new List<Dictionary<string, string>>();
        }
    }
}
