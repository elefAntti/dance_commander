using DanceCommander.Core.Fs;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// Names another program hands over with a drag (an Outlook attachment) choose what a file is
/// called, never where it goes.
/// </summary>
public sealed class ForeignNameTests
{
    [Theory]
    [InlineData("Invoice 2026-10.pdf", "Invoice 2026-10.pdf")]
    [InlineData("  report.docx ", "report.docx")]
    [InlineData(@"..\..\Windows\evil.dll", "evil.dll")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Users\x\a.txt", "a.txt")]
    [InlineData("Re: what? <draft>.msg", "Re_ what_ _draft_.msg")]
    [InlineData("name.", "name")]
    [InlineData("name. . ", "name")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("invoice\u202Efdp.exe", "invoice_fdp.exe")]
    public void Keeps_the_name_and_drops_any_folders(string offered, string expected) =>
        ForeignName.ToFileName(offered).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"folder\")]
    [InlineData("a/..")]
    public void Nothing_left_falls_back_to_a_plain_name(string offered) =>
        ForeignName.ToFileName(offered).ShouldBe(ForeignName.Fallback);

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1.log", "_COM1.log")]
    [InlineData("lpt9", "_lpt9")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("COMX.txt", "COMX.txt")]
    [InlineData("conout$", "_conout$")]
    [InlineData("COM\u00B9.txt", "_COM\u00B9.txt")]
    public void Device_names_are_not_written_as_devices(string offered, string expected) =>
        ForeignName.ToFileName(offered).ShouldBe(expected);

    [Theory]
    [InlineData("NUL", true)]
    [InlineData("nul.txt", true)]
    [InlineData("Com3.md", true)]
    [InlineData("notes.md", false)]
    [InlineData("CONSOLE.txt", false)]
    public void A_typed_name_is_known_for_a_device(string name, bool device) =>
        ForeignName.NamesDevice(name).ShouldBe(device);
}
