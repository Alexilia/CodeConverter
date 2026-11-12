using System;
using Xunit;

public enum Img { None, Info }

public class Overloaded
{
    public string Chosen;
    public Overloaded(Img i) { Chosen = "img"; }
    public Overloaded(string s) { Chosen = "string"; }
    public Overloaded(int i) { Chosen = "int"; }
    public Overloaded(long l) { Chosen = "long"; }
    public Overloaded(double d) { Chosen = "double"; }
    public Overloaded(Func<int> f) { Chosen = "Func<int>"; }
    public Overloaded(Func<long> f) { Chosen = "Func<long>"; }
}

public class CastsSelectingConstructorOverloads
{
    [Fact]
    public void CastSelectsTheConstructor()
    {
        object o = Img.Info;
        long l = 1;
        int i = 1;
        bool b = true;
        double d = 1.7;
        Assert.Equal("img", new Overloaded((Img)o).Chosen);
        Assert.Equal("int", new Overloaded((int)l).Chosen);
        Assert.Equal("long", new Overloaded((long)i).Chosen);
        Assert.Equal("int", new Overloaded(b ? (int)l : 0).Chosen);
        Assert.Equal("Func<int>", new Overloaded(() => (int)l).Chosen);
        Assert.Equal("int", new Overloaded((int)d).Chosen);
    }
}
