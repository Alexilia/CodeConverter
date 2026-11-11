using System.Linq;
using Xunit;

public class Icon
{
    public static Icon FromHandle(int h) { return new Icon(); }
}

public class Item<T>
{
    public static Item<T> Create() { return new Item<T>(); }
    public static Item<T>[] All() { return new[] { new Item<T>(), new Item<T>() }; }
}

public class Entry
{
    public static Entry[] All() { return new[] { new Entry(), new Entry(), new Entry() }; }
}

public class VariableNamesThatMeanSomethingElseInVisualBasic
{
    private int Month() { return -1; }

    [Fact]
    public void LoopVariableNamedLikeMethodIsALocal()
    {
        int sum = 0;
        for (int month = 1; month <= 12; month++) sum += month;
        foreach (var month in new[] { 1, 2 }) sum += month;
        Assert.Equal(81, sum);
    }

    [Fact]
    public void VariableNamedLikeTypeInItsInitializer()
    {
        var icon = Icon.FromHandle(1);
        Icon typedIcon = Icon.FromHandle(2);
        int entries = 0;
        foreach (var entry in Entry.All()) entries++;
        foreach (Entry entry in Entry.All()) entries++;
        Assert.NotNull(icon);
        Assert.NotNull(typedIcon);
        Assert.Equal(6, entries);
    }

    [Fact]
    public void VariableNamedLikeTypeInItsNameOf()
    {
        var icon = nameof(Icon);
        string entry = nameof(Entry);
        Assert.Equal("IconEntry", icon + entry);
    }

    [Fact]
    public void VariableNamedLikeGenericTypeInItsInitializer()
    {
        var item = Item<int>.Create();
        int items = 0;
        foreach (var other in Item<int>.All()) items++;
        foreach (var item2 in Item<string>.All()) items++;
        Assert.NotNull(item);
        Assert.Equal(4, items);
    }

    [Fact]
    public void LoopsAfterQueryWithSameName()
    {
        var numbers = new[] { 1, 6, 7 };
        var res = from n in numbers where n > 5 select n;
        int sum = 0;
        foreach (var n in res) sum += n;
        foreach (int n in res) sum += n;
        Assert.Equal(26, sum);
    }
}
