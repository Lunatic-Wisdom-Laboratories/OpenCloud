using System;
using OpenCloud.Core.Scripts;

namespace OpenCloud.Game.Items;

public sealed partial class ItemCatalog
{
    /// <summary>The retail item table script, converted to <see cref="DocumentPath"/>.</summary>
    public const string CommonDataPath = "menu/cfg7/comdat.cfg";

    /// <summary>Tag table of <c>gamedata_tag</c>, for binary scripts.</summary>
    public static readonly string[] Tags =
    {
        "COMINIT", "COM", "WEPNUM", "WEP", "WEP_ST", "WEP_ST_L", "WEP_ST2", "WEP_ST2_L", "WEP_SPE",
        "WEP_BUILD", "ITEMINIT", "ITEM", "AT_INIT", "AT_ST", "AT_ST2", "AT_ST_SP", "ROBOINIT",
        "RB_PARTS", "GRDNUM", "GRD", "FISHINIT", "FISH", "MES_SYS", "MES_SYSSPE",
    };

    public static ItemCatalog ParseScript(ReadOnlySpan<byte> script)
    {
        var catalog = new ItemCatalog();
        foreach (var command in GameScript.Parse(script, Tags))
        {
            if (command.Tag != "COM")
                continue;
            catalog.Add(new ItemRecord(
                ItemNo: command.Arg(0).AsInt,
                Type: (ItemDataType)command.Arg(1).AsInt,
                ListNo: command.Arg(2).AsInt,
                ActiveSet: command.Arg(3).AsInt != 0,
                StackNum: command.Arg(4).AsInt,
                MaxNum: (ushort)command.Arg(5).AsInt,
                IconTextureNo: command.Arg(6).AsInt,
                IconNo: command.Arg(7).AsInt,
                MessageNo: command.Arg(8).AsInt,
                FileName: command.Arg(9).AsString ?? string.Empty,
                Attribute: (ItemAttribute)(uint)command.Arg(10).AsInt));
        }
        return catalog;
    }
}
