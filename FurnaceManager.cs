
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("FurnaceManager", "Milestorme", "0.1.0")]
    [Description("Unified furnace management with splitter, quick smelt, fuel automation, and accurate UI")]
    public class FurnaceManager : RustPlugin
    {
        [PluginReference]
        private Plugin UIScaleManager;

        private static FurnaceManager Instance;
        private const string permUse = "furnacemanager.use";

        private class OvenSlot
        {
            public Item Item;
            public int? Position;
            public int Index;
            public int DeltaAmount;
        }

        public class OvenInfo
        {
            public float ETA;
            public float FuelNeeded;
        }

        private class StoredData
        {
            public Dictionary<ulong, PlayerOptions> AllPlayerOptions { get; private set; } = new Dictionary<ulong, PlayerOptions>();
        }

        private class PlayerOptions
        {
            public bool Enabled = true;
            public Dictionary<string, int> TotalStacks = new Dictionary<string, int>();
        }

        public enum MoveResult
        {
            Ok,
            SlotsFilled,
            NotEnoughSlots
        }

        private StoredData storedData = new StoredData();
        private Dictionary<ulong, PlayerOptions> allPlayerOptions => storedData.AllPlayerOptions;
        private Dictionary<string, int> initialStackOptions = new Dictionary<string, int>();
        private PluginConfig config;

        private readonly Dictionary<ulong, string> openUis = new Dictionary<ulong, string>();
        private readonly Dictionary<BaseOven, List<BasePlayer>> looters = new Dictionary<BaseOven, List<BasePlayer>>();
        private readonly Stack<BaseOven> queuedUiUpdates = new Stack<BaseOven>();

        #region Hooks

        private void Init()
        {
            Instance = this;
            permission.RegisterPermission(permUse, this);
        }

        private void OnServerInitialized()
        {
            var saveCfg = false;

            foreach (var prefab in GameManifest.Current.entities)
            {
                var gameObj = GameManager.server.FindPrefab(prefab);
                if (gameObj == null) continue;

                var oven = gameObj.GetComponent<BaseOven>();
                if (oven != null && oven.allowByproductCreation)
                {
                    if (!initialStackOptions.ContainsKey(oven.ShortPrefabName))
                        initialStackOptions[oven.ShortPrefabName] = oven.inputSlots;

                    if (!config.ovens.ContainsKey(oven.ShortPrefabName))
                    {
                        config.ovens[oven.ShortPrefabName] = PluginConfig.OvenConfig.CreateDefault(false);
                        saveCfg = true;
                    }
                }
            }

            if (saveCfg)
                SaveConfig();

            if (config.savePlayerData)
                storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name) ?? new StoredData();

            var ovens = UnityEngine.Object.FindObjectsOfType<BaseOven>();
            foreach (var oven in ovens)
            {
                EnsureController(oven);
            }

            timer.Once(1f, () =>
            {
                foreach (var oven in ovens)
                {
                    if (oven == null || oven.IsDestroyed || !oven.IsOn())
                        continue;

                    var controller = EnsureController(oven);
                    if (controller != null && CanUse(oven.OwnerID))
                        controller.StartCooking();
                }
            });
        }

        private void Unload()
        {
            SaveData();

            foreach (var kv in openUis.ToDictionary(kv => kv.Key, kv => kv.Value))
            {
                var player = BasePlayer.FindByID(kv.Key);
                if (player != null)
                    DestroyUI(player);
            }

            foreach (var oven in UnityEngine.Object.FindObjectsOfType<BaseOven>())
            {
                var component = oven.GetComponent<FurnaceController>();
                if (component == null) continue;

                if (oven.IsOn())
                {
                    component.StopCooking();
                    oven.StartCooking();
                }

                UnityEngine.Object.Destroy(component);
            }
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var oven = entity as BaseOven;
            if (oven == null)
                return;

            EnsureController(oven);

            if (!config.ovens.ContainsKey(oven.ShortPrefabName))
            {
                config.ovens[oven.ShortPrefabName] = PluginConfig.OvenConfig.CreateDefault(false);
                SaveConfig();
            }
        }

        private void OnEntityKill(BaseNetworkable networkable)
        {
            var oven = networkable as BaseOven;
            if (oven != null)
                DestroyOvenUI(oven);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            DestroyUI(player);
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void OnFuelConsume(BaseOven oven, Item fuel, ItemModBurnable burnable)
        {
            if (IsOvenCompatible(oven))
                queuedUiUpdates.Push(oven);
        }

        private object OnOvenToggle(BaseOven oven, BasePlayer player)
        {
            if (oven == null || !IsOvenCompatible(oven))
                return null;

            var controller = EnsureController(oven);
            var canUse = CanUse(oven.OwnerID) || (player != null && CanUse(player.userID));

            if (oven.IsOn())
            {
                if (controller != null)
                    controller.StopCooking();
            }
            else
            {
                if (!canUse)
                    return null;

                if (controller != null)
                    controller.StartCooking();
            }

            queuedUiUpdates.Push(oven);
            return false;
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            var oven = entity as BaseOven;
            if (oven == null || !HasPermission(player) || !IsOvenCompatible(oven))
                return;

            AddLooter(oven, player);
            if (GetEnabled(player))
                queuedUiUpdates.Push(oven);
            else
                CreateUi(player, oven, new OvenInfo());
        }

        private void OnLootEntityEnd(BasePlayer player, BaseCombatEntity entity)
        {
            var oven = entity as BaseOven;
            if (oven == null || !IsOvenCompatible(oven))
                return;

            DestroyUI(player);
            RemoveLooter(oven, player);
        }

        private void OnTick()
        {
            while (queuedUiUpdates.Count > 0)
            {
                var oven = queuedUiUpdates.Pop();
                if (!oven || oven.IsDestroyed)
                    continue;

                var ovenInfo = GetOvenInfo(oven);
                GetLooters(oven)?.ForEach(player =>
                {
                    if (player != null && !player.IsDestroyed && HasPermission(player) && GetEnabled(player))
                        CreateUi(player, oven, ovenInfo);
                });
            }
        }

        #endregion

        #region Splitter

        private void SaveData()
        {
            if (!config.savePlayerData) return;
            Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);
        }

        private void InitPlayer(BasePlayer player)
        {
            if (!allPlayerOptions.ContainsKey(player.userID))
            {
                allPlayerOptions[player.userID] = new PlayerOptions();
            }

            var options = allPlayerOptions[player.userID];
            foreach (var kv in initialStackOptions)
            {
                if (!options.TotalStacks.ContainsKey(kv.Key))
                    options.TotalStacks.Add(kv.Key, kv.Value);
            }
        }

        private bool GetEnabled(BasePlayer player)
        {
            if (!allPlayerOptions.ContainsKey(player.userID))
                InitPlayer(player);
            return allPlayerOptions[player.userID].Enabled;
        }

        private void SetEnabled(BasePlayer player, bool enabled)
        {
            if (allPlayerOptions.ContainsKey(player.userID))
                allPlayerOptions[player.userID].Enabled = enabled;
            CreateUiIfFurnaceOpen(player);
        }

        private object CanMoveItem(Item item, PlayerInventory inventory, ItemContainerId targetContainerId, int targetSlotIndex, int splitAmount)
        {
            if (item == null || inventory == null)
                return null;

            var player = inventory.GetComponent<BasePlayer>();
            if (player == null || !HasPermission(player) || !GetEnabled(player))
                return null;

            var oven = inventory.loot?.entitySource as BaseOven;
            if (oven == null || !IsOvenCompatible(oven) || !GetSettings(oven.ShortPrefabName).allowSplitting)
                return null;

            ItemContainer targetContainer = inventory.FindContainer(targetContainerId);
            if (targetContainer != null && !(targetContainer?.entityOwner is BaseOven))
                return null;

            var container = oven.inventory;
            var originalContainer = item.GetRootContainer();
            if (container == null || originalContainer == null || originalContainer == container || originalContainer?.entityOwner is BaseOven)
                return null;

            var allowedSlots = oven.GetAllowedSlots(item);
            if (allowedSlots == null)
                return null;

            for (int i = allowedSlots.Value.Min; i <= allowedSlots.Value.Max; i++)
            {
                var slot = oven.inventory.GetSlot(i);
                if (slot != null && slot.info.shortname != item.info.shortname)
                    return null;
            }

            var cookable = item.info.GetComponent<ItemModCookable>();
            if (cookable == null || oven.IsOutputItem(item))
                return null;

            int totalSlots = oven.inputSlots;
            var playerOptions = allPlayerOptions[player.userID];
            if (playerOptions.TotalStacks.ContainsKey(oven.ShortPrefabName))
                totalSlots = playerOptions.TotalStacks[oven.ShortPrefabName];

            if (cookable.lowTemp > oven.cookingTemperature || cookable.highTemp < oven.cookingTemperature)
                return null;

            MoveResult result = MoveSplitItem(item, oven, totalSlots, splitAmount);

            if (result == MoveResult.Ok || result == MoveResult.SlotsFilled)
            {
                if (GetSettings(oven.ShortPrefabName).allowAutoFuel)
                    AutoAddFuel(inventory, oven);

                queuedUiUpdates.Push(oven);
                return true;
            }

            return null;
        }

        private MoveResult MoveSplitItem(Item item, BaseOven oven, int totalSlots, int splitAmount)
        {
            ItemContainer container = oven.inventory;
            int numOreSlots = totalSlots;
            int totalMoved = 0;
            int itemAmount = item.amount > splitAmount && splitAmount > 0 ? splitAmount : item.amount;
            int totalAmount = Math.Min(itemAmount + container.itemList.Where(slotItem => slotItem.info == item.info).Take(numOreSlots).Sum(slotItem => slotItem.amount), Math.Abs(item.info.stackable * numOreSlots));

            if (numOreSlots <= 0)
                return MoveResult.NotEnoughSlots;

            int totalStackSize = Math.Min(totalAmount / numOreSlots, item.info.stackable);
            int remaining = totalAmount - totalAmount / numOreSlots * numOreSlots;

            List<int> addedSlots = new List<int>();
            List<OvenSlot> ovenSlots = new List<OvenSlot>();

            for (int i = 0; i < numOreSlots; ++i)
            {
                Item existingItem;
                int slot = FindMatchingSlotIndex(oven, container, out existingItem, item.info, addedSlots);

                if (slot == -1)
                    return MoveResult.NotEnoughSlots;

                addedSlots.Add(slot);

                var ovenSlot = new OvenSlot
                {
                    Position = existingItem?.position,
                    Index = slot,
                    Item = existingItem
                };

                int currentAmount = existingItem?.amount ?? 0;
                int missingAmount = totalStackSize - currentAmount + (i < remaining ? 1 : 0);
                ovenSlot.DeltaAmount = missingAmount;

                if (currentAmount + missingAmount <= 0)
                    continue;

                ovenSlots.Add(ovenSlot);
            }

            foreach (OvenSlot slot in ovenSlots)
            {
                if (slot.Item == null)
                {
                    Item newItem = ItemManager.Create(item.info, slot.DeltaAmount, item.skin);
                    slot.Item = newItem;
                    newItem.MoveToContainer(container, slot.Position ?? slot.Index);
                }
                else
                {
                    slot.Item.amount += slot.DeltaAmount;
                    slot.Item.MarkDirty();
                }

                totalMoved += slot.DeltaAmount;
            }

            container.MarkDirty();

            if (totalMoved >= item.amount)
            {
                item.Remove();
                item.GetRootContainer()?.MarkDirty();
                return MoveResult.Ok;
            }
            else
            {
                item.amount -= totalMoved;
                item.GetRootContainer()?.MarkDirty();
                return MoveResult.SlotsFilled;
            }
        }

        private void AutoAddFuel(PlayerInventory playerInventory, BaseOven oven)
        {
            int neededFuel = (int)Math.Ceiling(GetOvenInfo(oven).FuelNeeded);
            neededFuel -= oven.inventory.GetAmount(oven.fuelType.itemid, false);

            List<Item> playerFuel = Facepunch.Pool.Get<List<Item>>();
            try
            {
                playerInventory.FindItemsByItemID(playerFuel, oven.fuelType.itemid);
                int fuelSlotIndex = 0;

                if (neededFuel <= 0 || playerFuel.Count <= 0)
                    return;

                foreach (Item fuelItem in playerFuel)
                {
                    var existingFuel = oven.inventory.GetSlot(fuelSlotIndex);
                    if (existingFuel != null && existingFuel.amount >= existingFuel.info.stackable)
                    {
                        if (fuelSlotIndex < oven.fuelSlots)
                            fuelSlotIndex++;
                        else
                            break;
                    }

                    Item largestFuelStack = oven.inventory.itemList.Where(x => x.info == oven.fuelType).OrderByDescending(x => x.amount).FirstOrDefault();
                    int toTake = Math.Min(neededFuel, (oven.fuelType.stackable * oven.fuelSlots) - (largestFuelStack?.amount ?? 0));

                    if (toTake > fuelItem.amount)
                        toTake = fuelItem.amount;

                    if (toTake <= 0)
                        break;

                    neededFuel -= toTake;

                    int currentFuelAmount = oven.inventory.GetAmount(oven.fuelType.itemid, false);
                    if (currentFuelAmount >= oven.fuelType.stackable * oven.fuelSlots)
                        break;

                    if (toTake >= fuelItem.amount)
                    {
                        fuelItem.MoveToContainer(oven.inventory, fuelSlotIndex);
                    }
                    else
                    {
                        Item splitItem = fuelItem.SplitItem(toTake);
                        if (!splitItem.MoveToContainer(oven.inventory, fuelSlotIndex))
                            break;
                    }

                    if (neededFuel <= 0)
                        break;
                }
            }
            finally
            {
                Facepunch.Pool.Free(ref playerFuel);
            }
        }

        private int FindMatchingSlotIndex(BaseOven oven, ItemContainer container, out Item existingItem, ItemDefinition itemType, List<int> indexBlacklist)
        {
            existingItem = null;
            int firstIndex = -1;
            int inputSlotsMin = oven._inputSlotIndex;
            int inputSlotsMax = oven._inputSlotIndex + oven.inputSlots;
            Dictionary<int, Item> existingItems = new Dictionary<int, Item>();

            for (int i = inputSlotsMin; i < inputSlotsMax; ++i)
            {
                if (indexBlacklist.Contains(i))
                    continue;

                Item itemSlot = container.GetSlot(i);
                if (itemSlot == null || itemType != null && itemSlot.info == itemType)
                {
                    if (itemSlot != null)
                        existingItems.Add(i, itemSlot);

                    if (firstIndex == -1)
                    {
                        existingItem = itemSlot;
                        firstIndex = i;
                    }
                }
            }

            if (existingItems.Count <= 0 && firstIndex != -1)
                return firstIndex;
            else if (existingItems.Count > 0)
            {
                var largestStackItem = existingItems.OrderByDescending(kv => kv.Value.amount).First();
                existingItem = largestStackItem.Value;
                return existingItem.position;
            }

            existingItem = null;
            return -1;
        }

        #endregion

        #region UI

        private List<BasePlayer> GetLooters(BaseOven oven)
        {
            if (looters.ContainsKey(oven))
                return looters[oven];
            return null;
        }

        private void AddLooter(BaseOven oven, BasePlayer player)
        {
            if (!looters.ContainsKey(oven))
                looters[oven] = new List<BasePlayer>();

            if (!looters[oven].Contains(player))
                looters[oven].Add(player);
        }

        private void RemoveLooter(BaseOven oven, BasePlayer player)
        {
            if (!looters.ContainsKey(oven))
                return;
            looters[oven].Remove(player);
        }

        private void CreateUiIfFurnaceOpen(BasePlayer player)
        {
            BaseOven oven = player.inventory.loot?.entitySource as BaseOven;
            if (oven != null && IsOvenCompatible(oven))
                queuedUiUpdates.Push(oven);
        }

        private CuiElementContainer CreateUi(BasePlayer player, BaseOven oven, OvenInfo ovenInfo)
        {
            PlayerOptions options = allPlayerOptions[player.userID];
            int totalSlots = GetTotalStacksOption(player, oven) ?? oven.inputSlots;
            string remainingTimeStr;
            string neededFuelStr;

            if (ovenInfo.ETA <= 0)
            {
                remainingTimeStr = "0s";
                neededFuelStr = "0";
            }
            else
            {
                remainingTimeStr = FormatTime(ovenInfo.ETA);
                neededFuelStr = ovenInfo.FuelNeeded.ToString("##,###");
            }

            float uiScale = 1.0f;
            float[] playerUiInfo = UIScaleManager?.Call<float[]>("API_CheckPlayerUIInfo", player.UserIDString);
            if (playerUiInfo?.Length > 0)
                uiScale = playerUiInfo[2];

            string contentColor = "0.7 0.7 0.7 1.0";
            int contentSize = Convert.ToInt32(10 * uiScale);
            string toggleStateStr = (!options.Enabled).ToString();
            string toggleButtonColor = !options.Enabled ? "0.415 0.5 0.258 0.4" : "0.8 0.254 0.254 0.4";
            string toggleButtonTextColor = !options.Enabled ? "0.607 0.705 0.431" : "0.705 0.607 0.431";
            string buttonColor = "0.75 0.75 0.75 0.1";
            string buttonTextColor = "0.77 0.68 0.68 1";

            int nextDecrementSlot = totalSlots - 1;
            int nextIncrementSlot = totalSlots + 1;

            DestroyUI(player);

            Vector2 uiPosition = new Vector2(
                ((((config.UiPosition.x) - 0.5f) * uiScale) + 0.5f),
                (config.UiPosition.y - 0.02f) + 0.02f * uiScale);
            Vector2 uiSize = new Vector2(0.1785f * uiScale, 0.111f * uiScale);

            CuiElementContainer result = new CuiElementContainer();
            string rootPanelName = result.Add(new CuiPanel
            {
                Image = new CuiImageComponent { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = uiPosition.x + " " + uiPosition.y, AnchorMax = uiPosition.x + uiSize.x + " " + (uiPosition.y + uiSize.y) }
            }, "Hud.Menu");

            string headerPanel = result.Add(new CuiPanel
            {
                Image = new CuiImageComponent { Color = "0.75 0.75 0.75 0.1" },
                RectTransform = { AnchorMin = "0 0.775", AnchorMax = "1 1" }
            }, rootPanelName);

            result.Add(new CuiLabel
            {
                RectTransform = { AnchorMin = "0.051 0", AnchorMax = "1 0.95" },
                Text = { Text = lang.GetMessage("title", this, player.UserIDString), Align = TextAnchor.MiddleLeft, Color = "0.77 0.7 0.7 1", FontSize = Convert.ToInt32(13 * uiScale) }
            }, headerPanel);

            string contentPanel = result.Add(new CuiPanel
            {
                Image = new CuiImageComponent { Color = "0.65 0.65 0.65 0.06" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.74" }
            }, rootPanelName);

            result.Add(new CuiLabel
            {
                RectTransform = { AnchorMin = "0.02 0.7", AnchorMax = "0.98 1" },
                Text = { Text = string.Format("{0}: " + (ovenInfo.ETA > 0 ? "~" : "") + remainingTimeStr + " (" + neededFuelStr + " " + oven.fuelType.displayName.english.ToLower() + ")", lang.GetMessage("eta", this, player.UserIDString)), Align = TextAnchor.MiddleLeft, Color = contentColor, FontSize = contentSize }
            }, contentPanel);

            result.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0.02 0.4", AnchorMax = "0.25 0.7" },
                Button = { Command = "furnacemanager.enabled " + toggleStateStr, Color = toggleButtonColor },
                Text = { Align = TextAnchor.MiddleCenter, Text = options.Enabled ? lang.GetMessage("turnoff", this, player.UserIDString) : lang.GetMessage("turnon", this, player.UserIDString), Color = toggleButtonTextColor, FontSize = Convert.ToInt32(11 * uiScale) }
            }, contentPanel);

            result.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0.27 0.4", AnchorMax = "0.52 0.7" },
                Button = { Command = "furnacemanager.trim", Color = buttonColor },
                Text = { Align = TextAnchor.MiddleCenter, Text = lang.GetMessage("trim", this, player.UserIDString), Color = contentColor, FontSize = Convert.ToInt32(11 * uiScale) }
            }, contentPanel);

            result.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0.02 0.05", AnchorMax = "0.07 0.35" },
                Button = { Command = "furnacemanager.totalstacks " + nextDecrementSlot, Color = buttonColor },
                Text = { Align = TextAnchor.MiddleCenter, Text = "<", Color = buttonTextColor, FontSize = contentSize }
            }, contentPanel);

            result.Add(new CuiLabel
            {
                RectTransform = { AnchorMin = "0.08 0.05", AnchorMax = "0.19 0.35" },
                Text = { Align = TextAnchor.MiddleCenter, Text = totalSlots.ToString(), Color = contentColor, FontSize = contentSize }
            }, contentPanel);

            result.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0.19 0.05", AnchorMax = "0.25 0.35" },
                Button = { Command = "furnacemanager.totalstacks " + nextIncrementSlot, Color = buttonColor },
                Text = { Align = TextAnchor.MiddleCenter, Text = ">", Color = buttonTextColor, FontSize = contentSize }
            }, contentPanel);

            result.Add(new CuiLabel
            {
                RectTransform = { AnchorMin = "0.27 0.05", AnchorMax = "1 0.35" },
                Text = { Align = TextAnchor.MiddleLeft, Text = string.Format("({0})", lang.GetMessage("totalstacks", this, player.UserIDString)), Color = contentColor, FontSize = contentSize }
            }, contentPanel);

            openUis[player.userID] = rootPanelName;
            CuiHelper.AddUi(player, result);
            return result;
        }

        private string FormatTime(float totalSeconds)
        {
            int hours = (int)Math.Floor(totalSeconds / 3600);
            int minutes = (int)Math.Floor(totalSeconds / 60 % 60);
            int seconds = (int)Math.Floor(totalSeconds % 60);

            if (hours <= 0 && minutes <= 0)
                return seconds + "s";
            if (hours <= 0)
                return minutes + "m" + seconds + "s";
            return hours + "h" + minutes + "m" + seconds + "s";
        }

        private int? GetTotalStacksOption(BasePlayer player, BaseOven oven)
        {
            PlayerOptions options = allPlayerOptions[player.userID];
            if (options.TotalStacks.ContainsKey(oven.ShortPrefabName))
                return options.TotalStacks[oven.ShortPrefabName];
            return null;
        }

        private void DestroyUI(BasePlayer player)
        {
            if (!openUis.ContainsKey(player.userID))
                return;

            string uiName = openUis[player.userID];
            if (openUis.Remove(player.userID))
                CuiHelper.DestroyUi(player, uiName);
        }

        private void DestroyOvenUI(BaseOven oven)
        {
            if (oven == null) throw new ArgumentNullException(nameof(oven));

            foreach (KeyValuePair<ulong, string> kv in openUis.ToDictionary(kv => kv.Key, kv => kv.Value))
            {
                BasePlayer player = BasePlayer.FindByID(kv.Key);
                BaseOven playerLootOven = player?.inventory.loot?.entitySource as BaseOven;

                if (playerLootOven != null && oven == playerLootOven)
                {
                    DestroyUI(player);
                    RemoveLooter(oven, player);
                }
            }
        }

        #endregion

        #region Commands

        [ChatCommand("fm")]
        void cmdToggle(BasePlayer player, string cmd, string[] args)
        {
            if (!HasPermission(player))
            {
                player.ConsoleMessage(lang.GetMessage("nopermission", this, player.UserIDString));
                return;
            }

            var statuson = lang.GetMessage("StatusONColor", this, player.UserIDString);
            var statusoff = lang.GetMessage("StatusOFFColor", this, player.UserIDString);
            string status = GetEnabled(player) ? statuson : statusoff;

            if (args.Length == 0)
            {
                var helpmsg = new StringBuilder();
                helpmsg.Append("<size=22><color=green>FurnaceManager</color></size> by: Milestorme\n");
                helpmsg.Append(lang.GetMessage("StatusMessage", this, player.UserIDString) + status + "\n");
                helpmsg.Append("<color=orange>/fm on</color> - Toggle on\n");
                helpmsg.Append("<color=orange>/fm off</color> - Toggle off\n");
                player.ChatMessage(helpmsg.ToString().TrimEnd());
                return;
            }

            switch (args[0].ToLower())
            {
                case "on":
                    SetEnabled(player, true);
                    CreateUiIfFurnaceOpen(player);
                    player.ChatMessage(lang.GetMessage("StatusMessage", this, player.UserIDString) + lang.GetMessage("StatusONColor", this, player.UserIDString));
                    break;
                case "off":
                    SetEnabled(player, false);
                    DestroyUI(player);
                    player.ChatMessage(lang.GetMessage("StatusMessage", this, player.UserIDString) + lang.GetMessage("StatusOFFColor", this, player.UserIDString));
                    break;
                default:
                    player.ChatMessage("Invalid syntax!");
                    break;
            }
        }

        [ConsoleCommand("furnacemanager.enabled")]
        private void ConsoleCommand_Toggle(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasPermission(player))
                return;

            if (!arg.HasArgs())
            {
                player.ConsoleMessage(GetEnabled(player).ToString());
                return;
            }

            bool enabled = arg.GetBool(0);
            SetEnabled(player, enabled);
            if (enabled)
                CreateUiIfFurnaceOpen(player);
            else
            {
                BaseOven oven = player.inventory.loot?.entitySource as BaseOven;
                CreateUi(player, oven, new OvenInfo());
            }
        }

        [ConsoleCommand("furnacemanager.totalstacks")]
        private void ConsoleCommand_TotalStacks(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !HasPermission(player))
                return;

            BaseOven lootSource = player.inventory.loot?.entitySource as BaseOven;
            if (lootSource == null || !IsOvenCompatible(lootSource))
            {
                player.ConsoleMessage(lang.GetMessage("lootsource_invalid", this, player.UserIDString));
                return;
            }

            if (!GetEnabled(player))
                return;

            string ovenName = lootSource.ShortPrefabName;
            PlayerOptions playerOption = allPlayerOptions[player.userID];

            if (playerOption.TotalStacks.ContainsKey(ovenName))
            {
                if (!arg.HasArgs())
                    player.ConsoleMessage(playerOption.TotalStacks[ovenName].ToString());
                else
                    playerOption.TotalStacks[ovenName] = (int)Mathf.Clamp(arg.GetInt(0), 1, lootSource.inputSlots);
            }
            else
            {
                player.ConsoleMessage(lang.GetMessage("unsupported_furnace", this, player.UserIDString));
            }

            CreateUiIfFurnaceOpen(player);
        }

        [ConsoleCommand("furnacemanager.trim")]
        private void ConsoleCommand_Trim(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !GetEnabled(player) || !HasPermission(player))
                return;

            BaseOven lootSource = player.inventory.loot?.entitySource as BaseOven;
            if (lootSource == null || !IsOvenCompatible(lootSource))
            {
                player.ConsoleMessage(lang.GetMessage("lootsource_invalid", this, player.UserIDString));
                return;
            }

            if (!GetSettings(lootSource.ShortPrefabName).allowTrimFuel)
                return;

            OvenInfo ovenInfo = GetOvenInfo(lootSource);
            var fuelSlots = lootSource.inventory.itemList.Where(item => item.info == lootSource.fuelType).ToList();
            int totalFuel = fuelSlots.Sum(item => item.amount);
            int toRemove = (int)Math.Floor(totalFuel - ovenInfo.FuelNeeded);

            if (toRemove <= 0)
                return;

            foreach (Item fuelItem in fuelSlots)
            {
                int toTake = Math.Min(fuelItem.amount, toRemove);
                toRemove -= toTake;

                Vector3 dropPosition = player.GetDropPosition();
                Vector3 dropVelocity = player.GetDropVelocity();

                if (toTake >= fuelItem.amount)
                {
                    if (!player.inventory.GiveItem(fuelItem))
                        fuelItem.Drop(dropPosition, dropVelocity, Quaternion.identity);
                }
                else
                {
                    Item splitItem = fuelItem.SplitItem(toTake);
                    if (!player.inventory.GiveItem(splitItem))
                        splitItem.Drop(dropPosition, dropVelocity, Quaternion.identity);
                }

                if (toRemove <= 0)
                    break;
            }

            queuedUiUpdates.Push(lootSource);
        }

        #endregion

        #region Accurate ETA / fuel

        public OvenInfo GetOvenInfo(BaseOven oven)
        {
            var cfg = GetSettings(oven.ShortPrefabName);
            float interval = GetCookInterval(oven);
            int frequency = Mathf.Max(1, cfg.smeltingFrequency);

            float eta = 0f;
            int amountPerEvent = Mathf.Max(1, (int)oven.GetSmeltingSpeed());

            for (int i = oven._inputSlotIndex; i < oven._inputSlotIndex + oven.inputSlots; i++)
            {
                var inputItem = oven.inventory.GetSlot(i);
                if (inputItem == null) continue;

                var cookable = inputItem.info.GetComponent<ItemModCookable>();
                if (cookable == null) continue;

                if (!CanCook(cookable, oven))
                    continue;

                int smeltEvents = Mathf.CeilToInt(inputItem.amount / (float)amountPerEvent);
                float slotEta = smeltEvents * Mathf.Max(1f, cookable.cookTime) * frequency * interval;
                if (slotEta > eta)
                    eta = slotEta;
            }

            var burnable = oven.fuelType?.GetComponent<ItemModBurnable>();
            float fuelNeeded = 0f;
            if (burnable != null && eta > 0f)
            {
                float tempFactor = oven.cookingTemperature / 200f;
                float fuelDrainPerTick = 0.5f * tempFactor * cfg.fuelUsageSpeedMultiplier;
                float drainPerSecond = fuelDrainPerTick / interval;
                float fuelSecondsPerUnit = burnable.fuelAmount / Mathf.Max(0.0001f, drainPerSecond);
                fuelNeeded = Mathf.Ceil((eta / fuelSecondsPerUnit) * Mathf.Max(1, cfg.fuelUsageMultiplier) * cfg.fuelMultiplier);
            }

            return new OvenInfo
            {
                ETA = eta,
                FuelNeeded = fuelNeeded
            };
        }

        private bool CanCook(ItemModCookable cookable, BaseOven oven)
        {
            return oven.cookingTemperature >= cookable.lowTemp && oven.cookingTemperature <= cookable.highTemp;
        }

        private float GetCookInterval(BaseOven oven)
        {
            var cfg = GetSettings(oven.ShortPrefabName);
            return 0.5f / Mathf.Max(0.01f, cfg.speedMultiplier);
        }

        #endregion

        #region Controller

        private FurnaceController EnsureController(BaseOven oven)
        {
            if (oven == null) return null;
            var controller = oven.GetComponent<FurnaceController>();
            if (controller == null)
                controller = oven.gameObject.AddComponent<FurnaceController>();
            controller.Initialize(this, oven);
            return controller;
        }

        private bool CanUse(ulong id) => !config.UsePermission || permission.UserHasPermission(id.ToString(), permUse);
        private bool HasPermission(BasePlayer player) => player != null && (!config.UsePermission || permission.UserHasPermission(player.UserIDString, permUse));

        private PluginConfig.OvenConfig GetSettings(string shortname)
        {
            PluginConfig.OvenConfig cfg;
            if (config.ovens.TryGetValue(shortname, out cfg) && cfg.enabled)
                return cfg;

            if (config.ovens.TryGetValue("*", out cfg))
                return cfg;

            return PluginConfig.OvenConfig.CreateDefault(true);
        }

        private bool IsOvenCompatible(BaseOven oven)
        {
            if (oven == null || !oven.allowByproductCreation)
                return false;

            return GetSettings(oven.ShortPrefabName).enabled;
        }

        public class FurnaceController : FacepunchBehaviour
        {
            private FurnaceManager plugin;
            private BaseOven oven;
            private int ticks;
            private PluginConfig.OvenConfig cfg;
            private Dictionary<string, float> outputModifiers;
            private List<string> whitelist;
            private List<string> blacklist;

            public void Initialize(FurnaceManager pluginInstance, BaseOven targetOven)
            {
                plugin = pluginInstance;
                oven = targetOven;
                cfg = plugin.GetSettings(oven.ShortPrefabName);
                outputModifiers = cfg.outputMultipliers ?? new Dictionary<string, float> { { "global", 1f } };
                whitelist = cfg.whitelist ?? new List<string>();
                blacklist = cfg.blacklist ?? new List<string>();
            }

            private float OutputMultiplier(string shortname)
            {
                float modifier;
                if (outputModifiers == null || (!outputModifiers.TryGetValue(shortname, out modifier) && !outputModifiers.TryGetValue("global", out modifier)))
                    modifier = 1f;
                return modifier;
            }

            private bool? IsAllowed(string shortname)
            {
                if (blacklist != null && blacklist.Contains(shortname))
                    return false;
                if (whitelist != null && whitelist.Count > 0)
                    return whitelist.Contains(shortname);
                return null;
            }

            private Item FindBurnable()
            {
                if (oven?.inventory == null)
                    return null;

                var burnable = Interface.Call<Item>("OnFindBurnable", oven);
                if (burnable != null)
                    return burnable;

                foreach (var item in oven.inventory.itemList)
                {
                    if (!oven.IsBurnableItem(item))
                        continue;
                    return item;
                }

                return null;
            }

            public void StartCooking()
            {
                if (oven == null || !plugin.IsOvenCompatible(oven))
                    return;

                if (FindBurnable() == null)
                    return;

                StopCooking();
                cfg = plugin.GetSettings(oven.ShortPrefabName);
                outputModifiers = cfg.outputMultipliers ?? new Dictionary<string, float> { { "global", 1f } };
                whitelist = cfg.whitelist ?? new List<string>();
                blacklist = cfg.blacklist ?? new List<string>();

                oven.inventory.temperature = oven.cookingTemperature;
                oven.UpdateAttachmentTemperature();
                float interval = 0.5f / Mathf.Max(0.01f, cfg.speedMultiplier);
                oven.InvokeRepeating(Cook, interval, interval);
                oven.SetFlag(BaseEntity.Flags.On, true);
            }

            public void StopCooking()
            {
                if (oven == null) return;
                oven.CancelInvoke(Cook);
                oven.StopCooking();
            }

            public void Cook()
            {
                var burnableItem = FindBurnable();
                if (Interface.CallHook("OnOvenCook", this, burnableItem) != null)
                    return;

                if (burnableItem == null)
                {
                    StopCooking();
                    return;
                }

                SmeltItems();

                foreach (var itemCooking in oven.inventory.itemList)
                {
                    if (itemCooking.position >= oven._inputSlotIndex &&
                        itemCooking.position < oven._inputSlotIndex + oven.inputSlots &&
                        !itemCooking.HasFlag(global::Item.Flag.Cooking))
                    {
                        itemCooking.SetFlag(global::Item.Flag.Cooking, true);
                        itemCooking.MarkDirty();
                    }
                }

                var slot = oven.GetSlot(BaseEntity.Slot.FireMod);
                if (slot)
                    slot.SendMessage("Cook", 0.5f, SendMessageOptions.DontRequireReceiver);

                var burnable = burnableItem.info.GetComponent<ItemModBurnable>();
                burnableItem.fuel -= 0.5f * (oven.cookingTemperature / 200f) * cfg.fuelUsageSpeedMultiplier;

                if (!burnableItem.HasFlag(global::Item.Flag.OnFire))
                {
                    burnableItem.SetFlag(global::Item.Flag.OnFire, true);
                    burnableItem.MarkDirty();
                }

                if (burnableItem.fuel <= 0f)
                    ConsumeFuel(burnableItem, burnable);

                ticks++;
                plugin.queuedUiUpdates.Push(oven);
                Interface.CallHook("OnOvenCooked", this, burnableItem, slot);
            }

            private void ConsumeFuel(Item fuel, ItemModBurnable burnable)
            {
                if (Interface.CallHook("OnFuelConsume", oven, fuel, burnable) != null)
                    return;

                if (oven.allowByproductCreation && burnable.byproductItem != null && Random.Range(0f, 1f) > burnable.byproductChance)
                {
                    var def = burnable.byproductItem;
                    var item = ItemManager.Create(def, (int)(burnable.byproductAmount * OutputMultiplier(def.shortname)));
                    if (!item.MoveToContainer(oven.inventory))
                    {
                        StopCooking();
                        item.Drop(oven.inventory.dropPosition, oven.inventory.dropVelocity);
                    }
                }

                int consumeAmount = Mathf.Max(1, cfg.fuelUsageMultiplier);
                if (fuel.amount <= consumeAmount)
                {
                    fuel.Remove();
                    return;
                }

                fuel.UseItem(consumeAmount);
                fuel.fuel = burnable.fuelAmount;
                fuel.MarkDirty();
                Interface.CallHook("OnFuelConsumed", oven, fuel, burnable);
            }

            private void SmeltItems()
            {
                if (ticks % Mathf.Max(1, cfg.smeltingFrequency) != 0)
                    return;

                for (var i = 0; i < oven.inventory.itemList.Count; i++)
                {
                    var item = oven.inventory.itemList[i];
                    if (item == null || !item.IsValid())
                        continue;

                    var cookable = item.info.GetComponent<ItemModCookable>();
                    if (cookable == null)
                        continue;

                    var allowed = IsAllowed(item.info.shortname);
                    if (allowed != null && !allowed.Value)
                        continue;

                    var temperature = item.temperature;
                    if (!cookable.CanBeCookedByAtTemperature(temperature) && allowed == null)
                    {
                        if (!cookable.setCookingFlag || !item.HasFlag(global::Item.Flag.Cooking))
                            continue;

                        item.SetFlag(global::Item.Flag.Cooking, false);
                        item.MarkDirty();
                        continue;
                    }

                    if (cookable.cookTime > 0 && ticks * 1f / Mathf.Max(1, cfg.smeltingFrequency) % cookable.cookTime > 0)
                        continue;

                    if (cookable.setCookingFlag && !item.HasFlag(global::Item.Flag.Cooking))
                    {
                        item.SetFlag(global::Item.Flag.Cooking, true);
                        item.MarkDirty();
                    }

                    int amountConsumed = Math.Max(1, (int)oven.GetSmeltingSpeed());
                    amountConsumed = Math.Min(amountConsumed, item.amount);

                    if (item.amount > amountConsumed)
                    {
                        item.amount -= amountConsumed;
                        item.MarkDirty();
                    }
                    else
                    {
                        item.Remove();
                    }

                    if (cookable.becomeOnCooked == null)
                        continue;

                    var itemProduced = ItemManager.Create(cookable.becomeOnCooked,
                        (int)(cookable.amountOfBecome * amountConsumed * OutputMultiplier(cookable.becomeOnCooked.shortname) * cfg.outputMultiplier));

                    if (itemProduced == null || itemProduced.MoveToContainer(item.parent))
                        continue;

                    itemProduced.Drop(item.parent.dropPosition, item.parent.dropVelocity);
                    StopCooking();
                }
            }
        }

        #endregion

        #region Configuration

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string> {
                { "turnon", "Turn On" },
                { "turnoff", "Turn Off" },
                { "title", "Furnace Manager" },
                { "eta", "ETA" },
                { "totalstacks", "Total stacks" },
                { "trim", "Trim fuel" },
                { "lootsource_invalid", "Current loot source invalid" },
                { "unsupported_furnace", "Unsupported furnace." },
                { "nopermission", "You don't have permission to use this." },
                { "StatusONColor", "<color=green>ON</color>"},
                { "StatusOFFColor", "<color=red>OFF</color>"},
                { "StatusMessage", "FurnaceManager status set to: "}
            }, this);
        }

        protected override void LoadDefaultConfig()
        {
            config = PluginConfig.CreateDefault();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Config.Settings.Converters.Add(new Vector2Converter());
            config = Config.ReadObject<PluginConfig>();
            if (!config.ovens.ContainsKey("*"))
                config.ovens["*"] = PluginConfig.OvenConfig.CreateDefault(true);
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        private class PluginConfig
        {
            public Vector2 UiPosition = new Vector2(0.6505f, 0.022f);
            public bool savePlayerData = true;
            public bool UsePermission = true;
            public SortedDictionary<string, OvenConfig> ovens = new SortedDictionary<string, OvenConfig>();

            public static PluginConfig CreateDefault()
            {
                return new PluginConfig
                {
                    ovens = new SortedDictionary<string, OvenConfig>
                    {
                        { "*", OvenConfig.CreateDefault(true) }
                    }
                };
            }

            public class OvenConfig
            {
                public bool enabled = true;
                public bool allowSplitting = true;
                public bool allowAutoFuel = true;
                public bool allowTrimFuel = true;
                public float fuelMultiplier = 1.0f;
                public float speedMultiplier = 5.0f;
                public float fuelUsageSpeedMultiplier = 1.0f;
                public int fuelUsageMultiplier = 1;
                public float outputMultiplier = 1.0f;
                public int smeltingFrequency = 1;
                public Dictionary<string, float> outputMultipliers = new Dictionary<string, float> { { "global", 1.0f } };
                public List<string> whitelist = new List<string>();
                public List<string> blacklist = new List<string>();

                public static OvenConfig CreateDefault(bool enabled)
                {
                    return new OvenConfig { enabled = enabled };
                }
            }
        }

        private class Vector2Converter : JsonConverter
        {
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                Vector2 vec = (Vector2)value;
                serializer.Serialize(writer, new { vec.x, vec.y });
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                Vector2 result = new Vector2();
                JObject jVec = JObject.Load(reader);
                result.x = jVec["x"].ToObject<float>();
                result.y = jVec["y"].ToObject<float>();
                return result;
            }

            public override bool CanConvert(Type objectType) => objectType == typeof(Vector2);
        }

        #endregion
    }
}
