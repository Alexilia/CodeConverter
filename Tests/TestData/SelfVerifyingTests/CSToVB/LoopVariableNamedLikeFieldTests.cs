using Xunit;

public class LoopVariableNamedLikeField
{
    private int i;

    private void Bump() { i++; }

    [Fact]
    public void LoopDoesNotChangeTheField()
    {
        int count = 0;
        for (int i = 0; i < 3; i++) { Bump(); count++; }
        for (var i = 0; i < 3; i++) { Bump(); count++; }
        Assert.Equal(6, count);
        Assert.Equal(6, i);
    }
}
