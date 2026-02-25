using System;
using System.Linq;
using M3Undle.Cli;
using M3Undle.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// Suppress MSTEST0032 for this file - we intentionally test constant values as documentation
#pragma warning disable MSTEST0032

namespace M3Undle.Cli.Tests;

[TestClass]
public class ExitCodesTests
{
    [TestMethod]
    public void ExitCodes_Success_IsZero()
    {
        Assert.AreEqual(0, ExitCodes.Success);
    }

    [TestMethod]
    public void ExitCodes_AllErrorCodes_AreNonZero()
    {
        Assert.AreNotEqual(0, ExitCodes.ConfigError);
        Assert.AreNotEqual(0, ExitCodes.NetworkError);
        Assert.AreNotEqual(0, ExitCodes.AuthError);
        Assert.AreNotEqual(0, ExitCodes.IoError);
        Assert.AreNotEqual(0, ExitCodes.ParseError);
    }

    [TestMethod]
    public void ExitCodes_AllErrorCodes_AreUnique()
    {
        var codes = new[]
        {
            ExitCodes.ConfigError,
            ExitCodes.NetworkError,
            ExitCodes.AuthError,
            ExitCodes.IoError,
            ExitCodes.ParseError
        };

        var uniqueCodes = codes.Distinct().ToList();
        Assert.HasCount(codes.Length, uniqueCodes, "All exit codes should be unique");
    }

    [TestMethod]
    public void ExitCodes_ConfigError_HasCorrectValue()
    {
        Assert.AreEqual(2, ExitCodes.ConfigError);
    }

    [TestMethod]
    public void ExitCodes_NetworkError_HasCorrectValue()
    {
        Assert.AreEqual(3, ExitCodes.NetworkError);
    }

    [TestMethod]
    public void ExitCodes_AuthError_HasCorrectValue()
    {
        Assert.AreEqual(4, ExitCodes.AuthError);
    }

    [TestMethod]
    public void ExitCodes_IoError_HasCorrectValue()
    {
        Assert.AreEqual(5, ExitCodes.IoError);
    }

    [TestMethod]
    public void ExitCodes_ParseError_HasCorrectValue()
    {
        Assert.AreEqual(6, ExitCodes.ParseError);
    }
}

#pragma warning restore MSTEST0032

