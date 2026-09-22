using Dalamud.Game.Network.Structures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Soumen.Services;

public enum MarketMapPurchaseState
{
    Idle,
    Searching,
    WaitingForSearchResults,
    WaitingForOfferings,
    WaitingForPurchase,
    Success,
    Failed,
}

public sealed class MarketMapPurchaseService : IDisposable
{
    private static readonly InventoryType[] MainInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private readonly DiagnosticLogger diagnostics;
    private readonly object eventLock = new();

    private IReadOnlyList<IMarketBoardItemListing>? receivedOfferings;
    private bool offeringsReceived;
    private bool purchaseReceived;
    private uint targetItemId;
    private string targetItemName = string.Empty;
    private uint maximumUnitPrice;
    private uint maximumTotalPrice;
    private int baselineInventoryCount;
    private DateTime deadlineUtc = DateTime.MinValue;
    private DateTime nextActionUtc = DateTime.MinValue;
    private int searchAttempts;

    public MarketMapPurchaseService(DiagnosticLogger diagnostics)
    {
        this.diagnostics = diagnostics;
        Plugin.MarketBoard.OfferingsReceived += OnOfferingsReceived;
        Plugin.MarketBoard.ItemPurchased += OnItemPurchased;
    }

    public MarketMapPurchaseState State { get; private set; }

    public string StatusText { get; private set; } = "等待市场板";

    public uint PurchasedUnitPrice { get; private set; }

    public uint PurchasedTotalPrice { get; private set; }

    public bool IsBoardOpen
    {
        get
        {
            unsafe
            {
                var addon = Plugin.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch", 1);
                return addon != null && addon->AtkUnitBase.IsVisible;
            }
        }
    }

    public void Dispose()
    {
        Plugin.MarketBoard.OfferingsReceived -= OnOfferingsReceived;
        Plugin.MarketBoard.ItemPurchased -= OnItemPurchased;
    }

    public void Begin(uint itemId, string itemName, uint maxUnitPrice, uint maxTotalPrice)
    {
        targetItemId = itemId;
        targetItemName = itemName;
        maximumUnitPrice = maxUnitPrice;
        maximumTotalPrice = maxTotalPrice;
        baselineInventoryCount = ReadMainInventoryCount(itemId);
        PurchasedUnitPrice = 0;
        PurchasedTotalPrice = 0;
        searchAttempts = 0;
        lock (eventLock)
        {
            receivedOfferings = null;
            offeringsReceived = false;
            purchaseReceived = false;
        }

        if (!HasCapacityForOne(itemId))
        {
            Fail("背包没有可容纳藏宝图的空位");
            return;
        }

        State = MarketMapPurchaseState.Searching;
        StatusText = $"准备搜索 {itemName}";
        nextActionUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        deadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    }

    public void Reset()
    {
        State = MarketMapPurchaseState.Idle;
        StatusText = "等待市场板";
        lock (eventLock)
        {
            receivedOfferings = null;
            offeringsReceived = false;
            purchaseReceived = false;
        }
    }

    public unsafe void Update(DateTime now)
    {
        switch (State)
        {
            case MarketMapPurchaseState.Searching:
                if (now < nextActionUtc)
                {
                    return;
                }

                if (!IsBoardOpen)
                {
                    Fail("市场板界面未打开");
                    return;
                }

                RunSearch();
                searchAttempts++;
                State = MarketMapPurchaseState.WaitingForSearchResults;
                StatusText = $"正在搜索 {targetItemName}";
                deadlineUtc = now + TimeSpan.FromSeconds(5);
                return;

            case MarketMapPurchaseState.WaitingForSearchResults:
            {
                var addon = Plugin.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch", 1);
                var list = addon == null ? null : addon->ResultsList;
                if (addon != null && addon->AtkUnitBase.IsVisible && list != null && list->ListLength > 0)
                {
                    lock (eventLock)
                    {
                        receivedOfferings = null;
                        offeringsReceived = false;
                    }

                    list->DispatchItemEvent(0, AtkEventType.ListItemClick);
                    State = MarketMapPurchaseState.WaitingForOfferings;
                    StatusText = "正在读取市场板价格";
                    deadlineUtc = now + TimeSpan.FromSeconds(12);
                    diagnostics.Write("自动补图", $"已请求 {targetItemName} 的市场板在售列表。" );
                    return;
                }

                if (now <= deadlineUtc)
                {
                    return;
                }

                RetrySearch($"市场板没有搜索到 {targetItemName}");
                return;
            }

            case MarketMapPurchaseState.WaitingForOfferings:
            {
                IReadOnlyList<IMarketBoardItemListing>? offerings;
                bool received;
                lock (eventLock)
                {
                    received = offeringsReceived;
                    offerings = receivedOfferings;
                }

                if (received)
                {
                    SelectAndPurchase(offerings ?? []);
                    return;
                }

                if (now > deadlineUtc)
                {
                    RetrySearch("读取市场板价格超时");
                }

                return;
            }

            case MarketMapPurchaseState.WaitingForPurchase:
            {
                bool purchased;
                lock (eventLock)
                {
                    purchased = purchaseReceived;
                }

                if (purchased || ReadMainInventoryCount(targetItemId) > baselineInventoryCount)
                {
                    State = MarketMapPurchaseState.Success;
                    StatusText = $"已购买 {targetItemName}，含税 {PurchasedTotalPrice:N0} Gil";
                    diagnostics.Write("自动补图", StatusText);
                    return;
                }

                if (now > deadlineUtc)
                {
                    Fail("购买请求未得到服务器确认");
                }

                return;
            }
        }
    }

    public unsafe void CloseBoard()
    {
        CloseAddon("ItemSearchResult");
        CloseAddon("ItemSearch");
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        if (State != MarketMapPurchaseState.WaitingForOfferings)
        {
            return;
        }

        if (offerings.ItemListings.Count > 0
            && offerings.ItemListings[0].ItemId != targetItemId)
        {
            return;
        }

        lock (eventLock)
        {
            receivedOfferings = offerings.ItemListings.ToList();
            offeringsReceived = true;
        }
    }

    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        if (State != MarketMapPurchaseState.WaitingForPurchase
            || purchase.CatalogId != targetItemId
            || purchase.ItemQuantity != 1)
        {
            return;
        }

        lock (eventLock)
        {
            purchaseReceived = true;
        }
    }

    private unsafe void RunSearch()
    {
        CloseAddon("ItemSearchResult");
        var addon = Plugin.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch", 1);
        if (addon == null || !addon->AtkUnitBase.IsVisible)
        {
            return;
        }

        addon->SearchText.SetString(targetItemName);
        if (addon->SearchTextInput != null)
        {
            var encoded = System.Text.Encoding.UTF8.GetBytes(targetItemName + "\0");
            fixed (byte* pointer = encoded)
            {
                addon->SearchTextInput->SetText(pointer);
            }
        }

        addon->RunSearch(false);
    }

    private unsafe void SelectAndPurchase(IReadOnlyList<IMarketBoardItemListing> offerings)
    {
        if (!HasCapacityForOne(targetItemId))
        {
            Fail("背包没有可容纳藏宝图的空位");
            return;
        }

        var exactListings = offerings
            .Where(listing => listing.ItemId == targetItemId && listing.ItemQuantity == 1)
            .OrderBy(listing => listing.PricePerUnit)
            .ThenBy(listing => listing.TotalTax)
            .ToList();
        if (exactListings.Count == 0)
        {
            Fail(offerings.Count == 0
                ? $"当前没有 {targetItemName} 在售"
                : "当前没有单张上架的藏宝图");
            return;
        }

        var listing = exactListings.FirstOrDefault(candidate => candidate.PricePerUnit <= maximumUnitPrice);
        if (listing == null)
        {
            Fail($"最低单价 {exactListings[0].PricePerUnit:N0} Gil，超过上限 {maximumUnitPrice:N0} Gil");
            return;
        }

        var totalCost = (long)listing.PricePerUnit + listing.TotalTax;
        if (totalCost > maximumTotalPrice)
        {
            Fail($"含税价格 {totalCost:N0} Gil，超过本轮剩余预算 {maximumTotalPrice:N0} Gil");
            return;
        }

        var manager = InventoryManager.Instance();
        var gil = manager == null ? -1 : manager->GetInventoryItemCount(1);
        if (gil < totalCost)
        {
            Fail($"金币不足：需要 {totalCost:N0} Gil，当前 {Math.Max(gil, 0):N0} Gil");
            return;
        }

        var agent = AgentItemSearch.Instance();
        var proxy = agent == null ? null : agent->InfoProxyItemSearch;
        if (proxy == null)
        {
            Fail("无法读取市场板购买状态");
            return;
        }

        var nativeListings = (MarketBoardListing*)((byte*)proxy + 0x30);
        MarketBoardListing* target = null;
        for (var index = 0; index < (int)proxy->ListingCount; index++)
        {
            var candidate = nativeListings + index;
            if (candidate->ListingId == listing.ListingId)
            {
                target = candidate;
                break;
            }
        }

        if (target == null
            || target->ItemId != targetItemId
            || target->Quantity != 1
            || target->UnitPrice > maximumUnitPrice
            || target->UnitPrice != listing.PricePerUnit)
        {
            RetrySearch("在售记录或价格已经变化，正在刷新");
            return;
        }

        if (!proxy->SetLastPurchasedItem(target))
        {
            Fail("无法建立市场板购买请求");
            return;
        }

        proxy->LastPurchasedMarketboardItem.Quantity = 1;
        if (!proxy->SendPurchaseRequestPacket())
        {
            Fail("市场板购买请求提交失败");
            return;
        }

        PurchasedUnitPrice = target->UnitPrice;
        PurchasedTotalPrice = checked((uint)totalCost);
        State = MarketMapPurchaseState.WaitingForPurchase;
        StatusText = $"正在购买 {targetItemName}，含税 {PurchasedTotalPrice:N0} Gil";
        deadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        diagnostics.Write("自动补图", $"已提交单张 {targetItemName} 的购买请求，含税 {PurchasedTotalPrice:N0} Gil。" );
    }

    private void RetrySearch(string reason)
    {
        lock (eventLock)
        {
            receivedOfferings = null;
            offeringsReceived = false;
        }

        if (searchAttempts >= 3)
        {
            Fail(reason);
            return;
        }

        State = MarketMapPurchaseState.Searching;
        StatusText = reason;
        nextActionUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        deadlineUtc = nextActionUtc + TimeSpan.FromSeconds(15);
        diagnostics.Write("自动补图", reason);
    }

    private void Fail(string reason)
    {
        State = MarketMapPurchaseState.Failed;
        StatusText = reason;
        diagnostics.Write("自动补图", reason);
    }

    private static unsafe int ReadMainInventoryCount(uint itemId)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return 0;
        }

        var count = 0;
        foreach (var type in MainInventories)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var index = 0; index < container->Size; index++)
            {
                var item = container->GetInventorySlot(index);
                if (item != null && item->ItemId == itemId)
                {
                    count += (int)item->Quantity;
                }
            }
        }

        return count;
    }

    private static unsafe bool HasCapacityForOne(uint itemId)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return false;
        }

        var item = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        var stackSize = item?.StackSize ?? 1;
        foreach (var type in MainInventories)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot == null || slot->ItemId == 0)
                {
                    return true;
                }

                if (slot->ItemId == itemId && slot->Quantity < stackSize)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static unsafe void CloseAddon(string name)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(name, 1);
        if (addon != null && addon->IsVisible)
        {
            addon->Close(true);
        }
    }
}
