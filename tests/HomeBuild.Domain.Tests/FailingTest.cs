namespace HomeBuild.Domain.Tests;

public class FailingTest
{
    [Fact]
    public void Deliberately_fails() => Assert.Equal(1, 2);
}
