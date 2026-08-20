# AutoCardSeller

> **⚠️ 本项目已归档（Archived）/ 停止维护**
>
> 本仓库已进入只读归档状态，不再接受任何功能更新、修复或 issue 请求。
> 代码仅供学习参考，请自行评估风险后使用，一切后果由使用者自负。

AutoCardSeller 是一个 [ArchiSteamFarm (ASF)](https://github.com/JustArchiNET/ArchiSteamFarm) 插件，用于自动出售 Steam 集换式卡牌。

它完全复用 ASF 的安全会话体系（登录、会话刷新、请求限速），以**市场最低价**为锚点定价，目标是让卡牌**快速卖出**，而不是追求最高收益。

## 功能

- 扫描库存中的普通卡牌（TradingCard），自动挂单出售
- 闪卡（FoilTradingCard）默认**跳过**，留给你手动处理
- 定价锚定「当前市场最低挂单价」，并支持压价（比最低价再低 N%）
- `preview` 只读预览，每张卡展示 **参考价 / 挂单价 / 到手价**
- 可选定时自动出售
- 最低到手价保护、每种保留数量、取价窗口等可配置

## 安装

1. 编译后，将 `AutoCardSeller.dll` 放入 ASF 的 `plugins/AutoCardSeller/` 目录
2. 重启 ASF，日志中出现 `AutoCardSellerPlugin 加载成功！` 即完成

## 配置

在 bot 的配置文件中（如 `config/KNSOR.json`）添加以下字段：

| 字段 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `AutoCardSellerEnabled` | bool | false | 启用插件 |
| `AutoCardSellerAutoSell` | bool | false | 登录后定时自动出售 |
| `AutoCardSellerAutoSellIntervalHours` | uint | 24 | 自动出售周期（小时） |
| `AutoCardSellerMinPriceCents` | uint | 3 | 最低到手价（分），低于不卖 |
| `AutoCardSellerKeepCount` | uint | 0 | 每种卡保留数量 |
| `AutoCardSellerHistoryDays` | uint | 7 | 历史均价回退窗口（天） |
| `AutoCardSellerUndercutPercent` | uint | 0 | 比市场最低价再低 N% 挂单 |

示例：

```json
{
	"AutoCardSellerEnabled": true,
	"AutoCardSellerUndercutPercent": 3
}
```

## 命令

| 命令 | 权限 | 说明 |
|------|------|------|
| `sellcards preview` | Master | 只查价，列出每张卡的参考价/挂单价/到手价，不实际挂单 |
| `sellcards` | Master | 立即扫描库存并按定价挂单出售 |

## 定价逻辑

1. 查 `priceoverview` 获取该卡当前市场**最低挂单价**（买家支付价）
2. **挂单价** = 最低价 × (1 - 压价比例)
3. **到手价** = 挂单价 × 0.85（扣除 Steam 约 15% 手续费）
4. 查不到最低价时，回退到历史成交均价

## 编译

- 需要 .NET 10 SDK
- 需引用 ASF 的 `ArchiSteamFarm.dll` 与 `SteamKit2.dll`（见 `.csproj` 中的 `HintPath`）
- 从 [ASF Releases](https://github.com/JustArchiNET/ArchiSteamFarm/releases) 下载 generic 包获取上述 dll，放入 `refs/` 目录
- 编译命令：`dotnet build -c Release -o out`

## 许可证

[Apache License 2.0](LICENSE)

## 免责声明

本插件涉及 Steam 市场自动化操作，可能违反 Steam 用户协议。请在了解并接受相关风险的前提下使用，一切后果由使用者自行承担。
