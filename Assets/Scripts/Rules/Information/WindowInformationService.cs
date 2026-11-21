using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.Rules.Information
{
    public sealed partial class SharedInformationService
    {
        private sealed class WindowRecord
        {
            public string Id, Player, Company, Source;
            public int Start, Paid, Length = 5;
            public bool Automatic;
            public double HiddenUntil;
        }
        private sealed class WindowTurn
        {
            public string RoomId;
            public bool RandomPrepared;
            public readonly List<WindowRecord> Records = new List<WindowRecord>();
            public readonly HashSet<string> UsedCategories = new HashSet<string>();
            public readonly Dictionary<string, Tuple<string, RuleResult>> Requests = new Dictionary<string, Tuple<string, RuleResult>>();
        }
        private readonly Dictionary<string, WindowTurn> windowTurns = new Dictionary<string, WindowTurn>();
        private readonly Dictionary<string, LobbySettings> roomSettings = new Dictionary<string, LobbySettings>();
        private readonly Random informationRandom = new Random();
        private bool RandomInformation => source is IStockMarketSystem stock && stock.StockEnabled;
        public void ConfigureRoom(string roomId, LobbySettings settings)
        {
            if (settings == null || !settings.IsValid) throw new ArgumentException("Invalid lobby settings.");
            lock (gate) roomSettings[roomId] = settings.Copy();
        }
        private bool PublicInformationComplete(RequestContext c) =>
            RandomInformation ? Windows(c).RandomPrepared : roomSettings.TryGetValue(c.RoomId, out var options) && options.publicRevealDays == 0 ||
            Windows(c).Records.Where(r => r.Source == "PUBLIC").Select(r => r.Player).Distinct().Count() == 4;
        private int[] DrawInformationDays(IEnumerable<int> days, int count)
        {
            var available = days.ToArray();
            for (int i = available.Length - 1; i > 0; i--)
            { int j = informationRandom.Next(i + 1); int value = available[i]; available[i] = available[j]; available[j] = value; }
            return available.Take(count).OrderBy(d => d).ToArray();
        }
        public void PrepareRandomInformation(RequestContext c)
        {
            if (!RandomInformation) return;
            lock (gate)
            {
                var state = Windows(c);
                if (state.RandomPrepared) return;
                var options = roomSettings.TryGetValue(c.RoomId, out var configured) ? configured : new LobbySettings();
                foreach (string company in source.GetCompanyIds())
                {
                    if (company == "BANK") { AddWindow(c, company, 0, "PUBLIC", 0, true, 31); continue; }
                    int first = Math.Max(1, options.informationStartDay);
                    foreach (int day in DrawInformationDays(Enumerable.Range(first, Math.Max(0, options.informationEndDay - first + 1)), options.publicRevealDays))
                        AddWindow(c, company, day, "PUBLIC", 0, true, 1);
                }
                state.RandomPrepared = true;
            }
        }
        public void GrantAnalystCoins(RequestContext c, string player)
        {
            lock (gate)
                if (source is IStockMarketSystem stock && stock.StockEnabled && stock.GetPortfolio(player).augments?.Contains("ANALYST") == true)
                    rooms[c.RoomId].Service.RewardCoins(player, c.Turn, "augment-analyst", 2);
        }
        public RuleResult BuyRandomDays(RequestContext c, string company, int count = 1)
        {
            lock (gate) return WindowRequest(c, "random-buy|" + company + "|" + count, () =>
            {
                if (!RandomInformation) return new RuleResult("WINDOWS_UNAVAILABLE");
                string error = ValidateWindow(c, company, 1, 1); if (error != "") return new RuleResult(error);
                if (!PublicInformationComplete(c)) return new RuleResult("PUBLIC_SELECTION_PENDING");
                if (count < 1 || count > 25) return new RuleResult("INVALID_WINDOW");
                if (company == "BANK") return new RuleResult("ALREADY_VISIBLE");
                var options = roomSettings.TryGetValue(c.RoomId, out var configured) ? configured : new LobbySettings();
                int first = Math.Max(1, options.informationStartDay);
                EnsureAnalysisRecords(c);
                var mask = Mask(Windows(c), c.PlayerId, company);
                var available = Enumerable.Range(first, Math.Max(0, options.informationEndDay - first + 1)).Where(day => !mask[day]).ToArray();
                if (available.Length < count) return new RuleResult(available.Length == 0 ? "ALREADY_VISIBLE" : "NOT_ENOUGH_INFORMATION_DAYS");
                int bought = PurchasedDays(Windows(c), c.PlayerId, company);
                int cost = InformationWindowRules.RandomDaysCost(bought, count);
                if (rooms[c.RoomId].Service.GetCoins(c.PlayerId) < cost) return new RuleResult("NOT_ENOUGH_COINS");
                var days = DrawInformationDays(available, count);
                foreach (int day in days) ((IWindowMarketDataSource)source).GetWindow(c.Turn, company, day, 1);
                if (!rooms[c.RoomId].Service.SpendWindowCoins(c.PlayerId, c.Turn, cost)) return new RuleResult("NOT_ENOUGH_COINS");
                WindowRecord firstRecord = null;
                foreach (int day in days)
                {
                    var record = AddWindow(c, company, day, "PURCHASED", InformationWindowRules.RandomDayCost(bought++), length: 1);
                    if (firstRecord == null) firstRecord = record;
                }
                var result = WindowResult(c, firstRecord); result.Payload.paidCoins = cost;
                return result;
            });
        }
        public bool UsesWindowInformation => source is IWindowMarketDataSource windows && windows.UsesWindowInformation;
        private WindowTurn Windows(RequestContext c)
        {
            string key = c.RoomId + ":" + c.Turn;
            if (!windowTurns.TryGetValue(key, out var state)) windowTurns.Add(key, state = new WindowTurn { RoomId = c.RoomId });
            return state;
        }
        private string ValidateWindow(RequestContext c, string company, int start, int length = 5)
        {
            if (!UsesWindowInformation) return "WINDOWS_UNAVAILABLE";
            if (!rooms.TryGetValue(c.RoomId, out var room) || !room.Players.Contains(c.PlayerId)) return "INVALID_REQUEST";
            if (c.Turn != room.CurrentTurn) return "STALE_TURN";
            if (c.Phase != Protocol.Info) return "WRONG_PHASE";
            if (!source.GetCompanyIds().Contains(company)) return "INVALID_COMPANY";
            if (start < 1 || start > 30 || length < 1 || length > 31 - start) return "INVALID_WINDOW";
            return "";
        }
        private RuleResult WindowRequest(RequestContext c, string fingerprint, Func<RuleResult> action)
        {
            if (string.IsNullOrEmpty(c.RequestId)) return new RuleResult("INVALID_REQUEST");
            var state = Windows(c); string key = c.PlayerId + ":" + c.RequestId;
            if (state.Requests.TryGetValue(key, out var cached))
                return cached.Item1 == fingerprint ? CloneWindowResult(cached.Item2) : new RuleResult("REQUEST_ID_CONFLICT");
            var result = action(); state.Requests.Add(key, Tuple.Create(fingerprint, CloneWindowResult(result)));
            return result;
        }
        private static RuleResult CloneWindowResult(RuleResult r)
        {
            var p = r.Payload;
            return new RuleResult(r.ErrorCode, p == null ? null : new ResultPayload { companyId = p.companyId,
                startDay = p.startDay, dailyChangesBps = (int[])p.dailyChangesBps?.Clone(), source = p.source,
                dailyOpenBps = (int[])p.dailyOpenBps?.Clone(),
                recordId = p.recordId, remainingCoins = p.remainingCoins, paidCoins = p.paidCoins, automatic = p.automatic });
        }
        private static bool Accessible(WindowRecord r, string player) => r.Source == "PUBLIC" || r.Player == player;
        private bool[] Mask(WindowTurn state, string player, string company, bool publicOnly = false, WindowRecord except = null)
        {
            var mask = new bool[31]; mask[0] = true;
            foreach (var r in state.Records.Where(r => r != except && r.Company == company &&
                (publicOnly ? r.Source == "PUBLIC" : Accessible(r, player)) && r.HiddenUntil == 0))
                for (int d = r.Start; d < r.Start + r.Length; d++) mask[d] = true;
            return mask;
        }
        private WindowRecord AddWindow(RequestContext c, string company, int start, string sourceName, int paid, bool automatic = false, int length = 5)
        {
            var state = Windows(c);
            var record = new WindowRecord { Id = c.RoomId + ":" + c.Turn + ":window:" + (state.Records.Count + 1),
                Player = c.PlayerId, Company = company, Start = start, Source = sourceName, Paid = paid, Automatic = automatic, Length = length };
            state.Records.Add(record); return record;
        }
        private RuleResult WindowResult(RequestContext c, WindowRecord record)
        {
            return new RuleResult(payload: new ResultPayload { companyId = record.Company, startDay = record.Start,
                recordId = record.Id, source = record.Source, paidCoins = record.Paid, automatic = record.Automatic,
                remainingCoins = rooms[c.RoomId].Service.GetCoins(c.PlayerId),
                dailyChangesBps = ((IWindowMarketDataSource)source).GetWindow(c.Turn, record.Company, record.Start, record.Length),
                dailyOpenBps = ((IWindowMarketDataSource)source).GetOpenWindow(c.Turn, record.Company, record.Start, record.Length) });
        }
        public RuleResult RevealWindow(RequestContext c, string company, int start, bool automatic)
            => RevealPublicDays(c, company, start >= 1 && start <= 26 ? Enumerable.Range(start, 5).ToArray() : Array.Empty<int>(), automatic, start);
        public RuleResult RevealPublicDays(RequestContext c, string company, int[] days, bool automatic)
            => RevealPublicDays(c, company, days, automatic, days?.FirstOrDefault() ?? 0);
        private RuleResult RevealPublicDays(RequestContext c, string company, int[] days, bool automatic, int requestedStart)
        {
            // One public choice can have multiple records; each record contains consecutive days only.
            var selected = days?.OrderBy(d => d).ToArray() ?? Array.Empty<int>();
            lock (gate) return WindowRequest(c, "public|" + company + "|" + requestedStart + "|" + string.Join(",", selected) + "|" + automatic, () =>
            {
                if (requestedStart < 1 || selected.Length == 0 || selected.Length > 30 || selected.Any(d => d < 1 || d > 30)
                    || selected.Distinct().Count() != selected.Length) return new RuleResult("INVALID_WINDOW");
                string error = ValidateWindow(c, company, selected[0], 1); if (error != "") return new RuleResult(error);
                var state = Windows(c);
                var revealed = state.Records.Where(r => r.Source == "PUBLIC").ToArray();
                if (PublicInformationComplete(c)) return new RuleResult("PUBLIC_SELECTION_COMPLETE");
                if (revealed.Any(r => r.Player == c.PlayerId)) return new RuleResult("ALREADY_REVEALED");
                var mask = Mask(state, c.PlayerId, company, true);
                if (selected.Any(d => mask[d])) return new RuleResult("PUBLIC_WINDOW_OVERLAP");
                // Read all selected data before recording anything, so a failure cannot partially reveal a choice.
                var sourceWindows = (IWindowMarketDataSource)source;
                foreach (int day in selected) { sourceWindows.GetWindow(c.Turn, company, day, 1); sourceWindows.GetOpenWindow(c.Turn, company, day, 1); }
                WindowRecord first = null;
                for (int i = 0; i < selected.Length; i++) {
                    int start = selected[i], length = 1;
                    while (i + 1 < selected.Length && selected[i + 1] == start + length) { i++; length++; }
                    var record = AddWindow(c, company, start, "PUBLIC", 0, automatic, length);
                    if (first == null) first = record;
                }
                return WindowResult(c, first);
            });
        }
        public RuleResult BuyWindow(RequestContext c, string company, int start, int endDay = 0)
        {
            if (RandomInformation) return BuyRandomDays(c, company);
            int length = (endDay == 0 ? start + 4 : endDay) - start + 1;
            lock (gate) return WindowRequest(c, "buy|" + company + "|" + start + "|" + length, () =>
            {
                string error = ValidateWindow(c, company, start, length); if (error != "") return new RuleResult(error);
                var state = Windows(c);
                if (!PublicInformationComplete(c)) return new RuleResult("PUBLIC_SELECTION_PENDING");
                var mask = Mask(state, c.PlayerId, company);
                int newDays = Enumerable.Range(start, length).Count(d => !mask[d]);
                if (newDays == 0) return new RuleResult("ALREADY_VISIBLE");
                // Read before charging, so unavailable data never consumes a coin/credit.
                ((IWindowMarketDataSource)source).GetWindow(c.Turn, company, start, length);
                var feature = FeaturePlayer(c.RoomId, c.PlayerId);
                var stock = source as IStockMarketSystem;
                int stockDiscount = stock?.StockEnabled == true ? stock.InformationDiscount(c.PlayerId) : 0;
                int featureFree = Math.Min(newDays, feature?.FreeCredits ?? 0);
                int stockFree = Math.Min(newDays - featureFree, stockDiscount);
                int free = featureFree + stockFree, cost = newDays - free;
                if (!rooms[c.RoomId].Service.SpendWindowCoins(c.PlayerId, c.Turn, cost)) return new RuleResult("NOT_ENOUGH_COINS");
                if (feature != null) feature.FreeCredits -= featureFree;
                if (stockFree > 0) stock.ConsumeInformationDiscount(c.PlayerId, stockFree);
                return WindowResult(c, AddWindow(c, company, start, cost == 0 ? "ABILITY" : "PURCHASED", cost, length: length));
            });
        }
        private static int PurchasedDays(WindowTurn state, string player, string company)
            => state.Records.Where(r => r.Player == player && r.Company == company && r.Source == "PURCHASED").Sum(r => r.Length);
        public CompanyInterest[] GetInformationInterest(RequestContext c)
        {
            lock (gate)
            {
                var records = Windows(c).Records;
                return source.GetCompanyIds().Where(id => id != "BANK").Select(id => new CompanyInterest { companyId = id,
                    purchasedDays = records.Where(r => r.Company == id && r.Source == "PURCHASED").Sum(r => r.Length) }).ToArray();
            }
        }
        public PublicInformationChoice[] GetPublicInformation(RequestContext c)
        {
            lock (gate) return Windows(c).Records.Where(r => r.Source == "PUBLIC").GroupBy(r => RandomInformation ? r.Company : r.Player).Select(group => {
                var first = group.First();
                return new PublicInformationChoice { playerId = first.Player, companyId = first.Company, startDay = first.Start,
                    automatic = first.Automatic, days = group.SelectMany(r => Enumerable.Range(r.Start, r.Length)).OrderBy(d => d).ToArray() };
            }).ToArray();
        }
        private void EnsureAnalysisRecords(RequestContext c)
        {
            var state = Windows(c);
            if (source is IStockMarketSystem stock && stock.StockEnabled && c.Turn == rooms[c.RoomId].CurrentTurn)
                foreach (var analysis in stock.AnalysisWindows(c.PlayerId))
                    if (!state.Records.Any(r => r.Player == c.PlayerId && r.Company == analysis.companyId && r.Source == "AUGMENT"))
                        AddWindow(c, analysis.companyId, analysis.startDay, "AUGMENT", 0, length: analysis.dailyChangesBps.Length);
        }
        private void PopulateWindows(RequestContext c, PlayerView view)
        {
            var state = Windows(c);
            EnsureAnalysisRecords(c);
            foreach (var company in view.companies)
            {
                company.visibleBundles = Array.Empty<VisibleBundle>();
                // Keep every independent proof. Hiding one paid record cannot remove another proof.
                company.visibleWindows = state.Records.Where(r => r.Company == company.companyId && Accessible(r, c.PlayerId) && r.HiddenUntil == 0)
                    .Select(r => new VisibleInformationWindow { recordId = r.Id, startDay = r.Start, source = r.Source, paidCoins = r.Paid,
                        dailyChangesBps = ((IWindowMarketDataSource)source).GetWindow(c.Turn, r.Company, r.Start, r.Length),
                        dailyOpenBps = ((IWindowMarketDataSource)source).GetOpenWindow(c.Turn, r.Company, r.Start, r.Length) }).ToArray();
            }
        }
        public bool ExpireDistortions(RequestContext c, double now)
        {
            lock (gate)
            {
                bool changed = false;
                foreach (var r in windowTurns.Values.Where(t => t.RoomId == c.RoomId).SelectMany(t => t.Records).Where(r => r.HiddenUntil > 0 && now >= r.HiddenUntil))
                { r.HiddenUntil = 0; changed = true; }
                return changed;
            }
        }
        public RuleResult UseInformationItem(RequestContext c, string itemId, string company, int start, string target, double now)
        {
            lock (gate) return WindowRequest(c, "item|" + itemId + "|" + company + "|" + start + "|" + target, () =>
            {
                if (!UsesWindowInformation || !rooms.TryGetValue(c.RoomId, out var room) || c.Turn != room.CurrentTurn)
                    return new RuleResult("STALE_TURN");
                if (c.Phase != Protocol.Info) return new RuleResult("WRONG_PHASE");
                var state = Windows(c);
                if (!PublicInformationComplete(c)) return new RuleResult("PUBLIC_SELECTION_PENDING");
                var feature = FeaturePlayer(c.RoomId, c.PlayerId);
                if (feature == null || !feature.Inventory.Contains(itemId)) return new RuleResult("INVALID_ITEM");
                bool blackbox = itemId == "BLACKBOX";
                if (!blackbox && itemId != "STEAL" && itemId != "INFORMATION_STEAL" && itemId != "DISTORT") return new RuleResult("INVALID_ITEM");
                string category = c.PlayerId + (blackbox ? ":INFO" : ":ATTACK");
                if (state.UsedCategories.Contains(category)) return new RuleResult("CATEGORY_ALREADY_USED");
                RuleResult result;
                if (blackbox)
                {
                    int length = Math.Min(5, 31 - start);
                    string error = ValidateWindow(c, company, start, length); if (error != "") return new RuleResult(error);
                    if (Enumerable.Range(start, length).All(d => Mask(state, c.PlayerId, company)[d])) return new RuleResult("ALREADY_VISIBLE");
                    ((IWindowMarketDataSource)source).GetWindow(c.Turn, company, start, length);
                    result = WindowResult(c, AddWindow(c, company, start, "ITEM", 0, length: length));
                    if (feature.Ability == "INFORMATION_BROKER")
                    {
                        var mask = Mask(state, c.PlayerId, company);
                        int next = Enumerable.Range(1, 26).FirstOrDefault(s => Enumerable.Range(s, 5).Any(d => !mask[d]));
                        if (next > 0) AddWindow(c, company, next, "ITEM", 0);
                    }
                }
                else
                {
                    if (target == c.PlayerId || !room.Players.Contains(target)) return new RuleResult("INVALID_TARGET");
                    var candidates = state.Records.Where(r => r.Player == target && r.Paid > 0 && r.HiddenUntil == 0);
                    var record = candidates.FirstOrDefault(r => Enumerable.Range(r.Start, r.Length).Any(d =>
                        !Mask(state, itemId == "DISTORT" ? target : c.PlayerId, r.Company, false, itemId == "DISTORT" ? r : null)[d]));
                    if (record == null) return new RuleResult("NO_INFORMATION_TARGET");
                    if (itemId == "DISTORT") { record.HiddenUntil = now + 10; result = new RuleResult(); }
                    else result = WindowResult(c, AddWindow(c, record.Company, record.Start, "STOLEN", 0, length: record.Length));
                }
                feature.Inventory.Remove(itemId); state.UsedCategories.Add(category);
                return result;
            });
        }
    }
}
