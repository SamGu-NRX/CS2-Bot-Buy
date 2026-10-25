namespace BotBuyPatch;

// Guns bought this round, keyed by entity handle, so a save refunds only a gun the bot bought and still
// holds, never a dropped, replaced or gifted one. No CounterStrikeSharp types, so it can be checked outside the game.
internal sealed class PurchaseLedger
{
    // Round lets a deferred refund see that the round it was recorded in is over.
    public readonly record struct Purchase(int Round, int Slot, uint Handle, int Paid);

    private readonly List<Purchase> _bought = new();

    public int Round { get; private set; }

    public void NewRound() { Round++; _bought.Clear(); }

    public Purchase Add(int slot, uint handle, int paid)
    {
        var p = new Purchase(Round, slot, handle, paid);
        _bought.Add(p);
        return p;
    }

    // Forgets the bot's recorded guns, or only the one given, and returns the credit for those that
    // removeIfHeld finds and removes. A purchase from an earlier round credits nothing.
    public int Undo(int slot, Purchase? only, Func<uint, bool> removeIfHeld)
    {
        if (only?.Round is { } r && r != Round) return 0;
        int credit = 0;
        for (int i = _bought.Count - 1; i >= 0; i--)
        {
            var p = _bought[i];
            if (p.Slot != slot || (only != null && p.Handle != only.Value.Handle)) continue;
            _bought.RemoveAt(i);
            if (removeIfHeld(p.Handle)) credit += p.Paid;
        }
        return credit;
    }
}
