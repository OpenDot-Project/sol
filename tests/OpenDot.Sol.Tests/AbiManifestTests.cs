// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AbiManifestTests
{
    private static string PathToManifest => Path.Combine(AppContext.BaseDirectory, "fixtures", "abi-manifest.xml");

    [Fact]
    public void DocumentMatchesTargetLayoutsAndEveryRuntimeCExport()
    {
        using var input = File.OpenRead(PathToManifest);
        var document = AbiManifest.Validate(input);
        var exports = document.Root!.Element("runtime-exports")!.Elements().ToDictionary(
            e => (string)e.Attribute("name")!, StringComparer.Ordinal);
        var declarations = new Dictionary<string, (string Header, string Signature)>(StringComparer.Ordinal);
        foreach (string header in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "abi"), "*.h"))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(header),
                @"SOL_RT_API\s+(?:extern\s+)?([^;{}]*?\b(sol_(?:rt|gc)_\w+)\s*(?:\([^;{}]*?\))?(?:\s*noexcept(?:\(false\))?)?)\s*;"))
            {
                declarations.Add(match.Groups[2].Value,
                    (Path.GetFileName(header), Regex.Replace(match.Groups[1].Value, @"\s+", " ")));
            }
        }
        Assert.Equal(declarations.Keys.Order(StringComparer.Ordinal), exports.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, declaration) in declarations)
        {
            Assert.Equal(declaration.Header, (string?)exports[name].Attribute("header"));
            Assert.Equal(declaration.Signature, (string?)exports[name].Attribute("signature"));
        }
    }

    [Theory]
    [InlineData("schema", "2")]
    [InlineData("revision", "1")]
    [InlineData("contract", "0x534F4C0000000001")]
    [InlineData("bridgeProtocol", "0x0000000000000001")]
    [InlineData("fir", "7")]
    [InlineData("solib", "2")]
    public void IncompatibleIdentitiesAreRejected(string attribute, string value)
    {
        var document = XDocument.Load(PathToManifest);
        document.Root!.SetAttributeValue(attribute, value);
        Reject(document);
    }

    [Theory]
    [InlineData("size", "24")]
    [InlineData("alignment", "16")]
    [InlineData("naturalAlignment", "16")]
    public void AlteredLayoutIsRejected(string attribute, string value)
    {
        var document = XDocument.Load(PathToManifest);
        document.Root!.Element("layouts")!.Elements().First().SetAttributeValue(attribute, value);
        Reject(document);
    }

    [Fact]
    public void DuplicateMissingAndIncorrectRootMetadataAreRejected()
    {
        var document = XDocument.Load(PathToManifest);
        var layouts = document.Root!.Element("layouts")!;
        var header = layouts.Elements().First();
        layouts.Add(new XElement(header)); Reject(document);
        layouts.Elements().Last().Remove();
        header.Elements().First().SetAttributeValue("offset", "8"); Reject(document);
        header.Elements().First().SetAttributeValue("offset", "0");
        var objectReference = layouts.Elements().Single(e => (string?)e.Attribute("name") == "InterfaceValue")
            .Elements().First();
        objectReference.SetAttributeValue("gcReference", "false"); Reject(document);
    }

    [Theory]
    [InlineData("<!DOCTYPE sol-abi [<!ENTITY leak SYSTEM 'file:///not-readable'>]><sol-abi>&leak;</sol-abi>")]
    [InlineData("<!DOCTYPE sol-abi SYSTEM 'https://invalid.example/abi.dtd'><sol-abi/>")]
    public void DtdAndExternalEntitiesAreRejected(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.Throws<XmlException>(() => AbiManifest.Validate(stream));
    }

    [Fact]
    public void OversizedDocumentIsRejectedBeforeContractValidation()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<sol-abi>" + new string(' ', 1024 * 1024) + "</sol-abi>"));
        Assert.Throws<XmlException>(() => AbiManifest.Validate(stream));
    }

    private static void Reject(XDocument document)
    {
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString()));
        Assert.Throws<InvalidDataException>(() => AbiManifest.Validate(bytes));
    }
}
