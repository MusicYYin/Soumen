using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Soumen.Models;

namespace Soumen.Services;

public sealed class AutoDiscardService : IDisposable
{
    private static readonly InventoryType[] MainInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static readonly InventoryType[] TrackedInventories =
    [
        .. MainInventories,
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2,
    ];

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LootQuietPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan OutdoorSessionDuration = TimeSpan.FromHours(2);

    private static readonly HashSet<string> G18EnglishNames = new(StringComparer.Ordinal)
    {
        "Yan Horn",
        "Twilight Gemstone",
        "Gimme Kitten",
        "Mini Yan",
        "The Lawnblazer",
        "Honeysuckler",
        "Blade Unbroken Orchestrion Roll",
        "Cast Stones in Shadow Orchestrion Roll",
        "Etched in Memory Orchestrion Roll",
        "Fun and Games Orchestrion Roll",
        "Taco Delight Orchestrion Roll",
        "For Hope and Happiness Orchestrion Roll",
        "Figmental Weapon Coffer",
        "The Faces We Wear - Cat Eye Glasses",
        "The Faces We Wear - Professorial Glasses",
        "The Faces We Wear - Tactical Goggles",
        "The Faces We Wear - Coeurl Eyeglasses",
        "The Faces We Wear - Slim Frame Glasses",
        "Modern Aesthetics - Doing the Wave",
        "Torch",
        "Ribboned Parasol",
        "Cloth of Legend",
        "Corduroy Felt",
        "Dichromatic Dye",
        "Summerlight Linen",
        "Indigo Twill Cloth",
        "Sweatcloth",
        "Chocobo Chick Fountain",
        "Dumbbell Rack",
        "Everkeep Mixing Booth",
        "Training Bench",
        "Unfriendly Yan Neon Wall Light",
        "Everkeep Monitor",
        "Everkeep Stereo",
    };

    private static readonly string[] PriorityMateriaPrefixes =
    [
        "Savage Aim Materia",
        "Savage Might Materia",
        "Heavens' Eye Materia",
    ];

    private readonly Configuration configuration;
    private readonly MapFlagAutomation mapAutomation;
    private readonly DiagnosticLogger diagnostics;
    private readonly Dictionary<uint, DiscardCatalogItem> catalogById;
    private readonly Dictionary<ItemKey, int> lastCounts = [];
    private readonly Dictionary<ItemKey, int> earnedCounts = [];
    private readonly HashSet<uint> observedItemIds = [];

    private DateTime nextPollUtc = DateTime.MinValue;
    private DateTime lastInventoryIncreaseUtc = DateTime.MinValue;
    private DateTime sessionExpiresUtc = DateTime.MinValue;
    private DateTime nextOperationUtc = DateTime.MinValue;
    private PendingOperation? pendingOperation;
    private bool sessionActive;

    public AutoDiscardService(
        Configuration configuration,
        MapFlagAutomation mapAutomation,
        DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.mapAutomation = mapAutomation;
        this.diagnostics = diagnostics;
        catalogById = BuildCatalog();
        mapAutomation.DestinationReached += OnDestinationReached;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public bool SessionActive => sessionActive;

    public string StatusText { get; private set; } = "等待开始挖宝";

    public IReadOnlyCollection<uint> ObservedItemIds => observedItemIds;

    public IReadOnlyList<DiscardCatalogItem> G18Items
        => catalogById.Values.Where(item => item.IsG18Loot).OrderBy(item => item.Name).ToList();

    public IReadOnlyList<DiscardCatalogItem> NonPriorityMateria
        => catalogById.Values.Where(item => item.IsNonPriorityMateria).OrderBy(item => item.Name).ToList();

    public IReadOnlyList<DiscardCatalogItem> SelectedItems
        => configuration.AutoDiscardItemIds
            .Select(GetCatalogItem)
            .Where(item => item != null)
            .Cast<DiscardCatalogItem>()
            .OrderBy(item => item.Name)
            .ToList();

    public IReadOnlyList<DiscardCatalogItem> ObservedItems
        => observedItemIds
            .Select(GetCatalogItem)
            .Where(item => item != null)
            .Cast<DiscardCatalogItem>()
            .OrderBy(item => item.Name)
            .ToList();

    public void Dispose()
    {
        mapAutomation.DestinationReached -= OnDestinationReached;
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    public DiscardCatalogItem? GetCatalogItem(uint itemId)
        => catalogById.GetValueOrDefault(itemId);

    public IReadOnlyList<DiscardCatalogItem> Search(string query, int limit = 200)
    {
        query = query.Trim();
        if (query.Length < 2)
        {
            return [];
        }

        return catalogById.Values
            .Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.ItemId.ToString().Contains(query, StringComparison.Ordinal))
            .OrderBy(item => item.Name)
            .Take(limit)
            .ToList();
    }

    public int PendingQuantity(uint itemId)
        => earnedCounts.Where(pair => pair.Key.ItemId == itemId).Sum(pair => pair.Value);

    private void OnDestinationReached()
        => StartOrExtendSession("已到达藏宝图坐标，开始记录本轮新增物品");

    private void OnFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        var now = DateTime.UtcNow;
        if (now < nextPollUtc)
        {
            return;
        }

        nextPollUtc = now + PollInterval;

        if (!configuration.Enabled || Plugin.ObjectTable.LocalPlayer == null)
        {
            EndSession();
            return;
        }

        if (TreasureContext.IsTreasureDungeon())
        {
            StartOrExtendSession("宝物库中，正在记录新增物品");
        }
        else if (sessionActive && now > sessionExpiresUtc)
        {
            EndSession();
            return;
        }

        if (!sessionActive)
        {
            return;
        }

        UpdateInventoryDeltas(now);
        ProcessPendingOperation(now);

        if (pendingOperation != null
            || !configuration.AutoDiscardEnabled
            || now < nextOperationUtc
            || now - lastInventoryIncreaseUtc < LootQuietPeriod
            || !CanOperateInventory())
        {
            if (!configuration.AutoDiscardEnabled)
            {
                StatusText = "正在记录本轮新增物品，自动丢弃未开启";
            }

            return;
        }

        TryStartNextDiscard(now);
    }

    private void StartOrExtendSession(string status)
    {
        sessionExpiresUtc = DateTime.UtcNow + OutdoorSessionDuration;
        if (sessionActive)
        {
            StatusText = status;
            return;
        }

        sessionActive = true;
        lastCounts.Clear();
        foreach (var pair in ReadTrackedCounts())
        {
            lastCounts[pair.Key] = pair.Value;
        }

        earnedCounts.Clear();
        observedItemIds.Clear();
        pendingOperation = null;
        StatusText = status;
        diagnostics.Write("自动丢弃", "已建立背包基线，只会处理此后增加的数量。");
    }

    private void EndSession()
    {
        if (!sessionActive)
        {
            return;
        }

        sessionActive = false;
        sessionExpiresUtc = DateTime.MinValue;
        pendingOperation = null;
        lastCounts.Clear();
        earnedCounts.Clear();
        StatusText = "等待开始挖宝";
        diagnostics.Write("自动丢弃", "本轮物品记录已结束。");
    }

    private void UpdateInventoryDeltas(DateTime now)
    {
        var current = ReadTrackedCounts();
        var keys = lastCounts.Keys.Concat(current.Keys).Distinct().ToList();
        foreach (var key in keys)
        {
            var oldCount = lastCounts.GetValueOrDefault(key);
            var newCount = current.GetValueOrDefault(key);
            var delta = newCount - oldCount;
            if (delta > 0)
            {
                earnedCounts[key] = Math.Min(newCount, earnedCounts.GetValueOrDefault(key) + delta);
                observedItemIds.Add(key.ItemId);
                lastInventoryIncreaseUtc = now;
                diagnostics.Write(
                    "物品记录",
                    $"{GetItemName(key.ItemId)}{(key.IsHq ? " HQ" : string.Empty)} +{delta}，本轮可处理 {earnedCounts[key]}。");
            }
            else if (delta < 0 && earnedCounts.TryGetValue(key, out var earned))
            {
                var remaining = Math.Max(0, earned + delta);
                if (remaining == 0)
                {
                    earnedCounts.Remove(key);
                }
                else
                {
                    earnedCounts[key] = remaining;
                }
            }
        }

        lastCounts.Clear();
        foreach (var pair in current)
        {
            lastCounts[pair.Key] = pair.Value;
        }
    }

    private unsafe void TryStartNextDiscard(DateTime now)
    {
        foreach (var pair in earnedCounts
                     .Where(pair => pair.Value > 0 && configuration.AutoDiscardItemIds.Contains(pair.Key.ItemId))
                     .OrderBy(pair => pair.Key.ItemId)
                     .ToList())
        {
            var slots = ReadMainInventorySlots(pair.Key).OrderBy(slot => slot.Quantity).ToList();
            if (slots.Count == 0)
            {
                continue;
            }

            var earned = Math.Min(pair.Value, slots.Sum(slot => slot.Quantity));
            var wholeStack = slots.FirstOrDefault(slot => slot.Quantity <= earned);
            if (wholeStack != null)
            {
                BeginDiscard(wholeStack, wholeStack.Quantity, now);
                return;
            }

            var source = slots[0];
            var emptySlots = ReadEmptyMainSlots();
            if (emptySlots.Count == 0)
            {
                StatusText = $"{GetItemName(pair.Key.ItemId)} 已并入旧堆，但背包没有空格可拆分";
                diagnostics.WriteThrottled(
                    $"discard-no-empty-{pair.Key.ItemId}",
                    "自动丢弃",
                    $"{GetItemName(pair.Key.ItemId)} 需要先拆分 {earned} 个，但背包没有空格；为保护旧物品已跳过。",
                    TimeSpan.FromSeconds(30));
                continue;
            }

            var manager = InventoryManager.Instance();
            if (manager == null)
            {
                return;
            }

            var result = manager->SplitItem(source.Inventory, source.Slot, earned);
            pendingOperation = PendingOperation.WaitingForSplit(
                pair.Key,
                earned,
                emptySlots,
                now + OperationTimeout);
            StatusText = $"正在拆分本轮新增的 {GetItemName(pair.Key.ItemId)} ×{earned}";
            diagnostics.Write(
                "自动丢弃",
                $"拆分 {GetItemName(pair.Key.ItemId)} ×{earned}，source={source.Inventory}/{source.Slot}，result={result}。");
            return;
        }

        StatusText = "正在记录本轮新增物品";
    }

    private void ProcessPendingOperation(DateTime now)
    {
        if (pendingOperation == null)
        {
            return;
        }

        if (now > pendingOperation.TimeoutUtc)
        {
            diagnostics.Write("自动丢弃", $"操作超时：{pendingOperation.Kind}，已停止本次处理。");
            StatusText = "物品操作超时，稍后重试";
            pendingOperation = null;
            nextOperationUtc = now + TimeSpan.FromSeconds(3);
            return;
        }

        switch (pendingOperation.Kind)
        {
            case PendingOperationKind.WaitingForSplit:
            {
                var splitSlot = pendingOperation.EmptySlots
                    .Select(ReadSlot)
                    .FirstOrDefault(slot => slot != null
                        && slot.Key == pendingOperation.Key
                        && slot.Quantity == pendingOperation.Quantity);
                if (splitSlot != null)
                {
                    BeginDiscard(splitSlot, pendingOperation.Quantity, now);
                }

                break;
            }
            case PendingOperationKind.WaitingForConfirmation:
                ProcessDiscardConfirmation(now);
                break;
            case PendingOperationKind.WaitingForRemoval:
            {
                var slot = ReadSlot(pendingOperation.TargetSlot);
                if (slot == null
                    || slot.Key != pendingOperation.Key
                    || slot.Quantity < pendingOperation.Quantity)
                {
                    diagnostics.Write(
                        "自动丢弃",
                        $"已丢弃 {GetItemName(pendingOperation.Key.ItemId)} ×{pendingOperation.Quantity}。");
                    pendingOperation = null;
                    nextOperationUtc = now + TimeSpan.FromSeconds(1);
                }

                break;
            }
        }
    }

    private unsafe void BeginDiscard(InventorySlot slot, int quantity, DateTime now)
    {
        if (quantity <= 0
            || slot.Quantity != quantity
            || earnedCounts.GetValueOrDefault(slot.Key) < quantity)
        {
            diagnostics.Write("自动丢弃", "丢弃前数量复核失败，已取消本次操作。");
            pendingOperation = null;
            nextOperationUtc = now + TimeSpan.FromSeconds(2);
            return;
        }

        var manager = InventoryManager.Instance();
        var context = AgentInventoryContext.Instance();
        if (manager == null || context == null)
        {
            pendingOperation = null;
            return;
        }

        var item = manager->GetInventorySlot(slot.Inventory, slot.Slot);
        if (item == null)
        {
            pendingOperation = null;
            return;
        }

        context->DiscardItem(item, slot.Inventory, slot.Slot, 0);
        pendingOperation = PendingOperation.WaitingForConfirmation(
            slot.Key,
            quantity,
            new SlotAddress(slot.Inventory, slot.Slot),
            now + OperationTimeout);
        StatusText = $"正在丢弃本轮新增的 {GetItemName(slot.Key.ItemId)} ×{quantity}";
    }

    private unsafe void ProcessDiscardConfirmation(DateTime now)
    {
        if (pendingOperation == null)
        {
            return;
        }

        var slot = ReadSlot(pendingOperation.TargetSlot);
        if (slot == null || slot.Key != pendingOperation.Key || slot.Quantity != pendingOperation.Quantity)
        {
            pendingOperation = null;
            nextOperationUtc = now + TimeSpan.FromSeconds(2);
            diagnostics.Write("自动丢弃", "确认前物品槽发生变化，已取消本次操作。");
            return;
        }

        var context = AgentInventoryContext.Instance();
        if (context == null || context->DialogType != 1)
        {
            return;
        }

        for (var index = 1; index < 10; index++)
        {
            var addon = Plugin.GameGui.GetAddonByName<AddonSelectYesno>("SelectYesno", index);
            if (addon == null || !addon->IsVisible || addon->PromptText == null)
            {
                continue;
            }

            var prompt = addon->PromptText->NodeText.ExtractText();
            var expectedName = GetItemName(pendingOperation.Key.ItemId);
            if (!string.IsNullOrWhiteSpace(expectedName)
                && !prompt.Contains(expectedName, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Write(
                    "自动丢弃",
                    $"检测到其他确认框，未点击：{prompt}");
                return;
            }

            addon->YesButton->AtkComponentBase.SetEnabledState(true);
            addon->FireCallbackInt(0);
            pendingOperation = pendingOperation with
            {
                Kind = PendingOperationKind.WaitingForRemoval,
                TimeoutUtc = now + OperationTimeout,
            };
            return;
        }
    }

    private static bool CanOperateInventory()
        => !Plugin.Condition.Any(
            ConditionFlag.InCombat,
            ConditionFlag.BetweenAreas,
            ConditionFlag.BetweenAreas51,
            ConditionFlag.Occupied,
            ConditionFlag.Occupied30,
            ConditionFlag.Occupied33,
            ConditionFlag.OccupiedInCutSceneEvent,
            ConditionFlag.WatchingCutscene,
            ConditionFlag.WatchingCutscene78,
            ConditionFlag.Casting,
            ConditionFlag.Casting87);

    private Dictionary<uint, DiscardCatalogItem> BuildCatalog()
    {
        var englishNames = new Dictionary<uint, string>();
        try
        {
            englishNames = Plugin.DataManager
                .GetExcelSheet<Item>(ClientLanguage.English)
                .Where(item => item.RowId != 0 && !string.IsNullOrWhiteSpace(item.Name.ToString()))
                .ToDictionary(item => item.RowId, item => item.Name.ToString());
        }
        catch (Exception exception)
        {
            // Some regional clients may not have the English sheet available. The
            // localized catalog still supports search and explicit selections; only
            // the English-name presets become unavailable for that session.
            diagnostics.Write("自动丢弃", $"读取英文物品表失败，改用当前客户端语言：{exception.Message}");
        }

        var result = new Dictionary<uint, DiscardCatalogItem>();
        foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
        {
            if (item.RowId == 0
                || item.IsIndisposable
                || string.IsNullOrWhiteSpace(item.Name.ToString()))
            {
                continue;
            }

            var localizedName = item.Name.ToString();
            var englishName = englishNames.GetValueOrDefault(item.RowId, localizedName);
            var isMateria = englishName.Contains("Materia", StringComparison.Ordinal)
                && !PriorityMateriaPrefixes.Any(prefix => englishName.StartsWith(prefix, StringComparison.Ordinal));
            result[item.RowId] = new(
                item.RowId,
                localizedName,
                englishName,
                item.Icon,
                G18EnglishNames.Contains(englishName),
                isMateria);
        }

        return result;
    }

    private string GetItemName(uint itemId)
        => catalogById.TryGetValue(itemId, out var item) ? item.Name : $"物品 #{itemId}";

    private static unsafe Dictionary<ItemKey, int> ReadTrackedCounts()
    {
        var result = new Dictionary<ItemKey, int>();
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return result;
        }

        foreach (var type in TrackedInventories)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var index = 0; index < container->Size; index++)
            {
                var item = container->GetInventorySlot(index);
                if (item == null || item->ItemId == 0 || item->Quantity <= 0)
                {
                    continue;
                }

                var key = new ItemKey(
                    item->ItemId,
                    item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                result[key] = result.GetValueOrDefault(key) + item->Quantity;
            }
        }

        return result;
    }

    private static unsafe List<InventorySlot> ReadMainInventorySlots(ItemKey key)
    {
        var result = new List<InventorySlot>();
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return result;
        }

        foreach (var type in MainInventories)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (ushort index = 0; index < container->Size; index++)
            {
                var item = container->GetInventorySlot(index);
                if (item == null || item->ItemId == 0 || item->Quantity <= 0)
                {
                    continue;
                }

                var itemKey = new ItemKey(
                    item->ItemId,
                    item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                if (itemKey == key)
                {
                    result.Add(new(type, index, itemKey, item->Quantity));
                }
            }
        }

        return result;
    }

    private static unsafe HashSet<SlotAddress> ReadEmptyMainSlots()
    {
        var result = new HashSet<SlotAddress>();
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return result;
        }

        foreach (var type in MainInventories)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (ushort index = 0; index < container->Size; index++)
            {
                var item = container->GetInventorySlot(index);
                if (item != null && item->ItemId == 0)
                {
                    result.Add(new(type, index));
                }
            }
        }

        return result;
    }

    private static unsafe InventorySlot? ReadSlot(SlotAddress address)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return null;
        }

        var item = manager->GetInventorySlot(address.Inventory, address.Slot);
        if (item == null || item->ItemId == 0 || item->Quantity <= 0)
        {
            return null;
        }

        var key = new ItemKey(
            item->ItemId,
            item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
        return new(address.Inventory, address.Slot, key, item->Quantity);
    }

    private readonly record struct ItemKey(uint ItemId, bool IsHq);

    private readonly record struct SlotAddress(InventoryType Inventory, ushort Slot);

    private sealed record InventorySlot(
        InventoryType Inventory,
        ushort Slot,
        ItemKey Key,
        int Quantity);

    private enum PendingOperationKind
    {
        WaitingForSplit,
        WaitingForConfirmation,
        WaitingForRemoval,
    }

    private sealed record PendingOperation(
        PendingOperationKind Kind,
        ItemKey Key,
        int Quantity,
        HashSet<SlotAddress> EmptySlots,
        SlotAddress TargetSlot,
        DateTime TimeoutUtc)
    {
        public static PendingOperation WaitingForSplit(
            ItemKey key,
            int quantity,
            HashSet<SlotAddress> emptySlots,
            DateTime timeoutUtc)
            => new(PendingOperationKind.WaitingForSplit, key, quantity, emptySlots, default, timeoutUtc);

        public static PendingOperation WaitingForConfirmation(
            ItemKey key,
            int quantity,
            SlotAddress targetSlot,
            DateTime timeoutUtc)
            => new(PendingOperationKind.WaitingForConfirmation, key, quantity, [], targetSlot, timeoutUtc);
    }
}
