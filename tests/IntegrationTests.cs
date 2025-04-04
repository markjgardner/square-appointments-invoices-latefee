using System;
using System.Threading.Tasks;
using Xunit;

public class IntegrationTests
{
    [Fact]
    public async Task ApplicationRunsSuccessfully()
    {
        // Arrange
        Environment.SetEnvironmentVariable("SQUARE_ACCESS_TOKEN", "your-test-access-token");

        // Act
        var exception = await Record.ExceptionAsync(() => Program.Main(Array.Empty<string>()));

        // Assert
        Assert.Null(exception);
    }
}