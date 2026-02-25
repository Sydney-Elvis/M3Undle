using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using M3Undle.Cli.Commands;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests.Commands;

[TestClass]
public class CommandContextBuilderValidationTests
{
    [TestMethod]
    public async Task GroupsCommand_UnknownOption_ThrowsException()
    {
        var options = new CommandOptionSet(new Dictionary<string, List<string>>
        {
            { "playlist-url", new List<string>{ "http://test" } },
            { "out", new List<string>{ "groups.txt" } } // invalid for groups
        });
        await Assert.ThrowsAsync<CommandOptionException>(async () =>
        {
            await CommandContextBuilder.CreateAsync(options, CommandKind.Groups, TextWriter.Null, CancellationToken.None);
        });
    }

    [TestMethod]
    public async Task RunCommand_UnknownOption_ThrowsException()
    {
        var options = new CommandOptionSet(new Dictionary<string, List<string>>
        {
            { "playlist-url", new List<string>{ "http://test" } },
            { "out-groups", new List<string>{ "groups.txt" } } // invalid for run
        });
        await Assert.ThrowsAsync<CommandOptionException>(async () =>
        {
            await CommandContextBuilder.CreateAsync(options, CommandKind.Run, TextWriter.Null, CancellationToken.None);
        });
    }
}

