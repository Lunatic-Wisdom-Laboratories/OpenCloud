using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using OpenCloud.Game.Items;
using OpenCloud.Game.Save;
using OpenCloud.Net;

namespace OpenCloud.Scenes;

/// <summary>
/// Headless check of the shared save state and its network sync, with no game data: it builds a
/// small made-up item table, tests the inventory rules, then runs a host and a client in this
/// one process (each on its own multiplayer API) over ENet on localhost, makes changes from both
/// sides, breaks the client's copy on purpose and waits for the digest check to repair it.
/// Quits with exit code 0 on success. Run with
/// <c>godot --headless res://src/Scenes/NetSyncSmokeTest.tscn</c>.
/// </summary>
public partial class NetSyncSmokeTest : Node
{
    private const int Port = 17777;
    private const int Potion = 100;
    private const int Gem = 101;
    private const int Sword = 102;
    private const int Key = 103;

    private int _failures;

    public override void _Ready() => _ = Run();

    private async Task Run()
    {
        try
        {
            CheckInventoryRules();
            await CheckNetworkSync();
        }
        catch (Exception e)
        {
            Fail($"exception: {e}");
        }
        GD.Print(_failures == 0 ? "NetSyncSmokeTest: OK" : $"NetSyncSmokeTest: {_failures} failure(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private static ItemCatalog MakeCatalog()
    {
        var catalog = new ItemCatalog();
        catalog.Add(new ItemRecord(Potion, ItemDataType.Recovery, 0, true, 10, 99, 0, 0, 0, "", ItemAttribute.None));
        catalog.Add(new ItemRecord(Gem, ItemDataType.Gem, 0, false, 99, 20, 0, 0, 0, "", ItemAttribute.None));
        catalog.Add(new ItemRecord(Sword, ItemDataType.MonicaMelee, 0, false, 1, 500, 0, 0, 0, "", ItemAttribute.None));
        catalog.Add(new ItemRecord(Key, ItemDataType.KeyItem, 0, false, 1, 1, 0, 0, 0, "", ItemAttribute.Unique));
        return catalog;
    }

    private void CheckInventoryRules()
    {
        var state = new GlobalSaveState(MakeCatalog());

        Expect(state.Apply(SaveOperation.AddItem(Potion, 25)) == 25, "25 potions fit on the board");
        Expect(state.Inventory[0].Count == 10 && state.Inventory[1].Count == 10 && state.Inventory[2].Count == 5, "potions stack by 10");
        Expect(state.Apply(SaveOperation.AddItem(Potion, 80)) == 74, "potions stop at the owned limit of 99");
        Expect(state.CountItem(Potion) == 105, "potions past the limit go to the overflow places");

        Expect(state.Apply(SaveOperation.RemoveItem(Potion, 7)) == 7, "7 potions removed");
        Expect(state.CountItem(Potion) == 98, "removal counts");

        Expect(state.Apply(SaveOperation.AddItem(Sword, 2)) == 2, "two swords");
        Expect(state.Inventory.Slots.Count(s => s.ItemNo == Sword) == 2, "weapons never stack");
        Expect(state.Inventory.Slots.First(s => s.ItemNo == Sword).Weapon != null, "weapons carry weapon state");

        Expect(state.Apply(SaveOperation.AddItem(Key, 1)) == 1, "key received");
        Expect(state.Apply(SaveOperation.AddItem(Key, 1)) == 1 && state.CountItem(Key) == 1, "unique items are not duplicated");

        Expect(state.Inventory.MainSize == Inventory.BoardSize, "board starts small");
        state.Apply(SaveOperation.SetBitFlag(SaveBitFlag.ItemBoardExpanded, true));
        Expect(state.Inventory.MainSize == Inventory.ExpandedBoardSize, "story flag expands the board");

        Expect(state.Apply(SaveOperation.AddMoney(2_000_000)) == GlobalSaveState.MoneyMax, "money capped at 999999");
        Expect(state.Apply(SaveOperation.AddMoney(-5_000_000)) == 0, "money floored at 0");
        Expect(!state.Validate(SaveOperation.SetBitFlag(GlobalSaveState.BitFlagCount, true)), "out-of-range flag refused");

        var copy = new GlobalSaveState(state.Catalog);
        SaveStateCodec.Deserialize(copy, SaveStateCodec.Serialize(state));
        Expect(SaveStateCodec.Hash(copy) == SaveStateCodec.Hash(state), "snapshot round trip keeps the digest");
    }

    private async Task CheckNetworkSync()
    {
        var host = new NetworkManager { Name = "Host", PlayerName = "Host", HashInterval = 0.5 };
        var client = new NetworkManager { Name = "Client", PlayerName = "Guest", HashInterval = 0.5 };
        GetTree().SetMultiplayer(MultiplayerApi.CreateDefaultInterface(), GetPath().ToString() + "/Host");
        GetTree().SetMultiplayer(MultiplayerApi.CreateDefaultInterface(), GetPath().ToString() + "/Client");
        AddChild(host);
        AddChild(client);
        host.Initialize(new GlobalSaveState(MakeCatalog()));
        client.Initialize(new GlobalSaveState(MakeCatalog()));

        host.Submit(SaveOperation.AddMoney(1234));
        host.Submit(SaveOperation.SetMapFlag(3, 7, true));

        Expect(host.Host(Port) == Error.Ok, "host listens");
        Expect(client.Join("127.0.0.1", Port) == Error.Ok, "client connects");
        Expect(await WaitFor(() => client.IsInSession && host.Roster.Count == 2, 5), "client admitted");
        Expect(client.Roster.Count == 2, "client sees both players");
        Expect(client.LocalPlayer?.Character == PlayableCharacter.Monica, "second player controls Monica");
        Expect(host.LocalPlayer?.Character == PlayableCharacter.Max, "host controls Max");
        Expect(client.State.Money == 1234 && client.State.GetMapFlag(3, 7), "joining copies the host's state");

        Expect(client.Submit(SaveOperation.AddItem(Potion, 12)) == null, "client changes go through the host");
        Expect(client.State.CountItem(Potion) == 0, "client waits for the host's confirmation");
        client.Submit(SaveOperation.AddMoney(-234));
        client.Submit(SaveOperation.SetBitFlag(0x40, true));
        host.Submit(SaveOperation.AddItem(Gem, 3));
        client.Submit(SaveOperation.SetBitFlag(GlobalSaveState.BitFlagCount + 5, true));

        Expect(await WaitFor(() => client.Sync.Revision == host.Sync.Revision && host.Sync.Revision == 4, 5), "operations replayed in order");
        Expect(host.State.CountItem(Potion) == 12 && client.State.CountItem(Potion) == 12, "inventory synchronised");
        Expect(host.State.Money == 1000 && client.State.Money == 1000, "money synchronised");
        Expect(host.State.GetBitFlag(0x40) && client.State.GetBitFlag(0x40), "flags synchronised");
        Expect(host.Sync.Hash() == client.Sync.Hash(), "digests match");

        bool desynced = false;
        bool repaired = false;
        client.DesyncDetected += _ => desynced = true;
        client.SnapshotApplied += _ => repaired = true;
        client.State.Apply(SaveOperation.AddMoney(1));
        Expect(await WaitFor(() => repaired, 5), "digest check repairs a broken copy");
        Expect(desynced, "desync reported");
        Expect(host.Sync.Hash() == client.Sync.Hash() && client.State.Money == 1000, "copy matches the host after repair");

        long left = 0;
        host.PlayerLeft += id => left = id;
        client.Leave();
        Expect(await WaitFor(() => left != 0 && host.Roster.Count == 1, 5), "host notices the client leaving");
        host.Leave();
    }

    private async Task<bool> WaitFor(Func<bool> condition, double seconds)
    {
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (!condition())
        {
            if (Time.GetTicksMsec() / 1000.0 > end)
                return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    private void Expect(bool condition, string what)
    {
        if (condition)
        {
            GD.Print($"  ok   {what}");
            return;
        }
        Fail(what);
    }

    private void Fail(string what)
    {
        _failures++;
        GD.PrintErr($"  FAIL {what}");
    }
}
