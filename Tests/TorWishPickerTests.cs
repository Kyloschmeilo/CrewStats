using System.Collections.Generic;
using Xunit;

namespace CrewStats.Tests;

public class TorWishPickerTests
{
    private static TorWishPicker Picker(params TorWishPicker.Wish[] wishes) => new(wishes, _ => 0);

    private static TorWishPicker.Wish Wish(long id, byte player, bool admin = false, params string[] roles) => new(id, player, roles, admin);

    [Fact]
    public void Wunschrolle_geht_an_den_Wuenschenden()
    {
        var picker = Picker(Wish(1, 5, false, "Sheriff"));
        Assert.Equal((byte)5, picker.Pick("Sheriff", new List<byte> { 1, 2, 5, 7 }));
        Assert.True(picker.Wishes[0].Fulfilled);
    }

    [Fact]
    public void Gross_und_Kleinschreibung_egal_und_mehrere_Varianten()
    {
        var picker = Picker(Wish(1, 3, false, "NiceGuesser", "EvilGuesser"));
        Assert.Equal((byte)3, picker.Pick("evilguesser", new List<byte> { 3, 4 }));
    }

    [Fact]
    public void Andere_Rollen_gehen_nicht_an_Wartende()
    {
        var picker = Picker(Wish(1, 2, false, "Sheriff"));
        // Zufall liefert immer Index 0 – der Wartende (2) steht vorne, darf aber nicht gezogen werden
        Assert.Equal((byte)4, picker.Pick("Medic", new List<byte> { 2, 4 }));
        Assert.False(picker.Wishes[0].Fulfilled);
        Assert.Equal((byte)2, picker.Pick("Sheriff", new List<byte> { 2 }));
    }

    [Fact]
    public void Nur_Wartende_uebrig_dann_zieht_TOR_selbst()
    {
        var picker = Picker(Wish(1, 2, false, "Sheriff"), Wish(2, 3, false, "Medic"));
        Assert.Null(picker.Pick("Jester", new List<byte> { 2, 3 }));
    }

    [Fact]
    public void Niemand_wartet_dann_zieht_TOR_selbst()
    {
        var picker = Picker(Wish(1, 9, false, "Sheriff"));
        Assert.Null(picker.Pick("Medic", new List<byte> { 1, 2 })); // Wartender nicht in der Liste (anderes Team)
        Assert.Null(picker.Pick("Medic", new List<byte>()));
    }

    [Fact]
    public void Admin_zuerst_dann_wer_zuerst_gewuenscht_hat()
    {
        var picker = Picker(Wish(1, 2, false, "Sheriff"), Wish(5, 3, true, "Sheriff"), Wish(3, 4, false, "Sheriff"));
        Assert.Equal((byte)3, picker.Pick("Sheriff", new List<byte> { 2, 3, 4 }));
        Assert.Equal((byte)2, picker.Pick("Sheriff", new List<byte> { 2, 4 }));
        Assert.Equal((byte)4, picker.Pick("Sheriff", new List<byte> { 4 }));
    }

    [Fact]
    public void Erfuellter_Wunsch_zaehlt_nicht_mehr_als_wartend()
    {
        var picker = Picker(Wish(1, 2, false, "Sheriff"));
        picker.Pick("Sheriff", new List<byte> { 2, 3 });
        Assert.Null(picker.Pick("Medic", new List<byte> { 2, 3 }));
    }
}
