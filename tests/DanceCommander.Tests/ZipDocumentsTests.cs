using DanceCommander.Providers.Archive;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>Enter opens a document that is a zip inside in its application, not as a folder.</summary>
public class ZipDocumentsTests
{
    [Theory]
    [InlineData("Quality Report 2026H1.docx")]
    [InlineData("budget.XLSX")]
    [InlineData("slides.pptx")]
    [InlineData("notes.odt")]
    [InlineData("book.epub")]
    public void DocumentsOpenInTheirApplication(string name) => ZipDocuments.IsDocument(name).ShouldBeTrue();

    [Theory]
    [InlineData("pack.zip")]
    [InlineData("library.jar")]
    [InlineData("Package.1.0.0.nupkg")]
    [InlineData("extension.vsix")]
    [InlineData("logs.tar.gz")]
    [InlineData("README")]
    public void ArchivesAndPackagesStayBrowsable(string name) => ZipDocuments.IsDocument(name).ShouldBeFalse();
}
