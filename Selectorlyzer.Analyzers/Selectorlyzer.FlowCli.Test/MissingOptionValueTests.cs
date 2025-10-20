using System;
using System.IO;
using System.Threading.Tasks;
using Selectorlyzer.FlowCli;
using Xunit;

namespace Selectorlyzer.FlowCli.Test;

public class MissingOptionValueTests
{
    [Fact]
    public async Task SolutionOptionWithoutValueProducesError()
    {
        var args = new[] { "--solution", "--flow", "Pattern" };
        var originalError = Console.Error;
        using var errorWriter = new StringWriter();
        Console.SetError(errorWriter);

        try
        {
            var exitCode = await Program.Main(args);
            Assert.Equal(1, exitCode);

            var errorOutput = errorWriter.ToString();
            Assert.Contains("Option '--solution' requires a value", errorOutput);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
