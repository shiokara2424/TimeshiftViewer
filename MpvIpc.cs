using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TimeshiftViewer
{
    // ------------------------------------------------------------
    //  mpv の JSON IPC（--input-ipc-server）
    //  命令の送信と応答、script-message（client-message）とプロパティ変化の受信
    // ------------------------------------------------------------
    sealed class MpvIpc : IDisposable
    {
        readonly NamedPipeClientStream pipe;
        readonly StreamWriter writer;
        readonly object writeLock = new object();
        readonly ConcurrentDictionary<int, TaskCompletionSource<object>> pending = new ConcurrentDictionary<int, TaskCompletionSource<object>>();
        int nextId;
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        /// <summary>script-message の受信（1つ目の引数が名前、残りが引数）。読み取りスレッドから呼ばれる</summary>
        public event Action<string, string[]> ClientMessage;
        /// <summary>observe_property したプロパティの変化。読み取りスレッドから呼ばれる</summary>
        public event Action<string, object> PropertyChanged;
        public event Action Closed;

        MpvIpc(NamedPipeClientStream p)
        {
            pipe = p;
            writer = new StreamWriter(p, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var t = new Thread(ReadLoop) { IsBackground = true, Name = "mpv-ipc" };
            t.Start();
        }

        /// <summary>mpv が IPC を開くまで待ってつなぐ（mpv が終了したら null）</summary>
        public static MpvIpc Connect(string pipeName, Process mp, int timeoutMs = 15000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && !mp.HasExited)
            {
                var c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { c.Connect(200); return new MpvIpc(c); }
                catch { c.Dispose(); Thread.Sleep(100); }
            }
            return null;
        }

        void ReadLoop()
        {
            try
            {
                using (var r = new StreamReader(pipe, new UTF8Encoding(false)))
                {
                    string line;
                    while ((line = r.ReadLine()) != null) Handle(line);
                }
            }
            catch { }
            foreach (var kv in pending) kv.Value.TrySetResult(null);
            Closed?.Invoke();
        }

        void Handle(string line)
        {
            Dictionary<string, object> o;
            try { o = json.Deserialize<Dictionary<string, object>>(line); } catch { return; }
            if (o == null) return;
            if (o.TryGetValue("request_id", out var rid) && rid is int id && pending.TryRemove(id, out var tcs))
            {
                o.TryGetValue("error", out var err);
                o.TryGetValue("data", out var data);
                tcs.TrySetResult((err as string) == "success" ? data : null);
                return;
            }
            if (!o.TryGetValue("event", out var ev)) return;
            switch (ev as string)
            {
                case "client-message":
                    if (o.TryGetValue("args", out var a) && a is ArrayList list && list.Count > 0)
                    {
                        var args = new string[list.Count - 1];
                        for (int i = 1; i < list.Count; i++) args[i - 1] = list[i]?.ToString() ?? "";
                        try { ClientMessage?.Invoke(list[0]?.ToString() ?? "", args); } catch { }
                    }
                    break;
                case "property-change":
                    o.TryGetValue("name", out var name);
                    o.TryGetValue("data", out var d);
                    try { PropertyChanged?.Invoke(name as string, d); } catch { }
                    break;
            }
        }

        void Write(Dictionary<string, object> msg)
        {
            string s = json.Serialize(msg);
            lock (writeLock) { try { writer.WriteLine(s); } catch { } }
        }

        /// <summary>命令を送って応答を待つ（失敗・切断時は null）</summary>
        public Task<object> Command(params object[] args)
        {
            int id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            Write(new Dictionary<string, object> { ["command"] = args, ["request_id"] = id });
            return tcs.Task;
        }

        /// <summary>応答を待たずに送る</summary>
        public void Send(params object[] args) => Write(new Dictionary<string, object> { ["command"] = args });

        public async Task<double?> GetDouble(string prop)
        {
            var v = await Command("get_property", prop).ConfigureAwait(false);
            if (v == null) return null;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return null; }
        }

        public void Observe(string prop) => Send("observe_property", Interlocked.Increment(ref nextId), prop);

        /// <summary>timeshift.lua へ script-message を送る（引数は JSON 文字列など）</summary>
        public void ToScript(string name, params string[] args)
        {
            var a = new object[args.Length + 3];
            a[0] = "script-message-to"; a[1] = "timeshift"; a[2] = name;
            for (int i = 0; i < args.Length; i++) a[i + 3] = args[i];
            Send(a);
        }

        public static string ToJson(object o) => json.Serialize(o);

        public void Dispose()
        {
            try { pipe.Dispose(); } catch { }
        }
    }
}
