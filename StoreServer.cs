using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RfidSmartStore.Local
{
    internal sealed class StoreServer
    {
        private readonly ManualResetEvent stopped = new ManualResetEvent(false);
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly StoreRepository repository;
        private readonly Kl9007uReader reader;
        private readonly string webRoot;
        private readonly int port;
        private readonly object paymentSync = new object();
        private string pendingPaymentToken = string.Empty;
        private long pendingPaymentCents;
        private DateTime pendingPaymentExpiresAt = DateTime.MinValue;
        private TcpListener listener;
        private Thread listenerThread;
        private volatile bool running;

        public StoreServer(string prefix, string baseDir, StoreRepository repository, Kl9007uReader reader)
        {
            Uri address = new Uri(prefix);
            port = address.Port;
            webRoot = Path.Combine(baseDir, "web");
            this.repository = repository;
            this.reader = reader;
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            running = true;
            listenerThread = new Thread(ListenLoop);
            listenerThread.IsBackground = true;
            listenerThread.Start();
        }
        public void Wait() { stopped.WaitOne(); }
        public void Stop()
        {
            running = false;
            try { reader.Shutdown(); } catch { }
            try { listener.Stop(); } catch { }
            stopped.Set();
        }

        private void ListenLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
                }
                catch { if (running) Thread.Sleep(100); }
            }
            stopped.Set();
        }

        private void Handle(TcpClient client)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                try
                {
                    Request request = ReadRequest(stream);
                    if (request == null) return;
                    if (request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) HandleApi(stream, request);
                    else ServeFile(stream, request.Path);
                }
                catch (Exception ex) { SendJson(stream, 400, new Dictionary<string, object> { { "ok", false }, { "message", ex.Message } }); }
            }
        }

        private void HandleApi(NetworkStream stream, Request request)
        {
            string path = request.Path;
            if (request.Method == "GET" && path == "/api/status")
            {
                string epc = reader.LastEpc;
                SendJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "connected", reader.IsConnected }, { "port", reader.PortName }, { "baudRate", reader.BaudRate }, { "autoConnect", reader.AutoConnectEnabled }, { "epc", epc }, { "readAt", reader.LastReadAt == DateTime.MinValue ? "" : reader.LastReadAt.ToString("o") }, { "readSequence", reader.ReadSequence }, { "readerError", reader.Error }, { "member", string.IsNullOrEmpty(epc) ? null : MemberDto(repository.GetMemberByEpc(epc)) } });
                return;
            }
            if (request.Method == "GET" && path == "/api/transactions")
            {
                SendJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "items", repository.GetRecentTransactions() } });
                return;
            }
            if (request.Method == "GET" && path == "/api/reports/daily")
            {
                SendJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "report", ReportDto(repository.GetDailyReport()) } });
                return;
            }
            if (request.Method == "GET" && path == "/api/cards")
            {
                List<Dictionary<string, object>> cards = repository.GetCards();
                List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
                for (int i = 0; i < cards.Count; i++) items.Add(CardDto(cards[i]));
                SendJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "items", items } });
                return;
            }
            if (request.Method == "POST" && path == "/api/reader/connect") { reader.Connect(); SendOk(stream, "KL9007U已连接"); return; }
            if (request.Method == "POST" && path == "/api/reader/disconnect") { reader.Disconnect(); SendOk(stream, "读写器已断开"); return; }
            Dictionary<string, object> body = string.IsNullOrWhiteSpace(request.Body) ? new Dictionary<string, object>() : json.Deserialize<Dictionary<string, object>>(request.Body);
            if (request.Method == "POST" && path == "/api/demo/card")
            {
                string epc = GetText(body, "epc");
                if (string.IsNullOrEmpty(epc)) epc = "E200341201" + DateTime.Now.ToString("yyMMddHHmmss00");
                reader.Simulate(epc); SendOk(stream, epc); return;
            }
            if (request.Method == "POST" && path == "/api/cards/open")
            {
                string epc = GetText(body, "epc").ToUpperInvariant();
                bool rewrite = GetBool(body, "rewriteCard");
                if (rewrite)
                {
                    string generated = GetText(body, "newEpc").ToUpperInvariant();
                    if (string.IsNullOrEmpty(generated)) generated = "E200" + DateTime.Now.ToString("yyMMddHHmmss") + new Random().Next(1000, 9999).ToString("0000");
                    while (generated.Length % 4 != 0) generated += "0";
                    reader.WriteEpc(generated);
                    epc = generated;
                }
                if (string.IsNullOrEmpty(epc)) throw new InvalidOperationException("请先读取实体卡");
                Dictionary<string, object> member = repository.CreateMember(epc, Required(body, "name", "请输入姓名"), Required(body, "phone", "请输入手机号"), Default(GetText(body, "level"), "普通会员"), OptionalMoneyToCents(body, "openingBalance"));
                SendJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "message", "实体卡开卡成功" }, { "member", MemberDto(member) } }); return;
            }
            if (request.Method == "POST" && path == "/api/cards/update")
            {
                Dictionary<string, object> card = repository.UpdateCard(Required(body, "epc", "卡片EPC不能为空"), Required(body, "name", "请输入姓名"), Required(body, "phone", "请输入手机号"), Required(body, "level", "请选择会员等级"), Required(body, "cardStatus", "请选择卡片状态"), Required(body, "memberStatus", "请选择会员状态"));
                SendJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "message", "卡片资料已更新" }, { "card", CardDto(card) } }); return;
            }
            if (request.Method == "POST" && path == "/api/recharge")
            {
                Dictionary<string, object> member = repository.Recharge(Required(body, "epc", "请先刷会员卡"), MoneyToCents(body, "amount"));
                SendJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "message", "充值成功" }, { "member", MemberDto(member) } }); return;
            }
            if (request.Method == "POST" && path == "/api/payment/prepare")
            {
                long amountCents = MoneyToCents(body, "amount");
                string token = Guid.NewGuid().ToString("N");
                lock (paymentSync)
                {
                    pendingPaymentToken = token;
                    pendingPaymentCents = amountCents;
                    pendingPaymentExpiresAt = DateTime.Now.AddMinutes(10);
                }
                SendJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "message", "应收金额已锁定，请刷会员卡" }, { "paymentToken", token }, { "amount", CentsToMoney(amountCents) } }); return;
            }
            if (request.Method == "POST" && path == "/api/payment/cancel")
            {
                lock (paymentSync)
                {
                    pendingPaymentToken = string.Empty;
                    pendingPaymentCents = 0;
                    pendingPaymentExpiresAt = DateTime.MinValue;
                }
                SendOk(stream, "本笔收银已取消"); return;
            }
            if (request.Method == "POST" && path == "/api/payment")
            {
                string epc = Required(body, "epc", "请先刷会员卡");
                string token = Required(body, "paymentToken", "请先设置并锁定应收金额");
                Dictionary<string, object> member;
                lock (paymentSync)
                {
                    if (string.IsNullOrEmpty(pendingPaymentToken) || !string.Equals(token, pendingPaymentToken, StringComparison.Ordinal)) throw new InvalidOperationException("本笔应收金额无效，请重新设置金额");
                    if (DateTime.Now > pendingPaymentExpiresAt)
                    {
                        pendingPaymentToken = string.Empty;
                        pendingPaymentCents = 0;
                        throw new InvalidOperationException("本笔应收金额已超时，请重新设置");
                    }
                    member = repository.PayFromBalance(epc, pendingPaymentCents);
                    pendingPaymentToken = string.Empty;
                    pendingPaymentCents = 0;
                    pendingPaymentExpiresAt = DateTime.MinValue;
                }
                Dictionary<string, object> dto = MemberDto(member); dto["paymentNo"] = member["payment_no"]; dto["paid"] = CentsToMoney(Convert.ToInt64(member["paid_cents"]));
                SendJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "message", "余额支付成功" }, { "member", dto } }); return;
            }
            SendJson(stream, 404, new Dictionary<string, object> { { "ok", false }, { "message", "接口不存在" } });
        }

        private void ServeFile(NetworkStream stream, string requestPath)
        {
            string pathOnly = requestPath.Split('?')[0];
            string relative = pathOnly == "/" ? "index.html" : Uri.UnescapeDataString(pathOnly.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
            string root = Path.GetFullPath(webRoot) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(webRoot, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) { Send(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found")); return; }
            byte[] bytes = File.ReadAllBytes(full);
            string contentType = full.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "text/html; charset=utf-8" : full.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "application/octet-stream";
            Send(stream, 200, contentType, bytes);
        }

        private static Request ReadRequest(NetworkStream stream)
        {
            MemoryStream headerBuffer = new MemoryStream();
            int matched = 0;
            while (headerBuffer.Length < 65536)
            {
                int value = stream.ReadByte();
                if (value < 0) return null;
                headerBuffer.WriteByte((byte)value);
                if ((matched == 0 && value == 13) || (matched == 1 && value == 10) || (matched == 2 && value == 13) || (matched == 3 && value == 10)) { matched++; if (matched == 4) break; }
                else matched = value == 13 ? 1 : 0;
            }
            string headerText = Encoding.ASCII.GetString(headerBuffer.ToArray());
            string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0 && lines[i].Substring(0, colon).Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(lines[i].Substring(colon + 1).Trim(), out contentLength);
            }
            byte[] body = new byte[contentLength];
            int read = 0;
            while (read < body.Length)
            {
                int n = stream.Read(body, read, body.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return new Request { Method = first[0].ToUpperInvariant(), Path = first[1], Body = Encoding.UTF8.GetString(body, 0, read) };
        }

        private Dictionary<string, object> MemberDto(Dictionary<string, object> member)
        {
            if (member == null) return null;
            return new Dictionary<string, object> { { "id", member["id"] }, { "memberNo", member["member_no"] }, { "name", member["name"] }, { "phone", member["phone"] }, { "level", member["level"] }, { "balance", CentsToMoney(Convert.ToInt64(member["balance_cents"])) }, { "points", member["points"] }, { "epc", member["epc"] } };
        }
        private Dictionary<string, object> CardDto(Dictionary<string, object> card)
        {
            return new Dictionary<string, object> { { "cardId", card["card_id"] }, { "epc", card["epc"] }, { "cardStatus", card["card_status"] }, { "createdAt", card["created_at"] }, { "memberId", card["member_id"] }, { "memberNo", card["member_no"] }, { "name", card["name"] }, { "phone", card["phone"] }, { "level", card["level"] }, { "balance", CentsToMoney(Convert.ToInt64(card["balance_cents"])) }, { "points", card["points"] }, { "memberStatus", card["member_status"] } };
        }
        private Dictionary<string, object> ReportDto(Dictionary<string, object> report)
        {
            List<Dictionary<string, object>> source = (List<Dictionary<string, object>>)report["periods"];
            List<Dictionary<string, object>> periods = new List<Dictionary<string, object>>();
            for (int i = 0; i < source.Count; i++)
            {
                periods.Add(new Dictionary<string, object> {
                    { "label", source[i]["label"] },
                    { "sales", CentsToMoney(Convert.ToInt64(source[i]["sales_cents"])) },
                    { "orders", source[i]["orders"] }
                });
            }
            return new Dictionary<string, object> {
                { "date", report["date"] },
                { "monthSales", CentsToMoney(Convert.ToInt64(report["month_sales_cents"])) },
                { "todaySales", CentsToMoney(Convert.ToInt64(report["today_sales_cents"])) },
                { "todayOrders", report["today_orders"] },
                { "averageOrder", CentsToMoney(Convert.ToInt64(report["average_order_cents"])) },
                { "memberCount", report["member_count"] },
                { "activeMembers", report["active_members"] },
                { "inactiveMembers", report["inactive_members"] },
                { "periods", periods }
            };
        }
        private static decimal CentsToMoney(long cents) { return cents / 100m; }
        private static long MoneyToCents(Dictionary<string, object> body, string key)
        {
            string text = GetText(body, key); decimal value;
            if (!decimal.TryParse(text, out value) || value <= 0) throw new InvalidOperationException("金额必须大于0元");
            return checked((long)Math.Round(value * 100m, MidpointRounding.AwayFromZero));
        }
        private static long OptionalMoneyToCents(Dictionary<string, object> body, string key)
        {
            string text = GetText(body, key); if (string.IsNullOrWhiteSpace(text)) return 0; decimal value;
            if (!decimal.TryParse(text, out value) || value < 0) throw new InvalidOperationException("金额不能小于0元");
            return checked((long)Math.Round(value * 100m, MidpointRounding.AwayFromZero));
        }
        private static string Required(Dictionary<string, object> body, string key, string message) { string value = GetText(body, key); if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(message); return value.Trim(); }
        private static string GetText(Dictionary<string, object> body, string key) { object value; return body.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : string.Empty; }
        private static bool GetBool(Dictionary<string, object> body, string key) { object value; return body.TryGetValue(key, out value) && value != null && Convert.ToBoolean(value); }
        private static string Default(string value, string fallback) { return string.IsNullOrWhiteSpace(value) ? fallback : value; }
        private void SendOk(NetworkStream stream, string message) { SendJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "message", message } }); }
        private void SendJson(NetworkStream stream, int status, object value) { Send(stream, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json.Serialize(value))); }
        private static void Send(NetworkStream stream, int status, string contentType, byte[] body)
        {
            string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: " + contentType + "\r\nContent-Length: " + body.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length); stream.Write(body, 0, body.Length); stream.Flush();
        }

        private sealed class Request { public string Method; public string Path; public string Body; }
    }
}
