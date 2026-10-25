using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BotBuyPatch;

// Human teammates steer bot buying with the chat wheel's "We should save" / "We should buy".
// Save = no further buys and refund this round's guns; utility and armor already bought are kept, like a half-save.
// Buy = bots without a primary that cannot full-buy get an SMG or rifle plus armor, if still in the buy zone.
// A call during buy time applies to the current round; a later call applies to the next round.
public sealed partial class BotBuyPatch
{
    private enum TeamCall { None, Save, Buy }

    private const string SaveToken = "#Chatwheel_requestecoround";
    private const string BuyToken = "#Chatwheel_requestspend";

    // Worst-case income after a lost round (CS2 loss bonus starts at $1400).
    private const int MinLossIncome = 1400;

    private readonly Dictionary<CsTeam, TeamCall> _activeCall = new();
    private readonly Dictionary<CsTeam, TeamCall> _pendingCall = new();
    // Every bot gun purchase is recorded (one inventory walk each, about 40 a round), so a save made after
    // a hot load or a mid-round join still finds this round's guns.
    private readonly PurchaseLedger _ledger = new();

    // CS2 can name variants after their base gun (player_hurt calls the M4A1-S "m4a1"); match either name.
    private static readonly Dictionary<string, string> BaseClass = new() { ["weapon_m4a1_silencer"] = "weapon_m4a1",
        ["weapon_usp_silencer"] = "weapon_hkp2000", ["weapon_cz75a"] = "weapon_p250", ["weapon_revolver"] = "weapon_deagle", ["weapon_mp5sd"] = "weapon_mp7" };

    public override void Load(bool hotReload)
    {
        AddCommandListener("playerchatwheel", OnChatWheel);
    }

    private HookResult OnChatWheel(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;
        if (player.Team != CsTeam.Terrorist && player.Team != CsTeam.CounterTerrorist) return HookResult.Continue;

        string args = info.ArgString;
        TeamCall call = args.Contains(SaveToken) ? TeamCall.Save
                      : args.Contains(BuyToken) ? TeamCall.Buy
                      : TeamCall.None;
        if (call == TeamCall.None) return HookResult.Continue;

        var rules = GameRules();
        if (rules == null || rules.WarmupPeriod || BotLoadoutForced()) return HookResult.Continue;

        if (rules.BuyTimeEnded)
        {
            _pendingCall[player.Team] = call;
            Announce(player.Team, call == TeamCall.Save ? "saving next round" : "buying next round");
        }
        else
        {
            ApplyCall(player.Team, call, rules.FreezePeriod);
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundStartApplyCalls(EventRoundStart @event, GameEventInfo info)
    {
        _activeCall.Clear();
        _ledger.NewRound();

        // Teams swap sides at halftime, so a call from the last round of a half is stale.
        if (IsFirstRoundOfHalf()) _pendingCall.Clear();

        var pending = _pendingCall.ToList();
        _pendingCall.Clear();
        if (BotLoadoutForced()) return HookResult.Continue;
        // At round_start bots have just spawned, inside the buy zone.
        foreach (var (team, call) in pending) ApplyCall(team, call, atSpawn: true);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        var bot = @event.Userid;
        if (bot == null || !bot.IsValid || !bot.IsBot) return HookResult.Continue;

        var purchase = RecordPurchase(bot, @event.Weapon, paid: null);
        // Native bot buying runs on its own schedule; undo its guns for bots that should be saving.
        // Deferred a frame so the game has finished the purchase; the ledger ignores it if the round changed.
        if (purchase is { } p && IsSaving(bot)) Server.NextFrame(() => RefundGuns(bot, p));
        return HookResult.Continue;
    }

    private void ApplyCall(CsTeam team, TeamCall call, bool atSpawn)
    {
        _activeCall[team] = call;
        var bots = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.Team == team && p.PawnIsAlive).ToList();

        if (call == TeamCall.Save)
        {
            int keeping = 0;
            foreach (var bot in bots)
            {
                if (!IsSaving(bot)) { keeping++; continue; }
                RefundGuns(bot, only: null);
            }
            Announce(team, keeping == 0 ? "saving" : $"saving ({keeping} can still full-buy next round and keep their buy)");
        }
        else
        {
            // Bots cannot leave spawn, a buy zone, during freeze time. After it, skip bots that have left the zone.
            foreach (var bot in bots)
                if (atSpawn || bot.PlayerPawn.Value?.InBuyZone == true) ForceBuy(bot);
            Announce(team, "buying");
        }
    }

    // A bot ignores a save when it could still afford a full buy next round after losing this one.
    private bool IsSaving(CCSPlayerController bot)
    {
        if (!bot.IsBot || _activeCall.GetValueOrDefault(bot.Team) != TeamCall.Save) return false;
        int money = bot.InGameMoneyServices?.Account ?? 0;
        return money + MinLossIncome < FullBuyCost(bot.Team);
    }

    private bool IsSavingTeam(CsTeam team) => _activeCall.GetValueOrDefault(team) == TeamCall.Save;

    // Cheapest rifle plus kevlar and helmet.
    private static int FullBuyCost(CsTeam team) => team == CsTeam.Terrorist ? 2700 + 1000 : 2900 + 1000;

    // Bots that cannot full-buy and hold no primary pick up the best rifle or SMG they can afford, then armor.
    // Bots that can full-buy are left to native buying, which picks their preferred rifle.
    private void ForceBuy(CCSPlayerController bot)
    {
        int money = bot.InGameMoneyServices?.Account ?? 0;
        if (money >= FullBuyCost(bot.Team) || OwnsPrimary(bot)) return;

        bool t = bot.Team == CsTeam.Terrorist;
        if (money >= (t ? 1800 : 1950) + 650) Buy(bot, t ? "weapon_galilar" : "weapon_famas");
        else if (money >= (t ? 1050 : 1250) + 650) Buy(bot, t ? "weapon_mac10" : "weapon_mp9");

        if ((bot.PlayerPawn.Value?.ArmorValue ?? 0) == 0)
            Buy(bot, (bot.InGameMoneyServices?.Account ?? 0) >= 1000 ? "item_assaultsuit" : "item_kevlar");
    }

    // Records a gun (primary or pistol) by entity handle; paid is null for native buys, which cost the VData price.
    private PurchaseLedger.Purchase? RecordPurchase(CCSPlayerController bot, string item, int? paid)
    {
        if (item.StartsWith("item_", StringComparison.Ordinal)) return null;
        if (!item.StartsWith("weapon_", StringComparison.Ordinal)) item = "weapon_" + item;
        string cls = BaseClass.GetValueOrDefault(item, item);
        foreach (var h in bot.PlayerPawn.Value?.WeaponServices?.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
        {
            var w = h.Value;
            if (w == null || !w.IsValid || (w.DesignerName != item && w.DesignerName != cls)) continue;
            var vdata = w.As<CCSWeaponBase>().VData;
            if (vdata == null || vdata.GearSlot is not (gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL)) return null;
            int price = paid ?? vdata.Price;
            return price > 0 ? _ledger.Add(bot.Slot, h.Raw, price) : null;
        }
        Logger.LogWarning("BotBuy: {Bot} bought {Item} but does not hold it; a save call cannot refund it", bot.PlayerName, item);
        return null;
    }

    // Refunds the bot's guns from this round (or only the one given) that it still holds, at the price it paid.
    private void RefundGuns(CCSPlayerController bot, PurchaseLedger.Purchase? only)
    {
        var weapons = bot.IsValid ? bot.PlayerPawn.Value?.WeaponServices?.MyWeapons : null;
        var money = bot.InGameMoneyServices;
        if (weapons == null || money == null) return;

        int credit = _ledger.Undo(bot.Slot, only, handle => KillIfHeld(weapons, handle));
        if (credit == 0) return;
        money.Account = Math.Min(money.Account + credit, ConVar.Find("mp_maxmoney")?.GetPrimitiveValue<int>() ?? 16000);
        Utilities.SetStateChanged(bot, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    // The same delayed kill RemoveItemByDesignerName uses, but for this exact entity.
    private static bool KillIfHeld(NetworkedVector<CHandle<CBasePlayerWeapon>> weapons, uint handle)
    {
        foreach (var h in weapons)
        {
            if (h.Raw != handle || h.Value is not { IsValid: true } w) continue;
            w.AddEntityIOEvent("Kill", w, delay: 0.1f);
            return true;
        }
        return false;
    }

    private static bool OwnsPrimary(CCSPlayerController bot) =>
        bot.PlayerPawn.Value?.WeaponServices?.MyWeapons.Any(w =>
            w.Value?.As<CCSWeaponBase>().VData?.GearSlot == gear_slot_t.GEAR_SLOT_RIFLE) ?? false;

    private static CCSGameRules? GameRules() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;

    private static bool BotLoadoutForced() => !string.IsNullOrEmpty(ConVar.Find("bot_loadout")?.StringValue);

    private static void Announce(CsTeam team, string what)
    {
        foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot && p.Team == team))
            p.PrintToChat($" {ChatColors.Green}Bots{ChatColors.Default}: {what}");
    }
}
