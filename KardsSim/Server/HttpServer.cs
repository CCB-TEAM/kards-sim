using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KardsSim.Ai;
using KardsSim.Bridge;
using KardsSim.Core;
using KardsSim.Engine;
using Enc = KardsSim.Ai.Encoder;

namespace KardsSim.Server;

/// <summary>
/// 面向外部 AI 后端的 HTTP 接口。
///
/// 一个 session = 一局对局：POST /games 拿 id，之后反复 GET /state|/legal 决策、
/// POST /step 推进。底层引擎与 CLI 自对弈完全共用，HTTP 只是薄壳。
///
/// 用 TcpListener 手写 HTTP/1.1，而不是 HttpListener —— 后者在 Windows 上依赖
/// HTTP.sys 与 URL ACL，需要额外权限，在受限环境直接起不来。
/// </summary>
public sealed class HttpServer
{
    private readonly string _prefix;
    private readonly Dictionary<string, Session> _sessions = new();
    private int _nextId = 1;

    private sealed class Session
    {
        public string Id;
        public GameEngine Engine;
        public int Seed;
        public string LeftDeck, RightDeck;
        public bool LogEnabled;
        public int Illegal;
    }

    public HttpServer(string prefix) { _prefix = prefix; }

    /// <summary>
    /// 建一局并挂上直译产物宿主。
    ///
    /// <para>
    /// <b>必须挂</b>：<see cref="GameEngine.Host"/> 为 null 时引擎会退回
    /// <c>EffectPlanner</c>（读卡面文本猜效果）。HTTP 接口是给 AI 后端用的，
    /// 不挂宿主就意味着「API 跑的那套规则」和 <c>--mode selfplay</c> 不是同一套，
    /// 而且是最差的那一套。
    /// </para>
    /// </summary>
    private static GameEngine NewEngine(int seed, string leftDeck, string rightDeck, bool log)
    {
        var g = new GameEngine(seed, leftDeck, rightDeck, log);
        g.Host = new EngineHost(g);
        return g;
    }

    public int Port
    {
        get
        {
            var u = new Uri(_prefix);
            return u.Port > 0 ? u.Port : 8642;
        }
    }

    public void Run()
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        Console.WriteLine($"KardsSim HTTP 服务已启动: {_prefix}");
        Console.WriteLine($"卡牌库 {CardDb.All.Count} 张，stateSize={Enc.StateSize}，actionSize={Enc.ActionSize}");
        Console.WriteLine("按 Ctrl+C 停止");
        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { break; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { using (client) Serve(client); }
                catch (Exception ex) { Console.Error.WriteLine($"  连接异常: {ex.GetType().Name}: {ex.Message}"); }
            });
        }
    }

    /// <summary>极简 HTTP/1.1：请求行 + 头 + Content-Length 正文，处理后关连接。</summary>
    private void Serve(TcpClient client)
    {
        client.ReceiveTimeout = 15000;
        client.SendTimeout = 15000;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);

        var requestLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(requestLine)) return;
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return;
        var method = parts[0].ToUpperInvariant();
        var target = parts[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string line;
        while (!string.IsNullOrEmpty(line = reader.ReadLine()))
        {
            var c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }

        string body = null;
        if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl) && cl > 0)
        {
            var buf = new char[cl];
            var read = 0;
            while (read < cl)
            {
                var n = reader.Read(buf, read, cl - read);
                if (n <= 0) break;
                read += n;
            }
            body = new string(buf, 0, read);
        }

        var path = target;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var qIdx = target.IndexOf('?');
        if (qIdx >= 0)
        {
            path = target[..qIdx];
            foreach (var kv in target[(qIdx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = kv.IndexOf('=');
                if (eq < 0) query[Uri.UnescapeDataString(kv)] = "";
                else query[Uri.UnescapeDataString(kv[..eq])] = Uri.UnescapeDataString(kv[(eq + 1)..].Replace('+', ' '));
            }
        }

        var (code, json) = Route(method, path, query, body);
        var bytes = Encoding.UTF8.GetBytes(json);
        var head = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {code} {Reason(code)}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Headers: content-type\r\n" +
            "Access-Control-Allow-Methods: GET,POST,DELETE,OPTIONS\r\n" +
            "Connection: close\r\n\r\n");
        stream.Write(head, 0, head.Length);
        if (method != "HEAD") stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static string Reason(int code) => code switch
    {
        200 => "OK", 400 => "Bad Request", 404 => "Not Found",
        405 => "Method Not Allowed", 500 => "Internal Server Error", _ => "OK",
    };

    /// <summary>路由：返回 (状态码, JSON)。所有端点的唯一入口。</summary>
    public (int Code, string Json) Route(string method, string path, Dictionary<string, string> query, string body)
    {
        var p = (path ?? "/").TrimEnd('/');
        if (p.Length == 0) p = "/";

        try
        {
            if (method == "OPTIONS") return (200, "{}");
            if (method == "GET" && p == "/") return (200, J(Info()));
            if (method == "GET" && p == "/cards") return (200, J(Cards(query)));
            if (method == "GET" && p.StartsWith("/cards/")) return Card(Uri.UnescapeDataString(p[7..]));
            if (method == "GET" && p == "/stats") return (200, J(Stats()));

            if (method == "POST" && p == "/games") return (200, J(NewGame(body)));
            if (method == "POST" && p == "/selfplay") return (200, J(SelfPlay(body)));

            if (p.StartsWith("/games/"))
            {
                var rest = p[7..];
                var slash = rest.IndexOf('/');
                var id = slash < 0 ? rest : rest[..slash];
                var sub = slash < 0 ? "" : rest[(slash + 1)..];
                if (!_sessions.TryGetValue(id, out var s)) return (404, J(new { error = "no such game", id }));

                if (method == "GET" && (sub == "" || sub == "state")) return (200, J(Observation(s)));
                if (method == "GET" && sub == "legal") return (200, J(Legal(s)));
                if (method == "GET" && sub == "log") return (200, J(new { id, log = s.Engine.S.Log.Text }));
                if (method == "POST" && sub == "step") return (200, J(Step(body, s)));
                if (method == "POST" && sub == "reset") return (200, J(Reset(body, s)));
                if (method == "DELETE" && sub == "") { _sessions.Remove(id); return (200, J(new { deleted = id })); }
                return (404, J(new { error = "unknown sub-route", path = p }));
            }

            return (404, J(new { error = "unknown route", path = p, method }));
        }
        catch (Exception ex)
        {
            return (500, J(new { error = ex.Message, type = ex.GetType().Name }));
        }
    }

    private object Info() => new
    {
        name = "KardsSim",
        description = "KARDS 对局模拟器 —— 用于训练/评测 AI 后端",
        cards = CardDb.All.Count,
        stateSize = Enc.StateSize,
        actionSize = Enc.ActionSize,
        sessions = _sessions.Count,
        endpoints = new[]
        {
            "GET  /",
            "GET  /cards?q=&limit=",
            "GET  /cards/{name}",
            "GET  /stats",
            "POST /games {seed?,leftDeck?,rightDeck?,log?}",
            "GET  /games/{id}",
            "GET  /games/{id}/state",
            "GET  /games/{id}/legal",
            "POST /games/{id}/step {index:N} 或 {action:{type,sourceId,targetId,handIndex}}",
            "POST /games/{id}/reset {seed?}",
            "GET  /games/{id}/log",
            "DELETE /games/{id}",
            "POST /selfplay {games?,maxSteps?}",
        },
        actionTypes = Enum.GetNames<ActionType>(),
    };

    private object Cards(Dictionary<string, string> q)
    {
        q.TryGetValue("q", out var text);
        var limit = q.TryGetValue("limit", out var ls) && int.TryParse(ls, out var l) ? Math.Clamp(l, 1, 2000) : 100;
        var items = CardDb.All
            .Where(c => string.IsNullOrEmpty(text)
                        || (c.Id?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (c.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(limit).Select(Brief).ToList();
        return new { total = CardDb.All.Count, returned = items.Count, items };
    }

    private (int, string) Card(string name)
    {
        var d = CardDb.Resolve(name);
        if (d == null) return (404, J(new { error = "no such card", name }));
        return (200, J(new
        {
            d.Id,
            d.Name,
            d.Text,
            type = d.Type.ToString(),
            faction = d.Faction.ToString(),
            rarity = d.Rarity.ToString(),
            d.CardSet,
            d.Kredits,
            d.Attack,
            d.Defense,
            d.Range,
            d.OperationCost,
            d.HeavyArmor,
            keywords = d.Keywords.ToString(),
            triggers = d.Triggers.Select(t => t.ToString()).ToArray(),
            tags = d.Tags.ToArray(),
            spawnCardNames = d.SpawnCardNames.ToArray(),
            chooseOneCards = d.ChooseOneCards.ToArray(),
            flavorText = d.FlavorText,
        }));
    }

    private static object Brief(CardDef d) => new
    {
        d.Id,
        d.Name,
        type = d.Type.ToString(),
        faction = d.Faction.ToString(),
        d.Kredits,
        d.Attack,
        d.Defense,
        d.Range,
        keywords = d.Keywords.ToString(),
    };

    private object Stats()
    {
        var byType = CardDb.All.GroupBy(c => c.Type.ToString()).ToDictionary(g => g.Key, g => g.Count());
        var byFaction = CardDb.All.GroupBy(c => c.Faction.ToString()).ToDictionary(g => g.Key, g => g.Count());
        return new
        {
            cards = CardDb.All.Count,
            triggered = CardDb.All.Count(c => c.Triggers.Count > 0),
            withSpawn = CardDb.All.Count(c => c.SpawnCardNames.Count > 0),
            byType,
            byFaction,
        };
    }

    private object NewGame(string body)
    {
        var b = Parse(body);
        var seed = JInt(b, "seed") ?? Environment.TickCount;
        var log = JBool(b, "log") ?? false;
        var s = new Session
        {
            Id = _nextId++.ToString("x") + Guid.NewGuid().ToString("N")[..8],
            Seed = seed,
            LeftDeck = JStr(b, "leftDeck"),
            RightDeck = JStr(b, "rightDeck"),
            LogEnabled = log,
        };
        s.Engine = NewEngine(seed, s.LeftDeck, s.RightDeck, log);
        _sessions[s.Id] = s;
        return Observation(s);
    }

    private object Reset(string body, Session s)
    {
        var b = Parse(body);
        s.Seed = JInt(b, "seed") ?? s.Seed;
        s.Engine = NewEngine(s.Seed, s.LeftDeck, s.RightDeck, s.LogEnabled);
        s.Illegal = 0;
        return Observation(s);
    }

    private object Step(string body, Session s)
    {
        var b = Parse(body);
        GameAction action;

        if (b != null && b.Value.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number)
        {
            var idx = idxEl.GetInt32();
            var (mask, acts) = Enc.Mask(s.Engine);
            if (idx < 0 || idx >= mask.Length || mask[idx] <= 0)
            {
                s.Illegal++;
                return new { ok = false, reason = "illegal index", index = idx, obs = Observation(s), done = s.Engine.IsDone };
            }
            action = acts.First(a => Enc.Index(s.Engine, a) == idx);
        }
        else if (b != null && b.Value.TryGetProperty("action", out var aEl))
        {
            var typeName = JStr(aEl, "type");
            if (!Enum.TryParse<ActionType>(typeName, true, out var at))
            {
                s.Illegal++;
                return new { ok = false, reason = "bad action type", type = typeName, obs = Observation(s), done = s.Engine.IsDone };
            }
            var src = JInt(aEl, "sourceId") ?? -1;
            var tgt = JInt(aEl, "targetId") ?? -1;
            var hi = JInt(aEl, "handIndex") ?? -1;
            if (at == ActionType.PlayCard && hi < 0)
                hi = s.Engine.S.CurrentPlayer.Hand.FindIndex(c => c.InstanceId == src);
            if (at == ActionType.PlayCard && hi < 0)
            {
                s.Illegal++;
                return new { ok = false, reason = "PlayCard needs handIndex or sourceId", obs = Observation(s), done = s.Engine.IsDone };
            }
            action = new GameAction { Type = at, SourceId = src, TargetId = tgt, HandIndex = hi };
        }
        else
        {
            return new { ok = false, reason = "body must contain 'index' or 'action'", obs = Observation(s), done = s.Engine.IsDone };
        }

        var before = s.Engine.S.Current;
        var ok = s.Engine.Apply(action);
        if (!ok) s.Illegal++;
        return new
        {
            ok,
            applied = action.ToString(),
            reward = Reward(s.Engine, before),
            obs = Observation(s),
            done = s.Engine.IsDone,
        };
    }

    private static double Reward(GameEngine g, Side actor)
    {
        if (!g.IsDone || g.S.Winner == Side.None) return 0;
        return g.S.Winner == actor ? 1 : -1;
    }

    private object SelfPlay(string body)
    {
        var b = Parse(body);
        var games = JInt(b, "games") ?? 10;
        var maxSteps = JInt(b, "maxSteps") ?? 2000;
        var illegal = 0; var stuck = 0; var exceptions = 0;
        var steps = 0L; var triggers = 0L;
        var wins = new Dictionary<string, int> { ["LeftWins"] = 0, ["RightWins"] = 0, ["Draw"] = 0 };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < games; i++)
        {
            try
            {
                var g = NewEngine(1000 + i, null, null, false);
                var n = 0;
                while (!g.IsDone && n < maxSteps)
                {
                    var legal = g.LegalActions();
                    if (legal.Count == 0) { stuck++; break; }
                    if (!g.Apply(legal[g.S.Rng.Next(legal.Count)])) illegal++;
                    n++;
                }
                steps += n;
                triggers += g.TriggerFireCount;
                wins[g.S.Winner == Side.None ? "Draw" : (g.S.Winner == Side.Left ? "LeftWins" : "RightWins")]++;
            }
            catch { exceptions++; }
        }
        sw.Stop();
        return new
        {
            games,
            steps,
            avgSteps = games > 0 ? Math.Round((double)steps / games, 1) : 0,
            msTotal = sw.ElapsedMilliseconds,
            msPerGame = games > 0 ? Math.Round((double)sw.ElapsedMilliseconds / games, 2) : 0,
            illegal,
            stuck,
            exceptions,
            triggers,
            wins,
        };
    }

    private object Legal(Session s)
    {
        var legal = s.Engine.LegalActions();
        var (mask, acts) = Enc.Mask(s.Engine);
        return new
        {
            id = s.Id,
            actions = legal.Select(a => new { text = a.ToString(), a.Type, a.SourceId, a.TargetId, a.HandIndex }).ToArray(),
            indices = acts.Select(a => Enc.Index(s.Engine, a)).ToArray(),
            mask,
        };
    }

    private object Observation(Session s)
    {
        var g = s.Engine;
        var me = g.S.Current;
        return new
        {
            sessionId = s.Id,
            turn = g.S.Turn,
            currentSide = me.ToString(),
            done = g.IsDone,
            winner = g.S.Winner.ToString(),
            frontlineOwner = g.S.FrontlineOwner.ToString(),
            state = Enc.Encode(g),
            stateSize = Enc.StateSize,
            actionSize = Enc.ActionSize,
            legalIndices = Enc.Mask(g).actions.Select(a => Enc.Index(g, a)).ToArray(),
            left = View(g, Side.Left),
            right = View(g, Side.Right),
            legalActions = g.LegalActions().Select(a => new { text = a.ToString(), a.Type, a.SourceId, a.TargetId, a.HandIndex }).ToArray(),
            stats = new { illegal = s.Illegal, triggers = g.TriggerFireCount, unhandled = g.Unhandled.Count },
            // 抉择预览：效果跑到一半要求选牌时，这里给出候选项，
            // 同时 legalActions / legalIndices 里只会剩 ChooseCard 这一种动作。
            pendingChoice = g.Pending is null ? null : new
            {
                kind = g.Pending.Kind,
                sourceCard = g.Pending.Source?.Id,
                sourceId = g.Pending.Source?.InstanceId ?? -1,
                isEffect = g.Pending.IsEffect,
                options = g.Pending.Options
                    .Select((o, i) => new { index = i, id = o.CardId, label = o.Label })
                    .ToArray(),
            },
        };
    }

    private static object View(GameEngine g, Side side)
    {
        var p = g.S.Player(side);
        return new
        {
            side = side.ToString(),
            hq = p.Hq,
            kredits = p.Kredits,
            kreditSlots = p.KreditSlots,
            handCount = p.Hand.Count,
            deckCount = p.Deck.Count,
            discardCount = p.Discard.Count,
            fatigue = p.Fatigue,
            frontline = g.S.Frontline.Where(c => c.Owner == side).Select(CardView).ToArray(),
            board = p.Board.Select(CardView).ToArray(),
            hand = p.Hand.Select(CardView).ToArray(),
        };
    }

    private static object CardView(Card c) => new
    {
        id = c.Id,
        instanceId = c.InstanceId,
        name = c.Name,
        type = c.Type.ToString(),
        kredits = c.KreditCost,
        attack = c.TotalAttack,
        defense = c.TotalDefense,
        range = c.Range,
        keywords = c.Keywords.ToString(),
        attacked = c.AttackedThisTurn,
        pinned = c.Pinned,
        suppressed = c.Suppressed,
    };

    // ---------- 工具 ----------

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 枚举必须序列化成名字。默认会写成数字，客户端按 "PlayCard" 过滤就会一个都匹配不到。
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string J(object o) => JsonSerializer.Serialize(o, JsonOpt);

    private static JsonElement? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static int? JInt(JsonElement? e, string k)
        => e != null && e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty(k, out var v)
           && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static string JStr(JsonElement? e, string k)
        => e != null && e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty(k, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? JBool(JsonElement? e, string k)
        => e != null && e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty(k, out var v)
           && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}