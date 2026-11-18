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

    // The hub owns this adapter and calls it on its serial polling worker.
    internal sealed class AntigravityEngine {
        readonly string home;
        readonly string database;
        readonly Dictionary<string, Session> sessions;
        DateTime attachedAt;
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
                var rows = ReadOnlySqlite.Query(database,
                    "SELECT conversation_id,title,workspace_uris,status,not_fully_idle,killed,last_modified_time," +
                    "last_user_input_time,last_user_input_step_index FROM conversation_summaries " +
                    "WHERE COALESCE(parent_conversation_id,'')='' AND nesting_depth=0 AND last_user_input_step_index>=0");
                foreach (var row in rows) {
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
                !int.TryParse(row["last_user_input_step_index"], NumberStyles.None, CultureInfo.InvariantCulture, out inputIndex) ||
                started == DateTime.MinValue)
                return null;

            string key = id.ToString("D");
            string turn = "agy-" + started.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + inputIndex.ToString(CultureInfo.InvariantCulture);
            Session previous;
            bool known = sessions.TryGetValue(key, out previous);
            bool sameRound = known && previous.TurnId == turn;
            string summaryStatus = AntigravitySource.Status(row);
            bool active = live && (summaryStatus == "running" || summaryStatus == "waiting");
            DateTime modified = Json.Date(row["last_modified_time"]);
            // Keep startup quiet, but catch fast turns that start and finish between two polls.
            bool newTurn = live && attachedAt != DateTime.MinValue && started >= attachedAt && started <= now;
            if (!known && !active && !newTurn) return null;
            if (known && !sameRound && !active && !newTurn) return null;
            if (sameRound && previous.Terminal && !active && modified == previous.LastActivity && row["title"] == previous.Title)
                return previous.Copy();

            var steps = ReadTail(key, inputIndex);
            string status = AntigravitySource.Status(row, steps);
            if (!live && (status == "running" || status == "waiting")) status = "unknown";
            if (status == "unknown" && sameRound && previous.Terminal) status = previous.Status;
            if (!sameRound && status == "unknown") return null;

            var session = new Session {
                Platform = AntigravitySource.Platform,
                Id = key,
                Title = row["title"],
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
