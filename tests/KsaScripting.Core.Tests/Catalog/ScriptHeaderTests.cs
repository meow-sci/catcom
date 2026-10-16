using KsaScripting.Core.Catalog;

namespace KsaScripting.Core.Tests.Catalog;

[TestFixture]
public sealed class ScriptHeaderTests
{
    [Test]
    public void Parse_ALuaHeader_ReadsNameAutostartAndTerminal()
    {
        var header = ScriptHeader.Parse("-- @ksa name=\"Ascent guidance\" autostart=true terminal=yes\nlocal x = 1\n");

        Assert.That(header, Is.EqualTo(new ScriptHeader("Ascent guidance", true, true)));
    }

    [Test]
    public void Parse_ATypeScriptHeader_UsesTheSlashPrefix()
    {
        var header = ScriptHeader.Parse("// @ksa name=Door autostart=1\nexport function main() {}\n");

        Assert.That(header, Is.EqualTo(new ScriptHeader("Door", true, false)));
    }

    [Test]
    public void Parse_BothPrefixesAreAcceptedForEitherLanguage()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptHeader.Parse("// @ksa terminal=true\nlocal x").Terminal, Is.True);
            Assert.That(ScriptHeader.Parse("-- @ksa terminal=true\nexport const x = 1").Terminal, Is.True);
        });
    }

    [TestCase("true", true)]
    [TestCase("TRUE", true)]
    [TestCase("1", true)]
    [TestCase("yes", true)]
    [TestCase("false", false)]
    [TestCase("0", false)]
    [TestCase("no", false)]
    [TestCase("maybe", false)]
    public void Parse_BooleanSpellings(string text, bool expected)
    {
        var header = ScriptHeader.Parse($"-- @ksa autostart={text}\n");

        Assert.That(header.Autostart, Is.EqualTo(expected));
    }

    [Test]
    public void Parse_MultipleKsaLinesMerge_LaterKeysWin()
    {
        var header = ScriptHeader.Parse("-- @ksa name=First autostart=true\n-- some prose\n-- @ksa name=Second terminal=1\n");

        Assert.That(header, Is.EqualTo(new ScriptHeader("Second", true, true)));
    }

    [Test]
    public void Parse_UnknownKeysAndBareWordsAreIgnored()
    {
        var header = ScriptHeader.Parse("-- @ksa colour=red bare name=\"Keep\" =orphan\n");

        Assert.That(header, Is.EqualTo(new ScriptHeader("Keep", false, false)));
    }

    [Test]
    public void Parse_QuotedValuesMayContainSpacesAndEscapedQuotes()
    {
        var header = ScriptHeader.Parse("-- @ksa name=\"The \\\"big\\\" burn\" terminal=true\n");

        Assert.That(header, Is.EqualTo(new ScriptHeader("The \"big\" burn", false, true)));
    }

    [Test]
    public void Parse_StopsAtTheFirstCodeLine()
    {
        var header = ScriptHeader.Parse("-- plain comment\n\n-- @ksa name=Early\nlocal x = 1\n-- @ksa autostart=true\n");

        Assert.That(header, Is.EqualTo(new ScriptHeader("Early", false, false)));
    }

    [Test]
    public void Parse_OnlyTheFirstThirtyLinesAreScanned()
    {
        var padding = string.Concat(Enumerable.Repeat("--\n", 30));

        Assert.Multiple(() =>
        {
            Assert.That(ScriptHeader.Parse(padding + "-- @ksa name=TooLate\n").Title, Is.Null);
            Assert.That(ScriptHeader.Parse(string.Concat(Enumerable.Repeat("--\n", 29)) + "-- @ksa name=JustInTime\n").Title, Is.EqualTo("JustInTime"));
        });
    }

    [Test]
    public void Parse_ToleratesCrLfBomShebangAndBlockCommentLines()
    {
        var code = "﻿#!/usr/bin/env ksa\r\n/* block */\r\n * @ksa name=Windows\r\n-- @ksa autostart=true\r\n";

        var header = ScriptHeader.Parse(code);

        Assert.That(header, Is.EqualTo(new ScriptHeader("Windows", true, false)));
    }

    [Test]
    public void Parse_AMentionOfKsaInsideProse_IsNotAHeader()
    {
        var header = ScriptHeader.Parse("-- this file is @ksa name=Nope\n-- @ksanothing name=Nope\n");

        Assert.That(header, Is.EqualTo(new ScriptHeader(null, false, false)));
    }

    [Test]
    public void Parse_NoHeader_YieldsDefaults()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptHeader.Parse(""), Is.EqualTo(new ScriptHeader(null, false, false)));
            Assert.That(ScriptHeader.Parse("local x = 1"), Is.EqualTo(new ScriptHeader(null, false, false)));
            Assert.That(ScriptHeader.Parse("-- @ksa"), Is.EqualTo(new ScriptHeader(null, false, false)));
            Assert.That(ScriptHeader.Parse("-- @ksa name="), Is.EqualTo(new ScriptHeader(null, false, false)));
        });
    }
}
