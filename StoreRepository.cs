using System;
using System.Collections.Generic;

namespace RfidSmartStore.Local
{
    internal sealed class StoreRepository
    {
        private const long StandardSingleRechargeLimitCents = 10000L * 100L;
        private const long StandardDailyRechargeLimitCents = 50000L * 100L;
        private const long DiamondSingleRechargeLimitCents = 20000L * 100L;
        private const long DiamondDailyRechargeLimitCents = 100000L * 100L;
        private readonly SqliteDb db;
        private readonly object sync = new object();

        public StoreRepository(string path)
        {
            db = new SqliteDb(path);
            Initialize();
        }

        private void Initialize()
        {
            db.Execute("CREATE TABLE IF NOT EXISTS members (id INTEGER PRIMARY KEY AUTOINCREMENT, member_no TEXT NOT NULL UNIQUE, name TEXT NOT NULL, phone TEXT NOT NULL, level TEXT NOT NULL DEFAULT '普通会员', balance_cents INTEGER NOT NULL DEFAULT 0, points INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT '正常', created_at TEXT NOT NULL)");
            db.Execute("CREATE TABLE IF NOT EXISTS member_cards (id INTEGER PRIMARY KEY AUTOINCREMENT, member_id INTEGER NOT NULL REFERENCES members(id), epc TEXT NOT NULL UNIQUE, status TEXT NOT NULL DEFAULT '正常', created_at TEXT NOT NULL)");
            db.Execute("CREATE TABLE IF NOT EXISTS balance_transactions (id INTEGER PRIMARY KEY AUTOINCREMENT, member_id INTEGER NOT NULL REFERENCES members(id), type TEXT NOT NULL, amount_cents INTEGER NOT NULL, balance_after_cents INTEGER NOT NULL, note TEXT, created_at TEXT NOT NULL)");
            db.Execute("CREATE TABLE IF NOT EXISTS payments (id INTEGER PRIMARY KEY AUTOINCREMENT, payment_no TEXT NOT NULL UNIQUE, member_id INTEGER NOT NULL REFERENCES members(id), epc TEXT NOT NULL, amount_cents INTEGER NOT NULL, method TEXT NOT NULL, created_at TEXT NOT NULL)");
            db.Execute("CREATE TABLE IF NOT EXISTS card_audit (id INTEGER PRIMARY KEY AUTOINCREMENT, epc TEXT NOT NULL, action TEXT NOT NULL, details TEXT, created_at TEXT NOT NULL)");
            db.Execute("CREATE INDEX IF NOT EXISTS idx_cards_epc ON member_cards(epc)");
        }

        public Dictionary<string, object> GetMemberByEpc(string epc)
        {
            return db.QueryOne("SELECT m.id, m.member_no, m.name, m.phone, m.level, m.balance_cents, m.points, m.status, c.epc FROM member_cards c JOIN members m ON m.id=c.member_id WHERE c.epc=? AND c.status='正常' AND m.status='正常'", epc);
        }

        public List<Dictionary<string, object>> GetRecentTransactions()
        {
            return db.Query("SELECT t.id,m.name,c.epc,t.type,t.amount_cents,t.balance_after_cents,t.note,t.created_at FROM balance_transactions t JOIN members m ON m.id=t.member_id JOIN member_cards c ON c.member_id=m.id AND c.status='正常' ORDER BY t.id DESC LIMIT 50");
        }

        public List<Dictionary<string, object>> GetCards()
        {
            return db.Query("SELECT c.id AS card_id,c.epc,c.status AS card_status,c.created_at,m.id AS member_id,m.member_no,m.name,m.phone,m.level,m.balance_cents,m.points,m.status AS member_status FROM member_cards c JOIN members m ON m.id=c.member_id ORDER BY c.id DESC");
        }

        public Dictionary<string, object> GetDailyReport()
        {
            lock (sync)
            {
                DateTime day = DateTime.Today;
                string today = day.ToString("yyyy-MM-dd HH:mm:ss");
                string tomorrow = day.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss");
                string monthStart = new DateTime(day.Year, day.Month, 1).ToString("yyyy-MM-dd HH:mm:ss");
                string nextMonth = new DateTime(day.Year, day.Month, 1).AddMonths(1).ToString("yyyy-MM-dd HH:mm:ss");
                string activeSince = day.AddDays(-30).ToString("yyyy-MM-dd HH:mm:ss");

                long monthSales = QueryLong("SELECT COALESCE(SUM(amount_cents),0) AS value FROM payments WHERE created_at>=? AND created_at<?", monthStart, nextMonth);
                long todaySales = QueryLong("SELECT COALESCE(SUM(amount_cents),0) AS value FROM payments WHERE created_at>=? AND created_at<?", today, tomorrow);
                long todayOrders = QueryLong("SELECT COUNT(*) AS value FROM payments WHERE created_at>=? AND created_at<?", today, tomorrow);
                long memberCount = QueryLong("SELECT COUNT(*) AS value FROM members WHERE status='正常'");
                long activeMembers = QueryLong("SELECT COUNT(DISTINCT member_id) AS value FROM payments WHERE created_at>=?", activeSince);
                long inactiveMembers = QueryLong("SELECT COUNT(*) AS value FROM members m WHERE m.status='正常' AND NOT EXISTS (SELECT 1 FROM payments p WHERE p.member_id=m.id AND p.created_at>=?)", activeSince);

                List<Dictionary<string, object>> periods = new List<Dictionary<string, object>>();
                for (int hour = 8; hour < 22; hour += 2)
                {
                    string from = day.AddHours(hour).ToString("yyyy-MM-dd HH:mm:ss");
                    string to = day.AddHours(hour + 2).ToString("yyyy-MM-dd HH:mm:ss");
                    Dictionary<string, object> row = db.QueryOne("SELECT COALESCE(SUM(amount_cents),0) AS sales_cents,COUNT(*) AS orders FROM payments WHERE created_at>=? AND created_at<?", from, to);
                    Dictionary<string, object> period = new Dictionary<string, object>();
                    period["label"] = hour.ToString("00") + ":00–" + (hour + 2).ToString("00") + ":00";
                    period["sales_cents"] = row == null || row["sales_cents"] == null ? 0L : Convert.ToInt64(row["sales_cents"]);
                    period["orders"] = row == null || row["orders"] == null ? 0L : Convert.ToInt64(row["orders"]);
                    periods.Add(period);
                }

                Dictionary<string, object> report = new Dictionary<string, object>();
                report["date"] = day.ToString("yyyy-MM-dd");
                report["month_sales_cents"] = monthSales;
                report["today_sales_cents"] = todaySales;
                report["today_orders"] = todayOrders;
                report["average_order_cents"] = todayOrders == 0 ? 0L : todaySales / todayOrders;
                report["member_count"] = memberCount;
                report["active_members"] = activeMembers;
                report["inactive_members"] = inactiveMembers;
                report["periods"] = periods;
                return report;
            }
        }

        private long QueryLong(string sql, params object[] values)
        {
            Dictionary<string, object> row = db.QueryOne(sql, values);
            return row == null || row["value"] == null ? 0L : Convert.ToInt64(row["value"]);
        }

        public Dictionary<string, object> UpdateCard(string epc, string name, string phone, string level, string cardStatus, string memberStatus)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("会员姓名不能为空");
            if (string.IsNullOrWhiteSpace(phone) || phone.Length != 11) throw new InvalidOperationException("请输入正确的11位手机号");
            string[] levels = new string[] { "普通会员", "银卡会员", "金卡会员", "钻石会员" };
            string[] cardStates = new string[] { "正常", "冻结", "挂失", "注销" };
            if (Array.IndexOf(levels, level) < 0) throw new InvalidOperationException("会员等级无效");
            if (Array.IndexOf(cardStates, cardStatus) < 0) throw new InvalidOperationException("卡片状态无效");
            if (memberStatus != "正常" && memberStatus != "冻结") throw new InvalidOperationException("会员状态无效");
            lock (sync)
            {
                Dictionary<string, object> card = db.QueryOne("SELECT c.member_id,m.name,m.phone,m.level,m.status AS member_status,c.status AS card_status FROM member_cards c JOIN members m ON m.id=c.member_id WHERE c.epc=?", epc);
                if (card == null) throw new InvalidOperationException("没有找到该会员卡");
                long memberId = Convert.ToInt64(card["member_id"]);
                string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string details = "姓名:" + card["name"] + "→" + name + "; 手机:" + card["phone"] + "→" + phone + "; 等级:" + card["level"] + "→" + level + "; 卡状态:" + card["card_status"] + "→" + cardStatus + "; 会员状态:" + card["member_status"] + "→" + memberStatus;
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    db.Execute("UPDATE members SET name=?,phone=?,level=?,status=? WHERE id=?", name, phone, level, memberStatus, memberId);
                    db.Execute("UPDATE member_cards SET status=? WHERE epc=?", cardStatus, epc);
                    db.Execute("INSERT INTO card_audit(epc,action,details,created_at) VALUES(?,?,?,?)", epc, "编辑卡片", details, now);
                    db.Execute("COMMIT");
                }
                catch { try { db.Execute("ROLLBACK"); } catch { } throw; }
                return db.QueryOne("SELECT c.id AS card_id,c.epc,c.status AS card_status,c.created_at,m.id AS member_id,m.member_no,m.name,m.phone,m.level,m.balance_cents,m.points,m.status AS member_status FROM member_cards c JOIN members m ON m.id=c.member_id WHERE c.epc=?", epc);
            }
        }

        public Dictionary<string, object> CreateMember(string epc, string name, string phone, string level, long openingBalanceCents)
        {
            lock (sync)
            {
                if (GetMemberByEpc(epc) != null) throw new InvalidOperationException("这张卡已经绑定会员");
                ValidateRechargeLimit(level, openingBalanceCents, 0);
                string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string memberNo = "M" + DateTime.Now.ToString("yyyyMMddHHmmss") + new Random().Next(10, 99);
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    db.Execute("INSERT INTO members(member_no,name,phone,level,balance_cents,created_at) VALUES(?,?,?,?,?,?)", memberNo, name, phone, level, openingBalanceCents, now);
                    long memberId = db.LastInsertRowId;
                    db.Execute("INSERT INTO member_cards(member_id,epc,created_at) VALUES(?,?,?)", memberId, epc, now);
                    if (openingBalanceCents > 0) db.Execute("INSERT INTO balance_transactions(member_id,type,amount_cents,balance_after_cents,note,created_at) VALUES(?,?,?,?,?,?)", memberId, "开卡充值", openingBalanceCents, openingBalanceCents, "首次储值", now);
                    db.Execute("COMMIT");
                }
                catch { try { db.Execute("ROLLBACK"); } catch { } throw; }
                return GetMemberByEpc(epc);
            }
        }

        public Dictionary<string, object> Recharge(string epc, long amountCents)
        {
            if (amountCents <= 0) throw new InvalidOperationException("充值金额必须大于0元");
            lock (sync)
            {
                Dictionary<string, object> member = RequireMember(epc);
                long id = Convert.ToInt64(member["id"]);
                string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    long todayRechargeCents = GetTodayRechargeCents(id);
                    ValidateRechargeLimit(Convert.ToString(member["level"]), amountCents, todayRechargeCents);
                    db.Execute("UPDATE members SET balance_cents=balance_cents+? WHERE id=?", amountCents, id);
                    Dictionary<string, object> balance = db.QueryOne("SELECT balance_cents FROM members WHERE id=?", id);
                    long after = Convert.ToInt64(balance["balance_cents"]);
                    db.Execute("INSERT INTO balance_transactions(member_id,type,amount_cents,balance_after_cents,note,created_at) VALUES(?,?,?,?,?,?)", id, "充值", amountCents, after, "收银台充值", now);
                    db.Execute("COMMIT");
                }
                catch { try { db.Execute("ROLLBACK"); } catch { } throw; }
                return GetMemberByEpc(epc);
            }
        }

        private long GetTodayRechargeCents(long memberId)
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd HH:mm:ss");
            string tomorrow = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss");
            Dictionary<string, object> row = db.QueryOne("SELECT COALESCE(SUM(amount_cents),0) AS total_cents FROM balance_transactions WHERE member_id=? AND type IN ('充值','开卡充值') AND created_at>=? AND created_at<?", memberId, today, tomorrow);
            return row == null || row["total_cents"] == null ? 0 : Convert.ToInt64(row["total_cents"]);
        }

        private static void ValidateRechargeLimit(string level, long amountCents, long todayRechargeCents)
        {
            if (amountCents <= 0) return;
            bool diamond = level == "钻石会员";
            long singleLimit = diamond ? DiamondSingleRechargeLimitCents : StandardSingleRechargeLimitCents;
            long dailyLimit = diamond ? DiamondDailyRechargeLimitCents : StandardDailyRechargeLimitCents;
            if (amountCents > singleLimit)
                throw new InvalidOperationException(diamond ? "钻石会员单次充值不能超过20000元" : "非钻石会员单次充值不能超过10000元");
            if (todayRechargeCents + amountCents > dailyLimit)
            {
                long remaining = Math.Max(0, dailyLimit - todayRechargeCents);
                throw new InvalidOperationException((diamond ? "钻石会员每日充值不能超过100000元" : "非钻石会员每日充值不能超过50000元") + "，今日还可充值" + (remaining / 100m).ToString("0.00") + "元");
            }
        }

        public Dictionary<string, object> PayFromBalance(string epc, long amountCents)
        {
            if (amountCents <= 0) throw new InvalidOperationException("付款金额必须大于0元");
            lock (sync)
            {
                Dictionary<string, object> member = RequireMember(epc);
                long id = Convert.ToInt64(member["id"]);
                long balance = Convert.ToInt64(member["balance_cents"]);
                if (balance < amountCents) throw new InvalidOperationException("会员余额不足，请先充值");
                string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string paymentNo = "P" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    db.Execute("UPDATE members SET balance_cents=balance_cents-? WHERE id=? AND balance_cents>=?", amountCents, id, amountCents);
                    long after = balance - amountCents;
                    db.Execute("INSERT INTO payments(payment_no,member_id,epc,amount_cents,method,created_at) VALUES(?,?,?,?,?,?)", paymentNo, id, epc, amountCents, "储值余额", now);
                    db.Execute("INSERT INTO balance_transactions(member_id,type,amount_cents,balance_after_cents,note,created_at) VALUES(?,?,?,?,?,?)", id, "消费", -amountCents, after, paymentNo, now);
                    db.Execute("COMMIT");
                }
                catch { try { db.Execute("ROLLBACK"); } catch { } throw; }
                Dictionary<string, object> result = GetMemberByEpc(epc);
                result["payment_no"] = paymentNo;
                result["paid_cents"] = amountCents;
                return result;
            }
        }

        private Dictionary<string, object> RequireMember(string epc)
        {
            Dictionary<string, object> member = GetMemberByEpc(epc);
            if (member == null) throw new InvalidOperationException("该卡尚未开卡");
            return member;
        }
    }
}
