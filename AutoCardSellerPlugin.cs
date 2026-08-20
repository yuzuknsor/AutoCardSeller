using System;
using System.Collections.Generic;
using System.Composition;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Data;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Web;
using ArchiSteamFarm.Web.Responses;

namespace ArchiSteamFarm.CustomPlugins.AutoCardSeller;

[Export(typeof(IPlugin))]
internal sealed class AutoCardSellerPlugin : IASF, IBot, IBotCommand2, IBotModules {
	private static readonly Dictionary<string, BotState> BotStates = new(StringComparer.Ordinal);

	[JsonInclude]
	public string Name => nameof(AutoCardSellerPlugin);

	[JsonInclude]
	public Version Version => typeof(AutoCardSellerPlugin).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

	public Task OnLoaded() => Task.CompletedTask;

	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) => Task.CompletedTask;

	public Task OnBotInit(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		BotStates[bot.BotName] = new BotState();

		return Task.CompletedTask;
	}

	public Task OnBotDestroy(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (BotStates.Remove(bot.BotName, out BotState? state)) {
			state.Dispose();
		}

		return Task.CompletedTask;
	}

	public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		ArgumentNullException.ThrowIfNull(bot);

		if (BotStates.TryGetValue(bot.BotName, out BotState? state)) {
			state.LoadConfig(additionalConfigProperties);

			if (state.Enabled && state.AutoSell) {
				state.EnsureTimerStarted(bot);
			} else {
				state.StopTimer();
			}
		}

		return Task.CompletedTask;
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);

		if ((args.Length == 0) || !BotStates.TryGetValue(bot.BotName, out BotState? state) || !state.Enabled) {
			return null;
		}

		switch (args[0].ToUpperInvariant()) {
			case "SELLCARDS" when access >= EAccess.Master:
				bool preview = (args.Length > 1) && args[1].Equals("preview", StringComparison.OrdinalIgnoreCase);

				return await RunSellCycle(bot, state, preview).ConfigureAwait(false);
			default:
				return null;
		}
	}

	private static async Task<string?> RunSellCycle(Bot bot, BotState state, bool preview) {
		if (!state.TryEnter()) {
			return "AutoCardSeller 任务已在运行中，请稍后再试。";
		}

		try {
			return await SellCards(bot, state, preview).ConfigureAwait(false);
		} finally {
			state.Exit();
		}
	}

	private static async Task<string> SellCards(Bot bot, BotState state, bool preview) {
		if (!bot.IsConnectedAndLoggedOn) {
			return "机器人未连接到 Steam。";
		}

		List<Asset> cards = [];

		try {
			await foreach (Asset asset in bot.ArchiHandler.GetMyInventoryAsync(Asset.SteamAppID, Asset.SteamCommunityContextID, tradableOnly: true, marketableOnly: true)) {
				if ((asset.Amount > 0) && (asset.Type == EAssetType.TradingCard) && !string.IsNullOrEmpty(asset.Description?.MarketHashName)) {
					cards.Add(asset);
				}
			}
		} catch (Exception e) {
			bot.ArchiLogger.LogGenericWarningException(e);

			return "获取库存失败。";
		}

		if (cards.Count == 0) {
			return "没有可出售的普通卡牌（闪卡已跳过，请自行处理）。";
		}

		int soldCount = 0;
		int failedCount = 0;
		int skippedCount = 0;
		uint totalCents = 0;
		List<string> lines = [];

		List<IGrouping<string, Asset>> groups = cards.GroupBy(static card => card.Description!.MarketHashName!).ToList();
		int totalGroups = groups.Count;
		int currentIndex = 0;

		bot.ArchiLogger.LogGenericInfo($"[AutoCardSeller] 开始{(preview ? "预览" : "出售")}，共 {cards.Count} 张卡牌、{totalGroups} 种，请稍候……");

		foreach (IGrouping<string, Asset> group in groups) {
			currentIndex++;

			string marketHashName = group.Key;

			int sellableAmount = (int) Math.Max(0, group.Count() - (int) state.KeepCount);

			if (sellableAmount == 0) {
				skippedCount += group.Count();

				continue;
			}

			bot.ArchiLogger.LogGenericInfo($"[AutoCardSeller] [{currentIndex}/{totalGroups}] {marketHashName}……");

			SellPrice? sellPrice = await GetSellPrice(bot, marketHashName, state).ConfigureAwait(false);

			if (!sellPrice.HasValue) {
				bot.ArchiLogger.LogGenericWarning($"{marketHashName} 无法获取市场价，已跳过。");
				skippedCount += group.Count();

				continue;
			}

			SellPrice price = sellPrice.Value;

			if (price.ReceiveCents < state.MinPriceCents) {
				bot.ArchiLogger.LogGenericInfo($"{marketHashName} 到手价 ¥{FormatCents(price.ReceiveCents)} 低于最低价 ¥{FormatCents(state.MinPriceCents)}，已跳过。");
				skippedCount += group.Count();

				continue;
			}

			int remaining = sellableAmount;

			foreach (Asset asset in group) {
				if (remaining <= 0) {
					break;
				}

				remaining--;

				if (preview) {
					string line = $"{marketHashName} ×1 | 参考 ¥{FormatCents(price.ReferenceCents)} | 挂单 ¥{FormatCents(price.ListingCents)} | 到手 ¥{FormatCents(price.ReceiveCents)}";

					lines.Add(line);
					bot.ArchiLogger.LogGenericInfo($"[AutoCardSeller] {line}");

					continue;
				}

				if (await SellAsset(bot, asset, price.ReceiveCents).ConfigureAwait(false)) {
					soldCount++;
					totalCents += price.ReceiveCents;
				} else {
					failedCount++;
				}
			}
		}

		bot.ArchiLogger.LogGenericInfo($"[AutoCardSeller] 处理完成：成功 {soldCount} 张，失败 {failedCount} 张，跳过 {skippedCount} 张。");

		if (preview) {
			return lines.Count == 0 ? "没有符合条件的卡牌。" : $"预览（共 {lines.Count} 张待售，闪卡已跳过）：\n{string.Join("\n", lines)}";
		}

		return $"出售完成：成功 {soldCount} 张，失败 {failedCount} 张，跳过 {skippedCount} 张。预计到手合计 {totalCents / 100}.{totalCents % 100:D2} 元。";
	}

	private static async Task<SellPrice?> GetSellPrice(Bot bot, string marketHashName, BotState state) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentException.ThrowIfNullOrEmpty(marketHashName);
		ArgumentNullException.ThrowIfNull(state);

		// 首选：当前市场最低挂单价（买家支付价）
		decimal? lowest = await GetLowestPrice(bot, marketHashName).ConfigureAwait(false);

		if (lowest.HasValue) {
			uint referenceCents = ToCents(lowest.Value);
			uint listingCents = ApplyUndercut(referenceCents, state.UndercutPercent);

			return new SellPrice(referenceCents, listingCents, ToReceiveCents(listingCents));
		}

		// 兜底：历史均价（买家支付价）
		uint? avgCents = await GetAveragePriceCents(bot, marketHashName, state.HistoryDays).ConfigureAwait(false);

		if (!avgCents.HasValue) {
			return null;
		}

		uint listingFallback = ApplyUndercut(avgCents.Value, state.UndercutPercent);

		return new SellPrice(avgCents.Value, listingFallback, ToReceiveCents(listingFallback));
	}

	private static uint ToCents(decimal price) => (uint) ((price * 100m) + 0.5m);

	private static uint ApplyUndercut(uint cents, uint percent) {
		if (percent == 0) {
			return cents;
		}

		return (uint) ((cents * (100 - percent) / 100m) + 0.5m);
	}

	private static uint ToReceiveCents(uint listingCents) => (uint) ((listingCents * 0.85m) + 0.5m);

	private static string FormatCents(uint cents) => $"{cents / 100}.{cents % 100:D2}";

	private readonly record struct SellPrice(uint ReferenceCents, uint ListingCents, uint ReceiveCents);

	private static async Task<decimal?> GetLowestPrice(Bot bot, string marketHashName) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentException.ThrowIfNullOrEmpty(marketHashName);

		int currency = (int) bot.WalletCurrency;

		if (currency <= 0) {
			return null;
		}

		Uri request = new(ArchiWebHandler.SteamCommunityURL, $"/market/priceoverview/?appid={Asset.SteamAppID}&currency={currency}&market_hash_name={Uri.EscapeDataString(marketHashName)}");
		Uri referer = new(ArchiWebHandler.SteamCommunityURL, $"/market/listings/{Asset.SteamAppID}/{Uri.EscapeDataString(marketHashName)}");

		ObjectResponse<PriceOverviewResponse>? response = await bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<PriceOverviewResponse>(request, referer: referer, requestOptions: WebBrowser.ERequestOptions.SteamWafWorkarounds).ConfigureAwait(false);

		if (response?.Content is not { Success: true }) {
			return null;
		}

		return ParsePriceText(response.Content.LowestPrice);
	}

	private static decimal? ParsePriceText(string? text) {
		if (string.IsNullOrEmpty(text)) {
			return null;
		}

		string digits = "";

		foreach (char character in text) {
			if (((character >= '0') && (character <= '9')) || (character == '.')) {
				digits += character;
			}
		}

		if ((digits.Length == 0) || !decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) || (value <= 0)) {
			return null;
		}

		return value;
	}

	private static async Task<uint?> GetAveragePriceCents(Bot bot, string marketHashName, uint historyDays) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentException.ThrowIfNullOrEmpty(marketHashName);

		int currency = (int) bot.WalletCurrency;

		if (currency <= 0) {
			return null;
		}

		Uri request = new(ArchiWebHandler.SteamCommunityURL, $"/market/pricehistory/?appid={Asset.SteamAppID}&currency={currency}&market_hash_name={Uri.EscapeDataString(marketHashName)}");
		Uri referer = new(ArchiWebHandler.SteamCommunityURL, $"/market/listings/{Asset.SteamAppID}/{Uri.EscapeDataString(marketHashName)}");

		ObjectResponse<PriceHistoryResponse>? response = await bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<PriceHistoryResponse>(request, referer: referer, requestOptions: WebBrowser.ERequestOptions.SteamWafWorkarounds).ConfigureAwait(false);

		if (response == null) {
			bot.ArchiLogger.LogGenericWarning($"[ACS] {marketHashName} pricehistory 请求返回 null");

			return null;
		}

		if (response.StatusCode != HttpStatusCode.OK) {
			bot.ArchiLogger.LogGenericWarning($"[ACS] {marketHashName} pricehistory HTTP {(int) response.StatusCode} {response.StatusCode}");

			return null;
		}

		if (response.Content == null) {
			bot.ArchiLogger.LogGenericWarning($"[ACS] {marketHashName} pricehistory 响应内容为空或反序列化失败");

			return null;
		}

		if (!response.Content.Success) {
			bot.ArchiLogger.LogGenericWarning($"[ACS] {marketHashName} pricehistory success=false");

			return null;
		}

		if (response.Content.Prices.Count == 0) {
			bot.ArchiLogger.LogGenericWarning($"[ACS] {marketHashName} pricehistory prices 为空（可能无成交记录）");

			return null;
		}

		DateTime cutoff = DateTime.UtcNow.AddDays(-(int) historyDays);

		List<decimal> recentPrices = [];
		List<decimal> allPrices = [];
		int parseFailures = 0;

		foreach (List<JsonElement> point in response.Content.Prices) {
			if (point.Count < 2) {
				parseFailures++;

				continue;
			}

			string? dateText = point[0].ToString();
			string? priceText = point[1].ToString();

			if (string.IsNullOrEmpty(dateText) || string.IsNullOrEmpty(priceText)) {
				parseFailures++;

				continue;
			}

			DateTime? date = ParsePriceHistoryDate(dateText);

			if (!date.HasValue) {
				parseFailures++;

				continue;
			}

			if (!decimal.TryParse(priceText, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price) || (price <= 0)) {
				parseFailures++;

				continue;
			}

			allPrices.Add(price);

			if (date.Value >= cutoff) {
				recentPrices.Add(price);
			}
		}

		if (allPrices.Count == 0) {
			bot.ArchiLogger.LogGenericWarning($"[ACS] {marketHashName} 无法解析任何成交记录（解析失败 {parseFailures} 条）。样例: {SamplePrices(response.Content.Prices)}");

			return null;
		}

		List<decimal> source = recentPrices.Count > 0 ? recentPrices : allPrices;

		decimal sum = 0;

		foreach (decimal price in source) {
			sum += price;
		}

		decimal average = sum / source.Count;

		if (recentPrices.Count == 0) {
			bot.ArchiLogger.LogGenericWarning($"[ACS] {marketHashName} 最近 {historyDays} 天无成交，回退到全部 {allPrices.Count} 条历史均价 {average.ToString("F3", CultureInfo.InvariantCulture)} 元。");
		}

		return (uint) ((average * 100m) + 0.5m);
	}

	private static DateTime? ParsePriceHistoryDate(string text) {
		ArgumentException.ThrowIfNullOrEmpty(text);

		// 格式 1：Unix 秒（纯数字）
		if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixSeconds) && (unixSeconds > 0)) {
			return DateTime.UnixEpoch.AddSeconds(unixSeconds);
		}

		// 格式 2：Steam 本地化日期字符串，如 "Apr 03 2024 01: +0"
		if (text.Length >= 11) {
			string datePart = text[..11];

			if (DateTime.TryParseExact(datePart, "MMM dd yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date)) {
				return date;
			}
		}

		return null;
	}

	private static string SamplePrices(List<List<JsonElement>> prices) {
		ArgumentNullException.ThrowIfNull(prices);

		return string.Join(" | ", prices.Take(3).Select(static point => point.Count >= 2 ? $"{point[0]},{point[1]}" : string.Join(",", point)));
	}

	private static async Task<bool> SellAsset(Bot bot, Asset asset, uint priceCents) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(asset);

		Uri request = new(ArchiWebHandler.SteamCommunityURL, "/market/sellitem/");
		Uri referer = new(ArchiWebHandler.SteamCommunityURL, $"/market/listings/{Asset.SteamAppID}/{Uri.EscapeDataString(asset.Description?.MarketHashName ?? "")}");

		Dictionary<string, string> data = new(StringComparer.Ordinal) {
			{ "appid", Asset.SteamAppID.ToString(CultureInfo.InvariantCulture) },
			{ "contextid", Asset.SteamCommunityContextID.ToString(CultureInfo.InvariantCulture) },
			{ "assetid", asset.AssetID.ToString(CultureInfo.InvariantCulture) },
			{ "amount", "1" },
			{ "price", priceCents.ToString(CultureInfo.InvariantCulture) }
		};

		ObjectResponse<SellItemResponse>? response = await bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<SellItemResponse>(request, data: data, referer: referer, requestOptions: WebBrowser.ERequestOptions.SteamWafWorkarounds).ConfigureAwait(false);

		if (response?.Content == null) {
			return false;
		}

		if (response.Content.Success) {
			bot.ArchiLogger.LogGenericInfo($"已挂单出售 {asset.Description?.MarketHashName}（assetID: {asset.AssetID}）@ {priceCents} 分到手价。");

			return true;
		}

		if (!string.IsNullOrEmpty(response.Content.Message)) {
			bot.ArchiLogger.LogGenericWarning($"出售 {asset.Description?.MarketHashName} 失败：{response.Content.Message}");
		}

		return false;
	}

	private sealed class PriceHistoryResponse {
		[JsonInclude]
		[JsonPropertyName("success")]
		public bool Success { get; init; }

		[JsonInclude]
		[JsonPropertyName("prices")]
		public List<List<JsonElement>> Prices { get; init; } = [];
	}

	private sealed class PriceOverviewResponse {
		[JsonInclude]
		[JsonPropertyName("success")]
		public bool Success { get; init; }

		[JsonInclude]
		[JsonPropertyName("lowest_price")]
		public string? LowestPrice { get; init; }
	}

	private sealed class SellItemResponse {
		[JsonInclude]
		[JsonPropertyName("success")]
		public bool Success { get; init; }

		[JsonInclude]
		[JsonPropertyName("message")]
		public string? Message { get; init; }
	}

	private sealed class BotState : IDisposable {
		internal bool Enabled;
		internal bool AutoSell;
		internal uint AutoSellIntervalHours = 24;
		internal uint MinPriceCents = 3;
		internal uint KeepCount;
		internal uint HistoryDays = 7;
		internal uint UndercutPercent;

		private Timer? Timer;
		private int Running;

		internal void LoadConfig(IReadOnlyDictionary<string, JsonElement>? properties) {
			if (properties == null) {
				return;
			}

			if (properties.TryGetValue("AutoCardSellerEnabled", out JsonElement enabled) && ((enabled.ValueKind == JsonValueKind.True) || (enabled.ValueKind == JsonValueKind.False))) {
				Enabled = enabled.GetBoolean();
			}

			if (properties.TryGetValue("AutoCardSellerAutoSell", out JsonElement autoSell) && ((autoSell.ValueKind == JsonValueKind.True) || (autoSell.ValueKind == JsonValueKind.False))) {
				AutoSell = autoSell.GetBoolean();
			}

			if (properties.TryGetValue("AutoCardSellerAutoSellIntervalHours", out JsonElement interval) && interval.TryGetUInt32(out uint intervalValue) && (intervalValue > 0)) {
				AutoSellIntervalHours = intervalValue;
			}

			if (properties.TryGetValue("AutoCardSellerMinPriceCents", out JsonElement minPrice) && minPrice.TryGetUInt32(out uint minPriceValue)) {
				MinPriceCents = Math.Max(1, minPriceValue);
			}

			if (properties.TryGetValue("AutoCardSellerKeepCount", out JsonElement keep) && keep.TryGetUInt32(out uint keepValue)) {
				KeepCount = keepValue;
			}

			if (properties.TryGetValue("AutoCardSellerHistoryDays", out JsonElement days) && days.TryGetUInt32(out uint daysValue) && (daysValue > 0)) {
				HistoryDays = Math.Min(daysValue, 30);
			}

			if (properties.TryGetValue("AutoCardSellerUndercutPercent", out JsonElement undercut) && undercut.TryGetUInt32(out uint undercutValue) && (undercutValue <= 90)) {
				UndercutPercent = undercutValue;
			}
		}

		internal void EnsureTimerStarted(Bot bot) {
			if (Timer != null) {
				return;
			}

			Timer = new Timer(
				_ => _ = RunSellCycle(bot, this, false),
				null,
				TimeSpan.FromSeconds(30), // 延迟 30 秒，等待机器人登录
				TimeSpan.FromHours(AutoSellIntervalHours) // 周期
			);
		}

		internal void StopTimer() {
			Timer?.Dispose();
			Timer = null;
		}

		internal bool TryEnter() => Interlocked.CompareExchange(ref Running, 1, 0) == 0;

		internal void Exit() => Interlocked.Exchange(ref Running, 0);

		public void Dispose() => StopTimer();
	}
}
