// ============================================================
//  AI 调用：OpenAI 兼容 /chat/completions，支持流式与图片
//  在后台线程执行，通过回调把增量文本推回界面线程
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace TaskHarbor
{
    internal class AiItem
    {
        public string title = "";
        public string date = "";
        public string pri = "mid";
        public string quadrant = "q2";
        public string period = "day";
        public string due = "";
    }

    internal class AiJob
    {
        public string BaseUrl = "";
        public string Endpoint = "/chat/completions";
        public string ApiKey = "";
        public string Model = "";
        public string Prompt = "";
        public string Text = "";
        public List<string> Images = new List<string>();

        public Action<string> OnDelta;      // 流式增量
        public Action<List<AiItem>, string> OnDone;   // 结果 + 原始文本
        public Action<string> OnError;

        private HttpWebRequest _req;
        private volatile bool _cancelled;

        static AiJob()
        {
            // 用系统自带编译器打出来的 exe 会被当成「.NET 4.0 应用」，
            // 默认只开 TLS 1.0 —— 现代大模型 API 一律要求 TLS 1.2+，
            // 不显式打开就会报「无法建立 SSL/TLS 的安全通道」。
            try
            {
                ServicePointManager.SecurityProtocol =
                    (SecurityProtocolType)192 |    // Tls  1.0
                    (SecurityProtocolType)768 |    // Tls  1.1
                    (SecurityProtocolType)3072;    // Tls  1.2
            }
            catch { }
        }

        public void Cancel()
        {
            _cancelled = true;
            try { if (_req != null) _req.Abort(); } catch { }
        }

        public void Start()
        {
            Thread t = new Thread(new ThreadStart(Run));
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        private string BuildUrl()
        {
            string b = (BaseUrl == null ? "" : BaseUrl.Trim());
            if (b.Length == 0) return "";
            b = b.TrimEnd('/');
            string ep = (Endpoint == null ? "" : Endpoint.Trim());
            if (ep.Length == 0) ep = "/chat/completions";
            if (!ep.StartsWith("/")) ep = "/" + ep;
            if (b.EndsWith(ep, StringComparison.OrdinalIgnoreCase)) return b;
            if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
            return b + ep;
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private string BuildBody()
        {
            string sys = Prompt;
            if (string.IsNullOrEmpty(sys)) sys = DefaultPrompt;
            sys = sys.Replace("{TODAY}", DateTime.Now.ToString("yyyy-MM-dd"));

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(Esc(Model)).Append("\",\"stream\":true,\"temperature\":0.2,\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"").Append(Esc(sys)).Append("\"},");
            sb.Append("{\"role\":\"user\",\"content\":");

            if (Images != null && Images.Count > 0)
            {
                sb.Append("[");
                string txt = string.IsNullOrEmpty(Text) ? "请从这些图片里提取待办事项。" : Text;
                sb.Append("{\"type\":\"text\",\"text\":\"").Append(Esc(txt)).Append("\"}");
                for (int i = 0; i < Images.Count; i++)
                {
                    string d = Images[i];
                    if (d == null) continue;
                    if (d.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(",{\"type\":\"image_url\",\"image_url\":{\"url\":\"").Append(Esc(d)).Append("\"}}");
                    }
                    else
                    {
                        sb.Append(",{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,").Append(Esc(d)).Append("\"}}");
                    }
                }
                sb.Append("]");
            }
            else
            {
                sb.Append("\"").Append(Esc(Text)).Append("\"");
            }

            sb.Append("}]}");
            return sb.ToString();
        }

        public const string DefaultPrompt =
            "你是一个待办事项提取助手。用户会给你一段文字，或一张包含文字/信息的图片。\n" +
            "请从中提取所有需要执行的待办事项。\n\n" +
            "规则：\n" +
            "1. 每条任务精简到 2-18 个字，动词开头，删掉语气词和冗余描述\n" +
            "2. 把相对日期换算成具体日期（今天是 {TODAY}，以周一为一周第一天）\n" +
            "3. 独立判断重要性和紧急性，quadrant 取值：q1=重要紧急，q2=重要不紧急，q3=不重要紧急，q4=不重要不紧急；无法判断时用q2。pri兼容字段：q1=high，q4=low，其余=mid\n" +
            "4. 最多 12 条，重复的合并\n" +
            "5. 如果内容里确实没有待办事项，返回空数组\n" +
            "6. 区分计划与硬性截止：period 为 day/week/month。仅当用户明确要求截止日期时填写 date (YYYY-MM-DD)，否则 date 为空字符串；不要把本周自动变成周日截止。due 为 HH:mm，仅明确时间时填写，否则为空。\n\n" +
            "只输出 JSON，不要任何解释、不要 markdown 代码块：\n" +
            "{\"items\":[{\"title\":\"任务内容\",\"date\":\"\",\"due\":\"\",\"period\":\"week\",\"pri\":\"mid\",\"quadrant\":\"q2\"}]}";

        private void Fail(string msg)
        {
            if (_cancelled) return;
            if (OnError != null) OnError(msg);
        }

        private void Run()
        {
            try
            {
                string url = BuildUrl();
                if (url.Length == 0) { Fail("还没有填接口地址。进「设置 → AI 模型」填一下。"); return; }
                Uri endpoint;
                if (!Uri.TryCreate(url, UriKind.Absolute, out endpoint) || (endpoint.Scheme != "https" && endpoint.Scheme != "http")) { Fail("接口地址无效"); return; }
                if (string.IsNullOrEmpty(ApiKey) && !endpoint.IsLoopback) { Fail("远程 AI 服务需要 API Key；本地回环服务可以不填。"); return; }
                if (string.IsNullOrEmpty(Model)) { Fail("还没有填模型名。进「设置 → AI 模型」填一下。"); return; }

                string body = BuildBody();
                byte[] raw = Encoding.UTF8.GetBytes(body);

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                _req = req;
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Accept = "text/event-stream";
                req.Timeout = 180000;
                req.ReadWriteTimeout = 180000;
                req.KeepAlive = false;
                if (endpoint.IsLoopback) req.Proxy = null;
                try { req.UserAgent = "TaskHarbor/3.0"; } catch { }
                if (!string.IsNullOrEmpty(ApiKey)) req.Headers.Add("Authorization", "Bearer " + ApiKey);

                req.ContentLength = raw.Length;
                using (Stream rs = req.GetRequestStream())
                {
                    rs.Write(raw, 0, raw.Length);
                }

                StringBuilder all = new StringBuilder();
                HttpWebResponse resp = null;
                try
                {
                    resp = (HttpWebResponse)req.GetResponse();
                }
                catch (WebException wex)
                {
                    string detail = ReadErrorBody(wex);
                    Fail(DescribeError(wex, detail));
                    return;
                }

                using (resp)
                using (Stream st = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(st, Encoding.UTF8))
                {
                    string ctype = "";
                    try { ctype = resp.ContentType == null ? "" : resp.ContentType.ToLowerInvariant(); }
                    catch { }

                    if (ctype.IndexOf("event-stream") < 0)
                    {
                        // 有些中转站 / 老网关会忽略 stream:true，直接回一整个 JSON。
                        // 这种情况下按一次性响应解析，否则什么都读不到。
                        string whole = sr.ReadToEnd();
                        string one = ExtractFullContent(whole);
                        if (one == null)
                        {
                            string snip = whole == null ? "" : whole.Trim();
                            if (snip.Length > 200) snip = snip.Substring(0, 200) + "…";
                            Fail("接口返回的不是预期格式。\n" + snip);
                            return;
                        }
                        if (one.Length > 0)
                        {
                            all.Append(one);
                            if (OnDelta != null) OnDelta(one);
                        }
                    }
                    else
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (_cancelled) return;
                            if (line.Length == 0) continue;
                            if (!line.StartsWith("data:")) continue;
                            string payload = line.Substring(5).Trim();
                            if (payload.Length == 0) continue;
                            if (payload == "[DONE]") break;

                            string delta = ExtractDelta(payload);
                            if (delta != null && delta.Length > 0)
                            {
                                all.Append(delta);
                                if (OnDelta != null) OnDelta(delta);
                            }
                        }
                    }
                }

                string text = all.ToString();
                List<AiItem> items = ParseItems(text);
                if (_cancelled) return;
                if (OnDone != null) OnDone(items, text);
            }
            catch (WebException wex)
            {
                if (_cancelled) return;
                Fail(DescribeError(wex, ReadErrorBody(wex)));
            }
            catch (Exception ex)
            {
                if (_cancelled) return;
                Fail(ex.Message);
            }
        }

        private static string ReadErrorBody(WebException wex)
        {
            try
            {
                if (wex.Response == null) return "";
                using (Stream s = wex.Response.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                {
                    string t = sr.ReadToEnd();
                    if (t.Length > 600) t = t.Substring(0, 600) + "…";
                    return t;
                }
            }
            catch { return ""; }
        }

        private static string DescribeError(WebException wex, string body)
        {
            string head = "";
            if (wex.Status == WebExceptionStatus.Timeout) head = "请求超时";
            else if (wex.Status == WebExceptionStatus.NameResolutionFailure) head = "域名解析失败，检查接口地址是否写对";
            else if (wex.Status == WebExceptionStatus.ConnectFailure) head = "连不上服务器，检查网络或地址";
            else if (wex.Response is HttpWebResponse)
            {
                int code = (int)((HttpWebResponse)wex.Response).StatusCode;
                if (code == 401) head = "API Key 不对或没权限（401）";
                else if (code == 402) head = "余额不足或需要充值（402）";
                else if (code == 404) head = "接口地址不存在（404），检查地址和接口路径";
                else if (code == 429) head = "请求太频繁或超出配额（429）";
                else if (code >= 500) head = "服务端错误（" + code + "），稍后再试";
                else head = "请求失败（HTTP " + code + "）";
            }
            else head = "请求失败：" + wex.Message;

            if (!string.IsNullOrEmpty(body)) return head + "\n" + body;
            return head;
        }

        /// <summary>
        /// 解析「一次性返回」的完整响应，取出正文。
        /// 返回 null 表示这不是一个能识别的 JSON 响应（调用方据此报错）。
        /// </summary>
        private static string ExtractFullContent(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            body = body.Trim();
            if (body.Length == 0) return "";

            try
            {
                var js = new JavaScriptSerializer();
                var d = js.Deserialize<Dictionary<string, object>>(body);
                if (d == null) return null;

                // 有些网关用 200 带一个 {"error":{...}} 回来，
                // 这里返回 null，让调用方把原始片段显示出来（正好能看到报错原因）
                if (d.ContainsKey("error") && d["error"] != null) return null;

                if (!d.ContainsKey("choices")) return null;
                object[] ch = d["choices"] as object[];
                if (ch == null || ch.Length == 0) return null;
                var c0 = ch[0] as Dictionary<string, object>;
                if (c0 == null) return null;

                if (c0.ContainsKey("message"))
                {
                    var ms = c0["message"] as Dictionary<string, object>;
                    if (ms != null && ms.ContainsKey("content") && ms["content"] != null)
                        return ms["content"].ToString();
                }
                if (c0.ContainsKey("text") && c0["text"] != null) return c0["text"].ToString();
                if (c0.ContainsKey("delta"))
                {
                    var dl = c0["delta"] as Dictionary<string, object>;
                    if (dl != null && dl.ContainsKey("content") && dl["content"] != null)
                        return dl["content"].ToString();
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>从一行 SSE JSON 里取出 delta.content。</summary>
        private static string ExtractDelta(string payload)
        {
            try
            {
                var js = new JavaScriptSerializer();
                var d = js.Deserialize<Dictionary<string, object>>(payload);
                if (d == null || !d.ContainsKey("choices")) return null;
                object[] ch = d["choices"] as object[];
                if (ch == null || ch.Length == 0) return null;
                var c0 = ch[0] as Dictionary<string, object>;
                if (c0 == null) return null;
                if (c0.ContainsKey("delta"))
                {
                    var dl = c0["delta"] as Dictionary<string, object>;
                    if (dl != null && dl.ContainsKey("content") && dl["content"] != null)
                        return dl["content"].ToString();
                }
                if (c0.ContainsKey("message"))
                {
                    var ms = c0["message"] as Dictionary<string, object>;
                    if (ms != null && ms.ContainsKey("content") && ms["content"] != null)
                        return ms["content"].ToString();
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>把模型返回的文本解析成任务列表（容错处理）。</summary>
        public static List<AiItem> ParseItems(string text)
        {
            var list = new List<AiItem>();
            if (string.IsNullOrEmpty(text)) return list;

            string json = ExtractJsonObject(text);
            if (json == null) return list;

            try
            {
                var js = new JavaScriptSerializer();
                var d = js.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return list;

                System.Collections.IList arr = null;
                if (d.ContainsKey("items")) arr = d["items"] as System.Collections.IList;
                if (arr == null && d.ContainsKey("tasks")) arr = d["tasks"] as System.Collections.IList;
                if (arr == null) return list;

                for (int i = 0; i < arr.Count && list.Count < 20; i++)
                {
                    var o = arr[i] as Dictionary<string, object>;
                    if (o == null) continue;
                    AiItem it = new AiItem();
                    it.title = Pick(o, "title", "task", "content", "name");
                    string date = Pick(o, "date", "deadline");
                    it.date = string.IsNullOrWhiteSpace(date) ? "" : NormalizeDate(date);
                    string period = Pick(o, "period");
                    it.period = period == "week" || period == "month" ? period : "day";
                    string due = Pick(o, "due", "time");
                    it.due = Regex.IsMatch(due, "^([01][0-9]|2[0-3]):[0-5][0-9]$") && it.date.Length > 0 ? due : "";
                    it.pri = NormalizePri(Pick(o, "pri", "priority", "level"));
                    string q = Pick(o, "quadrant");
                    it.quadrant = Regex.IsMatch(q, "^q[1-4]$") ? q : it.pri == "high" ? "q1" : it.pri == "low" ? "q4" : "q2";
                    it.title = it.title.Trim();
                    if (it.title.Length > 60) it.title = it.title.Substring(0, 60);
                    if (it.title.Length == 0) continue;
                    list.Add(it);
                }
            }
            catch { }
            return list;
        }

        private static string Pick(Dictionary<string, object> d, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                if (d.ContainsKey(keys[i]) && d[keys[i]] != null)
                {
                    string v = d[keys[i]].ToString();
                    if (v.Trim().Length > 0) return v;
                }
            }
            return "";
        }

        /// <summary>从文本中找出最外层 JSON 对象（容忍 markdown 代码块和前后废话）。</summary>
        public static string ExtractJsonObject(string text)
        {
            if (text == null) return null;
            int fence = text.IndexOf("```");
            if (fence >= 0)
            {
                int nl = text.IndexOf('\n', fence);
                int end = text.LastIndexOf("```");
                if (nl > 0 && end > nl) text = text.Substring(nl + 1, end - nl - 1);
            }
            int s = text.IndexOf('{');
            int e = text.LastIndexOf('}');
            if (s < 0 || e <= s) return null;
            return text.Substring(s, e - s + 1);
        }

        private static readonly Regex ReIso = new Regex(@"(\d{4})\s*[-/年]\s*(\d{1,2})\s*[-/月]\s*(\d{1,2})", RegexOptions.Compiled);
        private static readonly Regex ReMd = new Regex(@"(\d{1,2})\s*[-/月]\s*(\d{1,2})\s*日?", RegexOptions.Compiled);

        public static string NormalizeDate(string s)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (string.IsNullOrEmpty(s)) return today;
            s = s.Trim();

            Match m = ReIso.Match(s);
            if (m.Success)
            {
                int y, mo, d;
                if (int.TryParse(m.Groups[1].Value, out y) &&
                    int.TryParse(m.Groups[2].Value, out mo) &&
                    int.TryParse(m.Groups[3].Value, out d))
                {
                    if (y >= 1900 && y <= 9999 && mo >= 1 && mo <= 12 && d >= 1 && d <= 31)
                    {
                        // 模型偶尔会吐出 2 月 31 号这种日期，直接构造会抛异常
                        int max = DateTime.DaysInMonth(y, mo);
                        if (d > max) d = max;
                        return new DateTime(y, mo, d).ToString("yyyy-MM-dd");
                    }
                }
            }
            m = ReMd.Match(s);
            if (m.Success)
            {
                int mo, d;
                if (int.TryParse(m.Groups[1].Value, out mo) && int.TryParse(m.Groups[2].Value, out d))
                {
                    if (mo >= 1 && mo <= 12 && d >= 1 && d <= 31)
                    {
                        int y2 = DateTime.Now.Year;
                        int max2 = DateTime.DaysInMonth(y2, mo);
                        if (d > max2) d = max2;
                        return new DateTime(y2, mo, d).ToString("yyyy-MM-dd");
                    }
                }
            }
            return today;
        }

        public static string NormalizePri(string s)
        {
            if (string.IsNullOrEmpty(s)) return "mid";
            string v = s.Trim().ToLowerInvariant();
            if (v == "high" || v == "h" || v == "1" || v == "高" || v == "紧急" || v == "重要") return "high";
            if (v == "low" || v == "l" || v == "3" || v == "低" || v == "低优先级") return "low";
            if (v == "mid" || v == "medium" || v == "m" || v == "2" || v == "中") return "mid";
            return "mid";
        }
    }
}
